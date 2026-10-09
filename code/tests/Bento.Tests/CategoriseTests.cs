using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Bento.Api.Services.Ai;

namespace Bento.Tests;

/// <summary>
/// Categorising on import (Step 4.8): rules first, then earlier rows with the
/// same payee, then the AI for the rest; the chosen values go into the import
/// per line; rules are kept per table and column.
/// </summary>
public sealed class CategoriseTests
{
    private const string Schema = """
        {
            "tables": [
                {
                    "name": "categories",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false, "unique": true },
                        { "name": "parent_id", "type": "reference", "references": "categories" }
                    ]
                },
                {
                    "name": "transactions",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "booked_on", "type": "date", "nullable": false },
                        { "name": "counterparty", "type": "text" },
                        { "name": "purpose", "type": "text" },
                        { "name": "amount", "type": "currency", "nullable": false },
                        { "name": "kind", "type": "enum", "values": ["fixed", "variable"] },
                        { "name": "category_id", "type": "reference", "references": "categories" }
                    ]
                }
            ],
            "relations": [
                { "name": "category_children", "type": "1:N", "from": "categories", "to": "categories", "column": "parent_id" },
                { "name": "category_transactions", "type": "1:N", "from": "categories", "to": "transactions", "column": "category_id" }
            ]
        }
        """;

    // Ids in the order they are created.
    private const int Food = 1, Groceries = 2, Salary = 3, Rent = 4, Streaming = 5;

    [Fact]
    public Task Suggest_UsesRules_ThenEarlierRows_ThenTheAi() => SuggestScenarioAsync(postgres: false);

