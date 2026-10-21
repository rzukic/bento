using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bento.Tests;

/// <summary>
/// The MCP server and its OAuth sign-in (Step 4.6): discovery, client
/// registration, consent, PKCE, token rotation, scopes, the tools and the call log.
/// </summary>
public sealed class McpTests
{
    private const string Redirect = "http://127.0.0.1:33418/callback";

    [Fact]
    public async Task Discovery_PointsClientsAtBentoAsAuthorizationServer()
    {
        await using var host = await CreateHostAsync(publicUrl: "https://bento.example.com/");

        var resource = await host.Client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource");
        Assert.Equal("https://bento.example.com/mcp", resource.GetProperty("resource").GetString());
        Assert.Equal("https://bento.example.com", resource.GetProperty("authorization_servers")[0].GetString());

        var server = await host.Client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
        Assert.Equal("https://bento.example.com", server.GetProperty("issuer").GetString());
        Assert.Equal("https://bento.example.com/oauth/authorize", server.GetProperty("authorization_endpoint").GetString());
        Assert.Equal("https://bento.example.com/api/v1/oauth/token", server.GetProperty("token_endpoint").GetString());
        Assert.Equal("https://bento.example.com/api/v1/oauth/register", server.GetProperty("registration_endpoint").GetString());
        Assert.Equal("S256", server.GetProperty("code_challenge_methods_supported")[0].GetString());

        // Readable from a browser-based client on another origin.
        using var cors = new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-authorization-server");
        cors.Headers.Add("Origin", "http://localhost:6274");
        using var corsResponse = await host.Client.SendAsync(cors);
        Assert.Equal(HttpStatusCode.OK, corsResponse.StatusCode);

        // Without a token /mcp says where to sign in.
        using var unauthorized = await host.Client.PostAsJsonAsync("/mcp", Rpc("initialize", new { protocolVersion = "2025-06-18" }));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        var challenge = unauthorized.Headers.WwwAuthenticate.ToString();
        Assert.Contains("resource_metadata=\"https://bento.example.com/.well-known/oauth-protected-resource\"", challenge);
    }

    [Fact]
    public async Task OAuthFlow_ConnectsAClient_ThatUsesTheTools()
    {
        await using var host = await CreateHostAsync();
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var clientId = await RegisterAsync(host, "Claude");

        // The consent page reads what it is approving.
        var info = await ReadDataAsync(await ApiKeyTests.SendAsync(host, HttpMethod.Get, $"/api/v1/oauth/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString(Redirect)}", admin));
        Assert.Equal("Claude", info.GetProperty("clientName").GetString());
        Assert.Equal("127.0.0.1:33418", info.GetProperty("redirectHost").GetString());

        var (verifier, challenge) = Pkce();
        var redirectTo = await ApproveAsync(host, admin, clientId, challenge, "read-write", state: "xyz");
        var query = ParseQuery(redirectTo);
        Assert.StartsWith(Redirect + "?", redirectTo);
        Assert.Equal("xyz", query["state"]);
        Assert.False(query.ContainsKey("error"));

        var tokens = await TokenAsync(host, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = query["code"],
            ["redirect_uri"] = Redirect, ["code_verifier"] = verifier
        });
        Assert.Equal(HttpStatusCode.OK, tokens.Status);
        var access = tokens.Body.GetProperty("access_token").GetString()!;
        var refresh = tokens.Body.GetProperty("refresh_token").GetString()!;
        Assert.StartsWith("bento_at_", access);
        Assert.Equal("data.read data.write", tokens.Body.GetProperty("scope").GetString());

