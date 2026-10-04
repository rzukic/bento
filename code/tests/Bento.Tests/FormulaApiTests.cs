using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>Step 3.4: the formula endpoints (validate, preview, functions) and the OpenAPI entries.</summary>
public sealed class FormulaApiTests
{
    [Fact]
    public async Task Validate_ReturnsTypeColumnsAndErrors()
    {
        await using var host = await CreateHostAsync();

        var ok = await PostAsync(host, "/api/v1/formulas/validate", new { table = "samples", formula = "= IF(amount < 0, \"expense\", label)" });
        Assert.True(ok.GetProperty("valid").GetBoolean());
        Assert.Equal("text", ok.GetProperty("type").GetString());
        Assert.Equal(["amount", "label"], ok.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.Empty(ok.GetProperty("errors").EnumerateArray());

        var bad = await PostAsync(host, "/api/v1/formulas/validate", new { table = "samples", formula = "= amout * 2" });
        Assert.False(bad.GetProperty("valid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, bad.GetProperty("type").ValueKind);
        var error = Assert.Single(bad.GetProperty("errors").EnumerateArray());
        Assert.Equal("Column 'amout' not found in samples — did you mean 'amount'?", error.GetProperty("message").GetString());
        Assert.Equal(2, error.GetProperty("start").GetInt32());
        Assert.Equal(7, error.GetProperty("end").GetInt32());

        // Draft columns from the schema designer: the table need not exist yet.
        var draft = await PostAsync(host, "/api/v1/formulas/validate", new
        {
            table = "budgets",
            formula = "planned - spent",
            columns = new object[]
            {
                new { name = "planned", type = "currency" },
                new { name = "spent", type = "currency", displayName = "Spent so far" }
            }
        });
        Assert.True(draft.GetProperty("valid").GetBoolean());
        Assert.Equal("currency", draft.GetProperty("type").GetString());

        // A whole draft model: links and tables that are not applied yet.
        var draftModel = await PostAsync(host, "/api/v1/formulas/validate", new
        {
            table = "budgets",
            column = "left",
            formula = "planned + SUM(expenses.amount)",
            model = System.Text.Json.JsonDocument.Parse("""
                { "tables": [
                    { "name": "budgets", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }, { "name": "planned", "type": "currency" },
                                                      { "name": "left", "type": "computed", "formula": "x" } ] },
                    { "name": "expenses", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }, { "name": "amount", "type": "currency" },
                                                       { "name": "budget_id", "type": "reference", "references": "budgets" } ] } ],
                  "relations": [ { "name": "budget_expenses", "type": "1:N", "from": "budgets", "to": "expenses", "column": "budget_id" } ] }
                """).RootElement
        });
        Assert.True(draftModel.GetProperty("valid").GetBoolean(), draftModel.ToString());
        Assert.Equal("currency", draftModel.GetProperty("type").GetString());

        var self = await PostAsync(host, "/api/v1/formulas/validate", new { table = "samples", formula = "amount + spent", column = "spent",
            columns = new object[] { new { name = "amount", type = "currency" }, new { name = "spent", type = "computed", formula = "amount + spent" } } });
        Assert.Equal("Circular reference: samples.spent → samples.spent.", self.GetProperty("errors")[0].GetProperty("message").GetString());

        var mismatch = await PostAsync(host, "/api/v1/formulas/validate", new { table = "samples", formula = "label", resultType = "currency" });
        Assert.False(mismatch.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Preview_ReturnsValuesWithTheirInputs()
    {
        await using var host = await CreateHostAsync();

        var preview = await PostAsync(host, "/api/v1/formulas/preview", new { table = "samples", formula = "= amount * -1", limit = 2 });
        Assert.Equal("currency", preview.GetProperty("type").GetString());
        var rows = preview.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0].GetProperty("id").GetInt64());
        Assert.Equal(45.9m, rows[0].GetProperty("value").GetDecimal());
        Assert.Equal(-45.9m, rows[0].GetProperty("inputs").GetProperty("amount").GetDecimal());

        var stamps = await PostAsync(host, "/api/v1/formulas/preview", new { table = "samples", formula = "HOUR(recorded_at)", limit = 2 });
        Assert.Equal(["2026-01-31T23:30:00Z", "2026-03-01T00:15:00+02:00"],
            stamps.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("inputs").GetProperty("recorded_at").GetString()));
    }

    [Theory]
    [InlineData("""{ "table": "samples", "formula": "amout" }""", HttpStatusCode.BadRequest, "formula")]
    [InlineData("""{ "table": "samples", "formula": "1", "limit": 101 }""", HttpStatusCode.BadRequest, "limit")]
    [InlineData("""{ "formula": "1" }""", HttpStatusCode.BadRequest, "table")]
    [InlineData("""{ "table": "nope", "formula": "1" }""", HttpStatusCode.NotFound, "table")]
    [InlineData("""{ "table": "samples", "formula": "1", "resultType": "money" }""", HttpStatusCode.BadRequest, "resultType")]
    public async Task Preview_RejectsInvalidRequests(string body, HttpStatusCode status, string field)
    {
        await using var host = await CreateHostAsync();
        using var response = await host.Client.PostAsync("/api/v1/formulas/preview", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(status, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.Equal(field, error.GetProperty("details").GetProperty("field").GetString());
    }

    [Fact]
    public async Task Functions_ListTheCatalog_AndOpenApiDescribesTheEndpoints()
    {
        await using var host = await CreateHostAsync();

        var functions = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/formulas/functions");
        var names = functions.GetProperty("data").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();
        Assert.Contains("DATEDIFF", names);
        Assert.Equal(Bento.Api.Formula.FormulaFunctions.Catalog.Count, names.Count);
        Assert.Contains("AND", functions.GetProperty("meta").GetProperty("keywords").EnumerateArray().Select(k => k.GetString()));

        var openApi = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/openapi.json");
        var validate = openApi.GetProperty("paths").GetProperty("/formulas/validate").GetProperty("post");
        Assert.Equal("read", validate.GetProperty("x-bento-scope").GetString());
        Assert.True(openApi.GetProperty("paths").TryGetProperty("/formulas/preview", out _));
    }

    private static async Task<ApiIntegrationTestHost> CreateHostAsync()
    {
        var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(FormulaConformanceTests.Schema);
        await FormulaConformanceTests.SeedAsync(host);
        return host;
    }

    private static async Task<JsonElement> PostAsync(ApiIntegrationTestHost host, string url, object body)
    {
        using var response = await host.Client.PostAsJsonAsync(url, body);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, payload.ToString());
        return payload.GetProperty("data");
    }
}
