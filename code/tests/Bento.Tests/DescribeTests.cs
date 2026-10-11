using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Bento.Api.Services.Ai;

namespace Bento.Tests;

/// <summary>
/// "Describe instead of click" (Step 4.9): a sentence becomes a checked widget
/// or formula — Bento runs what the AI hands in and sends problems back — and
/// every dashboard change keeps the state before it, so it can be restored.
/// </summary>
public sealed class DescribeTests
{
    [Fact]
    public async Task Widget_IsCheckedByRunningItsQuery_AndKeepsIdAndPosition()
    {
        var model = new AiTests.ScriptedModel();
        await using var host = await CreateHostAsync(model);
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        await Seed(host, admin);

        // 1: a column that does not exist; 2: a filter that matches nothing in the range; 3: right.
        model.Script.Enqueue(_ => Submit("su1", new { type = "bar", title = "Kunden", data = new { query = new { source = "customers", groupBy = new[] { new { column = "level" } }, measures = new[] { new { name = "n", fn = "count" } } } } }));
        model.Script.Enqueue(_ => Submit("su2", new { type = "bar", title = "Kunden", data = new { dateColumn = "joined_on", query = new { source = "customers", filter = new { tier = "platinum" }, groupBy = new[] { new { column = "tier" } }, measures = new[] { new { name = "n", fn = "count" } } } } }));
        model.Script.Enqueue(_ => Submit("su3", new { type = "bar", title = "Kunden nach Stufe", data = new { dateColumn = "joined_on", query = new { source = "customers", groupBy = new[] { new { column = "tier" } }, measures = new[] { new { name = "n", fn = "count" } } } } }, "Zählt die Kunden je Stufe."));

        var response = await AiTests.DataAsync(await Send(host, HttpMethod.Post, "/api/v1/ai/describe/widget", admin, new
        {
            language = "de-DE",
            sentence = "Kunden pro Stufe als Balken",
            dashboard = "Sales",
            widget = new { id = "w7", type = "kpi", title = "", position = new { x = 6, y = 2, w = 4, h = 3 } },
            range = new { from = "2026-01-01", to = "2027-01-01" }
        }));

        var widget = response.GetProperty("widget");
        Assert.Equal("w7", widget.GetProperty("id").GetString());
        Assert.Equal(6, widget.GetProperty("position").GetProperty("x").GetInt32());
        Assert.Equal("bar", widget.GetProperty("type").GetString());
        Assert.Equal("Zählt die Kunden je Stufe.", response.GetProperty("note").GetString());
        Assert.Equal(3, response.GetProperty("toolCalls").GetInt32());

        // What the model heard back: the API's error, then "no rows" — the range filter was applied.
        var firstResult = model.Requests[1].Messages[^1].Blocks.OfType<AiToolResult>().Single();
        Assert.True(firstResult.IsError);
        Assert.Contains("level", firstResult.Content);
        var secondResult = model.Requests[2].Messages[^1].Blocks.OfType<AiToolResult>().Single();
        Assert.Contains("no rows", secondResult.Content);

        // The model saw the reference and only reading tools besides submit.
        var first = model.Requests[0];
        Assert.Contains(first.Tools, t => t.Name == "submit_widget");
        Assert.Contains(first.Tools, t => t.Name == "aggregate");
        Assert.DoesNotContain(first.Tools, t => t.Name is "update_dashboard" or "create_row" or "call_api");
        Assert.Contains("Dashboard JSON", first.System);
        Assert.Contains("Kunden pro Stufe als Balken", ((AiText)first.Messages[0].Blocks[0]).Text);
        Assert.Equal("describe_widget", (await AiTests.DataAsync(await Send(host, HttpMethod.Get, "/api/v1/ai/usage", admin)))[0].GetProperty("feature").GetString());
    }

