using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Repere.Api.Models;

namespace Repere.Tests;

/// <summary>
/// The one-time demo workspace loader behind <c>POST /api/v1/setup/sample-data</c>.
/// </summary>
public sealed class SetupSampleDataTests
{
    [Fact]
    public async Task LoadsDemoWorkspace_IntoEmptyInstallation()
    {
        await using var host = await StartOpenAsync();

        using var response = await host.Client.PostAsync("/api/v1/setup/sample-data", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summary = await response.Content.ReadFromJsonAsync<SampleDataResponse>();
        summary = Assert.IsType<SampleDataResponse>(summary);
        Assert.Equal(1, summary.SchemaVersion);
        Assert.Equal(["customers", "orders", "products"], summary.TablesCreated);
        Assert.Equal(6, summary.RowsInserted["customers"]);
        Assert.Equal(10, summary.RowsInserted["orders"]);
        Assert.Equal(5, summary.RowsInserted["products"]);

        // Data is reachable through the normal routes, including relation includes.
        var orders = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/orders?include=customer&sort=-total");
        Assert.Equal(10, orders.GetProperty("meta").GetProperty("total").GetInt64());
        Assert.Equal("Stark Industries", orders.GetProperty("data")[0].GetProperty("customer").GetProperty("name").GetString());

        // Every kanban lane of the demo has at least one card.
        var statuses = orders.GetProperty("data").EnumerateArray()
            .Select(o => o.GetProperty("status").GetString())
            .ToHashSet();
        Assert.Superset(new HashSet<string?> { "pending", "paid", "shipped", "cancelled" }, statuses);

        // Status now reports the loader as spent.
        var status = await host.Client.GetFromJsonAsync<SetupStatusResponse>("/api/v1/setup/status");
        status = Assert.IsType<SetupStatusResponse>(status);
        Assert.Equal(1, status.SchemaVersion);
        Assert.False(status.SampleDataAvailable);
    }

    [Fact]
    public async Task SecondLoad_IsRejected_AndChangesNothing()
    {
        await using var host = await StartOpenAsync();

        using var first = await host.Client.PostAsync("/api/v1/setup/sample-data", content: null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await host.Client.PostAsync("/api/v1/setup/sample-data", content: null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var customers = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/customers");
        Assert.Equal(6, customers.GetProperty("meta").GetProperty("total").GetInt64());

        var migrations = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/migrations");
        Assert.Equal(1, migrations.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task Load_IsRejected_OnceAUserSchemaExists()
    {
        await using var host = await StartOpenAsync();

        const string ownSchema = """
            {
                "tables": [
                    {
                        "name": "notes",
                        "columns": [
                            { "name": "id", "type": "integer", "primaryKey": true },
                            { "name": "body", "type": "text" }
                        ]
                    }
                ],
                "relations": []
            }
            """;
        using var apply = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(ownSchema, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);

        using var response = await host.Client.PostAsync("/api/v1/setup/sample-data", content: null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, error.GetProperty("error").GetProperty("details").GetProperty("schemaVersion").GetInt32());

        var schema = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/schema");
        Assert.Equal("notes", schema.GetProperty("tables")[0].GetProperty("name").GetString());
        Assert.Equal(1, schema.GetProperty("tables").GetArrayLength());
    }

    [Fact]
    public async Task StandardMode_RequiresAdmin()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "sample-data-test-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });

        using var register = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new { username = "admin", password = "supersecure" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var adminToken = await LoginAsync(host, "admin", "supersecure");

        using var createViewer = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = JsonContent.Create(new { username = "viewer", password = "anotherpass", role = "viewer" })
        };
        createViewer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var createViewerResponse = await host.Client.SendAsync(createViewer);
        Assert.Equal(HttpStatusCode.Created, createViewerResponse.StatusCode);
        var viewerToken = await LoginAsync(host, "viewer", "anotherpass");

        using var asViewer = new HttpRequestMessage(HttpMethod.Post, "/api/v1/setup/sample-data");
        asViewer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var viewerResponse = await host.Client.SendAsync(asViewer);
        Assert.Equal(HttpStatusCode.Forbidden, viewerResponse.StatusCode);

        using var asAdmin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/setup/sample-data");
        asAdmin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var adminResponse = await host.Client.SendAsync(asAdmin);
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
    }

    private static Task<ApiIntegrationTestHost> StartOpenAsync()
    {
        return ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
    }

    private static async Task<string> LoginAsync(ApiIntegrationTestHost host, string username, string password)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();
        return Assert.IsType<AuthTokenResponse>(payload).AccessToken;
    }
}
