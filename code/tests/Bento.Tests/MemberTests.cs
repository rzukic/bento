using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Step 5.5 part B: members and roles per workspace, users managed by
/// installation admins, API keys and MCP connections limited to workspaces.
/// </summary>
public sealed class MemberTests
{
    private const string HealthSchema = """
        { "tables": [ { "name": "entries", "columns": [
            { "name": "id", "type": "integer", "primaryKey": true },
            { "name": "steps", "type": "integer" } ] } ] }
        """;

    [Fact]
    public async Task Members_SeeOnlyTheirWorkspaces() => await MembersAsync(await McpTests.CreateHostAsync());

    internal static async Task MembersAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        const string health = "/api/v1/w/gesundheit";

        var workspace = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/workspaces", admin, new { name = "Gesundheit" }));
        var healthId = workspace.GetProperty("id").GetInt64();
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Put, health + "/admin/schema", admin, JsonDocument.Parse(HealthSchema).RootElement));
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Post, health + "/data/entries", admin, new { steps = 8000 }));

        // A user for "Gesundheit" only, as editor there.
        var anna = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/admin/users", admin,
            new { username = "anna", password = "anna-password", role = "viewer", workspaces = new[] { new { id = healthId, role = "editor" } } }));
        var annaId = anna.GetProperty("id").GetInt64();
        Assert.Equal("editor", Assert.Single(anna.GetProperty("workspaces").EnumerateArray()).GetProperty("role").GetString());
        var users = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/admin/users", admin));
        Assert.Equal(2, users.GetArrayLength());

        // Signed in in Gesundheit she is an editor there; in default she is no member.
        var signIn = await LoginAsync(host, "anna", "anna-password", "gesundheit");
        Assert.Equal("editor", signIn.GetProperty("user").GetProperty("role").GetString());
        Assert.Equal("viewer", signIn.GetProperty("user").GetProperty("installation_role").GetString());
        var token = signIn.GetProperty("access_token").GetString()!;
        Assert.Equal(JsonValueKind.Null, (await LoginAsync(host, "anna", "anna-password", null)).GetProperty("user").GetProperty("workspace_role").ValueKind);

        var listed = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/workspaces", token));
        var only = Assert.Single(listed.EnumerateArray());
        Assert.Equal("gesundheit", only.GetProperty("slug").GetString());
        Assert.Equal("editor", only.GetProperty("role").GetString());

        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/data/customers", token), HttpStatusCode.NotFound, "no workspace 'default'");
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/dashboards", token), HttpStatusCode.NotFound);
        Assert.Equal(1, (await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, health + "/data/entries", token))).GetArrayLength());
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Post, health + "/data/entries", token, new { steps = 4000 }));
        // An editor neither changes the schema nor manages members or users.
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Put, health + "/admin/schema", token, JsonDocument.Parse(HealthSchema).RootElement), HttpStatusCode.Forbidden);
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Get, $"/api/v1/workspaces/{healthId}/members", token), HttpStatusCode.Forbidden);
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/admin/users", token), HttpStatusCode.Forbidden);

        // Admin of Gesundheit: its schema and members, not the installation's things.
        var members = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Put, $"/api/v1/workspaces/{healthId}/members/{annaId}", admin, new { role = "admin" }));
        Assert.Contains(members.EnumerateArray(), m => m.GetProperty("username").GetString() == "anna" && m.GetProperty("role").GetString() == "admin");
        Assert.Contains(members.EnumerateArray(), m => m.GetProperty("installationAdmin").GetBoolean());
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Put, health + "/admin/schema", token, JsonDocument.Parse(HealthSchema).RootElement));
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Get, $"/api/v1/workspaces/{healthId}/members", token));
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/admin/credentials", token), HttpStatusCode.Forbidden);
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/workspaces", token, new { name = "Mine" }), HttpStatusCode.Forbidden);
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Post, health + "/functions", token, new
        {
            name = "leak", resultType = "text", arguments = Array.Empty<object>(), code = "return '';", credentials = new[] { "bank_token" }
        }), HttpStatusCode.Forbidden, "installation admins");
        // She manages Gesundheit's members, not default's.
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/workspaces/1/members", token), HttpStatusCode.NotFound);

        // Taken out: at once without access.
        using (var removed = await ApiKeyTests.SendAsync(host, HttpMethod.Delete, $"/api/v1/workspaces/{healthId}/members/{annaId}", admin))
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Get, health + "/data/entries", token), HttpStatusCode.NotFound);
        Assert.Equal(0, (await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/workspaces", token))).GetArrayLength());
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Put, $"/api/v1/workspaces/{healthId}/members/1", admin, new { role = "viewer" }), HttpStatusCode.BadRequest, "installation admin");

        // A new password signs her out; the last admin stays one.
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Put, $"/api/v1/admin/users/{annaId}", admin, new { password = "new-anna-password" }));
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/admin/users/1", admin, new { role = "viewer" }), HttpStatusCode.BadRequest, "last installation admin");

        // A key for Gesundheit only.
        using (var created = await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/admin/api-keys", admin, new { name = "Health sync", scope = "read", workspaces = new[] { healthId } }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var body = await created.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(healthId, Assert.Single(body.GetProperty("data").GetProperty("workspaces").EnumerateArray()).GetInt64());
            var key = body.GetProperty("key").GetString()!;
            Assert.Equal(2, (await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, health + "/data/entries", key))).GetArrayLength());
            await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/data/customers", key), HttpStatusCode.Forbidden, "may not use the workspace 'default'");
            Assert.Equal("gesundheit", Assert.Single((await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/workspaces", key))).EnumerateArray()).GetProperty("slug").GetString());
        }
        // Keys made without a choice keep to the workspace they were made in.
        var defaultKey = await ApiKeyTests.CreateKeyAsync(host, admin, "read");
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Get, health + "/data/entries", defaultKey), HttpStatusCode.Forbidden);
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/data/customers", defaultKey));

        // An MCP connection for Gesundheit: its address works, default's does not.
        var connection = await McpTests.ConnectAsync(host, admin, "read", workspaces: [healthId]);
        using (var mcp = await McpAsync(host, "/mcp/gesundheit", connection))
            Assert.Equal(HttpStatusCode.OK, mcp.StatusCode);
        using (var mcp = await McpAsync(host, "/mcp", connection))
        {
            Assert.Equal(HttpStatusCode.Forbidden, mcp.StatusCode);
            Assert.Contains("may not use the workspace 'default'", await mcp.Content.ReadAsStringAsync());
        }
        var grants = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/mcp/connections", admin));
        var grant = Assert.Single(grants.EnumerateArray());
        Assert.Equal(healthId, Assert.Single(grant.GetProperty("workspaces").EnumerateArray()).GetInt64());
        Assert.Equal(2, grant.GetProperty("openable").GetArrayLength());
        // Widened to default too.
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Put, $"/api/v1/mcp/connections/{grant.GetProperty("id").GetInt64()}", admin,
            new { permissions = new[] { "data.read" }, workspaces = new[] { 1L, healthId } }));
        using (var mcp = await McpAsync(host, "/mcp", connection))
            Assert.Equal(HttpStatusCode.OK, mcp.StatusCode);
    }

    [Fact]
    public async Task DeletingAUser_HandsOverTheirThingsAndEndsTheirAccess() => await DeleteUserAsync(await McpTests.CreateHostAsync());

    internal static async Task DeleteUserAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var anna = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/admin/users", admin,
            new { username = "anna", password = "anna-password", role = "editor" }));
        var annaId = anna.GetProperty("id").GetInt64();
        var token = (await LoginAsync(host, "anna", "anna-password", null)).GetProperty("access_token").GetString()!;
        var dashboard = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/dashboards", token, new { name = "Anna's", dashboard = new { widgets = Array.Empty<object>() } }));
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/views", token, new { name = "Mine", table_name = "customers", type = "table", config = new { }, is_shared = false }));
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/explorer/preferences/customers", token, new { auto_save = true }));

        // A second installation admin with a key.
        var carl = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/admin/users", admin, new { username = "carl", password = "carl-password", role = "admin" }));
        var carlToken = (await LoginAsync(host, "carl", "carl-password", null)).GetProperty("access_token").GetString()!;
        var carlKey = await ApiKeyTests.CreateKeyAsync(host, carlToken, "read");

        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Delete, "/api/v1/admin/users/1", admin), HttpStatusCode.BadRequest, "yourself");
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Delete, $"/api/v1/admin/users/{carl.GetProperty("id").GetInt64()}?transferTo={annaId}", admin),
            HttpStatusCode.BadRequest, "installation admin");

        using (var deleted = await ApiKeyTests.SendAsync(host, HttpMethod.Delete, $"/api/v1/admin/users/{annaId}", admin))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        // Signed out at once, and cannot sign in again.
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/dashboards", token), HttpStatusCode.NotFound);
        using (var login = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new { username = "anna", password = "anna-password" }))
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        // Her dashboard is the admin's now.
        var dashboards = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/dashboards", admin));
        var handed = Assert.Single(dashboards.EnumerateArray());
        Assert.Equal(dashboard.GetProperty("id").GetInt64(), handed.GetProperty("id").GetInt64());
        Assert.True(handed.GetProperty("isOwner").GetBoolean());

        // An admin deleted: their key stops and stays listed without an owner.
        using (var deleted = await ApiKeyTests.SendAsync(host, HttpMethod.Delete, $"/api/v1/admin/users/{carl.GetProperty("id").GetInt64()}", admin))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/data/customers", carlKey), HttpStatusCode.Unauthorized);
        var keys = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/admin/api-keys", admin));
        Assert.Equal("revoked", Assert.Single(keys.EnumerateArray()).GetProperty("status").GetString());
        Assert.Single((await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/admin/users", admin))).EnumerateArray());
    }

    [Fact]
    public async Task KeysLimitedToTables_UseOnlyTheirRows()
    {
        await using var host = await McpTests.CreateHostAsync();
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/admin/api-keys", admin, new { name = "Bad", scope = "read-write", tables = new[] { "nope" } }),
            HttpStatusCode.BadRequest, "no table 'nope'");
        string key;
        using (var created = await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/admin/api-keys", admin, new { name = "Sync", scope = "read-write", tables = new[] { "Customers" } }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var body = await created.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("customers", Assert.Single(body.GetProperty("data").GetProperty("tables").EnumerateArray()).GetString());
            key = body.GetProperty("key").GetString()!;
        }
        var row = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/data/customers", key, new { name = "Ada" }));
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Get, $"/api/v1/data/customers/{row.GetProperty("id").GetInt64()}", key));
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/data/customers", key));
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/admin/schema", key));
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/data/customers?include=orders", key), HttpStatusCode.Forbidden, "only use the rows of customers");
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/dashboards", key), HttpStatusCode.Forbidden);
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/query", key, new { source = "customers", measures = new[] { new { name = "n", fn = "count" } } }), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ConsentPage_OffersTheWorkspacesAndPicksTheNamedOne()
    {
        await using var host = await McpTests.CreateHostAsync();
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var created = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/workspaces", admin, new { name = "Gesundheit" }));
        var clientId = await RegisterClientAsync(host);
        var info = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get,
            $"/api/v1/oauth/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString("https://claude.example/cb")}&resource={Uri.EscapeDataString("https://bento.example/mcp/gesundheit")}", admin));
        Assert.Equal(2, info.GetProperty("workspaces").GetArrayLength());
        Assert.Equal(created.GetProperty("id").GetInt64(), Assert.Single(info.GetProperty("selectedWorkspaces").EnumerateArray()).GetInt64());
        var plain = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get,
            $"/api/v1/oauth/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString("https://claude.example/cb")}", admin));
        Assert.Equal(1L, Assert.Single(plain.GetProperty("selectedWorkspaces").EnumerateArray()).GetInt64());
    }

    private static async Task<string> RegisterClientAsync(ApiIntegrationTestHost host)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/oauth/register", new
        {
            client_name = "Claude", redirect_uris = new[] { "https://claude.example/cb" }, grant_types = new[] { "authorization_code", "refresh_token" }, token_endpoint_auth_method = "none"
        });
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    private static async Task<HttpResponseMessage> McpAsync(ApiIntegrationTestHost host, string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } })
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return await host.Client.SendAsync(request);
    }

    private static async Task<JsonElement> LoginAsync(ApiIntegrationTestHost host, string username, string password, string? workspace)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { username, password }) };
        if (workspace is not null)
            request.Headers.Add("X-Bento-Workspace", workspace);
        using var response = await host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> Data(HttpResponseMessage response, bool expectOk = true)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            if (expectOk)
                Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
            var root = JsonDocument.Parse(text).RootElement;
            return (root.TryGetProperty("data", out var data) ? data : root).Clone();
        }
    }

    private static async Task Ok(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.RequestUri} {(int)response.StatusCode}: {text}");
        }
    }

    private static async Task Status(HttpResponseMessage response, HttpStatusCode expected, string? contains = null)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.RequestUri}: expected {(int)expected}, got {(int)response.StatusCode}: {text}");
            if (contains is not null)
                Assert.Contains(contains, text);
        }
    }
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresMemberTests
{
    [PostgresFact]
    public async Task Members_OnPostgres()
    {
        var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(ApiKeyTests.Schema);
        await MemberTests.MembersAsync(host);
    }

    [PostgresFact]
    public async Task DeletingAUser_OnPostgres()
    {
        var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(ApiKeyTests.Schema);
        await MemberTests.DeleteUserAsync(host);
    }
}
