using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Repere.Api.Models;

namespace Repere.Tests;

/// <summary>
/// Role enforcement on the auto-generated <c>/api/v1/data/*</c> routes in standard auth mode.
/// Reads are open to every authenticated role; writes require <c>editor</c>/<c>admin</c> or <c>api</c>.
/// </summary>
public sealed class DataRouteAuthorizationTests
{
    private const string TestSchema = """
        {
            "tables": [
                {
                    "name": "customers",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false }
                    ]
                }
            ],
            "relations": []
        }
        """;

    [Fact]
    public async Task Viewer_CanRead_ButCannotWrite()
    {
        await using var host = await CreateStandardModeHostAsync();
        var adminToken = await RegisterBootstrapAdminAsync(host);
        var viewerToken = await CreateUserAndLoginAsync(host, adminToken, "viewer-user", "viewer");

        var createdId = await CreateCustomerAsync(host, adminToken, "Seed");

        using var listResponse = await SendAsync(host, HttpMethod.Get, "/api/v1/data/customers", viewerToken);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        using var getResponse = await SendAsync(host, HttpMethod.Get, $"/api/v1/data/customers/{createdId}", viewerToken);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        using var createResponse = await SendAsync(host, HttpMethod.Post, "/api/v1/data/customers", viewerToken, new { name = "Blocked" });
        await AssertForbiddenAsync(createResponse);

        using var putResponse = await SendAsync(host, HttpMethod.Put, $"/api/v1/data/customers/{createdId}", viewerToken, new { name = "Blocked" });
        await AssertForbiddenAsync(putResponse);

        using var patchResponse = await SendAsync(host, HttpMethod.Patch, $"/api/v1/data/customers/{createdId}", viewerToken, new { name = "Blocked" });
        await AssertForbiddenAsync(patchResponse);

        using var deleteResponse = await SendAsync(host, HttpMethod.Delete, $"/api/v1/data/customers/{createdId}", viewerToken);
        await AssertForbiddenAsync(deleteResponse);

        // The row the viewer tried to mutate is untouched.
        using var verifyResponse = await SendAsync(host, HttpMethod.Get, $"/api/v1/data/customers/{createdId}", adminToken);
        var verifyJson = await verifyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Seed", verifyJson.GetProperty("data").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Editor_CanReadAndWrite()
    {
        await using var host = await CreateStandardModeHostAsync();
        var adminToken = await RegisterBootstrapAdminAsync(host);
        var editorToken = await CreateUserAndLoginAsync(host, adminToken, "editor-user", "editor");

        using var createResponse = await SendAsync(host, HttpMethod.Post, "/api/v1/data/customers", editorToken, new { name = "Editor Row" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var id = (await createResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetInt64();

        using var patchResponse = await SendAsync(host, HttpMethod.Patch, $"/api/v1/data/customers/{id}", editorToken, new { name = "Edited" });
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        using var deleteResponse = await SendAsync(host, HttpMethod.Delete, $"/api/v1/data/customers/{id}", editorToken);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task ApiRole_CanReadAndWrite()
    {
        await using var host = await CreateStandardModeHostAsync();
        var adminToken = await RegisterBootstrapAdminAsync(host);
        var apiToken = await CreateUserAndLoginAsync(host, adminToken, "api-user", "api");

        using var listResponse = await SendAsync(host, HttpMethod.Get, "/api/v1/data/customers", apiToken);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        using var createResponse = await SendAsync(host, HttpMethod.Post, "/api/v1/data/customers", apiToken, new { name = "Api Row" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
    }

    [Fact]
    public async Task Viewer_CanReadSchema_ButNotPreviewOrMigrations()
    {
        await using var host = await CreateStandardModeHostAsync();
        var adminToken = await RegisterBootstrapAdminAsync(host);
        var viewerToken = await CreateUserAndLoginAsync(host, adminToken, "viewer-user", "viewer");

        using var schemaResponse = await SendAsync(host, HttpMethod.Get, "/api/v1/admin/schema", viewerToken);
        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);
        var schemaJson = await schemaResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("customers", schemaJson.GetProperty("tables")[0].GetProperty("name").GetString());

        using var modelResponse = await SendAsync(host, HttpMethod.Get, "/api/v1/admin/schema/model", viewerToken);
        Assert.Equal(HttpStatusCode.OK, modelResponse.StatusCode);

        using var previewResponse = await SendAsync(host, HttpMethod.Post, "/api/v1/admin/schema/preview", viewerToken, new { tables = Array.Empty<object>(), relations = Array.Empty<object>() });
        await AssertForbiddenAsync(previewResponse);

        using var applyResponse = await SendAsync(host, HttpMethod.Put, "/api/v1/admin/schema", viewerToken, new { tables = Array.Empty<object>(), relations = Array.Empty<object>() });
        await AssertForbiddenAsync(applyResponse);

        using var migrationsResponse = await SendAsync(host, HttpMethod.Get, "/api/v1/admin/migrations", viewerToken);
        await AssertForbiddenAsync(migrationsResponse);
    }

    [Fact]
    public async Task Admin_CanWrite()
    {
        await using var host = await CreateStandardModeHostAsync();
        var adminToken = await RegisterBootstrapAdminAsync(host);

        var id = await CreateCustomerAsync(host, adminToken, "Admin Row");

        using var deleteResponse = await SendAsync(host, HttpMethod.Delete, $"/api/v1/data/customers/{id}", adminToken);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    private static async Task<ApiIntegrationTestHost> CreateStandardModeHostAsync()
    {
        var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });

        await host.ApplySchemaAsync(TestSchema);
        return host;
    }

    private static async Task<string> RegisterBootstrapAdminAsync(ApiIntegrationTestHost host)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await LoginAsync(host, "bootstrap-admin", "supersecure");
    }

    private static async Task<string> CreateUserAndLoginAsync(
        ApiIntegrationTestHost host,
        string adminToken,
        string username,
        string role)
    {
        using var response = await SendAsync(host, HttpMethod.Post, "/api/v1/auth/register", adminToken, new
        {
            username,
            password = "anotherpass",
            role
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await LoginAsync(host, username, "anotherpass");
    }

    private static async Task<string> LoginAsync(ApiIntegrationTestHost host, string username, string password)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();
        payload = Assert.IsType<AuthTokenResponse>(payload);
        return payload.AccessToken;
    }

    private static async Task<long> CreateCustomerAsync(ApiIntegrationTestHost host, string token, string name)
    {
        using var response = await SendAsync(host, HttpMethod.Post, "/api/v1/data/customers", token, new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("data").GetProperty("id").GetInt64();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        ApiIntegrationTestHost host,
        HttpMethod method,
        string path,
        string token,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        return await host.Client.SendAsync(request);
    }

    private static async Task AssertForbiddenAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var error = json.GetProperty("error");
        Assert.Equal("viewer", error.GetProperty("details").GetProperty("actualRole").GetString());
    }
}
