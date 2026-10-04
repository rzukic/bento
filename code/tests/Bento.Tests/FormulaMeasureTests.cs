using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Step 3.6 acceptance: formula measures (a savings rate per month, so a KPI
/// can compare it with the month before), values at the end of each period
/// (net worth from account balances plus holdings × price at that time),
/// dashboard variables and LOOKUP_ASOF. The same scenario runs on Postgres
/// (<see cref="PostgresFormulaMeasureTests"/>).
/// </summary>
public sealed class FormulaMeasureTests
{
    internal const string Schema = """
        {
            "tables": [
                {
                    "name": "accounts",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text" },
                        { "name": "opening_balance", "type": "currency" },
                        { "name": "spent", "type": "computed", "formula": "SUM(transactions.amount WHERE transactions.amount < 0)" }
                    ]
                },
                {
                    "name": "transactions",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "account_id", "type": "reference", "references": "accounts" },
                        { "name": "booked_on", "type": "date" },
                        { "name": "amount", "type": "currency" },
                        { "name": "category", "type": "text" }
                    ]
                },
                {
                    "name": "holdings",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "account_id", "type": "reference", "references": "accounts" },
                        { "name": "ticker", "type": "text" },
                        { "name": "shares", "type": "float" },
                        { "name": "bought_on", "type": "date" },
                        { "name": "price_mid_september", "type": "computed", "formula": "LOOKUP_ASOF(\"prices\", \"ticker\", ticker, \"date\", \"2026-09-15\", \"price\")" }
                    ]
                },
                {
                    "name": "prices",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "ticker", "type": "text" },
                        { "name": "date", "type": "date" },
                        { "name": "price", "type": "currency" }
                    ]
                }
            ],
            "relations": [
                { "name": "account_transactions", "type": "1:N", "from": "accounts", "to": "transactions", "column": "account_id" },
                { "name": "account_holdings", "type": "1:N", "from": "accounts", "to": "holdings", "column": "account_id" }
            ]
        }
        """;

    internal static async Task SeedAsync(ApiIntegrationTestHost host)
    {
        await PostAsync(host, "accounts", new { name = "Girokonto", opening_balance = 1000m });
        await PostAsync(host, "accounts", new { name = "Depot", opening_balance = 0m });

        (string Day, decimal Amount, string Category)[] bookings =
        [
            ("2026-08-01", 3000m, "Gehalt"), ("2026-08-03", -1000m, "Miete"), ("2026-08-20", -200m, "Essen"),
            ("2026-09-01", 3000m, "Gehalt"), ("2026-09-03", -1100m, "Miete"), ("2026-09-18", -300m, "Essen"),
            ("2026-10-01", 3100m, "Gehalt"), ("2026-10-03", -900m, "Miete"), ("2026-10-10", -150m, "Essen")
        ];
        foreach (var (day, amount, category) in bookings)
            await PostAsync(host, "transactions", new { account_id = 1, booked_on = day, amount, category });

        await PostAsync(host, "holdings", new { account_id = 2, ticker = "VWCE", shares = 10.0, bought_on = "2026-08-15" });
        await PostAsync(host, "holdings", new { account_id = 2, ticker = "VWCE", shares = 5.0, bought_on = "2026-09-20" });

        foreach (var (day, price) in new[] { ("2026-07-31", 100m), ("2026-08-31", 102m), ("2026-09-30", 105m), ("2026-10-15", 110m) })
            await PostAsync(host, "prices", new { ticker = "VWCE", date = day, price });
    }

    internal static async Task SavingsRatePerMonth(ApiIntegrationTestHost host)
    {
        var result = await QueryAsync(host, new
        {
            source = "transactions",
            groupBy = new[] { new { column = "booked_on", bucket = "month", @as = "month" } },
            measures = new object[]
            {
                new { name = "income", formula = "SUM(amount WHERE amount > 0)" },
                new { name = "spent", formula = "SUMIF(amount, amount < 0)" },
                new { name = "rate", formula = "(SUM(amount WHERE amount > 0) + SUM(amount WHERE amount < 0)) / SUM(amount WHERE amount > 0)" },
                new { name = "bookings", formula = "COUNT()" },
                new { name = "rent", formula = "COUNT(WHERE category = \"miete\")" },
                new { name = "balance", formula = "RUNNING_SUM(SUM(amount))" }
            }
        });

        Assert.Equal(
            ["2026-08-01 3000 -1200 0.6 3 1 1800", "2026-09-01 3000 -1400 0.5333333333 3 1 3400", "2026-10-01 3100 -1050 0.6612903226 3 1 5450"],
            Rows(result, "month", "income", "spent", "rate", "bookings", "rent", "balance"));
        Assert.Equal(
            ["month:date", "income:currency", "spent:currency", "rate:float", "bookings:integer", "rent:integer", "balance:currency"],
            result.GetProperty("meta").GetProperty("columns").EnumerateArray().Select(c => $"{c.GetProperty("name").GetString()}:{c.GetProperty("type").GetString()}"));
    }

    internal static async Task ValuesAtTheEndOfEachMonth(ApiIntegrationTestHost host)
    {
        var worth = await QueryAsync(host, new
        {
            source = "accounts",
            asOf = new { bucket = "month", from = "2026-08-01", to = "2026-11-30", @as = "month" },
            measures = new[]
            {
                new
                {
                    name = "net_worth",
                    formula = "SUM(opening_balance + SUM(transactions.amount WHERE transactions.booked_on <= BUCKET_END())"
                            + " + SUM(holdings.shares * LOOKUP_ASOF(\"prices\", \"ticker\", holdings.ticker, \"date\", BUCKET_END(), \"price\") WHERE holdings.bought_on <= BUCKET_END()))"
                }
            }
        });
        Assert.Equal(["2026-08-01 3820", "2026-09-01 5975", "2026-10-01 8100", "2026-11-01 8100"], Rows(worth, "month", "net_worth"));

        var net = await QueryAsync(host, new
        {
            source = "transactions",
            asOf = new { bucket = "month", from = "2026-09-10", to = "2026-10-31" },
            measures = new[] { new { name = "net", formula = "SUM(amount WHERE booked_on >= BUCKET_START() AND booked_on <= BUCKET_END())" } }
        });
        Assert.Equal(["2026-09-01 1600", "2026-10-01 2050"], Rows(net, "period", "net"));
    }

    internal static async Task Variables(ApiIntegrationTestHost host)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/query/variables", new
        {
            variables = new object[]
            {
                new { name = "runway", formula = "cash / burn_rate" },
                new { name = "income", source = "transactions", formula = "SUM(amount WHERE amount > 0)" },
                new { name = "spent", source = "transactions", formula = "-SUM(amount WHERE amount < 0)" },
                new { name = "months", formula = "3" },
                new { name = "burn_rate", formula = "spent / months" },
                new { name = "savings", formula = "(income - spent) / income" },
                new { name = "cash", source = "accounts", formula = "SUM(opening_balance + SUM(transactions.amount))" },
                new { name = "september", source = "transactions", formula = "SUM(amount)", filter = new { booked_on = new { gte = "2026-09-01", lte = "2026-09-30" } } },
                new { name = "broken", formula = "nothing + 1" },
                new { name = "follows", formula = "broken * 2" },
                new { name = "a", formula = "b + 1" },
                new { name = "b", formula = "a + 1" }
            }
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        var values = body.GetProperty("data").EnumerateArray().ToDictionary(
            v => v.GetProperty("name").GetString()!,
            v => v.GetProperty("error").ValueKind == JsonValueKind.String
                ? "error: " + v.GetProperty("error").GetString()
                : $"{FormulaConformanceTests.Text(v.GetProperty("value"))} {v.GetProperty("type").GetString()}");

        Assert.Equal("9100 currency", values["income"]);
        Assert.Equal("3650 currency", values["spent"]);
        Assert.Equal("3 integer", values["months"]);
        Assert.Equal("1216.6667 currency", values["burn_rate"]);
        Assert.Equal("0.5989010989 float", values["savings"]);
        Assert.Equal("6450 currency", values["cash"]);
        Assert.Equal("5.3013697178 float", values["runway"]);
        Assert.Equal("1600 currency", values["september"]);
        Assert.Equal("error: Column 'nothing' not found in the dashboard variables.", values["broken"]);
        Assert.Equal("error: 'broken' has an error.", values["follows"]);
        Assert.StartsWith("error: Circular reference", values["a"]);

        // A widget measure using a variable.
        var perMonth = await QueryAsync(host, new
        {
            source = "transactions",
            measures = new[] { new { name = "per_month", formula = "SUM(amount) / months" } },
            variables = new { months = 3 }
        });
        Assert.Equal(["1816.6667"], Rows(perMonth, "per_month"));
    }

    internal static async Task LookupsAndErrors(ApiIntegrationTestHost host)
    {
        var holdings = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/holdings?sort=id");
        Assert.Equal([102m, 102m], holdings.GetProperty("data").EnumerateArray().Select(h => h.GetProperty("price_mid_september").GetDecimal()));
        var accounts = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/accounts?sort=id");
        Assert.Equal([-3650m, 0m], accounts.GetProperty("data").EnumerateArray().Select(a => a.GetProperty("spent").GetDecimal()));

        using var rowValue = await host.Client.PostAsJsonAsync("/api/v1/query", new
        {
            source = "transactions",
            measures = new[] { new { name = "x", formula = "amount * 2" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, rowValue.StatusCode);
        var error = (await rowValue.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.Equal("'amount' differs from row to row; use it inside SUM(…), AVG(…), MIN(…), MAX(…) or COUNT(…).", error.GetProperty("message").GetString());
        Assert.Equal("measures[0].formula", error.GetProperty("details").GetProperty("field").GetString());

        using var plainAsOf = await host.Client.PostAsJsonAsync("/api/v1/query", new
        {
            source = "transactions",
            asOf = new { bucket = "month", from = "2026-08-01", to = "2026-08-31" },
            measures = new[] { new { name = "n", fn = "count" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, plainAsOf.StatusCode);
    }

    internal static async Task RunAsync(ApiIntegrationTestHost host)
    {
        await host.ApplySchemaAsync(Schema);
        await SeedAsync(host);
        await SavingsRatePerMonth(host);
        await ValuesAtTheEndOfEachMonth(host);
        await Variables(host);
        await LookupsAndErrors(host);
    }

    [Fact]
    public async Task FormulaMeasures_OnSqlite()
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

    private static async Task<JsonElement> QueryAsync(ApiIntegrationTestHost host, object query)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/query", query);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        return body;
    }

    private static string[] Rows(JsonElement result, params string[] columns) =>
        result.GetProperty("data").EnumerateArray()
            .Select(row => string.Join(" ", columns.Select(c => FormulaConformanceTests.Text(row.GetProperty(c)))))
            .ToArray();
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresFormulaMeasureTests
{
    [PostgresFact]
    public async Task FormulaMeasures_OnPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await FormulaMeasureTests.RunAsync(host);
    }
}
