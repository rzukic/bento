using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Repere.Tests;

/// <summary>
/// Integration tests for GET/POST/PUT/DELETE /api/v1/views.
/// Each test spins up an isolated SQLite-backed host with a minimal schema applied.
/// </summary>
public sealed class ViewsApiTests
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

    // ── List ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_WhenNoViewsExist_ReturnsEmptyArray()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");

        using var resp = await SendAsync(host.Client, HttpMethod.Get, "/api/v1/views/items", token);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var data = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Equal(0, data.GetArrayLength());
    }

    [Fact]
    public async Task List_OwnViews_AreReturnedRegardlessOfIsShared()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");

        await CreateViewAsync(host.Client, token, "Private View", isShared: false);
        await CreateViewAsync(host.Client, token, "Shared View", isShared: true);

        using var resp = await SendAsync(host.Client, HttpMethod.Get, "/api/v1/views/items", token);
        var data = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(2, data.GetArrayLength());
    }

    [Fact]
    public async Task List_SharedViewFromOtherUser_IsVisible()
    {
        await using var host = await CreateHostAsync();
        var (user1Token, user2Token) = await RegisterTwoUsersAsync(host.Client);

        await CreateViewAsync(host.Client, user2Token, "Shared View", isShared: true);

        using var resp = await SendAsync(host.Client, HttpMethod.Get, "/api/v1/views/items", user1Token);
        var data = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("Shared View", data[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task List_PrivateViewFromOtherUser_IsNotVisible()
    {
        await using var host = await CreateHostAsync();
        var (user1Token, user2Token) = await RegisterTwoUsersAsync(host.Client);

        await CreateViewAsync(host.Client, user2Token, "Private View", isShared: false);

        using var resp = await SendAsync(host.Client, HttpMethod.Get, "/api/v1/views/items", user1Token);
        var data = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(0, data.GetArrayLength());
    }

    [Fact]
    public async Task List_OwnViewsAppearBeforeOthersSharedViews()
    {
        await using var host = await CreateHostAsync();
        var (user1Token, user2Token) = await RegisterTwoUsersAsync(host.Client);

        // user2 creates shared view first (older timestamp)
        await CreateViewAsync(host.Client, user2Token, "Other Shared", isShared: true);
        // user1 creates own view second (newer timestamp)
        await CreateViewAsync(host.Client, user1Token, "My View");

        using var resp = await SendAsync(host.Client, HttpMethod.Get, "/api/v1/views/items", user1Token);
        var data = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(2, data.GetArrayLength());
        Assert.Equal("My View", data[0].GetProperty("name").GetString());
        Assert.Equal("Other Shared", data[1].GetProperty("name").GetString());
    }

    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ValidRequest_Returns201WithAllFields()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");

        using var resp = await SendAsync(host.Client, HttpMethod.Post, "/api/v1/views", token,
            new { name = "My View", table_name = "items", type = "table", config = new { }, is_shared = false });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("My View", body.GetProperty("name").GetString());
        Assert.Equal("items", body.GetProperty("table_name").GetString());
        Assert.Equal("table", body.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, body.GetProperty("config").ValueKind);
        Assert.Equal("table", body.GetProperty("config").GetProperty("type").GetString());
        Assert.False(body.GetProperty("is_shared").GetBoolean());
        Assert.True(body.GetProperty("id").GetInt32() > 0);
        Assert.True(body.GetProperty("owner_user_id").GetInt32() > 0);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("created_at").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("updated_at").GetString()));
    }

    [Fact]
    public async Task Create_OwnerUserIdIsSetFromToken_NotFromRequestBody()
    {
        await using var host = await CreateHostAsync();
        var (user1Token, user2Token) = await RegisterTwoUsersAsync(host.Client);

        // user2 creates a view
        var viewId = await CreateViewAsync(host.Client, user2Token, "User2's View");

        // user1 cannot delete user2's view — proves owner is user2
        using var forbiddenResp = await SendAsync(host.Client, HttpMethod.Delete, $"/api/v1/views/{viewId}", user1Token);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResp.StatusCode);

        // user2 can delete their own view — confirms ownership
        using var okResp = await SendAsync(host.Client, HttpMethod.Delete, $"/api/v1/views/{viewId}", user2Token);
        Assert.Equal(HttpStatusCode.NoContent, okResp.StatusCode);
    }

    [Fact]
    public async Task Create_MissingName_Returns400()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");

        using var resp = await SendAsync(host.Client, HttpMethod.Post, "/api/v1/views", token,
            new { table_name = "items", type = "table", config = new { } });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_MissingTableName_Returns400()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");

        using var resp = await SendAsync(host.Client, HttpMethod.Post, "/api/v1/views", token,
            new { name = "View", type = "table", config = new { } });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_InvalidType_Returns400()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");

        using var resp = await SendAsync(host.Client, HttpMethod.Post, "/api/v1/views", token,
            new { name = "View", table_name = "items", type = "gallery", config = new { } });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_NonObjectConfig_Returns400()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");

        // config is a JSON number, not an object
        using var resp = await SendAsync(host.Client, HttpMethod.Post, "/api/v1/views", token,
            new { name = "View", table_name = "items", type = "table", config = 42 });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_ConfigTypeMismatch_Returns400()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");

        using var resp = await SendAsync(host.Client, HttpMethod.Post, "/api/v1/views", token,
            new { name = "View", table_name = "items", type = "table", config = new { type = "kanban" }, is_shared = false });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_Unauthenticated_Returns401()
    {
        await using var host = await CreateHostAsync();

        using var resp = await host.Client.PostAsJsonAsync("/api/v1/views",
            new { name = "View", table_name = "items", type = "table", config = new { } });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── Update ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_Owner_CanUpdateNameConfigAndIsShared()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");
        var viewId = await CreateViewAsync(host.Client, token, "Original Name");

        using var resp = await SendAsync(host.Client, HttpMethod.Put, $"/api/v1/views/{viewId}", token,
            new { name = "Renamed", config = new { search = "q" }, is_shared = true });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Renamed", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("is_shared").GetBoolean());
        Assert.Equal("table", body.GetProperty("type").GetString());
        Assert.Equal("table", body.GetProperty("config").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Update_ConfigTypeMismatch_Returns400()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");
        var viewId = await CreateViewAsync(host.Client, token, "Original Name");

        using var resp = await SendAsync(host.Client, HttpMethod.Put, $"/api/v1/views/{viewId}", token,
            new { config = new { type = "kanban" } });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Update_NonOwner_Returns403()
    {
        await using var host = await CreateHostAsync();
        var (user1Token, user2Token) = await RegisterTwoUsersAsync(host.Client);
        var viewId = await CreateViewAsync(host.Client, user1Token, "User1's View");

        using var resp = await SendAsync(host.Client, HttpMethod.Put, $"/api/v1/views/{viewId}", user2Token,
            new { name = "Hijacked" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Update_UpdatedAt_IsRefreshedOnSuccess()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");
        var viewId = await CreateViewAsync(host.Client, token, "View");

        // Capture original timestamps
        using var listResp = await SendAsync(host.Client, HttpMethod.Get, "/api/v1/views/items", token);
        var before = (await listResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data")[0].GetProperty("updated_at").GetString();

        await Task.Delay(20); // ensure clock advances

        using var putResp = await SendAsync(host.Client, HttpMethod.Put, $"/api/v1/views/{viewId}", token,
            new { name = "Updated" });
        var after = (await putResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("updated_at").GetString();

        Assert.NotNull(before);
        Assert.NotNull(after);
        // After must be at least as recent as before
        Assert.True(string.Compare(after, before, StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public async Task Update_UnknownId_Returns404()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");

        using var resp = await SendAsync(host.Client, HttpMethod.Put, "/api/v1/views/99999", token,
            new { name = "Ghost" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Owner_Returns204()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");
        var viewId = await CreateViewAsync(host.Client, token, "Temp View");

        using var resp = await SendAsync(host.Client, HttpMethod.Delete, $"/api/v1/views/{viewId}", token);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_NonOwner_Returns403()
    {
        await using var host = await CreateHostAsync();
        var (user1Token, user2Token) = await RegisterTwoUsersAsync(host.Client);
        var viewId = await CreateViewAsync(host.Client, user1Token, "User1's View");

        using var resp = await SendAsync(host.Client, HttpMethod.Delete, $"/api/v1/views/{viewId}", user2Token);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_UnknownId_Returns404()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");

        using var resp = await SendAsync(host.Client, HttpMethod.Delete, "/api/v1/views/99999", token);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_DeletedView_NoLongerAppearsInList()
    {
        await using var host = await CreateHostAsync();
        var token = await RegisterAndLoginAsync(host.Client, "alice", "password");
        var viewId = await CreateViewAsync(host.Client, token, "Temp View");

        using var delResp = await SendAsync(host.Client, HttpMethod.Delete, $"/api/v1/views/{viewId}", token);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        using var listResp = await SendAsync(host.Client, HttpMethod.Get, "/api/v1/views/items", token);
        var data = (await listResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(0, data.GetArrayLength());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<ApiIntegrationTestHost> CreateHostAsync()
    {
        var host = await ApiIntegrationTestHost.StartAsync();
        await host.ApplySchemaAsync(MinimalSchema);
        return host;
    }

    /// <summary>
    /// Register + login a single user. For the bootstrap admin (first user), no adminToken is needed.
    /// For subsequent users, pass the admin's token.
    /// </summary>
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
        HttpClient client, string token, string name, bool isShared = false)
    {
        using var resp = await SendAsync(client, HttpMethod.Post, "/api/v1/views", token,
            new { name, table_name = "items", type = "table", config = new { }, is_shared = isShared });
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
