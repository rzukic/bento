using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Step 4.3 acceptance: a depot's XIRR from trades, dividends and today's value;
/// its time-weighted return as an index at month ends; a benchmark rebased to 100.
/// Expected values are computed by hand (see the comments); the same scenario
/// runs on Postgres (<see cref="PostgresReturnsQueryTests"/>).
/// </summary>
public sealed class ReturnsQueryTests
{
    private const string Schema = """
        {
            "tables": [
                {
                    "name": "accounts",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text" }
                    ]
                },
                {
                    "name": "trades",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "account_id", "type": "reference", "references": "accounts" },
                        { "name": "isin", "type": "text" },
                        { "name": "date", "type": "date" },
                        { "name": "shares", "type": "float" },
                        { "name": "price", "type": "currency" },
                        { "name": "invested", "type": "computed", "formula": "shares * price" },
                        { "name": "value_end", "type": "computed", "formula": "shares * LOOKUP_ASOF(\"prices\", \"isin\", isin, \"date\", \"2026-03-31\", \"price\")" }
                    ]
                },
                {
                    "name": "dividends",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "account_id", "type": "reference", "references": "accounts" },
                        { "name": "date", "type": "date" },
                        { "name": "net", "type": "currency" }
                    ]
                },
                {
                    "name": "prices",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "isin", "type": "text" },
                        { "name": "date", "type": "date" },
                        { "name": "price", "type": "currency" }
                    ]
                },
                {
                    "name": "flows",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "date", "type": "date" },
                        { "name": "amount", "type": "currency" },
                        { "name": "kind", "type": "text" }
                    ]
                }
            ],
            "relations": [
                { "name": "account_trades", "type": "1:N", "from": "accounts", "to": "trades", "column": "account_id" },
                { "name": "account_dividends", "type": "1:N", "from": "accounts", "to": "dividends", "column": "account_id" }
            ]
        }
        """;

    private const string MonthEndValue =
        "SUM(SUM(trades.shares * LOOKUP_ASOF(\"prices\", \"isin\", trades.isin, \"date\", BUCKET_END(), \"price\") WHERE trades.date <= BUCKET_END()))";
    private const string MonthFlow =
        "SUM(SUM(trades.invested WHERE trades.date >= BUCKET_START() AND trades.date <= BUCKET_END())) - " +
        "SUM(SUM(dividends.net WHERE dividends.date >= BUCKET_START() AND dividends.date <= BUCKET_END()))";