    internal static async Task SuggestScenarioAsync(bool postgres)
    {
        var model = new AiTests.ScriptedModel();
        await using var host = await CreateHostAsync(model, postgres);
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        await SeedAsync(host, admin);
        await Send(host, HttpMethod.Post, "/api/v1/categorise/transactions/rules", admin,
            new { rules = new[] { new { column = "category_id", matchColumn = "counterparty", pattern = "REWE", value = Groceries.ToString() } } });
        await Send(host, HttpMethod.Put, "/api/v1/ai/settings", admin, new { apiKey = "sk-ant-test-123", monthlyLimitUsd = 5 });

        var rows = new object[]
        {
            Row("k1", "REWE Markt Berlin", "Einkauf", "-45,90"),
            Row("k2", "Vermieter Schmidt", "Miete Oktober", "-950,00"),
            Row("k3", "Netflix.com", "Abo", "-12,99"),
            Row("k4", "NETFLIX.COM", "abo", "-12,99"),
            Row("k5", "Unbekannt", "???", "-1,00"),
            // The same purpose as a rent payment, but another payee: a purpose alone decides nothing.
            Row("k6", "Hausverwaltung Nord", "Miete August", "-80,00"),
        };

        // Without the AI: only what rules and earlier rows decide, no model call.
        var plain = await AiTests.DataAsync(await Send(host, HttpMethod.Post, "/api/v1/categorise/transactions/suggest", admin, new { column = "category_id", rows }));
        var bySource = Suggestions(plain);
        Assert.Equal(("rule", Groceries.ToString(), "Groceries"), bySource["k1"]);
        Assert.Equal(("history", Rent.ToString(), "Rent"), bySource["k2"]);
        Assert.Equal(2, bySource.Count);
        Assert.Empty(model.Requests);
        Assert.Contains(plain.GetProperty("options").EnumerateArray(), o => o.GetProperty("label").GetString() == "Groceries" && o.GetProperty("group").GetString() == "Food");
        Assert.Equal(["counterparty", "purpose"], plain.GetProperty("matchColumns").EnumerateArray().Select(c => c.GetString()));

        // With the AI: the rest in one call, the same text asked once, the answer read from a code fence.
        model.Script.Enqueue(request =>
        {
            var message = ((AiText)request.Messages[0].Blocks[0]).Text;
            Assert.Contains("\"name\":\"Groceries\",\"group\":\"Food\"", message);
            Assert.Contains("Vermieter Schmidt", message); // an example of the user's own categorising
            Assert.DoesNotContain("REWE Markt Berlin", message); // decided by the rule
            Assert.Equal(3, JsonDocument.Parse(message).RootElement.GetProperty("rows").GetArrayLength());
            Assert.Empty(request.Tools);
            return new AiResponse([new AiText($$"""
                ```json
                [{"key":"r0","category":"{{Streaming}}","confidence":0.92,"keyword":"Netflix","column":"counterparty"},
                 {"key":"r1","category":null,"confidence":0.1},
                 {"key":"r2","category":"{{Rent}}","confidence":0.7,"keyword":"Hausverwaltung","column":"counterparty"}]
                ```
                """)], "end_turn", new AiUsage(3000, 200, 0, 0), "claude-opus-5-5");
        });
        var withAi = await AiTests.DataAsync(await Send(host, HttpMethod.Post, "/api/v1/categorise/transactions/suggest", admin, new { column = "category_id", rows, ai = true }));
        var all = withAi.GetProperty("suggestions").EnumerateArray().ToDictionary(s => s.GetProperty("key").GetString()!);
        Assert.Equal("rule", all["k1"].GetProperty("source").GetString());
        foreach (var k in new[] { "k3", "k4" })
        {
            Assert.Equal("ai", all[k].GetProperty("source").GetString());
            Assert.Equal(Streaming.ToString(), all[k].GetProperty("value").GetString());
            Assert.Equal("Netflix", all[k].GetProperty("keyword").GetString());
            Assert.Equal("counterparty", all[k].GetProperty("matchColumn").GetString());
            Assert.Equal(0.92, all[k].GetProperty("confidence").GetDouble());
        }
        Assert.False(all.ContainsKey("k5"));
        Assert.Single(model.Requests);
        Assert.True(withAi.GetProperty("ai").GetProperty("costUsd").GetDecimal() > 0);
        Assert.Equal("categorise", (await AiTests.DataAsync(await Send(host, HttpMethod.Get, "/api/v1/ai/usage", admin)))[0].GetProperty("feature").GetString());

        // Scripts may use rules and history, but not Bento's AI.
        var key = await ApiKeyTests.CreateKeyAsync(host, admin, "read-write");
        Assert.Equal(HttpStatusCode.OK, (await Send(host, HttpMethod.Post, "/api/v1/categorise/transactions/suggest", key, new { column = "category_id", rows })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(host, HttpMethod.Post, "/api/v1/categorise/transactions/suggest", key, new { column = "category_id", rows, ai = true })).StatusCode);

        // Choice columns work the same way; other columns are refused.
        var choice = await AiTests.DataAsync(await Send(host, HttpMethod.Post, "/api/v1/categorise/transactions/suggest", admin, new { column = "kind", rows }));
        Assert.Equal(["fixed", "variable"], choice.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("value").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(host, HttpMethod.Post, "/api/v1/categorise/transactions/suggest", admin, new { column = "amount", rows })).StatusCode);
    }

    [Fact]
    public async Task Suggest_WithoutAiSetUp_SaysWhy_AndKeepsTheRuleResults()
    {
        var model = new AiTests.ScriptedModel();
        await using var host = await CreateHostAsync(model);
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        await SeedAsync(host, admin);

        var result = await AiTests.DataAsync(await Send(host, HttpMethod.Post, "/api/v1/categorise/transactions/suggest", admin, new
        {
            column = "category_id",
            ai = true,
            rows = new[] { Row("a", "Vermieter Schmidt", "Miete", "-950,00"), Row("b", "Netflix.com", "Abo", "-12,99") }
        }));
        Assert.Single(result.GetProperty("suggestions").EnumerateArray());
        Assert.Contains("not set up", result.GetProperty("aiError").GetString());
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task Rules_AreKeptPerTable_TheSameKeywordReplacesTheOldRule()
    {
        await using var host = await CreateHostAsync(new AiTests.ScriptedModel());
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        await SeedAsync(host, admin);
        const string url = "/api/v1/categorise/transactions/rules";

        await Send(host, HttpMethod.Post, url, admin, new { rules = new[] { new { column = "category_id", matchColumn = "counterparty", pattern = "Netflix", value = Food.ToString() } } });
        var rules = await AiTests.DataAsync(await Send(host, HttpMethod.Post, url, admin, new
        {
            rules = new[]
            {
                new { column = "category_id", matchColumn = "counterparty", pattern = "netflix", value = Streaming.ToString() },
                new { column = "category_id", matchColumn = "purpose", pattern = "Miete", value = Rent.ToString() },
            }
        }));
        Assert.Equal(2, rules.GetArrayLength());
        var netflix = rules.EnumerateArray().Single(r => r.GetProperty("matchColumn").GetString() == "counterparty");
        Assert.Equal("netflix", netflix.GetProperty("pattern").GetString());
        Assert.Equal("Streaming", netflix.GetProperty("label").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(host, HttpMethod.Post, url, admin, new { rules = new[] { new { column = "category_id", matchColumn = "amount", pattern = "x1", value = Food.ToString() } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(host, HttpMethod.Post, url, admin, new { rules = new[] { new { column = "category_id", matchColumn = "purpose", pattern = "x1", value = "999" } } })).StatusCode);

        var viewer = await AiTests.ViewerTokenAsync(host, admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(host, HttpMethod.Get, url, viewer)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(host, HttpMethod.Delete, $"{url}/{netflix.GetProperty("id").GetInt64()}", admin)).StatusCode);
        Assert.Single((await AiTests.DataAsync(await Send(host, HttpMethod.Get, url, admin))).EnumerateArray());
    }

    [Fact]
    public Task Import_TakesTheValuesChosenPerLine() => ImportScenarioAsync(postgres: false);

    internal static async Task ImportScenarioAsync(bool postgres)
    {
        await using var host = await CreateHostAsync(new AiTests.ScriptedModel(), postgres);
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        await SeedAsync(host, admin);
        var mapping = ImportApiTests.Mapping.Take(4).ToArray(); // without the file's own category column
        var body = new { content = ImportApiTests.Encode(ImportApiTests.StatementSeptember), mapping, keyColumns = ImportApiTests.KeyColumns };

        var preview = await AiTests.DataAsync(await Send(host, HttpMethod.Post, "/api/v1/import/transactions/preview", admin, body));
        var lines = preview.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("line").GetInt32().ToString()).ToList();

        var assign = new Dictionary<string, string?> { [lines[0]] = Groceries.ToString(), [lines[1]] = "Salary", [lines[2]] = "Nope" };
        var checkedRows = await AiTests.DataAsync(await Send(host, HttpMethod.Post, "/api/v1/import/transactions/preview", admin,
            new { body.content, body.mapping, body.keyColumns, assign = new { column = "category_id", values = assign } }));
        var byLine = checkedRows.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("line").GetInt32().ToString());
        Assert.Equal("Groceries", byLine[lines[0]].GetProperty("display").GetProperty("category_id").GetString());
        Assert.Equal("Salary", byLine[lines[1]].GetProperty("display").GetProperty("category_id").GetString());
        Assert.Equal("invalid", byLine[lines[2]].GetProperty("status").GetString());
        Assert.Contains("Nope", byLine[lines[2]].GetProperty("errors")[0].GetProperty("message").GetString());

        assign.Remove(lines[2]);
        var done = await AiTests.DataAsync(await Send(host, HttpMethod.Post, "/api/v1/import/transactions", admin,
            new { body.content, body.mapping, body.keyColumns, assign = new { column = "category_id", values = assign } }));
        Assert.Equal(4, done.GetProperty("inserted").GetInt32());

        var stored = (await AiTests.DataAsync(await Send(host, HttpMethod.Get, "/api/v1/data/transactions?perPage=100", admin))).EnumerateArray().ToList();
        Assert.Equal(Groceries, stored.Single(r => r.GetProperty("counterparty").GetString() == "REWE Markt").GetProperty("category_id").GetInt32());
        Assert.Equal(Salary, stored.Single(r => r.GetProperty("counterparty").GetString() == "Arbeitgeber GmbH" && r.GetProperty("purpose").GetString() == "Gehalt September").GetProperty("category_id").GetInt32());
    }

    private static object Row(string key, string counterparty, string purpose, string amount) =>
        new { key, values = new Dictionary<string, string> { ["counterparty"] = counterparty, ["purpose"] = purpose, ["amount"] = amount, ["booked_on"] = "2026-10-01" } };

    private static Dictionary<string, (string Source, string Value, string Label)> Suggestions(JsonElement result) =>
        result.GetProperty("suggestions").EnumerateArray().ToDictionary(
            s => s.GetProperty("key").GetString()!,
            s => (s.GetProperty("source").GetString()!, s.GetProperty("value").GetString()!, s.GetProperty("label").GetString()!));

    private static async Task SeedAsync(ApiIntegrationTestHost host, string admin)
    {
        foreach (var category in new object[]
                 {
                     new { name = "Food" }, new { name = "Groceries", parent_id = Food }, new { name = "Salary" },
                     new { name = "Rent" }, new { name = "Streaming" }
                 })
            (await Send(host, HttpMethod.Post, "/api/v1/data/categories", admin, category)).EnsureSuccessStatusCode();
        foreach (var row in new object[]
                 {
                     new { booked_on = "2026-08-01", counterparty = "Vermieter Schmidt", purpose = "Miete August", amount = -950m, category_id = Rent },
                     new { booked_on = "2026-09-01", counterparty = "Vermieter Schmidt", purpose = "Miete September", amount = -950m, category_id = Rent },
                     new { booked_on = "2026-09-01", counterparty = "Arbeitgeber GmbH", purpose = "Gehalt", amount = 3250m, category_id = Salary },
                 })
            (await Send(host, HttpMethod.Post, "/api/v1/data/transactions", admin, row)).EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> Send(ApiIntegrationTestHost host, HttpMethod method, string url, string token, object? body = null) =>
        ApiKeyTests.SendAsync(host, method, url, token, body);

    private static async Task<ApiIntegrationTestHost> CreateHostAsync(IAiModel model, bool postgres = false)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        };
        void Replace(IServiceCollection services) => services.Replace(ServiceDescriptor.Singleton(model));
        var host = postgres
            ? await ApiIntegrationTestHost.StartPostgresAsync(settings, configureTestServices: Replace)
            : await ApiIntegrationTestHost.StartAsync(settings, configureTestServices: Replace);
        await host.ApplySchemaAsync(Schema);
        return host;
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresCategoriseTests
{
    [PostgresFact]
    public Task Suggest_WorksOnPostgres() => CategoriseTests.SuggestScenarioAsync(postgres: true);

    [PostgresFact]
    public Task Import_WithChosenValues_WorksOnPostgres() => CategoriseTests.ImportScenarioAsync(postgres: true);
}
