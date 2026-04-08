using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Repere.Tests;

public sealed class SearchEndpointTests
{
    private const string TextSchema = """
        {
            "tables": [
                {
                    "name": "products",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false },
                        { "name": "description", "type": "text" },
                        { "name": "category", "type": "text" },
                        { "name": "price", "type": "float" }
                    ]
                }
            ],
            "relations": []
        }
        """;

    private const string NoTextSchema = """
        {
            "tables": [
                {
                    "name": "metrics",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "value", "type": "float" },
                        { "name": "count", "type": "integer" }
                    ]
                }
            ],
            "relations": []
        }
        """;

    [Fact]
    public async Task Search_MatchesAcrossTextColumns_CaseInsensitive()
    {
        await using var host = await CreateHostAsync(TextSchema);
        await SeedAsync(host, "products", new { name = "Acme Widget", description = "shiny", category = "tools", price = 9.99 });
        await SeedAsync(host, "products", new { name = "Bolt", description = "ACME made", category = "parts", price = 1.5 });
        await SeedAsync(host, "products", new { name = "Hammer", description = "wooden", category = "ACME line", price = 12.0 });
        await SeedAsync(host, "products", new { name = "Nail", description = "small", category = "parts", price = 0.1 });

        using var response = await host.Client.GetAsync("/api/v1/data/products?search=acme");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, json.GetProperty("data").GetArrayLength());
        Assert.Equal(3, json.GetProperty("meta").GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task Search_SubstringMatch_FindsPartialWords()
    {
        await using var host = await CreateHostAsync(TextSchema);
        await SeedAsync(host, "products", new { name = "Widget Pro", description = "", category = "x" });
        await SeedAsync(host, "products", new { name = "Gadget", description = "", category = "x" });

        using var response = await host.Client.GetAsync("/api/v1/data/products?search=idge");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("data").GetArrayLength());
        Assert.Equal("Widget Pro", json.GetProperty("data")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Search_CombinesWithFilter_AndSemantics()
    {
        await using var host = await CreateHostAsync(TextSchema);
        await SeedAsync(host, "products", new { name = "Acme Widget", description = "", category = "active", price = 1.0 });
        await SeedAsync(host, "products", new { name = "Acme Bolt", description = "", category = "inactive", price = 1.0 });
        await SeedAsync(host, "products", new { name = "Other", description = "", category = "active", price = 1.0 });

        using var response = await host.Client.GetAsync("/api/v1/data/products?search=acme&filter[category]=active");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("data").GetArrayLength());
        Assert.Equal("Acme Widget", json.GetProperty("data")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Search_NoMatches_ReturnsEmpty()
    {
        await using var host = await CreateHostAsync(TextSchema);
        await SeedAsync(host, "products", new { name = "Widget", description = "", category = "x" });

        using var response = await host.Client.GetAsync("/api/v1/data/products?search=zzznotfound");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("data").GetArrayLength());
        Assert.Equal(0, json.GetProperty("meta").GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task Search_EmptyTerm_ReturnsAllRows()
    {
        await using var host = await CreateHostAsync(TextSchema);
        await SeedAsync(host, "products", new { name = "A", description = "", category = "x" });
        await SeedAsync(host, "products", new { name = "B", description = "", category = "x" });

        using var response = await host.Client.GetAsync("/api/v1/data/products?search=");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task Search_TableWithNoTextColumns_ReturnsAllRows()
    {
        await using var host = await CreateHostAsync(NoTextSchema);
        await SeedAsync(host, "metrics", new { value = 1.0, count = 1 });
        await SeedAsync(host, "metrics", new { value = 2.0, count = 2 });

        using var response = await host.Client.GetAsync("/api/v1/data/metrics?search=anything");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task Search_SqlInjectionAttempt_TreatedAsLiteral()
    {
        await using var host = await CreateHostAsync(TextSchema);
        await SeedAsync(host, "products", new { name = "Safe", description = "", category = "x" });

        using var response = await host.Client.GetAsync("/api/v1/data/products?search=%27%3B%20DROP%20TABLE%20products%3B--");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Table is still alive — verify with a follow-up list call
        using var followup = await host.Client.GetAsync("/api/v1/data/products");
        Assert.Equal(HttpStatusCode.OK, followup.StatusCode);
        var json = await followup.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task Search_RespectsPagination()
    {
        await using var host = await CreateHostAsync(TextSchema);
        for (int i = 1; i <= 30; i++)
        {
            await SeedAsync(host, "products", new { name = $"Acme {i:D3}", description = "", category = "x" });
        }
        await SeedAsync(host, "products", new { name = "Other", description = "", category = "x" });

        using var response = await host.Client.GetAsync("/api/v1/data/products?search=acme&per_page=10");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10, json.GetProperty("data").GetArrayLength());
        Assert.Equal(30, json.GetProperty("meta").GetProperty("total").GetInt64());
        Assert.Equal(3, json.GetProperty("meta").GetProperty("total_pages").GetInt32());
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

    private static async Task SeedAsync(ApiIntegrationTestHost host, string table, object body)
    {
        var resp = await host.Client.PostAsJsonAsync($"/api/v1/data/{table}", body);
        resp.EnsureSuccessStatusCode();
    }
}
