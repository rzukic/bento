using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Bento.Tests;

/// <summary>API keys (Part 3 R3.3) and the generated OpenAPI document (Part 6 R6.8).</summary>
public sealed class ApiKeyTests
{
    internal const string Schema = """
        {
            "tables": [
                {
                    "name": "customers",
                    "displayName": "Customers",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false },
                        { "name": "tier", "type": "enum", "values": ["bronze", "gold"] },
                        { "name": "joined_on", "type": "date" }
                    ]
                }
            ],
            "relations": []
        }
        """;

    [Fact]
    public async Task Keys_AreShownOnce_AndWorkThroughBothHeaders()
    {
        await using var host = await CreateHostAsync();
        var admin = await AdminTokenAsync(host);

        var created = await SendAsync(host, HttpMethod.Post, "/api/v1/admin/api-keys", admin, new { name = "Sync script", scope = "read" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var payload = await created.Content.ReadFromJsonAsync<JsonElement>();
        var key = payload.GetProperty("key").GetString()!;
        Assert.StartsWith("bento_", key);
        Assert.Equal(key[..12], payload.GetProperty("data").GetProperty("prefix").GetString());
        Assert.Equal("read", payload.GetProperty("data").GetProperty("scope").GetString());
        Assert.Equal("active", payload.GetProperty("data").GetProperty("status").GetString());

        // The list never contains the secret.
        var list = await (await SendAsync(host, HttpMethod.Get, "/api/v1/admin/api-keys", admin)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(key, list);
        Assert.Contains("Sync script", list);

        using var viaHeader = new HttpRequestMessage(HttpMethod.Get, "/api/v1/data/customers");
        viaHeader.Headers.Add("X-API-Key", key);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(viaHeader)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(host, HttpMethod.Get, "/api/v1/data/customers", key)).StatusCode);

        var used = (await (await SendAsync(host, HttpMethod.Get, "/api/v1/admin/api-keys", admin)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data")[0];
        Assert.NotEqual(JsonValueKind.Null, used.GetProperty("lastUsedAt").ValueKind);
        Assert.Equal("bootstrap-admin", used.GetProperty("createdBy").GetString());
    }

    [Fact]
    public async Task ReadKeys_ReadAndQuery_ButDoNotWrite()
    {
        await using var host = await CreateHostAsync();
        var key = await CreateKeyAsync(host, await AdminTokenAsync(host), "read");

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(host, HttpMethod.Get, "/api/v1/data/customers", key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(host, HttpMethod.Post, "/api/v1/query", key,
            new { source = "customers", measures = new[] { new { name = "n", fn = "count" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(host, HttpMethod.Post, "/api/v1/formulas/validate", key,
            new { table = "customers", formula = "UPPER(name)" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(host, HttpMethod.Post, "/api/v1/formulas/preview", key,
            new { table = "customers", formula = "UPPER(name)" })).StatusCode);

        using var write = await SendAsync(host, HttpMethod.Post, "/api/v1/data/customers", key, new { name = "Acme" });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Contains("only read", await write.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(host, HttpMethod.Post, "/api/v1/dashboards", key,
            new { name = "x", dashboard = new { widgets = Array.Empty<object>() } })).StatusCode);
    }

    [Fact]
    public async Task ReadWriteKeys_WriteData_ButNeverReachAdminRoutes()
    {
        await using var host = await CreateHostAsync();
        var key = await CreateKeyAsync(host, await AdminTokenAsync(host), "read-write");

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(host, HttpMethod.Post, "/api/v1/data/customers", key, new { name = "Acme" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(host, HttpMethod.Get, "/api/v1/admin/api-keys", key)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(host, HttpMethod.Post, "/api/v1/admin/api-keys", key, new { name = "escalate", scope = "read-write" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(host, HttpMethod.Put, "/api/v1/admin/schema", key, new { tables = Array.Empty<object>(), relations = Array.Empty<object>() })).StatusCode);
    }

    [Fact]
    public async Task RevokedExpiredAndMadeUpKeys_AreRejected()
    {
        await using var host = await CreateHostAsync();
        var admin = await AdminTokenAsync(host);
        var revoked = await CreateKeyAsync(host, admin, "read");
        var expired = await CreateKeyAsync(host, admin, "read", expiresInDays: 1);

        var keys = (await (await SendAsync(host, HttpMethod.Get, "/api/v1/admin/api-keys", admin)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").EnumerateArray().ToList();
        var revokedId = keys.Single(k => revoked.StartsWith(k.GetProperty("prefix").GetString()!)).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(host, HttpMethod.Delete, $"/api/v1/admin/api-keys/{revokedId}", admin)).StatusCode);

        await using (var conn = new SqliteConnection($"Data Source={host.SystemDatabasePath}"))
        {
            await conn.ExecuteAsync("UPDATE api_keys SET expires_at = '2020-01-01T00:00:00.000Z' WHERE prefix = @Prefix", new { Prefix = expired[..12] });
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(host, HttpMethod.Get, "/api/v1/data/customers", revoked)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(host, HttpMethod.Get, "/api/v1/data/customers", expired)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(host, HttpMethod.Get, "/api/v1/data/customers", "bento_" + new string('x', 43))).StatusCode);

        var statuses = (await (await SendAsync(host, HttpMethod.Get, "/api/v1/admin/api-keys", admin)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").EnumerateArray().Select(k => k.GetProperty("status").GetString()).OrderBy(s => s).ToList();
        Assert.Equal(["expired", "revoked"], statuses);
    }

    [Theory]
    [InlineData("""{ "scope": "read" }""", "name")]
    [InlineData("""{ "name": "x", "scope": "admin" }""", "scope")]
    [InlineData("""{ "name": "x", "scope": "read", "expiresInDays": 0 }""", "expiresInDays")]
    public async Task CreatingKeys_ValidatesTheRequest(string body, string field)
    {
        await using var host = await CreateHostAsync();
        var admin = await AdminTokenAsync(host);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/api-keys")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(field, payload.GetProperty("error").GetProperty("details").GetProperty("field").GetString());
    }

    [Fact]
    public async Task OpenApi_DescribesEveryTableWithItsFieldsAndScopes()
    {
        await using var host = await CreateHostAsync();
        var key = await CreateKeyAsync(host, await AdminTokenAsync(host), "read");

        using var response = await SendAsync(host, HttpMethod.Get, "/api/v1/openapi.json", key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("3.0.3", doc.GetProperty("openapi").GetString());
        Assert.Equal("/api/v1", doc.GetProperty("servers")[0].GetProperty("url").GetString());
        var paths = doc.GetProperty("paths");
        Assert.Equal("read", paths.GetProperty("/data/customers").GetProperty("get").GetProperty("x-bento-scope").GetString());
        Assert.Equal("write", paths.GetProperty("/data/customers").GetProperty("post").GetProperty("x-bento-scope").GetString());
        Assert.True(paths.GetProperty("/data/customers/{id}").TryGetProperty("patch", out _));
        Assert.True(paths.TryGetProperty("/query", out _));
        Assert.Equal("Data · Customers", paths.GetProperty("/data/customers").GetProperty("get").GetProperty("tags")[0].GetString());

        var schemas = doc.GetProperty("components").GetProperty("schemas");
        var input = schemas.GetProperty("CustomersInput");
        Assert.Equal(["name"], input.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.False(input.GetProperty("properties").TryGetProperty("id", out _));
        Assert.Equal(["bronze", "gold"], input.GetProperty("properties").GetProperty("tier").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("date", input.GetProperty("properties").GetProperty("joined_on").GetProperty("format").GetString());
        Assert.True(schemas.GetProperty("Customers").GetProperty("properties").GetProperty("id").GetProperty("readOnly").GetBoolean());
        Assert.Equal(["customers"], schemas.GetProperty("AggregationQuery").GetProperty("properties").GetProperty("source").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
    }

    // ── Helpers ──

    internal static async Task<string> CreateKeyAsync(ApiIntegrationTestHost host, string admin, string scope, int? expiresInDays = null)
    {
        using var response = await SendAsync(host, HttpMethod.Post, "/api/v1/admin/api-keys", admin, new { name = $"{scope} key", scope, expiresInDays });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
    }

    internal static async Task<HttpResponseMessage> SendAsync(ApiIntegrationTestHost host, HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(request);
    }

    internal static async Task<string> AdminTokenAsync(ApiIntegrationTestHost host)
    {
        (await host.Client.PostAsJsonAsync("/api/v1/auth/register", new { username = "bootstrap-admin", password = "supersecure" })).EnsureSuccessStatusCode();
        using var login = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new { username = "bootstrap-admin", password = "supersecure" });
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<Bento.Api.Models.AuthTokenResponse>())!.AccessToken;
    }

    private static async Task<ApiIntegrationTestHost> CreateHostAsync()
    {
        var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(Schema);
        return host;
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresApiKeyTests
{
    [PostgresFact]
    public async Task Keys_WorkOnPostgres()
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
        var key = await ApiKeyTests.CreateKeyAsync(host, admin, "read-write", expiresInDays: 30);

        Assert.Equal(HttpStatusCode.Created, (await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/data/customers", key, new { name = "Acme" })).StatusCode);
        var list = (await (await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/admin/api-keys", admin)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")[0];
        Assert.Equal("active", list.GetProperty("status").GetString());
        Assert.Equal("read-write", list.GetProperty("scope").GetString());
        Assert.NotEqual(JsonValueKind.Null, list.GetProperty("lastUsedAt").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/openapi.json", key)).StatusCode);
    }
}
