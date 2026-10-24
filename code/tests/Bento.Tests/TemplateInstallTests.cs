using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Step 5.15: the template recipes live on the server; installing one makes
/// its tables, example rows, dashboards and forms through the API, as the
/// caller — for the app and for MCP clients alike.
/// </summary>
public sealed class TemplateInstallTests
{
    private static Task<ApiIntegrationTestHost> StartAsync() => ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "sqlite-test-secret",
        ["Schema:AutoMigrateOnStartup"] = "false"
    });

    [Fact]
    public async Task EveryTemplate_Installs_AndItsDashboardsRun() => await EveryTemplateAsync(await StartAsync());

    internal static async Task EveryTemplateAsync(ApiIntegrationTestHost host)
    {
        await using var disposable = host;
        var catalog = await Data(await host.Client.GetAsync("/api/v1/templates/catalog"));
        Assert.Equal(["projects-hours", "habits-health", "appointments", "finance"], catalog.EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToArray());

        foreach (var template in catalog.EnumerateArray())
        {
            var id = template.GetProperty("id").GetString()!;
            var result = await Data(await host.Client.PostAsJsonAsync($"/api/v1/templates/{id}/install", new { }));
            Assert.Equal(template.GetProperty("exampleRows").GetInt32(), result.GetProperty("exampleRows").GetInt32());
            Assert.Equal(template.GetProperty("dashboards").GetArrayLength(), result.GetProperty("dashboards").GetArrayLength());
            Assert.Equal(template.GetProperty("forms").GetArrayLength(), result.GetProperty("forms").GetArrayLength());
            foreach (var table in result.GetProperty("tables").EnumerateObject())
                Assert.Equal(table.Name, table.Value.GetString());

            // Every widget's query runs against the new tables: the recipe's names, columns and formulas fit.
            foreach (var dashboard in result.GetProperty("dashboards").EnumerateArray())
            {
                var saved = await Data(await host.Client.GetAsync($"/api/v1/dashboards/{dashboard.GetProperty("id").GetInt64()}"));
                foreach (var widget in saved.GetProperty("dashboard").GetProperty("widgets").EnumerateArray())
                {
                    // Rows tables, and values at period ends (the app adds the periods; FinanceTemplateTests runs those), aside.
                    if (!widget.TryGetProperty("data", out var data) || !data.TryGetProperty("query", out var query) || data.TryGetProperty("rows", out _) ||
                        data.TryGetProperty("asOf", out _))
                        continue;
                    using var run = await host.Client.PostAsJsonAsync("/api/v1/query", query);
                    var text = await run.Content.ReadAsStringAsync();
                    Assert.True(run.IsSuccessStatusCode, $"{id} / {dashboard.GetProperty("name").GetString()} / {widget.GetProperty("id").GetString()}: {text}");
                    Assert.DoesNotContain("\"warnings\":[\"", text);
                }
            }
        }
        var installed = await Data(await host.Client.GetAsync("/api/v1/templates"));
        Assert.Equal(4, installed.GetArrayLength());
        // Finance's realised gains need avg_cost: made from its function template.
        var function = await Data(await host.Client.GetAsync("/api/v1/functions/avg_cost"));
        Assert.Equal("rows", function.GetProperty("mode").GetString());
        // The examples are linked: the appointments name their contacts.
        var appointments = await Data(await host.Client.GetAsync("/api/v1/data/appointments?per_page=100"));
        Assert.All(appointments.EnumerateArray(), a => Assert.Equal(JsonValueKind.Number, a.GetProperty("contact_id").ValueKind));
        // Dates are around today.
        var latest = appointments.EnumerateArray().Max(a => DateTime.Parse(a.GetProperty("starts_at").GetString()![..10]));
        Assert.InRange((latest - DateTime.Today).TotalDays, 1, 30);
    }

    [Fact]
    public async Task ATakenName_NeedsAPrefix_AndThePrefixNamesTheTables()
    {
        await using var host = await StartAsync();
        await Data(await host.Client.PostAsJsonAsync("/api/v1/templates/projects-hours/install", new { examples = false }));
        using (var again = await host.Client.PostAsJsonAsync("/api/v1/templates/projects-hours/install", new { }))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            var error = JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("message").GetString();
            Assert.Contains("prefix, e.g. \"work_\"", error);
        }
        var second = await Data(await host.Client.PostAsJsonAsync("/api/v1/templates/projects-hours/install", new { prefix = "work_", examples = false }));
        Assert.Equal("work_time_entries", second.GetProperty("tables").GetProperty("time_entries").GetString());
        var model = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/schema/model");
        var table = model.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "work_projects");
        Assert.Equal("Work projects", table.GetProperty("displayName").GetString());
        // The relation name was taken by the first install? No: it has the prefix too; each is unique.
        var relations = model.GetProperty("relations").EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.Equal(relations.Count, relations.Distinct().Count());
        Assert.Equal(0, (await Data(await host.Client.GetAsync("/api/v1/data/work_time_entries"))).GetArrayLength());

        using (var unknown = await host.Client.PostAsJsonAsync("/api/v1/templates/nope/install", new { }))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using (var badPrefix = await host.Client.PostAsJsonAsync("/api/v1/templates/habits-health/install", new { prefix = "Bad Prefix" }))
            Assert.Equal(HttpStatusCode.BadRequest, badPrefix.StatusCode);
    }

    [Fact]
    public async Task AConnection_InstallsWithEveryPermissionItNeeds()
    {
        await using var host = await McpTests.CreateHostAsync();
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var schemaOnly = await McpTests.ConnectAsync(host, admin, "read-write", ["data.read", "schema.write"]);
        var refused = await McpTests.CallToolAsync(host, schemaOnly, "install_template", new { template = "appointments" });
        Assert.True(refused.IsError);
        Assert.Contains("\"dashboards.write\", \"forms.write\", \"data.write\"", refused.Text);

        var full = await McpTests.ConnectAsync(host, admin, "read-write", ["data.read", "data.write", "schema.write", "dashboards.write", "forms.write"]);
        var listed = await McpTests.CallToolAsync(host, full, "list_templates", new { });
        Assert.Contains("appointments", listed.Text);
        var installed = await McpTests.CallToolAsync(host, full, "install_template", new { template = "appointments" });
        Assert.False(installed.IsError, installed.Text);
        Assert.Contains("\"exampleRows\":13", installed.Text);
        Assert.Contains("\"id\":\"appointments\"", (await McpTests.CallToolAsync(host, full, "list_templates", new { })).Text);
        // Finance needs functions too.
        var finance = await McpTests.CallToolAsync(host, full, "install_template", new { template = "finance" });
        Assert.True(finance.IsError);
        Assert.Contains("functions.write", finance.Text);
    }

    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.RequestUri} {(int)response.StatusCode}: {text}");
            var root = JsonDocument.Parse(text).RootElement;
            return (root.TryGetProperty("data", out var data) ? data : root).Clone();
        }
    }
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresTemplateInstallTests
{
    [PostgresFact]
    public async Task EveryTemplate_Installs_AndItsDashboardsRun_OnPostgres() =>
        await TemplateInstallTests.EveryTemplateAsync(await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        }));
}
