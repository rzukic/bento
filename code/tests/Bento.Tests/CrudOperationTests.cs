using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bento.Api.Models;

namespace Bento.Tests;

public sealed class CrudOperationTests
{
    private const string IdOnlySchema = """
        {
            "tables": [
                {
                    "name": "tokens",
                    "columns": []
                }
            ],
            "relations": []
        }
        """;

    private const string TestSchema = """
        {
            "tables": [
                {
                    "name": "customers",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false },
                        { "name": "email", "type": "text" },
                        { "name": "active", "type": "boolean" },
                        { "name": "score", "type": "float" }
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

    private const string UuidSchema = """
        {
            "tables": [
                {
                    "name": "accounts",
                    "columns": [
                        { "name": "id", "type": "uuid", "primaryKey": true, "nullable": false },
                        { "name": "name", "type": "text", "nullable": false }
                    ]
                }
            ],
            "relations": []
        }
        """;

    private const string VisibleJunctionSchema = """
        {
            "tables": [
                {
                    "name": "customers",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false }
                    ]
                },
                {
                    "name": "orders",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "status", "type": "text" },
                        { "name": "customer_id", "type": "reference", "references": "customers" }
                    ]
                },
                {
                    "name": "_jt_customers_orders",
                    "columns": [
                        { "name": "customer_id", "type": "reference", "references": "customers", "primaryKey": true },
                        { "name": "order_id", "type": "reference", "references": "orders", "primaryKey": true }
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
                    "name": "customer_order_links",
                    "type": "N:M",
                    "from": "customers",
                    "to": "orders",
                    "fromColumn": "customer_id",
                    "toColumn": "order_id",
                    "junctionTable": "_jt_customers_orders"
                }
            ]
        }
        """;

    private const string UuidVisibleJunctionSchema = """
        {
            "tables": [
                {
                    "name": "customers",
                    "columns": [
                        { "name": "id", "type": "uuid", "primaryKey": true, "nullable": false },
                        { "name": "name", "type": "text", "nullable": false }
                    ]
                },
                {
                    "name": "orders",
                    "columns": [
                        { "name": "id", "type": "uuid", "primaryKey": true, "nullable": false },
                        { "name": "status", "type": "text" }
                    ]
                },
                {
                    "name": "customer_order_links",
                    "columns": [
                        { "name": "customer_id", "type": "reference", "references": "customers", "primaryKey": true, "nullable": false },
                        { "name": "order_id", "type": "reference", "references": "orders", "primaryKey": true, "nullable": false },
                        { "name": "role", "type": "text" }
                    ]
                }
            ],
            "relations": [
                {
                    "name": "customer_orders",
                    "type": "N:M",
                    "from": "customers",
                    "to": "orders",
                    "fromColumn": "customer_id",
                    "toColumn": "order_id",
                    "junctionTable": "customer_order_links",
                    "fromAccessor": "orders",
                    "toAccessor": "customers"
                }
            ]
        }
        """;

    [Fact]
    public async Task Post_Create_ReturnsCreatedRowWithGeneratedId()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var response = await host.Client.PostAsJsonAsync("/api/v1/data/customers", new
        {
            name = "Acme Corp",
            email = "acme@example.com",
            active = true,
            score = 9.5
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        Assert.Equal(1, data.GetProperty("id").GetInt64());
        Assert.Equal("Acme Corp", data.GetProperty("name").GetString());
        Assert.Equal("acme@example.com", data.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Post_Create_AutoIncrementsId()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "First" });
        using var response = await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Second" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("data").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Post_Create_AutoGeneratesUuidPrimaryKey()
    {
        await using var host = await CreateHostAsync(UuidSchema);

        using var response = await host.Client.PostAsJsonAsync("/api/v1/data/accounts", new { name = "Acme" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = json.GetProperty("data").GetProperty("id").GetString();

        Assert.True(Guid.TryParse(id, out _));

        using var getResponse = await host.Client.GetAsync($"/api/v1/data/accounts/{id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Post_Create_SucceedsForIdOnlyTable()
    {
        await using var host = await CreateHostAsync(IdOnlySchema);

        using var response = await host.Client.PostAsJsonAsync("/api/v1/data/tokens", new { });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("data").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Get_ById_ReturnsCorrectRow()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new
        {
            name = "Acme Corp",
            email = "acme@example.com"
        });

        using var response = await host.Client.GetAsync("/api/v1/data/customers/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        Assert.Equal(1, data.GetProperty("id").GetInt64());
        Assert.Equal("Acme Corp", data.GetProperty("name").GetString());
        Assert.Equal("acme@example.com", data.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Get_ById_Returns404_ForNonExistentRow()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var response = await host.Client.GetAsync("/api/v1/data/customers/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();
        payload = Assert.IsType<ApiErrorEnvelope>(payload);
        Assert.Equal("NOT_FOUND", payload.Error.Code);
        Assert.Contains("999", payload.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Put_Update_ReplacesAllWritableColumns()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new
        {
            name = "Original",
            email = "original@example.com",
            active = true,
            score = 5.0
        });

        // PUT with only name — email, active, score should become null
        using var response = await host.Client.PutAsJsonAsync("/api/v1/data/customers/1", new
        {
            name = "Updated"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        Assert.Equal("Updated", data.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("email").ValueKind);
    }

    [Fact]
    public async Task Put_Update_Returns404_ForNonExistentRow()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var response = await host.Client.PutAsJsonAsync("/api/v1/data/customers/999", new
        {
            name = "Ghost"
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_PartialUpdate_OnlyChangesProvidedColumns()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new
        {
            name = "Original",
            email = "original@example.com"
        });

        using var patchRequest = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/data/customers/1")
        {
            Content = JsonContent.Create(new { name = "Patched" })
        };
        using var response = await host.Client.SendAsync(patchRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        Assert.Equal("Patched", data.GetProperty("name").GetString());
        Assert.Equal("original@example.com", data.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Patch_Returns404_ForNonExistentRow()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/data/customers/999")
        {
            Content = JsonContent.Create(new { name = "Ghost" })
        };
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesRow_AndReturns204()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "ToDelete" });

        using var deleteResponse = await host.Client.DeleteAsync("/api/v1/data/customers/1");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Verify the row is gone
        using var getResponse = await host.Client.GetAsync("/api/v1/data/customers/1");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_Returns404_ForNonExistentRow()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var response = await host.Client.DeleteAsync("/api/v1/data/customers/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithForeignKey_StoresReference()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Customer A" });

        using var response = await host.Client.PostAsJsonAsync("/api/v1/data/orders", new
        {
            total = 250.50,
            status = "paid",
            customer_id = 1
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        Assert.Equal(1, data.GetProperty("customer_id").GetInt64());
        Assert.Equal("paid", data.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Create_WithVisibleJunctionTable_AcceptsPrimaryKeyReferenceColumns()
    {
        await using var host = await CreateHostAsync(VisibleJunctionSchema);

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Customer A" });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new
        {
            status = "paid",
            customer_id = 1
        });

        using var response = await host.Client.PostAsJsonAsync("/api/v1/data/_jt_customers_orders", new
        {
            customer_id = 1,
            order_id = 1
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.Equal(1, data.GetProperty("customer_id").GetInt64());
        Assert.Equal(1, data.GetProperty("order_id").GetInt64());

        using var getResponse = await host.Client.GetAsync("/api/v1/data/_jt_customers_orders/customer_id:1|order_id:1");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var getJson = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var fetched = getJson.GetProperty("data");
        Assert.Equal(1, fetched.GetProperty("customer_id").GetInt64());
        Assert.Equal(1, fetched.GetProperty("order_id").GetInt64());
    }

    [Fact]
    public async Task VisibleJunctionTable_WithUuidReferenceCompositeKey_SupportsCrudRoutes()
    {
        await using var host = await CreateHostAsync(UuidVisibleJunctionSchema);

        using var customerResponse = await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Customer A" });
        Assert.Equal(HttpStatusCode.Created, customerResponse.StatusCode);
        var customerJson = await customerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var customerId = customerJson.GetProperty("data").GetProperty("id").GetString()!;

        using var orderResponse = await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { status = "paid" });
        Assert.Equal(HttpStatusCode.Created, orderResponse.StatusCode);
        var orderJson = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderJson.GetProperty("data").GetProperty("id").GetString()!;

        using var createResponse = await host.Client.PostAsJsonAsync("/api/v1/data/customer_order_links", new
        {
            customer_id = customerId,
            order_id = orderId,
            role = "viewer"
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var compositeId = Uri.EscapeDataString($"customer_id:{customerId}|order_id:{orderId}");
        var rowPath = $"/api/v1/data/customer_order_links/{compositeId}";

        using var getResponse = await host.Client.GetAsync(rowPath);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getJson = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("viewer", getJson.GetProperty("data").GetProperty("role").GetString());

        using var updateResponse = await host.Client.PutAsJsonAsync(rowPath, new
        {
            customer_id = customerId,
            order_id = orderId,
            role = "owner"
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updateJson = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("owner", updateJson.GetProperty("data").GetProperty("role").GetString());

        using var patchResponse = await host.Client.PatchAsJsonAsync(rowPath, new { role = "approver" });
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        var patchJson = await patchResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("approver", patchJson.GetProperty("data").GetProperty("role").GetString());

        using var relatedResponse = await host.Client.GetAsync($"/api/v1/data/customers/{Uri.EscapeDataString(customerId)}/orders");
        Assert.Equal(HttpStatusCode.OK, relatedResponse.StatusCode);
        var relatedJson = await relatedResponse.Content.ReadFromJsonAsync<JsonElement>();
        var relatedRows = relatedJson.GetProperty("data");
        Assert.Equal(1, relatedRows.GetArrayLength());
        Assert.Equal(orderId, relatedRows[0].GetProperty("id").GetString());

        var invalidCompositeId = Uri.EscapeDataString($"customer_id:not-a-uuid|order_id:{orderId}");
        using var invalidResponse = await host.Client.GetAsync($"/api/v1/data/customer_order_links/{invalidCompositeId}");
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        using var deleteResponse = await host.Client.DeleteAsync(rowPath);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var deletedResponse = await host.Client.GetAsync(rowPath);
        Assert.Equal(HttpStatusCode.NotFound, deletedResponse.StatusCode);
    }

    [Fact]
    public async Task Patch_JunctionTable_UpdatesViaCompositeKey()
    {
        // Use junction schema with an extra "quantity" column
        const string schema = """
            {
                "tables": [
                    {
                        "name": "products",
                        "columns": [
                            { "name": "id", "type": "integer", "primaryKey": true },
                            { "name": "name", "type": "text", "nullable": false }
                        ]
                    },
                    {
                        "name": "orders",
                        "columns": [
                            { "name": "id", "type": "integer", "primaryKey": true },
                            { "name": "status", "type": "text" }
                        ]
                    },
                    {
                        "name": "_jt_orders_products",
                        "columns": [
                            { "name": "product_id", "type": "reference", "references": "products", "primaryKey": true },
                            { "name": "order_id", "type": "reference", "references": "orders", "primaryKey": true },
                            { "name": "quantity", "type": "integer" }
                        ]
                    }
                ],
                "relations": []
            }
            """;

        await using var host = await CreateHostAsync(schema);

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Widget" });
        await host.Client.PostAsJsonAsync("/api/v1/data/orders", new { status = "pending" });

        // Create a junction row
        using var createResponse = await host.Client.PostAsJsonAsync("/api/v1/data/_jt_orders_products", new
        {
            product_id = 1,
            order_id = 1,
            quantity = 5
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // PATCH quantity via composite key
        using var patchResponse = await host.Client.PatchAsJsonAsync(
            "/api/v1/data/_jt_orders_products/product_id:1|order_id:1",
            new { quantity = 10 });
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        var patchJson = await patchResponse.Content.ReadFromJsonAsync<JsonElement>();
        var patched = patchJson.GetProperty("data");
        Assert.Equal(10, patched.GetProperty("quantity").GetInt64());

        // PATCH a FK/PK column
        using var patchFkResponse = await host.Client.PatchAsJsonAsync(
            "/api/v1/data/_jt_orders_products/product_id:1|order_id:1",
            new { product_id = 1 });
        Assert.Equal(HttpStatusCode.OK, patchFkResponse.StatusCode);
    }

    [Fact]
    public async Task FullCrudLifecycle_AcrossTwoTables()
    {
        await using var host = await CreateHostWithSchemaAsync();

        // Create customers
        using var c1 = await host.Client.PostAsJsonAsync("/api/v1/data/customers", new
        {
            name = "Acme Corp",
            email = "acme@example.com"
        });
        Assert.Equal(HttpStatusCode.Created, c1.StatusCode);

        using var c2 = await host.Client.PostAsJsonAsync("/api/v1/data/customers", new
        {
            name = "Globex Inc",
            email = "globex@example.com"
        });
        Assert.Equal(HttpStatusCode.Created, c2.StatusCode);

        // Create orders referencing customers
        using var o1 = await host.Client.PostAsJsonAsync("/api/v1/data/orders", new
        {
            total = 100.00,
            status = "pending",
            customer_id = 1
        });
        Assert.Equal(HttpStatusCode.Created, o1.StatusCode);

        using var o2 = await host.Client.PostAsJsonAsync("/api/v1/data/orders", new
        {
            total = 250.00,
            status = "paid",
            customer_id = 2
        });
        Assert.Equal(HttpStatusCode.Created, o2.StatusCode);

        // Read back
        using var getCustomer = await host.Client.GetAsync("/api/v1/data/customers/1");
        Assert.Equal(HttpStatusCode.OK, getCustomer.StatusCode);
        var customerJson = await getCustomer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Acme Corp", customerJson.GetProperty("data").GetProperty("name").GetString());

        using var getOrder = await host.Client.GetAsync("/api/v1/data/orders/2");
        Assert.Equal(HttpStatusCode.OK, getOrder.StatusCode);
        var orderJson = await getOrder.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, orderJson.GetProperty("data").GetProperty("customer_id").GetInt64());

        // Patch the order status
        using var patchRequest = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/data/orders/1")
        {
            Content = JsonContent.Create(new { status = "shipped" })
        };
        using var patchResponse = await host.Client.SendAsync(patchRequest);
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        var patchedJson = await patchResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("shipped", patchedJson.GetProperty("data").GetProperty("status").GetString());
        // total should be unchanged
        Assert.Equal(100.0, patchedJson.GetProperty("data").GetProperty("total").GetDouble());

        // Delete order 2 first (FK constraint), then customer 2
        using var deleteOrder = await host.Client.DeleteAsync("/api/v1/data/orders/2");
        Assert.Equal(HttpStatusCode.NoContent, deleteOrder.StatusCode);

        using var deleteCustomer = await host.Client.DeleteAsync("/api/v1/data/customers/2");
        Assert.Equal(HttpStatusCode.NoContent, deleteCustomer.StatusCode);

        using var verifyDeleted = await host.Client.GetAsync("/api/v1/data/customers/2");
        Assert.Equal(HttpStatusCode.NotFound, verifyDeleted.StatusCode);
    }

    [Fact]
    public async Task Create_CoercesStringNumberToInteger()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/customers", new { name = "Parent" });

        // Send customer_id as a string "1" — should be coerced to integer
        using var response = await host.Client.PostAsJsonAsync("/api/v1/data/orders", new
        {
            total = 50,
            status = "new",
            customer_id = "1"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("data").GetProperty("customer_id").GetInt64());
    }

    private static async Task<ApiIntegrationTestHost> CreateHostWithSchemaAsync()
    {
        return await CreateHostAsync(TestSchema);
    }

    private static async Task<ApiIntegrationTestHost> CreateHostAsync(string schema)
    {
        var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });

        await host.ApplySchemaAsync(schema);
        return host;
    }
}