    [Fact]
    public async Task Widget_TheModelMaySayWhyNot()
    {
        var model = new AiTests.ScriptedModel();
        await using var host = await CreateHostAsync(model);
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        await Seed(host, admin);
        model.Script.Enqueue(_ => new AiResponse([new AiText("There is no revenue column in your tables.")], "end_turn", new AiUsage(1000, 20, 0, 0), "claude-opus-5-5"));

        var response = await AiTests.DataAsync(await Send(host, HttpMethod.Post, "/api/v1/ai/describe/widget", admin, new { sentence = "Revenue per month" }));
        Assert.Equal(JsonValueKind.Null, response.GetProperty("widget").ValueKind);
        Assert.Equal("There is no revenue column in your tables.", response.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Formula_IsValidatedAsAColumn_OrRunAsAMeasure()
    {
        var model = new AiTests.ScriptedModel();
        await using var host = await CreateHostAsync(model);
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        await Seed(host, admin);

        model.Script.Enqueue(_ => SubmitFormula("f1", "UPPER(nam)"));
        model.Script.Enqueue(_ => SubmitFormula("f2", "UPPER(name)", "Der Name in Großbuchstaben."));
        var column = await AiTests.DataAsync(await Send(host, HttpMethod.Post, "/api/v1/ai/describe/formula", admin, new { table = "customers", kind = "column", sentence = "Name groß" }));
        Assert.Equal("UPPER(name)", column.GetProperty("formula").GetString());
        Assert.Equal("Der Name in Großbuchstaben.", column.GetProperty("note").GetString());
        Assert.True(model.Requests[1].Messages[^1].Blocks.OfType<AiToolResult>().Single().IsError);

        model.Script.Enqueue(_ => SubmitFormula("f3", "SUM(nope)"));
        model.Script.Enqueue(_ => SubmitFormula("f4", "COUNT(tier = \"gold\")"));
        var measure = await AiTests.DataAsync(await Send(host, HttpMethod.Post, "/api/v1/ai/describe/formula", admin, new { table = "customers", kind = "measure", sentence = "Wie viele Gold-Kunden" }));
        Assert.Equal("COUNT(tier = \"gold\")", measure.GetProperty("formula").GetString());
        Assert.Contains("failed as a measure", model.Requests[3].Messages[^1].Blocks.OfType<AiToolResult>().Single().Content);
    }

    [Fact]
    public Task DashboardChanges_KeepTheStateBefore_AndCanBeRestored() => HistoryScenarioAsync(postgres: false);

    internal static async Task HistoryScenarioAsync(bool postgres)
    {
        await using var host = await CreateHostAsync(new AiTests.ScriptedModel(), postgres);
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var created = await AiTests.DataAsync(await Send(host, HttpMethod.Post, "/api/v1/dashboards", admin, new { name = "Money", dashboard = Doc("a") }));
        var id = created.GetProperty("id").GetInt64();

        await Send(host, HttpMethod.Put, $"/api/v1/dashboards/{id}", admin, new { dashboard = Doc("a", "b"), note = "AI: add b" });
        // An app (Claude over MCP) changes it too.
        var app = await McpTests.ConnectAsync(host, admin, "read", ["data.read", "dashboards.write"]);
        (await Send(host, HttpMethod.Put, $"/api/v1/dashboards/{id}", app, new { name = "Money (Claude)", dashboard = Doc("x") })).EnsureSuccessStatusCode();

        var versions = await AiTests.DataAsync(await Send(host, HttpMethod.Get, $"/api/v1/dashboards/{id}/versions", admin));
        Assert.Equal(2, versions.GetArrayLength());
        Assert.Equal("Claude", versions[0].GetProperty("via").GetString());
        Assert.Equal(2, versions[0].GetProperty("widgets").GetInt32());
        Assert.Equal(JsonValueKind.Null, versions[1].GetProperty("via").ValueKind);
        Assert.Equal("AI: add b", versions[1].GetProperty("note").GetString());

        // The AI page lists what apps changed.
        var changes = await AiTests.DataAsync(await Send(host, HttpMethod.Get, "/api/v1/dashboards/changes", admin));
        Assert.Single(changes.EnumerateArray());

        // Undo Claude's change: back to a + b, and the restore itself can be undone.
        var restored = await AiTests.DataAsync(await Send(host, HttpMethod.Post, $"/api/v1/dashboards/{id}/versions/{versions[0].GetProperty("id").GetInt64()}/restore", admin));
        Assert.Equal("Money", restored.GetProperty("name").GetString());
        Assert.Equal(2, restored.GetProperty("dashboard").GetProperty("widgets").GetArrayLength());
        var after = await AiTests.DataAsync(await Send(host, HttpMethod.Get, $"/api/v1/dashboards/{id}/versions", admin));
        Assert.Equal("restore", after[0].GetProperty("action").GetString());
        Assert.Equal("Money (Claude)", after[0].GetProperty("name").GetString());

        // Deleted by the app: it can be brought back under its id.
        Assert.Equal(HttpStatusCode.NoContent, (await Send(host, HttpMethod.Delete, $"/api/v1/dashboards/{id}", app)).StatusCode);
        var deleted = await AiTests.DataAsync(await Send(host, HttpMethod.Get, "/api/v1/dashboards/deleted", admin));
        Assert.Equal("Claude", deleted[0].GetProperty("via").GetString());
        var back = await AiTests.DataAsync(await Send(host, HttpMethod.Post, $"/api/v1/dashboards/deleted/{deleted[0].GetProperty("id").GetInt64()}/restore", admin));
        Assert.Equal(id, back.GetProperty("id").GetInt64());
        Assert.Empty((await AiTests.DataAsync(await Send(host, HttpMethod.Get, "/api/v1/dashboards/deleted", admin))).EnumerateArray());
        // New dashboards still get fresh ids.
        var next = await AiTests.DataAsync(await Send(host, HttpMethod.Post, "/api/v1/dashboards", admin, new { name = "Next", dashboard = Doc("n") }));
        Assert.NotEqual(id, next.GetProperty("id").GetInt64());

        // Someone else's dashboard is not theirs to look into.
        var viewer = await AiTests.ViewerTokenAsync(host, admin);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(host, HttpMethod.Get, $"/api/v1/dashboards/{id}/versions", viewer)).StatusCode);
    }