        // The code works once.
        var again = await TokenAsync(host, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = query["code"],
            ["redirect_uri"] = Redirect, ["code_verifier"] = verifier
        });
        Assert.Equal("invalid_grant", again.Body.GetProperty("error").GetString());

        // …and using it twice revoked the grant, so connect once more.
        (verifier, challenge) = Pkce();
        query = ParseQuery(await ApproveAsync(host, admin, clientId, challenge, "read-write"));
        tokens = await TokenAsync(host, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = query["code"],
            ["redirect_uri"] = Redirect, ["code_verifier"] = verifier
        });
        access = tokens.Body.GetProperty("access_token").GetString()!;
        refresh = tokens.Body.GetProperty("refresh_token").GetString()!;

        var init = await McpAsync(host, access, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
        Assert.Equal("2025-06-18", init.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal("bento", init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

        using (var notification = await PostMcpAsync(host, access, new { jsonrpc = "2.0", method = "notifications/initialized" }))
            Assert.Equal(HttpStatusCode.Accepted, notification.StatusCode);

        var tools = ToolNames(await McpAsync(host, access, "tools/list"));
        Assert.Contains("describe_schema", tools);
        Assert.Contains("aggregate", tools);
        Assert.Contains("create_row", tools);

        var created = await CallToolAsync(host, access, "create_row", new { table = "customers", values = new { name = "Bakery", tier = "gold", joined_on = "2026-10-09" } });
        Assert.False(created.IsError, created.Text);
        Assert.Contains("Bakery", created.Text);

        var schema = await CallToolAsync(host, access, "describe_schema", new { });
        Assert.Contains("tier: enum [bronze|gold]", schema.Text);

        var rows = await CallToolAsync(host, access, "list_rows", new { table = "customers", filter = new { tier = "gold", joined_on = new { gte = "2026-01-01" } }, sort = "-id" });
        Assert.False(rows.IsError, rows.Text);
        Assert.Contains("Bakery", rows.Text);

        var counts = await CallToolAsync(host, access, "aggregate", new { source = "customers", groupBy = new[] { new { column = "tier" } }, measures = new[] { new { name = "n", fn = "count" } } });
        Assert.False(counts.IsError, counts.Text);
        Assert.Contains("\"n\":1", counts.Text);

        // Mistakes come back as tool errors the model can act on.
        var bad = await CallToolAsync(host, access, "list_rows", new { table = "customers", filter = new { nope = 1 } });
        Assert.True(bad.IsError);
        Assert.Contains("Unknown column 'nope'", bad.Text);

        // Refresh tokens rotate; an old one coming back ends the connection.
        var refreshed = await TokenAsync(host, new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = refresh });
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        var newAccess = refreshed.Body.GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await PostMcpAsync(host, newAccess, Rpc("ping"))).StatusCode);
        var replay = await TokenAsync(host, new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = refresh });
        Assert.Equal("invalid_grant", replay.Body.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcpAsync(host, newAccess, Rpc("ping"))).StatusCode);

        // Every tool call is in the log.
        var calls = (await (await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/mcp/calls", admin)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Contains(calls.EnumerateArray(), c => c.GetProperty("tool").GetString() == "create_row" && c.GetProperty("ok").GetBoolean() && c.GetProperty("credential").GetString() == "Claude");
        Assert.Contains(calls.EnumerateArray(), c => c.GetProperty("tool").GetString() == "list_rows" && !c.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ReadOnlyConnection_SeesNoWriteTools_AndCannotWrite()
    {
        await using var host = await CreateHostAsync();
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var access = await ConnectAsync(host, admin, "read");

        var tools = ToolNames(await McpAsync(host, access, "tools/list"));
        Assert.Contains("list_rows", tools);
        Assert.DoesNotContain("create_row", tools);
        Assert.DoesNotContain("update_row", tools);

        var refused = await CallToolAsync(host, access, "create_row", new { table = "customers", values = new { name = "Nope" } });
        Assert.True(refused.IsError);
        Assert.Contains("needs \"data.write\"", refused.Text);

        // The token itself is read-only on the REST API too.
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/data/customers", access, new { name = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/data/customers", access)).StatusCode);
        // Tokens never reach admin routes or the connection settings.
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/admin/api-keys", access)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/mcp/connections", access)).StatusCode);
    }

    [Fact]
    public async Task ViewerGrantingWrite_StillOnlyReads()
    {
        await using var host = await CreateHostAsync();
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        (await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/auth/register", admin, new { username = "viewer", password = "anotherpass", role = "viewer" })).EnsureSuccessStatusCode();
        using var login = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new { username = "viewer", password = "anotherpass" });
        var viewer = (await login.Content.ReadFromJsonAsync<Bento.Api.Models.AuthTokenResponse>())!.AccessToken;

        var access = await ConnectAsync(host, viewer, "read-write");
        Assert.DoesNotContain("create_row", ToolNames(await McpAsync(host, access, "tools/list")));

        // Viewers manage their own connections; the admin sees everyone's.
        var own = (await (await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/mcp/connections", viewer)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Single(own.EnumerateArray());
        var id = own[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await ApiKeyTests.SendAsync(host, HttpMethod.Delete, $"/api/v1/mcp/connections/{id}", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcpAsync(host, access, Rpc("ping"))).StatusCode);
        Assert.Empty((await (await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/mcp/connections", admin)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task OAuth_RejectsWhatItShould()
    {
        await using var host = await CreateHostAsync();
        var admin = await ApiKeyTests.AdminTokenAsync(host);

        // Redirects: https, or http only on loopback.
        using (var insecure = await host.Client.PostAsJsonAsync("/api/v1/oauth/register", new { client_name = "Evil", redirect_uris = new[] { "http://evil.example.com/cb" } }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, insecure.StatusCode);
            Assert.Equal("invalid_redirect_uri", (await insecure.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
        using (var script = await host.Client.PostAsJsonAsync("/api/v1/oauth/register", new { redirect_uris = new[] { "javascript:alert(1)" } }))
            Assert.Equal(HttpStatusCode.BadRequest, script.StatusCode);

        var clientId = await RegisterAsync(host, "Claude");
        // An address the client did not register is refused before anything is redirected.
        using (var foreign = await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/oauth/authorize", admin,
                   new { clientId, redirectUri = "https://evil.example.com/cb", codeChallenge = Pkce().Challenge, scope = "read", approve = true }))
            Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);

        // Declining sends the user back with access_denied.
        using (var declined = await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/oauth/authorize", admin,
                   new { clientId, redirectUri = Redirect, codeChallenge = Pkce().Challenge, scope = "read", state = "s", approve = false }))
        {
            var to = (await ReadDataAsync(declined)).GetProperty("redirectTo").GetString()!;
            Assert.Equal("access_denied", ParseQuery(to)["error"]);
        }

        // The loopback port may change between registration and use (RFC 8252).
        var (_, otherPortChallenge) = Pkce();
        var otherPort = await ApproveAsync(host, admin, clientId, otherPortChallenge, "read", redirectUri: "http://127.0.0.1:50999/callback");
        Assert.StartsWith("http://127.0.0.1:50999/callback?code=", otherPort);

        // A wrong PKCE verifier gets no token.
        var (_, challenge) = Pkce();
        var code = ParseQuery(await ApproveAsync(host, admin, clientId, challenge, "read"))["code"];
        var wrong = await TokenAsync(host, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code,
            ["redirect_uri"] = Redirect, ["code_verifier"] = Pkce().Verifier
        });
        Assert.Equal("invalid_grant", wrong.Body.GetProperty("error").GetString());

        var unknown = await TokenAsync(host, new() { ["grant_type"] = "authorization_code", ["client_id"] = "nobody", ["code"] = "x", ["code_verifier"] = "y" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Status);
        Assert.Equal("invalid_client", unknown.Body.GetProperty("error").GetString());

        // API keys cannot approve apps for their user.
        var key = await ApiKeyTests.CreateKeyAsync(host, admin, "read-write");
        using (var byKey = await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/oauth/authorize", key,
                   new { clientId, redirectUri = Redirect, codeChallenge = challenge, scope = "read", approve = true }))
            Assert.Equal(HttpStatusCode.Forbidden, byKey.StatusCode);
    }

    [Fact]
    public async Task ApiKey_WorksOnMcp_WithItsScope()
    {
        await using var host = await CreateHostAsync();
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var readKey = await ApiKeyTests.CreateKeyAsync(host, admin, "read");
        var writeKey = await ApiKeyTests.CreateKeyAsync(host, admin, "read-write");

        Assert.DoesNotContain("create_row", ToolNames(await McpAsync(host, readKey, "tools/list")));
        Assert.Contains("create_row", ToolNames(await McpAsync(host, writeKey, "tools/list")));

        var created = await CallToolAsync(host, writeKey, "create_row", new { table = "customers", values = new { name = "Via key" } });
        Assert.False(created.IsError, created.Text);
        var id = JsonDocument.Parse(created.Text).RootElement.GetProperty("data").GetProperty("id").GetInt64();
        var updated = await CallToolAsync(host, writeKey, "update_row", new { table = "customers", id = id.ToString(), values = new { tier = "bronze" } });
        Assert.False(updated.IsError, updated.Text);
        Assert.Contains("bronze", (await CallToolAsync(host, readKey, "get_row", new { table = "customers", id })).Text);

        // A bad key gets the same OAuth pointer as no key.
        using var bad = await PostMcpAsync(host, "bento_definitely-not-a-key-1234567890", Rpc("ping"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Contains("invalid_token", bad.Headers.WwwAuthenticate.ToString());

        // Unknown methods and tools are JSON-RPC errors; GET has no stream.
        Assert.Equal(-32601, (await McpAsync(host, readKey, "sampling/createMessage")).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32602, (await McpAsync(host, readKey, "tools/call", new { name = "drop_everything" })).GetProperty("error").GetProperty("code").GetInt32());
        using var get = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", readKey);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await host.Client.SendAsync(get)).StatusCode);
    }

    [Fact]
    public async Task DesktopBundle_CarriesThisInstallationsAddress()
    {
        await using var host = await CreateHostAsync(publicUrl: "https://bento.example.com");

        // A plain download: no sign-in, no secret inside.
        using var response = await host.Client.GetAsync("/api/v1/mcp/bento.mcpb");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("bento.mcpb", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        using var zip = new System.IO.Compression.ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.Equal(new[] { "icon.png", "manifest.json", "server/index.js" }, zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));

        using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        var manifest = await JsonDocument.ParseAsync(manifestStream);
        var root = manifest.RootElement;
        Assert.Equal("bento", root.GetProperty("name").GetString());
        Assert.Equal("node", root.GetProperty("server").GetProperty("type").GetString());
        Assert.Equal("${__dirname}/server/index.js", root.GetProperty("server").GetProperty("mcp_config").GetProperty("args")[0].GetString());
        Assert.Equal("https://bento.example.com/mcp", root.GetProperty("user_config").GetProperty("url").GetProperty("default").GetString());
        // It signs in through the browser like the connector: no key to enter.
        Assert.False(root.GetProperty("user_config").TryGetProperty("api_key", out _));
        Assert.DoesNotContain("{{", root.GetRawText());
    }

    [Fact]
    public async Task Permissions_LimitAnAdminsConnection_ToWhatWasTicked()
    {
        await using var host = await CreateHostAsync();
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var access = await ConnectAsync(host, admin, "read", ["data.read", "data.write"]);

        var tools = ToolNames(await McpAsync(host, access, "tools/list"));
        Assert.Contains("create_row", tools);
        Assert.Contains("bento_guide", tools);
        Assert.Contains("call_api", tools);
        Assert.DoesNotContain("delete_row", tools);
        Assert.DoesNotContain("create_dashboard", tools);
        Assert.DoesNotContain("apply_schema_change", tools);
        Assert.DoesNotContain("save_function", tools);

        // Not ticked: refused by Bento itself, also through call_api — although the user is an admin.
        var model = (await (await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/admin/schema/model", admin)).Content.ReadFromJsonAsync<JsonElement>());
        var viaApi = await CallToolAsync(host, access, "call_api", new { method = "PUT", path = "/api/v1/admin/schema", body = model });
        Assert.True(viaApi.IsError);
        Assert.Contains("schema.write", viaApi.Text);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/dashboards", access, new { name = "X", dashboard = new { widgets = Array.Empty<object>() } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiKeyTests.SendAsync(host, HttpMethod.Delete, "/api/v1/data/customers/1", access)).StatusCode);
        var hidden = await CallToolAsync(host, access, "delete_row", new { table = "customers", id = "1" });
        Assert.True(hidden.IsError);
        Assert.Contains("data.delete", hidden.Text);

        // What was ticked works.
        Assert.False((await CallToolAsync(host, access, "create_row", new { table = "customers", values = new { name = "Ok" } })).IsError);
        Assert.False((await CallToolAsync(host, access, "call_api", new { method = "GET", path = "/api/v1/formulas/functions" })).IsError);
        // Areas apps never reach.
        Assert.Contains("cannot use this part", (await CallToolAsync(host, access, "call_api", new { method = "GET", path = "/api/v1/admin/api-keys" })).Text);

        var connection = (await (await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/mcp/connections", admin)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")[0];
        Assert.Equal(new[] { "data.read", "data.write" }, connection.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task FullConnection_BuildsDashboards_Forms_Schema_AndFunctions()
    {
        await using var host = await CreateHostAsync();
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var access = await ConnectAsync(host, admin, "read", ["data.read", "data.write", "data.delete", "dashboards.write", "forms.write", "schema.write", "functions.write"]);
        // Everything ticked: every tool.
        Assert.Equal(host.GetService<Bento.Api.Services.Mcp.McpTools>().All.Count, ToolNames(await McpAsync(host, access, "tools/list")).Count);

        // Schema: add a column after a preview.
        var model = JsonNode.Parse((await CallToolAsync(host, access, "get_model", new { })).Text)!.AsObject();
        model["tables"]![0]!["columns"]!.AsArray().Add(new JsonObject { ["name"] = "vip", ["type"] = "boolean" });
        var preview = await CallToolAsync(host, access, "preview_schema_change", new { model });
        Assert.False(preview.IsError, preview.Text);
        Assert.Contains("vip", preview.Text);
        var applied = await CallToolAsync(host, access, "apply_schema_change", new { model });
        Assert.False(applied.IsError, applied.Text);
        Assert.Contains("vip: boolean", (await CallToolAsync(host, access, "describe_schema", new { })).Text);

        // Small changes as operations; the preview is compact (no whole model).
        var operations = new object[] { new { op = "addColumn", table = "customers", column = new { name = "code", type = "text" }, after = "name" } };
        var compact = await CallToolAsync(host, access, "preview_schema_change", new { operations });
        Assert.False(compact.IsError, compact.Text);
        Assert.Contains("AddColumn", compact.Text);
        Assert.DoesNotContain("normalizedModel", compact.Text);
        Assert.DoesNotContain("normalizedModel", preview.Text);
        Assert.False((await CallToolAsync(host, access, "apply_schema_change", new { operations })).IsError);
        Assert.Contains("code: text", (await CallToolAsync(host, access, "describe_schema", new { })).Text);

        // Many rows at once, matched by a column.
        var bulk = await CallToolAsync(host, access, "upsert_rows", new { table = "customers", rows = new object[] { new { name = "Bulk A", code = "A" }, new { name = "Bulk B", code = "B" } } });
        Assert.False(bulk.IsError, bulk.Text);
        var upsert = await CallToolAsync(host, access, "upsert_rows", new { table = "customers", rows = new object[] { new { code = "A", vip = true }, new { name = "Bulk C", code = "C" } }, upsert_on = new[] { "code" } });
        Assert.False(upsert.IsError, upsert.Text);
        Assert.Equal(1, JsonDocument.Parse(upsert.Text).RootElement.GetProperty("data").GetProperty("updated").GetInt32());
        Assert.Equal(1, JsonDocument.Parse(upsert.Text).RootElement.GetProperty("data").GetProperty("inserted").GetInt32());

        // Rows incl. delete.
        var row = await CallToolAsync(host, access, "create_row", new { table = "customers", values = new { name = "Temp", vip = true } });
        var rowId = JsonDocument.Parse(row.Text).RootElement.GetProperty("data").GetProperty("id").GetInt64();
        Assert.False((await CallToolAsync(host, access, "delete_row", new { table = "customers", id = rowId.ToString() })).IsError);

        // A dashboard, changed and deleted.
        var dashboard = await CallToolAsync(host, access, "create_dashboard", new
        {
            name = "Customers",
            dashboard = new
            {
                widgets = new object[]
                {
                    new { id = "count", type = "kpi", title = "Customers", position = new { x = 0, y = 0, w = 3, h = 2 }, data = new { query = new { source = "customers", measures = new[] { new { name = "n", fn = "count" } } } } }
                }
            }
        });
        Assert.False(dashboard.IsError, dashboard.Text);
        var dashboardId = JsonDocument.Parse(dashboard.Text).RootElement.GetProperty("data").GetProperty("id").GetInt64();
        Assert.False((await CallToolAsync(host, access, "update_dashboard", new { id = dashboardId, name = "All customers" })).IsError);
        Assert.Contains("All customers", (await CallToolAsync(host, access, "list_dashboards", new { })).Text);
        Assert.False((await CallToolAsync(host, access, "delete_dashboard", new { id = dashboardId })).IsError);

        // A saved query, a form, a function.
        Assert.False((await CallToolAsync(host, access, "save_query", new { name = "By tier", query = new { source = "customers", groupBy = new[] { new { column = "tier" } }, measures = new[] { new { name = "n", fn = "count" } } } })).IsError);
        var form = await CallToolAsync(host, access, "create_form", new { name = "Add customer", form = new { table = "customers", fields = new[] { new { column = "name" } } } });
        Assert.False(form.IsError, form.Text);
        var test = await CallToolAsync(host, access, "test_function", new { arguments = new[] { new { name = "x", type = "float" } }, resultType = "float", code = "return x * 2;", values = new[] { 21 } });
        Assert.Contains("42", test.Text);
        var saved = await CallToolAsync(host, access, "save_function", new { name = "double_it", arguments = new[] { new { name = "x", type = "float" } }, resultType = "float", code = "return x * 2;" });
        Assert.False(saved.IsError, saved.Text);
        var savedAgain = await CallToolAsync(host, access, "save_function", new { name = "double_it", arguments = new[] { new { name = "x", type = "float" } }, resultType = "float", code = "return x + x;" });
        Assert.False(savedAgain.IsError, savedAgain.Text);
        Assert.Contains("x + x", (await CallToolAsync(host, access, "get_function", new { name = "double_it" })).Text);

        // A formula check.
        var check = await CallToolAsync(host, access, "check_formula", new { table = "customers", formula = "UPPER(name)", preview = true, limit = 2 });
        Assert.False(check.IsError, check.Text);
    }

    [Fact]
    public async Task ConnectedApp_PermissionsChange_AndItsCallsAreListed()
    {
        await using var host = await CreateHostAsync();
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var reader = await ConnectAsync(host, admin, "read", ["data.read"]);
        var writer = await ConnectAsync(host, admin, "read", ["data.read", "data.write"]);
        var connections = await ReadDataAsync(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/mcp/connections", admin));
        // Newest first: the writer, then the reader.
        var readerId = connections[1].GetProperty("id").GetInt64();
        Assert.Contains("schema.write", connections[1].GetProperty("allowed").EnumerateArray().Select(p => p.GetString()));

        Assert.False((await CallToolAsync(host, reader, "describe_schema", new { })).IsError);
        Assert.True((await CallToolAsync(host, reader, "create_row", new { table = "customers", values = new { name = "No" } })).IsError);
        Assert.False((await CallToolAsync(host, writer, "create_row", new { table = "customers", values = new { name = "Yes" } })).IsError);

        // Only the reader's calls, newest first.
        var calls = await ReadDataAsync(await ApiKeyTests.SendAsync(host, HttpMethod.Get, $"/api/v1/mcp/connections/{readerId}/calls", admin));
        Assert.Equal(["create_row", "describe_schema"], calls.EnumerateArray().Select(c => c.GetProperty("tool").GetString()));
        Assert.False(calls[0].GetProperty("ok").GetBoolean());

        // Allowing rows applies to the next call, without connecting again.
        var updated = await ReadDataAsync(await ApiKeyTests.SendAsync(host, HttpMethod.Put, $"/api/v1/mcp/connections/{readerId}", admin, new { permissions = new[] { "data.write" } }));
        Assert.Equal(["data.read", "data.write"], updated.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("read-write", updated.GetProperty("scope").GetString());
        Assert.False((await CallToolAsync(host, reader, "create_row", new { table = "customers", values = new { name = "Now" } })).IsError);
        Assert.Contains("create_row", ToolNames(await McpAsync(host, reader, "tools/list")));

        // And taking it away again.
        await ApiKeyTests.SendAsync(host, HttpMethod.Put, $"/api/v1/mcp/connections/{readerId}", admin, new { permissions = Array.Empty<string>() });
        Assert.True((await CallToolAsync(host, reader, "create_row", new { table = "customers", values = new { name = "Again" } })).IsError);
        Assert.Equal(HttpStatusCode.BadRequest, (await ApiKeyTests.SendAsync(host, HttpMethod.Put, $"/api/v1/mcp/connections/{readerId}", admin, new { permissions = new[] { "everything" } })).StatusCode);

        // A viewer's app: capped by the viewer's role; other users' apps are not theirs to see.
        var viewer = await AiTests.ViewerTokenAsync(host, admin);
        await ConnectAsync(host, viewer, "read", ["data.read"]);
        var own = (await ReadDataAsync(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/mcp/connections", viewer)))[0];
        var ownId = own.GetProperty("id").GetInt64();
        Assert.DoesNotContain("data.write", own.GetProperty("allowed").EnumerateArray().Select(p => p.GetString()));
        var capped = await ReadDataAsync(await ApiKeyTests.SendAsync(host, HttpMethod.Put, $"/api/v1/mcp/connections/{ownId}", viewer, new { permissions = new[] { "schema.write", "dashboards.write" } }));
        Assert.Equal(["data.read", "dashboards.write"], capped.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await ApiKeyTests.SendAsync(host, HttpMethod.Get, $"/api/v1/mcp/connections/{readerId}/calls", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApiKeyTests.SendAsync(host, HttpMethod.Put, $"/api/v1/mcp/connections/{readerId}", viewer, new { permissions = Array.Empty<string>() })).StatusCode);

        // An app cannot widen its own access.
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiKeyTests.SendAsync(host, HttpMethod.Put, $"/api/v1/mcp/connections/{readerId}", reader, new { permissions = new[] { "schema.write" } })).StatusCode);
    }

    [Fact]
    public async Task Guide_HasEveryTopic_AndCoversTheFormats()
    {
        foreach (var topic in Bento.Api.Services.Mcp.McpTools.GuideTopics)
        {
            var result = Bento.Api.Services.Mcp.McpTools.Guide(topic);
            Assert.False(result.IsError, topic);
            Assert.True(result.Text.Length > 300, topic);
        }
        Assert.True(Bento.Api.Services.Mcp.McpTools.Guide("nope").IsError);

        // The guide stays in step with the code: every widget type and column type is explained.
        var dashboards = Bento.Api.Services.Mcp.McpTools.Guide("dashboards").Text;
        foreach (var type in Bento.Api.Controllers.DashboardsController.WidgetTypes)
            Assert.Contains($"`{type}`", dashboards);
        var schema = Bento.Api.Services.Mcp.McpTools.Guide("schema").Text;
        foreach (var type in new[] { "text", "integer", "float", "currency", "boolean", "date", "datetime", "enum", "reference", "computed", "json", "uuid", "file" })
            Assert.Contains($"`{type}`", schema);

        // And over MCP, for any connection.
        await using var host = await CreateHostAsync();
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var readKey = await ApiKeyTests.CreateKeyAsync(host, admin, "read");
        Assert.Contains("Model JSON", (await CallToolAsync(host, readKey, "bento_guide", new { topic = "schema" })).Text);
        Assert.Contains("bento_guide", (await McpAsync(host, readKey, "initialize", new { protocolVersion = "2025-06-18" })).GetProperty("result").GetProperty("instructions").GetString());
    }

    [Theory]
    [InlineData("GET", "/api/v1/data/transactions", "data.read")]
    [InlineData("POST", "/api/v1/query", "data.read")]
    [InlineData("POST", "/api/v1/queries/3/run", "data.read")]
    [InlineData("POST", "/api/v1/admin/schema/preview", "data.read")]
    [InlineData("POST", "/api/v1/data/transactions", "data.write")]
    [InlineData("PATCH", "/api/v1/data/transactions/4", "data.write")]
    [InlineData("DELETE", "/api/v1/data/transactions/4", "data.delete")]
    [InlineData("POST", "/api/v1/import/transactions", "data.write")]
    [InlineData("POST", "/api/v1/queries", "dashboards.write")]
    [InlineData("PUT", "/api/v1/dashboards/2", "dashboards.write")]
    [InlineData("DELETE", "/api/v1/forms/2", "forms.write")]
    [InlineData("PUT", "/api/v1/admin/schema", "schema.write")]
    [InlineData("POST", "/api/v1/functions", "functions.write")]
    [InlineData("POST", "/api/v1/price-sources/1/run", "functions.write")]
    [InlineData("GET", "/api/v1/admin/api-keys", null)]
    [InlineData("GET", "/api/v1/admin/credentials", null)]
    [InlineData("POST", "/api/v1/auth/register", null)]
    [InlineData("GET", "/api/v1/mcp/connections", null)]
    [InlineData("POST", "/api/v1/oauth/authorize", null)]
    public void RequiredPermission_PerRoute(string method, string path, string? expected) =>
        Assert.Equal(expected, Bento.Api.Services.McpPermissions.RequiredFor(method, path));

    // ── Helpers ──

    internal static async Task<ApiIntegrationTestHost> CreateHostAsync(string? publicUrl = null)
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        };
        if (publicUrl is not null) overrides["Backend:PublicUrl"] = publicUrl;
        var host = await ApiIntegrationTestHost.StartAsync(overrides);
        await host.ApplySchemaAsync(ApiKeyTests.Schema);
        return host;
    }

    /// <summary>Register a client, approve it as the given user and return an access token.</summary>
    internal static async Task<string> ConnectAsync(ApiIntegrationTestHost host, string userToken, string scope, string[]? permissions = null, long[]? workspaces = null)
    {
        var clientId = await RegisterAsync(host, "Claude");
        var (verifier, challenge) = Pkce();
        var code = ParseQuery(await ApproveAsync(host, userToken, clientId, challenge, scope, permissions: permissions, workspaces: workspaces))["code"];
        var tokens = await TokenAsync(host, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code,
            ["redirect_uri"] = Redirect, ["code_verifier"] = verifier
        });
        Assert.Equal(HttpStatusCode.OK, tokens.Status);
        return tokens.Body.GetProperty("access_token").GetString()!;
    }

    private static async Task<string> RegisterAsync(ApiIntegrationTestHost host, string name)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/oauth/register", new
        {
            client_name = name,
            redirect_uris = new[] { Redirect },
            grant_types = new[] { "authorization_code", "refresh_token" },
            token_endpoint_auth_method = "none"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    private static async Task<string> ApproveAsync(ApiIntegrationTestHost host, string userToken, string clientId, string challenge, string scope, string? state = null, string redirectUri = Redirect, string[]? permissions = null, long[]? workspaces = null)
    {
        using var response = await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/oauth/authorize", userToken,
            new { clientId, redirectUri, codeChallenge = challenge, codeChallengeMethod = "S256", scope, permissions, state, approve = true, workspaces });
        return (await ReadDataAsync(response)).GetProperty("redirectTo").GetString()!;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> TokenAsync(ApiIntegrationTestHost host, Dictionary<string, string> form)
    {
        using var response = await host.Client.PostAsync("/api/v1/oauth/token", new FormUrlEncodedContent(form));
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    private static object Rpc(string method, object? parameters = null) =>
        new { jsonrpc = "2.0", id = 1, method, @params = parameters ?? new { } };

    private static async Task<HttpResponseMessage> PostMcpAsync(ApiIntegrationTestHost host, string token, object message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(message) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return await host.Client.SendAsync(request);
    }

    private static async Task<JsonElement> McpAsync(ApiIntegrationTestHost host, string token, string method, object? parameters = null)
    {
        using var response = await PostMcpAsync(host, token, Rpc(method, parameters));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<(string Text, bool IsError)> CallToolAsync(ApiIntegrationTestHost host, string token, string tool, object arguments)
    {
        var response = await McpAsync(host, token, "tools/call", new { name = tool, arguments });
        var result = response.GetProperty("result");
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.GetProperty("isError").GetBoolean());
    }

    private static List<string> ToolNames(JsonElement response) =>
        response.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    private static Dictionary<string, string> ParseQuery(string url)
    {
        var query = new Uri(url).Query.TrimStart('?');
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresMcpTests
{
    [PostgresFact]
    public async Task OAuthAndTools_WorkOnPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(ApiKeyTests.Schema);
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var access = await McpTests.ConnectAsync(host, admin, "read-write");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = "create_row", arguments = new { table = "customers", values = new { name = "Pg" } } } })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await host.Client.SendAsync(request);
        var result = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean(), result.GetProperty("content")[0].GetProperty("text").GetString());

        var calls = (await (await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/mcp/calls", admin)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("create_row", calls[0].GetProperty("tool").GetString());
        var connections = (await (await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/mcp/connections", admin)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("read-write", connections[0].GetProperty("scope").GetString());
        var ownCalls = (await (await ApiKeyTests.SendAsync(host, HttpMethod.Get, $"/api/v1/mcp/connections/{connections[0].GetProperty("id").GetInt64()}/calls", admin)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("create_row", ownCalls[0].GetProperty("tool").GetString());
    }
}
