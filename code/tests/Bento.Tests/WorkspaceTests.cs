using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bento.Api.Services.Workspaces;

namespace Bento.Tests;

/// <summary>
/// Step 5.5: workspaces — separate areas of one installation, each with its
/// own tables, model, dashboards, functions and settings, addressed as
/// /api/v1/w/&lt;slug&gt;, with a header, or over MCP at /mcp/&lt;slug&gt;.
/// </summary>
public sealed class WorkspaceTests
{
    private static Dictionary<string, string?> Settings(string secret) => new()
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = secret,
        ["Backend:PublicUrl"] = "https://bento.example",
        ["Schema:AutoMigrateOnStartup"] = "false"
    };

    // The same table name in both workspaces, with other columns.
    private const string DefaultSchema = """
        { "tables": [ { "name": "items", "columns": [
            { "name": "id", "type": "integer", "primaryKey": true },
            { "name": "title", "type": "text" } ] } ] }
        """;

    private const string FinanceSchema = """
        { "tables": [ { "name": "items", "columns": [
            { "name": "id", "type": "integer", "primaryKey": true },
            { "name": "amount", "type": "float" },
            { "name": "doubled", "type": "computed", "formula": "CUSTOM(\"twice\", amount)" } ] } ] }
        """;

    [Fact]
    public void Slugs_ComeFromNames()
    {
        Assert.Equal("kalender-betrieb", WorkspaceService.SlugOf("Kalender & Betrieb"));
        Assert.Equal("fuer-mueller", WorkspaceService.SlugOf("Für Müller"));
        Assert.Equal("ws-2026", WorkspaceService.SlugOf("2026"));
        Assert.Equal("ws_kalender_betrieb", new Workspace(2, "kalender-betrieb", "Kalender").DataSchema);
        Assert.Equal("data-kalender-betrieb.db", new Workspace(2, "kalender-betrieb", "Kalender").DataFile);
        Assert.Equal("public", Workspace.Default.DataSchema);
    }

    [Fact]
    public async Task Workspaces_KeepTheirOwnThings() => await WorkspacesAsync(await ApiIntegrationTestHost.StartAsync(Settings("sqlite-test-secret")));

    internal static async Task WorkspacesAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        const string ws = "/api/v1/w/finances";

        // Only the default workspace at first; existing addresses work as before.
        var list = await GetAsync(host, "/api/v1/workspaces");
        Assert.Equal("default", Assert.Single(list.EnumerateArray()).GetProperty("slug").GetString());
        await SendAsync(host, HttpMethod.Put, "/api/v1/admin/schema", DefaultSchema);
        await PostAsync(host, "/api/v1/data/items", new { title = "In default" });

        Assert.Contains("short name", await FailAsync(host, HttpMethod.Post, "/api/v1/workspaces", new { name = "X", slug = "default" }, 400));
        var created = await PostAsync(host, "/api/v1/workspaces", new { name = "Finances" });
        Assert.Equal("finances", created.GetProperty("slug").GetString());
        var id = created.GetProperty("id").GetInt64();
        Assert.Contains("already", await FailAsync(host, HttpMethod.Post, "/api/v1/workspaces", new { name = "Finances" }, 400));

        // An empty model in the new workspace; the default one is unchanged.
        var model = await GetAsync(host, ws + "/admin/schema", data: false);
        Assert.Equal(0, model.GetProperty("tables").GetArrayLength());
        Assert.Contains("does not exist", await FailAsync(host, HttpMethod.Get, ws + "/data/items", null, 404));

        // A function and a table of the same name as in default, with other columns.
        await PostAsync(host, ws + "/functions", new
        {
            name = "twice", resultType = "number", arguments = new[] { new { name = "x", type = "number" } }, code = "return x * 2;"
        });
        await SendAsync(host, HttpMethod.Put, ws + "/admin/schema", FinanceSchema);
        await PostAsync(host, ws + "/data/items", new { amount = 21 });
        await PostAsync(host, ws + "/data/items", new { amount = 4 });

        var financeRows = await GetAsync(host, ws + "/data/items?sort=id");
        Assert.Equal(2, financeRows.GetArrayLength());
        Assert.Equal(42m, financeRows[0].GetProperty("doubled").GetDecimal());
        var defaultRows = await GetAsync(host, "/api/v1/data/items");
        Assert.Equal("In default", Assert.Single(defaultRows.EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal(0, (await GetAsync(host, "/api/v1/functions")).GetArrayLength());
        // Names are unique per workspace: default may have its own "twice".
        await PostAsync(host, "/api/v1/functions", new
        {
            name = "twice", resultType = "text", arguments = new[] { new { name = "x", type = "text" } }, code = "return x + x;"
        });
        Assert.Equal("float", (await GetAsync(host, ws + "/functions/twice")).GetProperty("resultType").GetString());

        // The header names the workspace as well as the address does.
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/data/items"))
        {
            request.Headers.Add("X-Bento-Workspace", "finances");
            using var response = await host.Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, text);
            Assert.Equal(2, JsonDocument.Parse(text).RootElement.GetProperty("data").GetArrayLength());
            Assert.Equal("finances", response.Headers.GetValues("X-Bento-Workspace").Single());
        }
        Assert.Contains("no workspace 'nowhere'", await FailAsync(host, HttpMethod.Get, "/api/v1/w/nowhere/data/items", null, 404));

        // Dashboards and settings: each workspace its own, the installation's values as defaults.
        await PostAsync(host, ws + "/dashboards", new { name = "Money", dashboard = new { widgets = Array.Empty<object>() } });
        Assert.Equal(0, (await GetAsync(host, "/api/v1/dashboards")).GetArrayLength());
        Assert.Equal("Money", (await GetAsync(host, ws + "/dashboards"))[0].GetProperty("name").GetString());

        await SendAsync(host, HttpMethod.Put, "/api/v1/admin/settings", JsonSerializer.Serialize(new { locale = "de-AT", currency = "EUR" }));
        Assert.Equal("de-AT", (await GetAsync(host, ws + "/admin/settings")).GetProperty("locale").GetString());
        await SendAsync(host, HttpMethod.Put, ws + "/admin/settings", JsonSerializer.Serialize(new { locale = "en-US", currency = "USD" }));
        Assert.Equal("USD", (await GetAsync(host, ws + "/admin/settings")).GetProperty("currency").GetString());
        Assert.Equal("EUR", (await GetAsync(host, "/api/v1/admin/settings")).GetProperty("currency").GetString());

        // MCP for one workspace: /mcp/<slug>, its discovery naming that address.
        using (var mcp = new HttpRequestMessage(HttpMethod.Post, "/mcp/finances")
               {
                   Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "describe_schema", arguments = new { } } })
               })
        {
            mcp.Headers.Accept.ParseAdd("application/json, text/event-stream");
            using var response = await host.Client.SendAsync(mcp);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, text);
            Assert.Contains("amount", text);
            Assert.DoesNotContain("title", text);
        }
        var resource = await host.Client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp/finances");
        Assert.Equal("https://bento.example/mcp/finances", resource.GetProperty("resource").GetString());

        // Renamed: the short name stays.
        await SendAsync(host, HttpMethod.Put, $"/api/v1/workspaces/{id}", JsonSerializer.Serialize(new { name = "Money matters" }));
        Assert.Contains(await GetAsync(host, "/api/v1/workspaces") is var all ? all.EnumerateArray() : default,
            w => w.GetProperty("slug").GetString() == "finances" && w.GetProperty("name").GetString() == "Money matters");

        // Deleted only with its short name typed again; the default one stays.
        Assert.Contains("stays", await FailAsync(host, HttpMethod.Delete, $"/api/v1/workspaces/{WorkspaceService_DefaultId}?confirm=default", null, 400));
        Assert.Contains("Type", await FailAsync(host, HttpMethod.Delete, $"/api/v1/workspaces/{id}?confirm=wrong", null, 400));
        using (var deleted = await host.Client.DeleteAsync($"/api/v1/workspaces/{id}?confirm=finances"))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Single((await GetAsync(host, "/api/v1/workspaces")).EnumerateArray());
        await FailAsync(host, HttpMethod.Get, ws + "/data/items", null, 404);
        Assert.Equal(0L, await host.QuerySystemScalarAsync<long>($"SELECT COUNT(*) FROM {(host.Provider == Bento.Api.Db.DatabaseProvider.Postgres ? "_system." : "")}dashboards WHERE workspace_id = @Id", new { Id = id }));
        Assert.Single((await GetAsync(host, "/api/v1/data/items")).EnumerateArray());

        // The same short name again starts empty.
        await PostAsync(host, "/api/v1/workspaces", new { name = "Finances" });
        Assert.Equal(0, (await GetAsync(host, ws + "/admin/schema", data: false)).GetProperty("tables").GetArrayLength());
    }

    private const long WorkspaceService_DefaultId = Workspace.DefaultId;

    private static async Task<JsonElement> GetAsync(ApiIntegrationTestHost host, string url, bool data = true)
    {
        using var response = await host.Client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} {(int)response.StatusCode}: {text}");
        var root = JsonDocument.Parse(text).RootElement;
        return (data && root.TryGetProperty("data", out var inner) ? inner : root).Clone();
    }

    private static Task<JsonElement> PostAsync(ApiIntegrationTestHost host, string url, object body) => ImportSourceTests.PostAsync(host, url, body);

    private static async Task SendAsync(ApiIntegrationTestHost host, HttpMethod method, string url, string json)
    {
        using var request = new HttpRequestMessage(method, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        using var response = await host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} {(int)response.StatusCode}: {text}");
    }

    private static async Task<string> FailAsync(ApiIntegrationTestHost host, HttpMethod method, string url, object? body, int status)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"{url} {(int)response.StatusCode}: {text}");
        return text;
    }

    [Fact]
    public async Task AFullWorkspace_IsDeletedWholly() => await DeleteFullAsync(await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "sqlite-test-secret",
        ["Schema:AutoMigrateOnStartup"] = "false"
    }));

    /// <summary>
    /// A workspace with a template, a calendar app and its device password, a share link
    /// goes as a whole — on Postgres too, where the app passwords once stopped it (IN @list).
    /// </summary>
    internal static async Task DeleteFullAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        var created = await PostAsync(host, "/api/v1/workspaces", new { name = "cal" });
        var id = created.GetProperty("id").GetInt64();
        const string w = "/api/v1/w/cal";
        await PostAsync(host, w + "/templates/appointments/install", new { });
        var app = await PostAsync(host, w + "/admin/apps", new
        {
            name = "Termine", kind = "calendar",
            config = new { calendars = new[] { new { name = "Appointments", table = "appointments", title = "title", start = "starts_at" } } }
        });
        await PostAsync(host, $"{w}/admin/apps/{app.GetProperty("id").GetInt64()}/passwords", new { name = "iPhone" });
        var dashboard = (await GetAsync(host, w + "/dashboards"))[0].GetProperty("id").GetInt64();
        await PostAsync(host, $"{w}/dashboards/{dashboard}/shares", new { name = "Family" });

        using (var deleted = await host.Client.DeleteAsync($"/api/v1/workspaces/{id}?confirm=cal"))
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        Assert.Single((await GetAsync(host, "/api/v1/workspaces")).EnumerateArray());
        var prefix = host.Provider == Bento.Api.Db.DatabaseProvider.Postgres ? "_system." : "";
        Assert.Equal(0L, await host.QuerySystemScalarAsync<long>($"SELECT COUNT(*) FROM {prefix}app_passwords"));
        Assert.Equal(0L, await host.QuerySystemScalarAsync<long>($"SELECT COUNT(*) FROM {prefix}apps"));
        Assert.Equal(0L, await host.QuerySystemScalarAsync<long>($"SELECT COUNT(*) FROM {prefix}share_links"));
    }
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresWorkspaceTests
{
    [PostgresFact]
    public async Task AFullWorkspace_IsDeletedWholly_OnPostgres() => await WorkspaceTests.DeleteFullAsync(await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "postgres-test-secret",
        ["Schema:AutoMigrateOnStartup"] = "false"
    }));

    [PostgresFact]
    public async Task Workspaces_OnPostgres() => await WorkspaceTests.WorkspacesAsync(await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "postgres-test-secret",
        ["Backend:PublicUrl"] = "https://bento.example",
        ["Schema:AutoMigrateOnStartup"] = "false"
    }));
}