    private static object Doc(params string[] widgetIds) => new
    {
        widgets = widgetIds.Select((w, i) => new { id = w, type = "text", title = w, position = new { x = 0, y = i * 2, w = 4, h = 2 }, display = new { text = w } }).ToArray()
    };

    private static AiResponse Submit(string id, object widget, string? note = null) =>
        new([new AiToolUse(id, "submit_widget", AiTests.Json(note is null ? new { widget } : new { widget, note }))], "tool_use", new AiUsage(2000, 150, 0, 0), "claude-opus-5-5");

    private static AiResponse SubmitFormula(string id, string formula, string? note = null) =>
        new([new AiToolUse(id, "submit_formula", AiTests.Json(note is null ? new { formula } : new { formula, note }))], "tool_use", new AiUsage(1500, 80, 0, 0), "claude-opus-5-5");

    private static async Task Seed(ApiIntegrationTestHost host, string admin)
    {
        await Send(host, HttpMethod.Post, "/api/v1/data/customers", admin, new { name = "Acme", tier = "gold", joined_on = "2026-03-01" });
        await Send(host, HttpMethod.Post, "/api/v1/data/customers", admin, new { name = "Globex", tier = "bronze", joined_on = "2026-05-01" });
        await Send(host, HttpMethod.Put, "/api/v1/ai/settings", admin, new { apiKey = "sk-ant-test-123", monthlyLimitUsd = 5 });
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
        await host.ApplySchemaAsync(ApiKeyTests.Schema);
        return host;
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresDescribeTests
{
    [PostgresFact]
    public Task DashboardHistory_WorksOnPostgres() => DescribeTests.HistoryScenarioAsync(postgres: true);
}
