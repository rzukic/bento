using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

public sealed class ListEndpointTests
{
    private const string TestSchema = """
        {
            "tables": [
                {
                    "name": "products",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false },
                        { "name": "category", "type": "text" },
                        { "name": "price", "type": "float" },
                        { "name": "in_stock", "type": "boolean" },
                        { "name": "notes", "type": "text" }
                    ]
                }
            ],
            "relations": []
        }
        """;

    [Fact]
    public async Task List_EmptyTable_ReturnsEmptyDataWithMeta()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var response = await host.Client.GetAsync("/api/v1/data/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        var meta = json.GetProperty("meta");

        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Equal(0, data.GetArrayLength());
        Assert.Equal(1, meta.GetProperty("page").GetInt32());
        Assert.Equal(25, meta.GetProperty("per_page").GetInt32());
        Assert.Equal(0, meta.GetProperty("total").GetInt64());
        Assert.Equal(0, meta.GetProperty("total_pages").GetInt32());
    }

    [Fact]
    public async Task List_DefaultPagination_Returns25PerPage()
    {
        await using var host = await CreateHostWithSchemaAsync();
        await SeedProducts(host, 30);

        using var response = await host.Client.GetAsync("/api/v1/data/products");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        var meta = json.GetProperty("meta");

        Assert.Equal(25, data.GetArrayLength());
        Assert.Equal(1, meta.GetProperty("page").GetInt32());
        Assert.Equal(25, meta.GetProperty("per_page").GetInt32());
        Assert.Equal(30, meta.GetProperty("total").GetInt64());
        Assert.Equal(2, meta.GetProperty("total_pages").GetInt32());
    }

    [Fact]
    public async Task List_Page2_ReturnsRemainingRows()
    {
        await using var host = await CreateHostWithSchemaAsync();
        await SeedProducts(host, 30);

        using var response = await host.Client.GetAsync("/api/v1/data/products?page=2");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        var meta = json.GetProperty("meta");

        Assert.Equal(5, data.GetArrayLength());
        Assert.Equal(2, meta.GetProperty("page").GetInt32());
        Assert.Equal(30, meta.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task List_CustomPerPage_RespectsLimit()
    {
        await using var host = await CreateHostWithSchemaAsync();
        await SeedProducts(host, 10);

        using var response = await host.Client.GetAsync("/api/v1/data/products?per_page=3");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        var meta = json.GetProperty("meta");

        Assert.Equal(3, data.GetArrayLength());
        Assert.Equal(3, meta.GetProperty("per_page").GetInt32());
        Assert.Equal(10, meta.GetProperty("total").GetInt64());
        Assert.Equal(4, meta.GetProperty("total_pages").GetInt32()); // ceil(10/3) = 4
    }

    [Fact]
    public async Task List_PerPageCappedAt1000()
    {
        await using var host = await CreateHostWithSchemaAsync();
        await SeedProducts(host, 3);

        using var response = await host.Client.GetAsync("/api/v1/data/products?per_page=5000");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var meta = json.GetProperty("meta");

        Assert.Equal(1000, meta.GetProperty("per_page").GetInt32());
    }

    [Fact]
    public async Task List_FilterExactMatch_FiltersRows()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Widget", category = "tools", price = 10.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Gadget", category = "electronics", price = 25.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Wrench", category = "tools", price = 15.0 });

        using var response = await host.Client.GetAsync("/api/v1/data/products?filter[category]=tools");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        var meta = json.GetProperty("meta");

        Assert.Equal(2, data.GetArrayLength());
        Assert.Equal(2, meta.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task List_FilterEqOperator_SameAsExactMatch()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Widget", category = "tools" });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Gadget", category = "electronics" });

        using var response = await host.Client.GetAsync("/api/v1/data/products?filter[category][eq]=tools");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("Widget", data[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task List_FilterNeq_ExcludesMatchingRows()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Widget", category = "tools" });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Gadget", category = "electronics" });

        using var response = await host.Client.GetAsync("/api/v1/data/products?filter[category][neq]=tools");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("Gadget", data[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task List_FilterGtAndLt_NumericComparison()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Cheap", price = 5.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Mid", price = 15.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Expensive", price = 50.0 });

        using var gtResponse = await host.Client.GetAsync("/api/v1/data/products?filter[price][gt]=10");
        var gtJson = await gtResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, gtJson.GetProperty("data").GetArrayLength());

        using var ltResponse = await host.Client.GetAsync("/api/v1/data/products?filter[price][lt]=20");
        var ltJson = await ltResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, ltJson.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task List_FilterGteAndLte_IncludesBoundary()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "A", price = 10.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "B", price = 20.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "C", price = 30.0 });

