using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bento.Api.Models;

namespace Bento.Tests;

public sealed class DataRouteTests
{
    private const string TestSchema = """
        {
            "tables": [
                {
                    "name": "customers",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false },
                        { "name": "email", "type": "text" }
                    ]
                },
                {
                    "name": "orders",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "total", "type": "currency" },
                        { "name": "customer_id", "type": "reference", "references": "customers" }
                    ]
                }
            ],
            "relations": [
                {
                    "name": "customer_orders",
                    "type": "1:N",
                    "from": "customers",
                    "to": "orders",
                    "column": "customer_id"
                }
            ]
        }
        """;

    [Fact]
    public async Task Get_DataTable_ResolvesForExistingTable_InOpenMode()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var response = await host.Client.GetAsync("/api/v1/data/customers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("data", out _));
        Assert.True(json.TryGetProperty("meta", out _));
    }

    [Fact]
    public async Task Get_DataTable_ResolvesForSecondTable_InOpenMode()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var response = await host.Client.GetAsync("/api/v1/data/orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_DataTable_Returns404_ForNonExistentTable()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var response = await host.Client.GetAsync("/api/v1/data/nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();
        payload = Assert.IsType<ApiErrorEnvelope>(payload);

        Assert.Equal("NOT_FOUND", payload.Error.Code);
        Assert.Contains("nonexistent", payload.Error.Message, StringComparison.Ordinal);
        Assert.NotNull(payload.Error.Details);

        var availableTables = Assert.IsType<JsonElement>(payload.Error.Details!["availableTables"]);
        Assert.Equal(JsonValueKind.Array, availableTables.ValueKind);
        Assert.Contains("customers", availableTables.EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("orders", availableTables.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Get_DataTableById_Returns404_ForNonExistentTable()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var response = await host.Client.GetAsync("/api/v1/data/nonexistent/1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();
        payload = Assert.IsType<ApiErrorEnvelope>(payload);
        Assert.Equal("NOT_FOUND", payload.Error.Code);
    }

    [Fact]
    public async Task Post_DataTable_Returns404_ForNonExistentTable()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var response = await host.Client.PostAsJsonAsync("/api/v1/data/nonexistent", new { name = "test" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_DataTable_Returns404_ForNonExistentTable()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var response = await host.Client.PutAsJsonAsync("/api/v1/data/nonexistent/1", new { name = "test" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_DataTable_Returns404_ForNonExistentTable()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/data/nonexistent/1")
        {
            Content = JsonContent.Create(new { name = "test" })
        };

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_DataTable_Returns404_ForNonExistentTable()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var response = await host.Client.DeleteAsync("/api/v1/data/nonexistent/1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_DataTable_RequiresAuth_InStandardMode()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });

        using var response = await host.Client.GetAsync("/api/v1/data/customers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();
        payload = Assert.IsType<ApiErrorEnvelope>(payload);
        Assert.Equal("UNAUTHORIZED", payload.Error.Code);
    }

    [Fact]
    public async Task Get_DataTable_Returns404_WhenNoSchemaIsLoaded()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });

        using var response = await host.Client.GetAsync("/api/v1/data/customers");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();
        payload = Assert.IsType<ApiErrorEnvelope>(payload);
        Assert.Equal("NOT_FOUND", payload.Error.Code);
    }

    [Fact]
    public async Task All_HttpMethods_ResolveForExistingTable()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var getList = await host.Client.GetAsync("/api/v1/data/customers");
        Assert.Equal(HttpStatusCode.OK, getList.StatusCode);

        // Create a row first so subsequent operations have something to work with
        using var post = await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "test" });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        using var getById = await host.Client.GetAsync("/api/v1/data/customers/1");
        Assert.Equal(HttpStatusCode.OK, getById.StatusCode);

        using var put = await host.Client.PutAsJsonAsync("/api/v1/data/customers/1", new { name = "updated" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var patchRequest = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/data/customers/1")
        {
            Content = JsonContent.Create(new { name = "patched" })
        };
        using var patch = await host.Client.SendAsync(patchRequest);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var delete = await host.Client.DeleteAsync("/api/v1/data/customers/1");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Get_DataTable_IsCaseInsensitive_ForTableName()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var lower = await host.Client.GetAsync("/api/v1/data/customers");
        using var upper = await host.Client.GetAsync("/api/v1/data/CUSTOMERS");
        using var mixed = await host.Client.GetAsync("/api/v1/data/Customers");

        Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
        Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mixed.StatusCode);
    }

    private static async Task<ApiIntegrationTestHost> CreateHostWithSchemaAsync()
    {
        var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });

        // Apply a test schema through the schema service so the schema cache picks it up
        await host.ApplySchemaAsync(TestSchema);

        return host;
    }
}
