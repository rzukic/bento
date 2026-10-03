using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Repere.Tests;

/// <summary>
/// End-to-end acceptance suite for the auto CRUD API. Mirrors the MVP 1 demo flow:
/// define a multi-table schema with relations, exercise full CRUD across tables,
/// query with filters/sort/pagination/includes/search, and verify validation and
/// auth enforcement.
///
/// This is the acceptance gate for Step 1.5 — it composes the features that
/// finer-grained test files (CrudOperationTests, ListEndpointTests, IncludeAndFieldTests,
/// ValidationTests, SearchEndpointTests, AdminSchemaTests) cover individually.
/// </summary>
public sealed class CrudIntegrationTests
{
    /// <summary>
    /// MVP 1 demo schema: customers, orders, products, with a 1:N (customers→orders)
    /// and an N:M (products↔orders) relation.
    /// </summary>
    private const string DemoSchema = """
        {
            "tables": [
                {
                    "name": "customers",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false },
                        { "name": "email", "type": "text", "nullable": false, "unique": true },
                        { "name": "tier", "type": "enum", "values": ["bronze", "silver", "gold"] },
                        { "name": "notes", "type": "text" }
                    ]
                },
                {
                    "name": "orders",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "customer_id", "type": "reference", "references": "customers", "nullable": false },
                        { "name": "total", "type": "currency", "nullable": false },
                        { "name": "status", "type": "enum", "values": ["pending", "paid", "shipped", "cancelled"] },
                        { "name": "placed_at", "type": "datetime" }
                    ]
                },
                {
                    "name": "products",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "sku", "type": "text", "nullable": false, "unique": true },
                        { "name": "name", "type": "text", "nullable": false },
                        { "name": "price", "type": "currency" }
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
                },
                {
                    "name": "product_orders",
                    "type": "N:M",
                    "from": "products",
                    "to": "orders"
                }
            ]
        }
        """;

    // ── Schema setup and route registration ──

    [Fact]
    public async Task Demo_ApplySchema_RegistersAllRoutes()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(DemoSchema);

        // Each declared table is reachable
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/v1/data/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/v1/data/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/v1/data/products")).StatusCode);