        using var gteResponse = await host.Client.GetAsync("/api/v1/data/products?filter[price][gte]=20");
        var gteJson = await gteResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, gteJson.GetProperty("data").GetArrayLength());

        using var lteResponse = await host.Client.GetAsync("/api/v1/data/products?filter[price][lte]=20");
        var lteJson = await lteResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, lteJson.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task List_FilterLike_PatternMatch()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Widget Pro" });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Widget Basic" });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Gadget" });

        using var response = await host.Client.GetAsync("/api/v1/data/products?filter[name][like]=%25Widget%25");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task List_FilterIn_MatchesMultipleValues()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "A", category = "tools" });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "B", category = "electronics" });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "C", category = "food" });

        using var response = await host.Client.GetAsync("/api/v1/data/products?filter[category][in]=tools,food");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task List_FilterNull_MatchesNullValues()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "WithNotes", notes = "some note" });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "NoNotes" });

        using var response = await host.Client.GetAsync("/api/v1/data/products?filter[notes][null]=");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("NoNotes", data[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task List_FilterNotNull_ExcludesNullValues()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "WithNotes", notes = "some note" });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "NoNotes" });

        using var response = await host.Client.GetAsync("/api/v1/data/products?filter[notes][notnull]=");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("WithNotes", data[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task List_SortAscending_OrdersByColumn()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Cherry", price = 30.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Apple", price = 10.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Banana", price = 20.0 });

        using var response = await host.Client.GetAsync("/api/v1/data/products?sort=name");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        Assert.Equal("Apple", data[0].GetProperty("name").GetString());
        Assert.Equal("Banana", data[1].GetProperty("name").GetString());
        Assert.Equal("Cherry", data[2].GetProperty("name").GetString());
    }

    [Fact]
    public async Task List_SortDescending_ReverseOrder()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Apple", price = 10.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Banana", price = 20.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Cherry", price = 30.0 });

        using var response = await host.Client.GetAsync("/api/v1/data/products?sort=-price");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        Assert.Equal("Cherry", data[0].GetProperty("name").GetString());
        Assert.Equal("Banana", data[1].GetProperty("name").GetString());
        Assert.Equal("Apple", data[2].GetProperty("name").GetString());
    }

    [Fact]
    public async Task List_SortDateTime_WithOverPaddedDayValues_OrdersChronologically()
    {
        const string temporalSchema = """
            {
                "tables": [
                    {
                        "name": "events",
                        "columns": [
                            { "name": "id", "type": "integer", "primaryKey": true },
                            { "name": "name", "type": "text", "nullable": false },
                            { "name": "starts_at", "type": "datetime" }
                        ]
                    }
                ],
                "relations": []
            }
            """;

        await using var host = await CreateHostWithSchemaAsync(temporalSchema);

        await host.Client.PostAsJsonAsync("/api/v1/data/events", new { name = "Day 9", starts_at = "2026-04-09T10:00:00Z" });
        await host.Client.PostAsJsonAsync("/api/v1/data/events", new { name = "Day 10", starts_at = "2026-04-010T10:00:00Z" });
        await host.Client.PostAsJsonAsync("/api/v1/data/events", new { name = "Day 11", starts_at = "2026-04-011T10:00:00Z" });
        await host.Client.PostAsJsonAsync("/api/v1/data/events", new { name = "Day 12", starts_at = "2026-04-012T10:00:00Z" });

        using var response = await host.Client.GetAsync("/api/v1/data/events?sort=-starts_at");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        Assert.Equal("Day 12", data[0].GetProperty("name").GetString());
        Assert.Equal("Day 11", data[1].GetProperty("name").GetString());
        Assert.Equal("Day 10", data[2].GetProperty("name").GetString());
        Assert.Equal("Day 9", data[3].GetProperty("name").GetString());
    }

    [Fact]
    public async Task List_MultiSort_AppliesInOrder()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "A", category = "tools", price = 20.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "B", category = "tools", price = 10.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "C", category = "electronics", price = 15.0 });

        using var response = await host.Client.GetAsync("/api/v1/data/products?sort=category,price");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        Assert.Equal("C", data[0].GetProperty("name").GetString());  // electronics
        Assert.Equal("B", data[1].GetProperty("name").GetString());  // tools, price 10
        Assert.Equal("A", data[2].GetProperty("name").GetString());  // tools, price 20
    }

    [Fact]
    public async Task List_FilterAndSort_CombineCorrectly()
    {
        await using var host = await CreateHostWithSchemaAsync();

        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Wrench", category = "tools", price = 15.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Hammer", category = "tools", price = 10.0 });
        await host.Client.PostAsJsonAsync("/api/v1/data/products", new { name = "Phone", category = "electronics", price = 500.0 });

        using var response = await host.Client.GetAsync("/api/v1/data/products?filter[category]=tools&sort=-price");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        var meta = json.GetProperty("meta");

        Assert.Equal(2, data.GetArrayLength());
        Assert.Equal(2, meta.GetProperty("total").GetInt64());
        Assert.Equal("Wrench", data[0].GetProperty("name").GetString());
        Assert.Equal("Hammer", data[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task List_TotalPages_CalculatedCorrectly()
    {
        await using var host = await CreateHostWithSchemaAsync();
        await SeedProducts(host, 7);

        using var response = await host.Client.GetAsync("/api/v1/data/products?per_page=3");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var meta = json.GetProperty("meta");

        Assert.Equal(7, meta.GetProperty("total").GetInt64());
        Assert.Equal(3, meta.GetProperty("total_pages").GetInt32()); // ceil(7/3) = 3
    }

    [Fact]
    public async Task List_FilterUnknownColumn_Returns400()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var response = await host.Client.GetAsync("/api/v1/data/products?filter[nonexistent]=value");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var error = json.GetProperty("error");
        Assert.Equal("VALIDATION_FAILED", error.GetProperty("code").GetString());
        Assert.Contains("nonexistent", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_SortUnknownColumn_Returns400()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var response = await host.Client.GetAsync("/api/v1/data/products?sort=nonexistent");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var error = json.GetProperty("error");
        Assert.Equal("VALIDATION_FAILED", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task List_InvalidOperator_Returns400()
    {
        await using var host = await CreateHostWithSchemaAsync();

        using var response = await host.Client.GetAsync("/api/v1/data/products?filter[name][bogus]=test");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var error = json.GetProperty("error");
        Assert.Equal("VALIDATION_FAILED", error.GetProperty("code").GetString());
        Assert.Contains("bogus", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ApiIntegrationTestHost> CreateHostWithSchemaAsync(string schema = TestSchema)
    {
        var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });

        await host.ApplySchemaAsync(schema);
        return host;
    }

    private static async Task SeedProducts(ApiIntegrationTestHost host, int count)
    {
        for (int i = 1; i <= count; i++)
        {
            await host.Client.PostAsJsonAsync("/api/v1/data/products", new
            {
                name = $"Product {i:D3}",
                category = i % 2 == 0 ? "even" : "odd",
                price = i * 1.5,
                in_stock = i % 3 != 0
            });
        }
    }
}
