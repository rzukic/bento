using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Repere.Api.Models;

namespace Repere.Tests;

[Trait("Category", "Postgres")]
public sealed class PostgresIntegrationTests
{
    private const string DemoSchema = """
        {
            "tables": [
                {
                    "name": "customers",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false },
                        { "name": "email", "type": "text", "nullable": false, "unique": true }
                    ]
                },
                {
                    "name": "orders",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "customer_id", "type": "reference", "references": "customers", "nullable": false },
                        { "name": "total", "type": "currency", "nullable": false },
                        { "name": "status", "type": "enum", "values": ["pending", "paid"] }
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

    [PostgresFact]
    public async Task AdminHealth_ReportsPostgresRuntimeState()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret"
        });

        using var response = await host.Client.GetAsync("/api/v1/admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<AdminHealthResponse>();
        payload = Assert.IsType<AdminHealthResponse>(payload);

        Assert.Equal("healthy", payload.Status);
        Assert.Equal("connected", payload.Database.SystemPool);
        Assert.Equal("connected", payload.Database.DataPool);
        Assert.Equal("postgres", payload.Runtime.DatabaseProvider);
        Assert.True(payload.Runtime.JwtConfigured);
    }

    [PostgresFact]
    public async Task RegisterAndLogin_WorkAgainstPostgresSystemSchema()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "postgres-test-secret"
        });

        using var registerResponse = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });

        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        using var loginResponse = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var loginPayload = await loginResponse.Content.ReadFromJsonAsync<AuthTokenResponse>();
        loginPayload = Assert.IsType<AuthTokenResponse>(loginPayload);
        Assert.False(string.IsNullOrWhiteSpace(loginPayload.AccessToken));

        var storedRole = await host.QuerySystemScalarAsync<string>(
            "SELECT role FROM users WHERE username = @username;",
            new { username = "bootstrap-admin" });
        Assert.Equal("admin", storedRole);
    }

    [PostgresFact]
    public async Task AdminSchemaCrudAndRelationReads_WorkAgainstPostgresDataPool()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open"
        });

        using var schemaResponse = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(DemoSchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);

        var customerId = await CreateAsync(host, "customers", new
        {
            name = "Alice",
            email = "alice@example.com"
        });

        await CreateAsync(host, "orders", new
        {
            customer_id = customerId,
            total = 125.5,
            status = "paid"
        });

        var orderList = await host.Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/data/orders?include=customer&filter[status]=paid");
        var order = orderList.GetProperty("data")[0];
        Assert.Equal(125.5, order.GetProperty("total").GetDouble());
        Assert.Equal("Alice", order.GetProperty("customer").GetProperty("name").GetString());

        var subresource = await host.Client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/data/customers/{customerId}/customer_orders?per_page=10&page=1");
        Assert.Equal(1, subresource.GetProperty("data").GetArrayLength());
        Assert.Equal(1, subresource.GetProperty("meta").GetProperty("total").GetInt64());

        var migrations = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/migrations");
        Assert.True(migrations.GetProperty("data").GetArrayLength() >= 1);
    }

    private static async Task<long> CreateAsync(ApiIntegrationTestHost host, string table, object body)
    {
        using var response = await host.Client.PostAsJsonAsync($"/api/v1/data/{table}", body);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("data").GetProperty("id").GetInt64();
    }
}
