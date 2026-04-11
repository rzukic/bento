using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Repere.Tests;

public sealed class IncludeAndFieldTests
{
    /// <summary>
    /// Schema with customers → orders (1:N) and products ←→ tags (N:M).
    /// </summary>
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
                        { "name": "status", "type": "text" },
                        { "name": "customer_id", "type": "reference", "references": "customers" }
                    ]
                },
                {
                    "name": "products",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false },
                        { "name": "price", "type": "float" }
                    ]
                },
                {
                    "name": "tags",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "label", "type": "text", "nullable": false }
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
                    "name": "product_tags",
                    "type": "N:M",
                    "from": "products",
                    "to": "tags"
                }
            ]
        }
        """;

    // ── Sparse fieldsets ──

    [Fact]
    public async Task Fields_ReturnsOnlyRequestedColumns()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Acme", email = "acme@test.com" });

        using var response = await host.Client.GetAsync("/api/v1/data/customers?fields=id,name");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = json.GetProperty("data")[0];

        Assert.True(row.TryGetProperty("id", out _));
        Assert.True(row.TryGetProperty("name", out _));
        Assert.False(row.TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Fields_InvalidColumnName_Returns400()
    {
        await using var host = await CreateHostAsync();

        using var response = await host.Client.GetAsync("/api/v1/data/customers?fields=id,nonexistent");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── 1:N includes ──

    [Fact]
    public async Task Include_OneToMany_FromSide_EmbedsChildArray()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Acme" });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 100.0, status = "paid", customer_id = 1 });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 200.0, status = "pending", customer_id = 1 });

        // customer_orders is the fromAccessor on customers
        using var response = await host.Client.GetAsync("/api/v1/data/customers?include=customer_orders");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var customer = json.GetProperty("data")[0];

        Assert.Equal("Acme", customer.GetProperty("name").GetString());
        var orders = customer.GetProperty("customer_orders");
        Assert.Equal(JsonValueKind.Array, orders.ValueKind);
        Assert.Equal(2, orders.GetArrayLength());
    }

    [Fact]
    public async Task Include_OneToMany_ToSide_EmbedsSingleParent()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Acme" });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 100.0, status = "paid", customer_id = 1 });

        // "customer" is the toAccessor on orders (derived from customer_id → customer)
        using var response = await host.Client.GetAsync("/api/v1/data/orders?include=customer");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var order = json.GetProperty("data")[0];

        var customer = order.GetProperty("customer");
        Assert.Equal(JsonValueKind.Object, customer.ValueKind);
        Assert.Equal("Acme", customer.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Include_OneToMany_MultipleParents_GroupsCorrectly()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Acme" });
        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Globex" });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 100.0, status = "paid", customer_id = 1 });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 200.0, status = "pending", customer_id = 1 });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 50.0, status = "new", customer_id = 2 });

        using var response = await host.Client.GetAsync("/api/v1/data/customers?include=customer_orders&sort=id");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        // Acme should have 2 orders
        Assert.Equal(2, data[0].GetProperty("customer_orders").GetArrayLength());
        // Globex should have 1 order
        Assert.Equal(1, data[1].GetProperty("customer_orders").GetArrayLength());
    }

    // ── N:M includes ──

    [Fact]
    public async Task Include_ManyToMany_FromSide_EmbedsRelatedArray()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Widget", price = 10.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/tags", new { label = "sale" });
        await host.Client.PostAsJsonAsync("/api/v1/data/tags", new { label = "new" });

        // Insert junction rows directly via SQL (junction tables aren't exposed via CRUD)
        await host.ExecuteDataSqlAsync("INSERT INTO \"_jt_products_tags\" (\"product_id\", \"tag_id\") VALUES (1, 1)");
        await host.ExecuteDataSqlAsync("INSERT INTO \"_jt_products_tags\" (\"product_id\", \"tag_id\") VALUES (1, 2)");

        // "tags" is the fromAccessor on products for the N:M relation
        using var response = await host.Client.GetAsync("/api/v1/data/products?include=tags");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var product = json.GetProperty("data")[0];

        var tags = product.GetProperty("tags");
        Assert.Equal(JsonValueKind.Array, tags.ValueKind);
        Assert.Equal(2, tags.GetArrayLength());
    }

    [Fact]
    public async Task Include_ManyToMany_ToSide_EmbedsRelatedArray()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Widget", price = 10.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Gadget", price = 20.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/tags", new { label = "sale" });

        await host.ExecuteDataSqlAsync("INSERT INTO \"_jt_products_tags\" (\"product_id\", \"tag_id\") VALUES (1, 1)");
        await host.ExecuteDataSqlAsync("INSERT INTO \"_jt_products_tags\" (\"product_id\", \"tag_id\") VALUES (2, 1)");

        // "products" is the toAccessor on tags
        using var response = await host.Client.GetAsync("/api/v1/data/tags?include=products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tag = json.GetProperty("data")[0];

        var products = tag.GetProperty("products");
        Assert.Equal(2, products.GetArrayLength());
    }

    // ── Dot-notation nesting ──

    [Fact]
    public async Task Include_DotNotation_NestsRelations()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Acme" });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 100.0, status = "paid", customer_id = 1 });

        // include=customer.customer_orders → on orders: embed customer, and on that customer embed their orders
        using var response = await host.Client.GetAsync("/api/v1/data/orders?include=customer.customer_orders");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var order = json.GetProperty("data")[0];

        var customer = order.GetProperty("customer");
        Assert.Equal("Acme", customer.GetProperty("name").GetString());

        var nestedOrders = customer.GetProperty("customer_orders");
        Assert.Equal(JsonValueKind.Array, nestedOrders.ValueKind);
        Assert.True(nestedOrders.GetArrayLength() >= 1);
    }

    // ── Invalid include ──

    [Fact]
    public async Task Include_InvalidAccessor_Returns400()
    {
        await using var host = await CreateHostAsync();

        using var response = await host.Client.GetAsync("/api/v1/data/customers?include=nonexistent");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var error = json.GetProperty("error");
        Assert.Equal("VALIDATION_FAILED", error.GetProperty("code").GetString());
        Assert.Contains("nonexistent", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // ── Fields + Include combined ──

    [Fact]
    public async Task Fields_And_Include_CanBeCombined()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Acme", email = "acme@test.com" });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 100.0, status = "paid", customer_id = 1 });

        using var response = await host.Client.GetAsync("/api/v1/data/customers?fields=id,name&include=customer_orders");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var customer = json.GetProperty("data")[0];

        // Only id and name in parent fields
        Assert.True(customer.TryGetProperty("id", out _));
        Assert.True(customer.TryGetProperty("name", out _));
        // Include still present
        Assert.True(customer.TryGetProperty("customer_orders", out var orders));
        Assert.Equal(1, orders.GetArrayLength());
    }

    [Fact]
    public async Task Fields_And_FromSideInclude_WorkWithoutIdInParentFieldset()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Acme", email = "acme@test.com" });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 100.0, status = "paid", customer_id = 1 });

        using var response = await host.Client.GetAsync("/api/v1/data/customers?fields=name&include=customer_orders");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var customer = json.GetProperty("data")[0];

        Assert.True(customer.TryGetProperty("name", out _));
        Assert.False(customer.TryGetProperty("id", out _));
        Assert.True(customer.TryGetProperty("customer_orders", out var orders));
        Assert.Equal(1, orders.GetArrayLength());
    }

    [Fact]
    public async Task Fields_And_ToSideInclude_WorkWithoutForeignKeyInParentFieldset()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Acme" });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 100.0, status = "paid", customer_id = 1 });

        using var response = await host.Client.GetAsync("/api/v1/data/orders?fields=id,total&include=customer");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var order = json.GetProperty("data")[0];

        Assert.True(order.TryGetProperty("id", out _));
        Assert.True(order.TryGetProperty("total", out _));
        Assert.False(order.TryGetProperty("customer_id", out _));
        Assert.True(order.TryGetProperty("customer", out var customer));
        Assert.Equal("Acme", customer.GetProperty("name").GetString());
    }

    // ── Sub-resource endpoint ──

    [Fact]
    public async Task SubResource_OneToMany_ReturnsRelatedRows()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Acme" });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 100.0, status = "paid", customer_id = 1 });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 200.0, status = "pending", customer_id = 1 });

        using var response = await host.Client.GetAsync("/api/v1/data/customers/1/customer_orders");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.Equal(2, data.GetArrayLength());
    }

    [Fact]
    public async Task SubResource_ToSide_ReturnsSingleParent()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Acme" });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 100.0, status = "paid", customer_id = 1 });

        using var response = await host.Client.GetAsync("/api/v1/data/orders/1/customer");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("Acme", data[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task SubResource_InvalidRelation_Returns404()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Acme" });

        using var response = await host.Client.GetAsync("/api/v1/data/customers/1/nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SubResource_ManyToMany_ReturnsRelatedRows()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Widget", price = 10.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/tags", new { label = "sale" });
        await host.Client.PostAsJsonAsync("/api/v1/data/tags", new { label = "new" });

        await host.ExecuteDataSqlAsync("INSERT INTO \"_jt_products_tags\" (\"product_id\", \"tag_id\") VALUES (1, 1)");
        await host.ExecuteDataSqlAsync("INSERT INTO \"_jt_products_tags\" (\"product_id\", \"tag_id\") VALUES (1, 2)");

        using var response = await host.Client.GetAsync("/api/v1/data/products/1/tags");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task SubResource_AppliesFiltersAndSorts()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Acme" });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 100.0, status = "paid", customer_id = 1 });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 200.0, status = "pending", customer_id = 1 });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 150.0, status = "paid", customer_id = 1 });

        using var response = await host.Client.GetAsync("/api/v1/data/customers/1/customer_orders?filter[status]=paid&sort=-total");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.Equal(2, data.GetArrayLength());
        Assert.Equal(150.0, data[0].GetProperty("total").GetDouble());
        Assert.Equal(100.0, data[1].GetProperty("total").GetDouble());
    }

    [Fact]
    public async Task SubResource_ReturnsAccuratePaginationMetadata()
    {
        await using var host = await CreateHostAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Acme" });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 100.0, status = "paid", customer_id = 1 });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 200.0, status = "pending", customer_id = 1 });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { total = 300.0, status = "new", customer_id = 1 });

        using var response = await host.Client.GetAsync("/api/v1/data/customers/1/customer_orders?sort=id&page=2&per_page=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        var meta = json.GetProperty("meta");

        Assert.Single(data.EnumerateArray());
        Assert.Equal(2, meta.GetProperty("page").GetInt32());
        Assert.Equal(1, meta.GetProperty("per_page").GetInt32());
        Assert.Equal(3, meta.GetProperty("total").GetInt64());
        Assert.Equal(3, meta.GetProperty("total_pages").GetInt32());
        Assert.Equal(2, data[0].GetProperty("id").GetInt32());
    }

    private static async Task<ApiIntegrationTestHost> CreateHostAsync()
    {
        var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });

        await host.ApplySchemaAsync(TestSchema);
        return host;
    }
}
