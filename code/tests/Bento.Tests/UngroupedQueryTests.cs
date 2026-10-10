using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// A query without groups gives one row, also when no measure adds up rows
/// (beta feedback: a CUSTOM value over constants ran once per row, 291 times).
/// </summary>
public sealed class UngroupedQueryTests
{
    [Fact]
    public async Task Measures_WithoutAggregates_GiveOneRow_AndRunCustomOnce()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "sqlite-test-secret"
        });
        await host.ApplySchemaAsync("""
            { "tables": [ { "name": "flows", "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "amount", "type": "currency" }
            ] } ], "relations": [] }
            """);
        using (var function = await host.Client.PostAsJsonAsync("/api/v1/functions", new
        {
            name = "double_it",
            arguments = new[] { new { name = "n", type = "float" } },
            resultType = "float",
            code = "console.log(\"ran\"); return n * 2;"
        }))
            Assert.True(function.IsSuccessStatusCode, await function.Content.ReadAsStringAsync());

        // Without rows the constants still give their one row.
        Assert.Equal(["42|3"], await RowsAsync(host));

        for (var i = 0; i < 25; i++)
            (await host.Client.PostAsJsonAsync("/api/v1/data/flows", new { amount = 10 })).EnsureSuccessStatusCode();
        Assert.Equal(["42|3"], await RowsAsync(host));
    }

    private static async Task<List<string>> RowsAsync(ApiIntegrationTestHost host)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/query", new
        {
            source = "flows",
            measures = new object[]
            {
                new { name = "doubled", formula = "CUSTOM(\"double_it\", 21)" },
                new { name = "three", formula = "1 + 2" }
            }
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        Assert.DoesNotContain(body.GetProperty("data").EnumerateArray(), r => r.TryGetProperty("__rows", out _));
        return body.GetProperty("data").EnumerateArray()
            .Select(r => $"{r.GetProperty("doubled").GetDecimal()}|{r.GetProperty("three").GetDecimal()}")
            .ToList();
    }
}