    internal static async Task RunAsync(ApiIntegrationTestHost host)
    {
        await host.ApplySchemaAsync(Schema);
        await PostAsync(host, "accounts", new { name = "Depot" });
        await PostAsync(host, "trades", new { account_id = 1, isin = "FUND", date = "2025-12-15", shares = 10.0, price = 98m });
        await PostAsync(host, "trades", new { account_id = 1, isin = "FUND", date = "2026-02-10", shares = 5.0, price = 104m });
        await PostAsync(host, "dividends", new { account_id = 1, date = "2026-03-20", net = 15m });
        foreach (var (isin, day, price) in new[]
                 {
                     ("FUND", "2025-12-31", 100m), ("FUND", "2026-01-31", 110m), ("FUND", "2026-02-28", 99m), ("FUND", "2026-03-31", 105m),
                     ("BENCH", "2025-12-31", 50m), ("BENCH", "2026-01-31", 52m), ("BENCH", "2026-02-28", 51m), ("BENCH", "2026-03-31", 55m)
                 })
            await PostAsync(host, "prices", new { isin, date = day, price });
        foreach (var (day, amount) in new[] { ("2008-01-01", -10000m), ("2008-03-01", 2750m), ("2008-10-30", 4250m), ("2009-02-15", 3250m), ("2009-04-01", 2750m) })
            await PostAsync(host, "flows", new { date = day, amount, kind = "excel" });

        // Excel's XIRR example over a table's rows: 0.373362535.
        var excel = await QueryAsync(host, new { source = "flows", measures = new[] { new { name = "rate", formula = "XIRR(amount, date WHERE kind = \"excel\")" } } });
        Assert.InRange(Value(excel, "rate"), 0.37336253, 0.37336254);
        Assert.Equal("rate:float", Columns(excel).Single());

        // The depot: −980 (Dec 15), −520 (Feb 10), +15 dividend (Mar 20), +1575 value (Mar 31) → 27.92 % a year.
        var depot = await QueryAsync(host, new
        {
            source = "accounts",
            measures = new[]
            {
                new { name = "xirr", formula = "XIRR(FLOWS(-trades.invested, trades.date), FLOWS(dividends.net, dividends.date), SUM(SUM(trades.value_end)), \"2026-03-31\")" },
                new { name = "value", formula = "SUM(SUM(trades.value_end))" }
            }
        });
        Assert.Equal(0.2791649509, Value(depot, "xirr"), 9);
        Assert.Equal(1575, Value(depot, "value"));

        // Time-weighted, at month ends, starting from the end of December:
        // Jan 1100 / 1000 → 110; Feb 1485 / (1100 + 520) → 100.83; Mar 1575 / (1485 − 15) → 108.04.
        // The benchmark from 50: 52, 51, 55 → 104, 102, 110.
        var index = await QueryAsync(host, new
        {
            source = "accounts",
            asOf = new { bucket = "month", from = "2026-01-01", to = "2026-03-31", @as = "month" },
            measures = new[]
            {
                new { name = "depot", formula = $"TWR({MonthEndValue}, {MonthFlow}, 100)" },
                new { name = "return", formula = $"TWR({MonthEndValue}, {MonthFlow})" },
                new { name = "benchmark", formula = "REBASE(LOOKUP_ASOF(\"prices\", \"isin\", \"BENCH\", \"date\", BUCKET_END(), \"price\"))" }
            }
        });
        Assert.Equal(["2026-01-01 110 0.1 104", "2026-02-01 100.8333333333 0.0083333333 102", "2026-03-01 108.0357142857 0.0803571429 110"],
            Rows(index, "month", "depot", "return", "benchmark"));

        // Rebasing works on plain date groups too (the first period in the range is 100).
        var monthly = await QueryAsync(host, new
        {
            source = "prices",
            filter = new { isin = "BENCH" },
            groupBy = new[] { new { column = "date", bucket = "month", @as = "month" } },
            measures = new[] { new { name = "bench", formula = "REBASE(MAX(price))" } }
        });
        Assert.Equal(["2025-12-01 100", "2026-01-01 104", "2026-02-01 102", "2026-03-01 110"], Rows(monthly, "month", "bench"));

        // A variable over a table can hold a return.
        var variables = await PostDataAsync(host, "/api/v1/query/variables", new
        {
            variables = new object[] { new { name = "excel_rate", source = "flows", formula = "XIRR(amount, date)" } }
        });
        Assert.InRange(variables[0].GetProperty("value").GetDouble(), 0.37336253, 0.37336254);

        // What does not work, and says so.
        await ExpectErrorAsync(host, new { source = "flows", measures = new[] { new { name = "x", formula = "XIRR(amount, date) * 100" } } },
            "XIRR(…) must be the whole formula of a dashboard measure; show it as a percentage in the widget.");
        await ExpectErrorAsync(host, new { source = "accounts", measures = new[] { new { name = "x", formula = $"TWR({MonthEndValue.Replace("BUCKET_END()", "TODAY()")}, 0)" } } },
            "TWR needs values at the end of each period (month, quarter, year).");
        await ExpectErrorAsync(host, new
        {
            source = "flows",
            groupBy = new[] { new { column = "date", bucket = "year" } },
            measures = new[] { new { name = "x", formula = "RUNNING_SUM(XIRR(amount, date))" } }
        }, "RUNNING_SUM cannot add up a return; TWR(…) already accumulates over the periods.");
        await ExpectErrorAsync(host, new { source = "flows", measures = new[] { new { name = "x", formula = "XIRR(amount, kind)" } } },
            "The date of FLOWS must be a date, not text.");

        var column = await PostDataAsync(host, "/api/v1/formulas/validate", new { table = "flows", formula = "XIRR(amount, date)" });
        Assert.Equal("XIRR works in dashboard measures and variables, over the rows of a table.", column.GetProperty("errors")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Returns_OnSqlite()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?> { ["Backend:AuthMode"] = "open", ["Schema:AutoMigrateOnStartup"] = "false" });
        await RunAsync(host);
    }

    // ── Helpers ──

    private static async Task PostAsync(ApiIntegrationTestHost host, string table, object body)
    {
        using var response = await host.Client.PostAsJsonAsync($"/api/v1/data/{table}", body);
        Assert.True(response.IsSuccessStatusCode, $"{table}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<JsonElement> PostDataAsync(ApiIntegrationTestHost host, string path, object body)
    {
        using var response = await host.Client.PostAsJsonAsync(path, body);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, json.ToString());
        return json.GetProperty("data");
    }

    private static async Task<JsonElement> QueryAsync(ApiIntegrationTestHost host, object query)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/query", query);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        return body;
    }

    private static async Task ExpectErrorAsync(ApiIntegrationTestHost host, object query, string message)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/query", query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(message, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("message").GetString());
    }

    private static double Value(JsonElement result, string column) => result.GetProperty("data")[0].GetProperty(column).GetDouble();

    private static IEnumerable<string> Columns(JsonElement result) =>
        result.GetProperty("meta").GetProperty("columns").EnumerateArray().Select(c => $"{c.GetProperty("name").GetString()}:{c.GetProperty("type").GetString()}");

    private static string[] Rows(JsonElement result, params string[] columns) =>
        result.GetProperty("data").EnumerateArray()
            .Select(row => string.Join(" ", columns.Select(c => FormulaConformanceTests.Text(row.GetProperty(c)))))
            .ToArray();
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresReturnsQueryTests
{
    [PostgresFact]
    public async Task Returns_OnPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await ReturnsQueryTests.RunAsync(host);
    }
}
