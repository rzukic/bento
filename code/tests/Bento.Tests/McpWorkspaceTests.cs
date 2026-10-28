using System.Net;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Step 5.13: one MCP connection for several or all workspaces — every tool
/// takes a workspace — and, for installation admins who allow it, creating
/// workspaces (which the connection may then use).
/// </summary>
public sealed class McpWorkspaceTests
{
    private const string GardenSchema = """
        { "tables": [ { "name": "plants", "columns": [
            { "name": "id", "type": "integer", "primaryKey": true },
            { "name": "name", "type": "text", "nullable": false } ] } ] }
        """;

    [Fact]
    public async Task OneConnection_ManyWorkspaces() => await ManyWorkspacesAsync(await McpTests.CreateHostAsync());

    internal static async Task ManyWorkspacesAsync(ApiIntegrationTestHost host)
    {
        await using var disposable = host;
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var garden = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/workspaces", admin, new { name = "Garten" }));
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/w/garten/admin/schema", admin, JsonDocument.Parse(GardenSchema).RootElement));

        // All workspaces, with creating them.
        var all = await McpTests.ConnectAsync(host, admin, "read-write",
            ["data.read", "data.write", "schema.write", "workspaces.manage"], allWorkspaces: true);
        var instructions = (await McpTests.McpAsync(host, all, "initialize", new { protocolVersion = "2025-06-18" })).GetProperty("result").GetProperty("instructions").GetString()!;
        Assert.Contains("Garten (workspace: \"garten\", you are admin)", instructions);
        Assert.Contains("(workspace: \"default\", you are admin) — used when a tool gets no workspace", instructions);
        var tools = (await McpTests.McpAsync(host, all, "tools/list")).GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        Assert.True(tools.Single(t => t.GetProperty("name").GetString() == "describe_schema").GetProperty("inputSchema").GetProperty("properties").TryGetProperty("workspace", out _));
        Assert.False(tools.Single(t => t.GetProperty("name").GetString() == "list_workspaces").GetProperty("inputSchema").GetProperty("properties").TryGetProperty("workspace", out _));
        Assert.Contains(tools, t => t.GetProperty("name").GetString() == "create_workspace");

        var listed = JsonDocument.Parse((await McpTests.CallToolAsync(host, all, "list_workspaces", new { })).Text).RootElement;
        Assert.Equal(["default", "garten"], listed.EnumerateArray().Select(w => w.GetProperty("workspace").GetString()).ToArray());
        Assert.True(listed[0].GetProperty("current").GetBoolean());

        // The same tools, in another workspace.
        Assert.DoesNotContain("plants", (await McpTests.CallToolAsync(host, all, "describe_schema", new { })).Text);
        Assert.Contains("plants", (await McpTests.CallToolAsync(host, all, "describe_schema", new { workspace = "garten" })).Text);
        var added = await McpTests.CallToolAsync(host, all, "create_row", new { workspace = "garten", table = "plants", values = new { name = "Tomate" } });
        Assert.False(added.IsError, added.Text);
        var rows = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/w/garten/data/plants", admin));
        Assert.Equal("Tomate", Assert.Single(rows.EnumerateArray()).GetProperty("name").GetString());
        var unknown = await McpTests.CallToolAsync(host, all, "describe_schema", new { workspace = "nope" });
        Assert.True(unknown.IsError);
        Assert.Contains("no workspace 'nope'", unknown.Text);

        // A new workspace: made, and usable at once — also by a connection for all of them.
        var made = await McpTests.CallToolAsync(host, all, "create_workspace", new { name = "Werkstatt" });
        Assert.False(made.IsError, made.Text);
        Assert.Contains("\"slug\":\"werkstatt\"", made.Text);
        Assert.False((await McpTests.CallToolAsync(host, all, "describe_schema", new { workspace = "werkstatt" })).IsError);

        // A connection for one workspace sees no workspace argument and may not reach another …
        var one = await McpTests.ConnectAsync(host, admin, "read-write", ["data.read", "workspaces.manage"], workspaces: [1]);
        var oneTools = (await McpTests.McpAsync(host, one, "tools/list")).GetProperty("result").GetProperty("tools").EnumerateArray();
        Assert.False(oneTools.Single(t => t.GetProperty("name").GetString() == "describe_schema").GetProperty("inputSchema").GetProperty("properties").TryGetProperty("workspace", out _));
        var refused = await McpTests.CallToolAsync(host, one, "describe_schema", new { workspace = "garten" });
        Assert.True(refused.IsError);
        Assert.Contains("may not use the workspace 'garten'", refused.Text);
        // … but one it makes joins it.
        Assert.False((await McpTests.CallToolAsync(host, one, "create_workspace", new { name = "Keller" })).IsError);
        var oneListed = JsonDocument.Parse((await McpTests.CallToolAsync(host, one, "list_workspaces", new { })).Text).RootElement;
        Assert.Equal(["default", "keller"], oneListed.EnumerateArray().Select(w => w.GetProperty("workspace").GetString()).ToArray());
        Assert.False((await McpTests.CallToolAsync(host, one, "describe_schema", new { workspace = "keller" })).IsError);

        // Deleting a workspace stays off limits for connections.
        var deleted = await McpTests.CallToolAsync(host, all, "call_api", new { method = "DELETE", path = $"/api/v1/workspaces/{garden.GetProperty("id").GetInt64()}?confirm=garten" });
        Assert.True(deleted.IsError);
        Assert.Contains("cannot use this part", deleted.Text);

        // A connection changed to all workspaces.
        var connections = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/mcp/connections", admin));
        var oneId = connections.EnumerateArray().Single(c => c.GetProperty("workspaces").ValueKind == JsonValueKind.Array).GetProperty("id").GetInt64();
        var changed = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Put, $"/api/v1/mcp/connections/{oneId}", admin, new { permissions = new[] { "data.read" }, allWorkspaces = true }));
        Assert.Equal(JsonValueKind.Null, changed.GetProperty("workspaces").ValueKind);
        Assert.False((await McpTests.CallToolAsync(host, one, "describe_schema", new { workspace = "garten" })).IsError);
    }

    [Fact]
    public async Task OnlyInstallationAdmins_MayLetAConnectionCreateWorkspaces()
    {
        await using var host = await McpTests.CreateHostAsync();
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var garden = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/workspaces", admin, new { name = "Garten" }));
        await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/admin/users", admin,
            new { username = "anna", password = "anna-password", role = "viewer", workspaces = new[] { new { id = garden.GetProperty("id").GetInt64(), role = "admin" } } }));
        string anna;
        using (var login = await host.Client.PostAsync("/api/v1/auth/login", System.Net.Http.Json.JsonContent.Create(new { username = "anna", password = "anna-password" })))
            anna = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString()!;

        // Admin of Garten, but not of the installation: the consent page does not offer it, and it is not granted.
        var connection = await McpTests.ConnectAsync(host, anna, "read-write", ["data.read", "schema.write", "workspaces.manage"], allWorkspaces: true);
        var tools = (await GardenMcpAsync(host, connection, "tools/list", new { })).GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("create_workspace", tools);
        // Only the workspaces she can open.
        var listed = await GardenMcpAsync(host, connection, "tools/call", new { name = "list_workspaces", arguments = new { } });
        var workspaces = JsonDocument.Parse(listed.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!).RootElement;
        Assert.Equal(["garten"], workspaces.EnumerateArray().Select(w => w.GetProperty("workspace").GetString()).ToArray());
        var refused = await GardenMcpAsync(host, connection, "tools/call", new { name = "describe_schema", arguments = new { workspace = "default" } });
        Assert.True(refused.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    /// <summary>An MCP call at Garten's address: Anna is no member of the default workspace.</summary>
    private static async Task<JsonElement> GardenMcpAsync(ApiIntegrationTestHost host, string token, string method, object parameters)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp/garten") { Content = System.Net.Http.Json.JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params = parameters }) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
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

    private static async Task Ok(HttpResponseMessage response)
    {
        using (response)
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresMcpWorkspaceTests
{
    [PostgresFact]
    public async Task OneConnection_ManyWorkspaces_OnPostgres()
    {
        var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(ApiKeyTests.Schema);
        await McpWorkspaceTests.ManyWorkspacesAsync(host);
    }
}
