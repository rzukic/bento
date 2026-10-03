using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Integration tests for GET/PUT /api/v1/explorer/preferences/{tableId}.
/// </summary>
public sealed class ExplorerPreferencesApiTests
{
    private const string MinimalSchema = """
        {
            "tables": [
                {
                    "name": "items",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "title", "type": "text" }
                    ]
                }
            ]
        }
        """;

    [Fact]
    public async Task Get_WhenNoPreferencesExist_ReturnsDefaults()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");

        using var resp = await SendAsync(host.Client, HttpMethod.Get, "/api/v1/explorer/preferences/items", token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("default_view_id", out var defaultViewId));
        Assert.Equal(JsonValueKind.Null, defaultViewId.ValueKind);
        Assert.False(body.GetProperty("auto_save").GetBoolean());
    }

    [Fact]
    public async Task Put_CanSetAndClearDefaultView_AndPersistsAutoSave()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");

        var viewId = await CreateViewAsync(host.Client, token, "My View");

        // Set default view + autosave on
        using (var putResp = await SendAsync(host.Client, HttpMethod.Put, "/api/v1/explorer/preferences/items", token,
                   new { default_view_id = viewId, auto_save = true }))
        {
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        }

        // Verify via GET
        using (var getResp = await SendAsync(host.Client, HttpMethod.Get, "/api/v1/explorer/preferences/items", token))
        {
            var body = await getResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(viewId, body.GetProperty("default_view_id").GetInt32());
            Assert.True(body.GetProperty("auto_save").GetBoolean());
        }

        // Verify the views list includes preferences (for frontend bootstrapping)
        using (var listResp = await SendAsync(host.Client, HttpMethod.Get, "/api/v1/views/items", token))
        {
            var body = await listResp.Content.ReadFromJsonAsync<JsonElement>();
            var prefs = body.GetProperty("preferences");
            Assert.Equal(viewId, prefs.GetProperty("default_view_id").GetInt32());
            Assert.True(prefs.GetProperty("auto_save").GetBoolean());
        }

        // Clear default view (explicit null) but keep autosave unchanged
        using (var clearResp = await SendAsync(host.Client, HttpMethod.Put, "/api/v1/explorer/preferences/items", token,
                   new { default_view_id = (int?)null }))
        {
            Assert.Equal(HttpStatusCode.OK, clearResp.StatusCode);
            var body = await clearResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(JsonValueKind.Null, body.GetProperty("default_view_id").ValueKind);
            Assert.True(body.GetProperty("auto_save").GetBoolean());
        }
    }

    [Fact]
    public async Task Put_OmittingDefaultViewId_DoesNotClearIt()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");

        var viewId = await CreateViewAsync(host.Client, token, "My View");

        // Set default view first
        using (var putResp = await SendAsync(host.Client, HttpMethod.Put, "/api/v1/explorer/preferences/items", token,
                   new { default_view_id = viewId, auto_save = true }))
        {
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        }

        // Now omit default_view_id (should remain set), but toggle autosave
        using (var putResp = await SendAsync(host.Client, HttpMethod.Put, "/api/v1/explorer/preferences/items", token,
                   new { auto_save = false }))
        {
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
            var body = await putResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(viewId, body.GetProperty("default_view_id").GetInt32());
            Assert.False(body.GetProperty("auto_save").GetBoolean());
        }
    }

    [Fact]
    public async Task Put_RejectsDefaultViewNotOwnedByUser()
    {
        await using var host = await CreateHostAsync();
        var (user1Token, user2Token) = await RegisterTwoUsersAsync(host.Client);

        var user2ViewId = await CreateViewAsync(host.Client, user2Token, "User2 View");

        using var resp = await SendAsync(host.Client, HttpMethod.Put, "/api/v1/explorer/preferences/items", user1Token,
            new { default_view_id = user2ViewId });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<ApiIntegrationTestHost> CreateHostAsync()
    {
        var host = await ApiIntegrationTestHost.StartAsync();
        await host.ApplySchemaAsync(MinimalSchema);
        return host;
    }

    private static async Task<string> RegisterAndLoginAsync(
        HttpClient client, string username, string password, string? adminToken = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = JsonContent.Create(new { username, password })
        };
        if (adminToken is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        await client.SendAsync(req);

        using var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        loginResp.EnsureSuccessStatusCode();
        var json = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }

    private static async Task<(string user1Token, string user2Token)> RegisterTwoUsersAsync(HttpClient client)
    {
        var user1Token = await RegisterAndLoginAsync(client, "user1", "password1");
        var user2Token = await RegisterAndLoginAsync(client, "user2", "password2", adminToken: user1Token);
        return (user1Token, user2Token);
    }

    private static async Task<int> CreateViewAsync(
        HttpClient client, string token, string name)
    {
        using var resp = await SendAsync(client, HttpMethod.Post, "/api/v1/views", token,
            new { name, table_name = "items", type = "table", config = new { }, is_shared = false });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string? token = null, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            req.Content = JsonContent.Create(body);
        return await client.SendAsync(req);
    }
}