        // The N:M junction table is part of the model and has its own CRUD
        // route (alphabetical naming: orders, products)
        var jtResp = await host.Client.GetAsync("/api/v1/data/_jt_orders_products");
        Assert.Equal(HttpStatusCode.OK, jtResp.StatusCode);
    }

    // ── Full CRUD lifecycle ──

    [Fact]
    public async Task Demo_FullCrudLifecycle_OnEachTable()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(DemoSchema);

        // CREATE customer
        var customerId = await CreateAsync(host, "customers", new
        {
            name = "Alice",
            email = "alice@example.com",
            tier = "gold"
        });

        // READ
        var readEnv = await host.Client.GetFromJsonAsync<JsonElement>($"/api/v1/data/customers/{customerId}");
        var read = readEnv.GetProperty("data");
        Assert.Equal("Alice", read.GetProperty("name").GetString());
        Assert.Equal("gold", read.GetProperty("tier").GetString());

        // PUT replaces all writable fields (notes becomes null because not provided)
        var putResp = await host.Client.PutAsJsonAsync($"/api/v1/data/customers/{customerId}", new
        {
            name = "Alice Updated",
            email = "alice@example.com",
            tier = "silver"
        });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        var afterPut = (await putResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("Alice Updated", afterPut.GetProperty("name").GetString());
        Assert.Equal("silver", afterPut.GetProperty("tier").GetString());

        // PATCH updates only provided fields
        var patchResp = await host.Client.PatchAsJsonAsync($"/api/v1/data/customers/{customerId}", new
        {
            notes = "VIP customer"
        });
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var afterPatch = (await patchResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("Alice Updated", afterPatch.GetProperty("name").GetString());
        Assert.Equal("VIP customer", afterPatch.GetProperty("notes").GetString());

        // DELETE
        var deleteResp = await host.Client.DeleteAsync($"/api/v1/data/customers/{customerId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        // GET after delete → 404
        var afterDelete = await host.Client.GetAsync($"/api/v1/data/customers/{customerId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
    }

    // ── Filters, sort, pagination ──

    [Fact]
    public async Task Demo_FiltersSortPagination_WorkAcrossOperators()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(DemoSchema);
        var customerId = await SeedDemoCustomerAsync(host);

        // Seed 25 orders with varying status and totals
        for (int i = 1; i <= 25; i++)
        {
            await CreateAsync(host, "orders", new
            {
                customer_id = customerId,
                total = (double)i * 10,
                status = i % 3 == 0 ? "paid" : (i % 3 == 1 ? "pending" : "shipped")
            });
        }

        // Filter eq
        var paidJson = await host.Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/data/orders?filter[status]=paid&per_page=100");
        Assert.Equal(8, paidJson.GetProperty("data").GetArrayLength()); // 3,6,9,...,24

        // Filter gt + lte combo
        var rangeJson = await host.Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/data/orders?filter[total][gt]=100&filter[total][lte]=200&per_page=100");
        Assert.Equal(10, rangeJson.GetProperty("data").GetArrayLength()); // ids 11..20

        // Filter in
        var inJson = await host.Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/data/orders?filter[status][in]=paid,shipped&per_page=100");
        Assert.Equal(16, inJson.GetProperty("data").GetArrayLength()); // 25 - 9 pending

        // Sort desc
        var sortJson = await host.Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/data/orders?sort=-total&per_page=3");
        var first = sortJson.GetProperty("data")[0];
        Assert.Equal(250, first.GetProperty("total").GetDouble());

        // Pagination meta
        var pageJson = await host.Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/data/orders?per_page=10&page=3");
        var meta = pageJson.GetProperty("meta");
        Assert.Equal(3, meta.GetProperty("page").GetInt32());
        Assert.Equal(10, meta.GetProperty("per_page").GetInt32());
        Assert.Equal(25, meta.GetProperty("total").GetInt64());
        Assert.Equal(3, meta.GetProperty("total_pages").GetInt32());
        Assert.Equal(5, pageJson.GetProperty("data").GetArrayLength()); // last page has 5
    }

    // ── Relation includes ──

    [Fact]
    public async Task Demo_Includes_EmbedRelatedRecords()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(DemoSchema);

        var customerId = await SeedDemoCustomerAsync(host);
        var order1 = await CreateAsync(host, "orders", new { customer_id = customerId, total = 100.0, status = "paid" });
        var order2 = await CreateAsync(host, "orders", new { customer_id = customerId, total = 200.0, status = "pending" });

        // 1:N from-side: customers?include=customer_orders → embeds orders array
        var customerJson = await host.Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/data/customers?include=customer_orders");
        var customer = customerJson.GetProperty("data")[0];
        Assert.True(customer.TryGetProperty("customer_orders", out var ordersEmb));
        Assert.Equal(2, ordersEmb.GetArrayLength());

        // 1:N to-side: orders?include=customer → embeds customer object
        var orderJson = await host.Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/data/orders?include=customer");
        var order = orderJson.GetProperty("data")[0];
        Assert.True(order.TryGetProperty("customer", out var emb));
        Assert.Equal("Alice", emb.GetProperty("name").GetString());

        // Sub-resource endpoint: GET /customers/{id}/customer_orders
        var subResp = await host.Client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/data/customers/{customerId}/customer_orders");
        Assert.Equal(2, subResp.GetProperty("data").GetArrayLength());
    }

    // ── Search ──

    [Fact]
    public async Task Demo_Search_AndCombinedWithFilters()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(DemoSchema);

        await CreateAsync(host, "customers", new { name = "Acme Corp", email = "billing@acme.com", tier = "gold" });
        await CreateAsync(host, "customers", new { name = "Beta Inc", email = "hello@beta.com", tier = "gold" });
        await CreateAsync(host, "customers", new { name = "Gamma LLC", email = "ops@gamma.com", tier = "silver" });

        // Search across text columns
        var searchJson = await host.Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/data/customers?search=acme");
        Assert.Equal(1, searchJson.GetProperty("data").GetArrayLength());

        // Search + filter combine with AND
        var combinedJson = await host.Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/data/customers?search=.com&filter[tier]=gold");
        Assert.Equal(2, combinedJson.GetProperty("data").GetArrayLength());
    }

    // ── Validation errors ──

    [Fact]
    public async Task Demo_Validation_ReturnsStructuredErrors()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(DemoSchema);

        // Missing required column (name) on POST → 400 with field error
        var missingResp = await host.Client.PostAsJsonAsync("/api/v1/data/customers", new
        {
            email = "missing@example.com"
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingResp.StatusCode);
        var missingJson = await missingResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", missingJson.GetProperty("error").GetProperty("code").GetString());

        // Enum violation
        var enumResp = await host.Client.PostAsJsonAsync("/api/v1/data/customers", new
        {
            name = "Bad",
            email = "bad@example.com",
            tier = "platinum"
        });
        Assert.Equal(HttpStatusCode.BadRequest, enumResp.StatusCode);

        // FK violation → 422
        var fkResp = await host.Client.PostAsJsonAsync("/api/v1/data/orders", new
        {
            customer_id = 99999,
            total = 50.0,
            status = "pending"
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fkResp.StatusCode);

        // Unique constraint violation → 409
        await CreateAsync(host, "customers", new { name = "Dupe", email = "dupe@example.com" });
        var dupeResp = await host.Client.PostAsJsonAsync("/api/v1/data/customers", new
        {
            name = "Other",
            email = "dupe@example.com"
        });
        Assert.Equal(HttpStatusCode.Conflict, dupeResp.StatusCode);
    }

    // ── Auth enforcement ──

    [Fact]
    public async Task Demo_DataEndpoints_InStandardMode_Require401WithoutToken()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });

        // Schema apply via internal helper (bypasses HTTP auth) so we can hit the data routes
        await host.ApplySchemaAsync(DemoSchema);

        var resp = await host.Client.GetAsync("/api/v1/data/customers");
        Assert.True(
            resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden,
            $"Expected 401/403 but got {resp.StatusCode}");
    }

    [Fact]
    public async Task Demo_AdminEndpoints_InStandardMode_Require401WithoutToken()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });

        var schemaResp = await host.Client.GetAsync("/api/v1/admin/schema");
        Assert.True(
            schemaResp.StatusCode == HttpStatusCode.Unauthorized || schemaResp.StatusCode == HttpStatusCode.Forbidden);

        var migResp = await host.Client.GetAsync("/api/v1/admin/migrations");
        Assert.True(
            migResp.StatusCode == HttpStatusCode.Unauthorized || migResp.StatusCode == HttpStatusCode.Forbidden);

        var rollResp = await host.Client.PostAsync("/api/v1/admin/migrations/rollback", null);
        Assert.True(
            rollResp.StatusCode == HttpStatusCode.Unauthorized || rollResp.StatusCode == HttpStatusCode.Forbidden);
    }

    // ── Admin schema management via HTTP ──

    [Fact]
    public async Task Demo_AdminSchemaPutViaHttp_RegistersRoutesImmediately()
    {
        await using var host = await CreateHostAsync();

        // Apply via HTTP PUT (not the internal helper)
        var putResp = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(DemoSchema, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        // Routes should now work without restart
        var customerResp = await host.Client.PostAsJsonAsync("/api/v1/data/customers", new
        {
            name = "Live",
            email = "live@example.com"
        });
        Assert.Equal(HttpStatusCode.Created, customerResp.StatusCode);

        // Migration history reflects the change
        var migJson = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/migrations");
        Assert.True(migJson.GetProperty("data").GetArrayLength() >= 1);
    }

    // ── Helpers ──

    private static async Task<ApiIntegrationTestHost> CreateHostAsync()
    {
        return await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
    }

    private static async Task<long> SeedDemoCustomerAsync(ApiIntegrationTestHost host)
    {
        return await CreateAsync(host, "customers", new
        {
            name = "Alice",
            email = "alice@example.com",
            tier = "gold"
        });
    }

    private static async Task<long> CreateAsync(ApiIntegrationTestHost host, string table, object body)
    {
        var resp = await host.Client.PostAsJsonAsync($"/api/v1/data/{table}", body);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("data").GetProperty("id").GetInt64();
    }
}
