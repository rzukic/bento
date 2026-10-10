using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// LOOKUP_ASOF on Postgres gets an index on the expression it searches by
/// (a weekly portfolio chart over three years made thousands of lookups,
/// each reading the whole prices table, and ran past the 30 s limit).
/// </summary>
[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class LookupIndexTests
{
    private const string Schema = """
        {
            "tables": [
                { "name": "securities", "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true },
                    { "name": "isin", "type": "text", "nullable": false }
                ] },
                { "name": "trades", "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true },
                    { "name": "date", "type": "date", "nullable": false },
                    { "name": "security_id", "type": "reference", "references": "securities", "nullable": false },
                    { "name": "isin", "type": "text", "nullable": false },
                    { "name": "shares", "type": "float", "nullable": false }
                ] },
                { "name": "prices", "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true },
                    { "name": "isin", "type": "text", "nullable": false },
                    { "name": "date", "type": "date", "nullable": false },
                    { "name": "price", "type": "float", "nullable": false }
                ] }
            ],
            "relations": [ { "name": "security_trades", "type": "1:N", "from": "securities", "to": "trades", "column": "security_id" } ]
        }
        """;

    [PostgresFact]
    public async Task LookupAsOf_CreatesItsIndex_AndStillMatchesIgnoringCase()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(Schema);
        await PostAsync(host, "securities", new { isin = "IE00B4L5Y983" });
        await PostAsync(host, "prices", new { isin = "IE00B4L5Y983", date = "2026-01-01", price = 10.0 });
        await PostAsync(host, "prices", new { isin = "IE00B4L5Y983", date = "2026-01-20", price = 20.0 });
        // Typed in lower case: the lookup still finds the prices.
        await PostAsync(host, "trades", new { date = "2026-01-02", security_id = 1, isin = "ie00b4l5y983", shares = 2.0 });

        Assert.Equal(0, await IndexCountAsync(host));

        using var response = await host.Client.PostAsJsonAsync("/api/v1/query", new
        {
            source = "securities",
            asOf = new { bucket = "week", from = "2026-01-05", to = "2026-02-01", @as = "week" },
            measures = new[]
            {
                new { name = "value", formula = "SUM(SUM(trades.shares * LOOKUP_ASOF(\"prices\", \"isin\", trades.isin, \"date\", BUCKET_END(), \"price\") WHERE trades.date <= BUCKET_END()))" }
            }
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        Assert.Equal(
            ["2026-01-05 20", "2026-01-12 20", "2026-01-19 40", "2026-01-26 40"],
            body.GetProperty("data").EnumerateArray().Select(r => $"{r.GetProperty("week").GetString()} {r.GetProperty("value").GetDecimal()}"));

        Assert.Equal(1, await IndexCountAsync(host));
        var definition = await host.QuerySystemScalarAsync<string>(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'bento_lookup_prices_isin_date'");
        Assert.Contains("lower(isin), date", definition);
    }

    private static Task<long> IndexCountAsync(ApiIntegrationTestHost host) =>
        host.QuerySystemScalarAsync<long>("SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'bento_lookup_prices_isin_date'");

    private static async Task PostAsync(ApiIntegrationTestHost host, string table, object row)
    {
        using var response = await host.Client.PostAsJsonAsync($"/api/v1/data/{table}", row);
        Assert.True(response.IsSuccessStatusCode, $"{table}: {await response.Content.ReadAsStringAsync()}");
    }
}
