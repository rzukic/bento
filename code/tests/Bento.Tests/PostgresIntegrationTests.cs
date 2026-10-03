using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using Bento.Api.Models;

namespace Bento.Tests;

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
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
    public async Task AdminHealth_PublicProbe_RemainsMinimalUnderPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret"
        });

        using var response = await host.Client.GetAsync("/api/v1/admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("healthy", payload.GetProperty("status").GetString());
        Assert.Equal("connected", payload.GetProperty("database").GetProperty("system_pool").GetString());
        Assert.Equal("connected", payload.GetProperty("database").GetProperty("data_pool").GetString());
        Assert.True(payload.TryGetProperty("version", out _));
        Assert.False(payload.TryGetProperty("runtime", out _));
        Assert.False(payload.TryGetProperty("uptime", out _));
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

    [PostgresFact]
    public async Task PostgresDataRole_CannotCreateTables_ButSchemaApplyStillSucceeds()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open"
        });

        var createTableException = await Assert.ThrowsAsync<PostgresException>(() =>
            host.ExecuteDataSqlAsync("""CREATE TABLE "should_not_exist" ("id" BIGINT PRIMARY KEY);"""));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, createTableException.SqlState);

        using var schemaResponse = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(DemoSchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);

        var customerId = await CreateAsync(host, "customers", new
        {
            name = "Boundary Test",
            email = "boundary@example.com"
        });

        Assert.True(customerId > 0);
    }

    private const string UuidSchema = """
        {
            "tables": [
                {
                    "name": "devices",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "token", "type": "uuid", "nullable": false, "unique": true },
                        { "name": "name", "type": "text", "nullable": false }
                    ]
                }
            ],
            "relations": []
        }
        """;

    [PostgresFact]
    public async Task UuidColumns_InsertFilterUpdateAndUniqueCheck_WorkAgainstPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open"
        });

        using var schemaResponse = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(UuidSchema, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);

        var token = Guid.NewGuid().ToString();
        var deviceId = await CreateAsync(host, "devices", new { token, name = "Sensor A" });
        Assert.True(deviceId > 0);

        // Non-primary-key uuid values must bind as uuid (not text) on Postgres.
        var filtered = await host.Client.GetFromJsonAsync<JsonElement>($"/api/v1/data/devices?filter[token]={token}");
        Assert.Equal(1, filtered.GetProperty("data").GetArrayLength());
        Assert.Equal(token, filtered.GetProperty("data")[0].GetProperty("token").GetString());

        // The unique pre-check compares uuid = uuid and reports a conflict.
        using var duplicate = await host.Client.PostAsJsonAsync("/api/v1/data/devices", new { token, name = "Sensor B" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var newToken = Guid.NewGuid().ToString();
        using var patch = await host.Client.PatchAsJsonAsync($"/api/v1/data/devices/{deviceId}", new { token = newToken });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var patched = await patch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(newToken, patched.GetProperty("data").GetProperty("token").GetString());
    }

    [PostgresFact]
    public async Task DisablingUnique_OnColumnCreatedWithTheTable_AppliesAgainstPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open"
        });

        using var schemaResponse = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(DemoSchema, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);

        // customers.email was created with an inline UNIQUE constraint. Turning
        // unique off must drop that constraint, not a non-existent ux_ index.
        var relaxedSchema = DemoSchema.Replace("\"unique\": true", "\"unique\": false");
        using var relaxResponse = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(relaxedSchema, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, relaxResponse.StatusCode);

        var first = await CreateAsync(host, "customers", new { name = "Dup One", email = "same@example.com" });
        var second = await CreateAsync(host, "customers", new { name = "Dup Two", email = "same@example.com" });
        Assert.NotEqual(first, second);
    }

    [PostgresFact]
    public async Task Rollback_AfterApply_RestoresEmptySchemaAgainstPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open"
        });

        using var schemaResponse = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(DemoSchema, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);

        using var rollbackResponse = await host.Client.PostAsync("/api/v1/admin/migrations/rollback", null);
        Assert.Equal(HttpStatusCode.OK, rollbackResponse.StatusCode);
        var rollback = await rollbackResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(rollback.GetProperty("rolledBack").GetBoolean());

        var schema = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/schema");
        Assert.Equal(0, schema.GetProperty("version").GetInt32());
        Assert.Equal(0, schema.GetProperty("tables").GetArrayLength());

        using var gone = await host.Client.GetAsync("/api/v1/data/customers");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [PostgresFact]
    public async Task SetupStatus_ReportsPostgresProvider_AndTracksBootstrap()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "postgres-test-secret"
        });

        var fresh = await host.Client.GetFromJsonAsync<SetupStatusResponse>("/api/v1/setup/status");
        fresh = Assert.IsType<SetupStatusResponse>(fresh);
        Assert.False(fresh.Initialized);
        Assert.Equal("postgres", fresh.DatabaseProvider);
        Assert.Equal(0, fresh.SchemaVersion);

        using var register = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var initialised = await host.Client.GetFromJsonAsync<SetupStatusResponse>("/api/v1/setup/status");
        initialised = Assert.IsType<SetupStatusResponse>(initialised);
        Assert.True(initialised.Initialized);
        Assert.False(initialised.RegistrationBootstrapOpen);
    }

    [PostgresFact]
    public async Task SampleData_LoadsDemoWorkspaceAgainstPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open"
        });

        using var response = await host.Client.PostAsync("/api/v1/setup/sample-data", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summary = await response.Content.ReadFromJsonAsync<SampleDataResponse>();
        summary = Assert.IsType<SampleDataResponse>(summary);
        Assert.Equal(10, summary.RowsInserted["orders"]);

        var orders = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/orders?include=customer&filter[status]=paid");
        Assert.Equal(4, orders.GetProperty("meta").GetProperty("total").GetInt64());

        // TIMESTAMPTZ columns: filtering and patching with ISO strings must bind as timestamps, not text.
        // Fixture: 5 of the 10 demo orders were placed on or after 2026-09-20.
        var recent = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/orders?filter[placed_at][gte]=2026-09-20T00:00:00Z");
        Assert.Equal(5, recent.GetProperty("meta").GetProperty("total").GetInt64());

        var firstOrderId = orders.GetProperty("data")[0].GetProperty("id").GetInt64();
        using var patch = await host.Client.PatchAsJsonAsync($"/api/v1/data/orders/{firstOrderId}", new { placed_at = "2026-10-02T08:00:00Z" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var patched = await patch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("2026-10-02", patched.GetProperty("data").GetProperty("placed_at").GetString());

        using var second = await host.Client.PostAsync("/api/v1/setup/sample-data", content: null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task SavedViewsAndPreferences_RoundTripAgainstPostgres()
    {
        // system_views.config is JSONB and the timestamps are TIMESTAMPTZ on Postgres;
        // the hand-written statements must cast their text parameters accordingly.
        // Standard mode: views are owned by a real user row (FK on Postgres), like the
        // packaged stack after the wizard created the admin.
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "postgres-test-secret"
        });

        using var register = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        using var login = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginPayload = await login.Content.ReadFromJsonAsync<AuthTokenResponse>();
        loginPayload = Assert.IsType<AuthTokenResponse>(loginPayload);
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", loginPayload.AccessToken);

        using var sample = await host.Client.PostAsync("/api/v1/setup/sample-data", content: null);
        Assert.Equal(HttpStatusCode.OK, sample.StatusCode);

        using var create = await host.Client.PostAsJsonAsync("/api/v1/views", new
        {
            name = "Order board",
            table_name = "orders",
            type = "kanban",
            is_shared = true,
            config = new
            {
                type = "kanban",
                search = "",
                sort = Array.Empty<object>(),
                filters = Array.Empty<object>(),
                hiddenColumnNames = Array.Empty<string>(),
                columnOrder = Array.Empty<string>(),
                kanbanColumn = "status",
                cardFieldNames = new[] { "total" },
                hiddenLaneValues = Array.Empty<string>()
            }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var viewId = created.GetProperty("id").GetInt32();
        Assert.Equal("status", created.GetProperty("config").GetProperty("kanbanColumn").GetString());
        Assert.StartsWith("20", created.GetProperty("created_at").GetString());
        Assert.EndsWith("Z", created.GetProperty("created_at").GetString());

        using var update = await host.Client.PutAsJsonAsync($"/api/v1/views/{viewId}", new
        {
            name = "Order board (renamed)",
            config = new { type = "kanban", kanbanColumn = "status", cardFieldNames = new[] { "total", "placed_at" } }
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, updated.GetProperty("config").GetProperty("cardFieldNames").GetArrayLength());

        var list = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/views/orders");
        Assert.Single(list.GetProperty("data").EnumerateArray());

        using var prefs = await host.Client.PutAsJsonAsync("/api/v1/explorer/preferences/orders", new
        {
            default_view_id = viewId,
            auto_save = true
        });
        Assert.Equal(HttpStatusCode.OK, prefs.StatusCode);
        var listWithDefault = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/views/orders");
        Assert.Equal(viewId, listWithDefault.GetProperty("preferences").GetProperty("default_view_id").GetInt32());
        Assert.True(listWithDefault.GetProperty("preferences").GetProperty("auto_save").GetBoolean());
    }

    private static async Task<long> CreateAsync(ApiIntegrationTestHost host, string table, object body)
    {
        using var response = await host.Client.PostAsJsonAsync($"/api/v1/data/{table}", body);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("data").GetProperty("id").GetInt64();
    }
}
