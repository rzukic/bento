using System.Net.Http.Json;
using System.Text.Json;
using Bento.Api.Formula;
using Bento.Api.Workers;

namespace Bento.Tests;

/// <summary>
/// Step 3.9: the finance template's tables and the formulas of its
/// dashboards (net worth at each month end from balances plus trades ×
/// price, depot value, gains, positions), with its example rows. Mirrors
/// <c>features/dashboards/financeTemplate.ts</c> (no prefix); the same
/// scenario runs on Postgres. Step 4.3b: realised gains by average cost
/// through the function avg_cost, and the time-weighted return by Modified Dietz.
/// </summary>
public sealed class FinanceTemplateTests
{
    internal const string Schema = """
        {
            "tables": [
                {
                    "name": "accounts",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false, "unique": true },
                        { "name": "kind", "type": "enum", "values": ["giro", "savings", "depot", "credit", "cash"] },
                        { "name": "opened_on", "type": "date" },
                        { "name": "opening_balance", "type": "currency" },
                        { "name": "balance", "type": "computed", "formula": "COALESCE(opening_balance, 0) + SUM(transactions.amount)" }
                    ]
                },
                {
                    "name": "categories",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false, "unique": true },
                        { "name": "kind", "type": "enum", "values": ["income", "expense", "savings", "transfer"] }
                    ]
                },
                {
                    "name": "transactions",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "date", "type": "date", "nullable": false },
                        { "name": "amount", "type": "currency", "nullable": false },
                        { "name": "payee", "type": "text" },
                        { "name": "purpose", "type": "text" },
                        { "name": "account_id", "type": "reference", "references": "accounts" },
                        { "name": "category_id", "type": "reference", "references": "categories" }
                    ]
                },
                {
                    "name": "trades",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "date", "type": "date", "nullable": false },
                        { "name": "isin", "type": "text", "nullable": false },
                        { "name": "name", "type": "text" },
                        { "name": "kind", "type": "enum", "values": ["buy", "sell"] },
                        { "name": "shares", "type": "float", "nullable": false },
                        { "name": "price", "type": "currency", "nullable": false },
                        { "name": "fees", "type": "currency" },
                        { "name": "account_id", "type": "reference", "references": "accounts" },
                        { "name": "signed_shares", "type": "computed", "formula": "IF(kind = \"sell\", -shares, shares)" },
                        { "name": "invested", "type": "computed", "formula": "signed_shares * price + COALESCE(fees, 0)" },
                        { "name": "value", "type": "computed", "formula": "signed_shares * LOOKUP_ASOF(\"prices\", \"isin\", isin, \"date\", TODAY(), \"price\")" },
                        { "name": "realized", "type": "computed", "resultType": "currency", "formula": "CUSTOM_ROWS(\"avg_cost\", PARTITION(account_id, isin), ORDER(date, id), signed_shares, price, COALESCE(fees, 0), \"realized\")" },
                        { "name": "cost_change", "type": "computed", "resultType": "currency", "formula": "CUSTOM_ROWS(\"avg_cost\", PARTITION(account_id, isin), ORDER(date, id), signed_shares, price, COALESCE(fees, 0), \"cost\")" }
                    ]
                },
                {
                    "name": "prices",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "isin", "type": "text", "nullable": false },
                        { "name": "date", "type": "date", "nullable": false },
                        { "name": "price", "type": "currency", "nullable": false }
                    ]
                },
                {
                    "name": "dividends",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "date", "type": "date", "nullable": false },
                        { "name": "isin", "type": "text" },
                        { "name": "name", "type": "text" },
                        { "name": "amount", "type": "currency", "nullable": false },
                        { "name": "tax", "type": "currency" },
                        { "name": "account_id", "type": "reference", "references": "accounts" }
                    ]
                }
            ],
            "relations": [
                { "name": "accounts_transactions", "type": "1:N", "from": "accounts", "to": "transactions", "column": "account_id" },
                { "name": "categories_transactions", "type": "1:N", "from": "categories", "to": "transactions", "column": "category_id" },
                { "name": "accounts_trades", "type": "1:N", "from": "accounts", "to": "trades", "column": "account_id" },
                { "name": "accounts_dividends", "type": "1:N", "from": "accounts", "to": "dividends", "column": "account_id" }
            ]
        }
        """;

    /// <summary>The function avg_cost from its template, as the setup creates it, then the tables.</summary>
    internal static async Task ApplyAsync(ApiIntegrationTestHost host)
    {
        var template = FunctionTemplates.All.Single(t => t.Id == "avg-cost");
        (await host.Client.PostAsJsonAsync("/api/v1/functions", new
        {
            name = FunctionTemplates.AverageCost,
            template.Arguments,
            template.ResultType,
            template.Code,
            template.Mode
        })).EnsureSuccessStatusCode();
        await host.ApplySchemaAsync(Schema);
    }

    internal static async Task SeedAsync(ApiIntegrationTestHost host)
    {
        async Task Post(string table, object row) => (await host.Client.PostAsJsonAsync($"/api/v1/data/{table}", row)).EnsureSuccessStatusCode();
        await Post("accounts", new { name = "Girokonto", kind = "giro", opened_on = "2026-01-01", opening_balance = 1000m });
        await Post("accounts", new { name = "Depot", kind = "depot", opened_on = "2026-01-01", opening_balance = 0m });
        foreach (var (name, kind) in new[] { ("Salary", "income"), ("Rent", "expense"), ("Groceries", "expense"), ("Leisure", "expense"), ("Investing", "savings") })
            await Post("categories", new { name, kind });
        foreach (var month in new[] { "07", "08", "09" })
        {
            await Post("transactions", new { date = $"2026-{month}-01", amount = 3000m, payee = "Employer", account_id = 1, category_id = 1 });
            await Post("transactions", new { date = $"2026-{month}-02", amount = -1000m, payee = "Landlord", account_id = 1, category_id = 2 });
            await Post("transactions", new { date = $"2026-{month}-10", amount = -200m, payee = "Supermarket", account_id = 1, category_id = 3 });
            await Post("transactions", new { date = $"2026-{month}-20", amount = -100m, payee = "Cinema", account_id = 1, category_id = 4 });
        }
        await Post("transactions", new { date = "2026-07-14", amount = -1000m, payee = "Depot", account_id = 1, category_id = 5 });
        await Post("transactions", new { date = "2026-08-09", amount = -500m, payee = "Depot", account_id = 1, category_id = 5 });
        await Post("trades", new { date = "2026-07-15", isin = "IE00B4L5Y983", name = "MSCI World ETF", kind = "buy", shares = 10, price = 100m, account_id = 2 });
        await Post("trades", new { date = "2026-08-10", isin = "IE00B4L5Y983", name = "MSCI World ETF", kind = "buy", shares = 5, price = 100m, account_id = 2 });
        foreach (var (date, price) in new[] { ("2026-07-31", 102m), ("2026-08-31", 98m), ("2026-09-30", 110m) })
            await Post("prices", new { isin = "IE00B4L5Y983", date, price });
        await Post("dividends", new { date = "2026-09-15", isin = "IE00B4L5Y983", name = "MSCI World ETF", amount = 12.5m, account_id = 2 });
    }

    internal static async Task Scenario(ApiIntegrationTestHost host)
    {
        // Net worth at each month end: cash from the balances, the depot from shares × price at that time.
        var worth = await AggregationQueryTests.QueryAsync(host, new
        {
            source = "accounts",
            asOf = new { bucket = "month", from = "2026-07-01", to = "2026-09-30", @as = "month" },
            measures = new[]
            {
                new { name = "cash", formula = "SUM(COALESCE(opening_balance, 0) + SUM(transactions.amount WHERE transactions.date <= BUCKET_END()))" },
                new { name = "depot", formula = "SUM(SUM(trades.signed_shares * LOOKUP_ASOF(\"prices\", \"isin\", trades.isin, \"date\", BUCKET_END(), \"price\") WHERE trades.date <= BUCKET_END()))" }
            }
        });
        Assert.Equal(["2026-07-01 1700 1020", "2026-08-01 2900 1470", "2026-09-01 4600 1650"], AggregationQueryTests.Rows(worth, "month", "cash", "depot"));

        // Today: net worth, depot value, invested and the unrealised gain.
        var today = await AggregationQueryTests.QueryAsync(host, new
        {
            source = "accounts",
            measures = new[] { new { name = "net_worth", formula = "SUM(balance + SUM(trades.value))" } }
        });
        Assert.Equal(["6250"], AggregationQueryTests.Rows(today, "net_worth"));
        var positions = await AggregationQueryTests.QueryAsync(host, new
        {
            source = "trades",
            groupBy = new[] { new { column = "name" } },
            measures = new[]
            {
                new { name = "shares", formula = "SUM(signed_shares)" },
                new { name = "cost", formula = "SUM(cost_change)" },
                new { name = "value", formula = "SUM(value)" },
                new { name = "gain", formula = "SUM(value - cost_change)" },
                new { name = "realized", formula = "SUM(realized)" }
            }
        });
        Assert.Equal(["MSCI World ETF 15 1500 1650 150 0"], AggregationQueryTests.Rows(positions, "name", "shares", "cost", "value", "gain", "realized"));

        // Step 4.3: the return a year (XIRR) of trades, dividends and today's value.
        var xirr = await AggregationQueryTests.QueryAsync(host, new
        {
            source = "accounts",
            measures = new[] { new { name = "xirr", formula = "XIRR(FLOWS(-trades.invested, trades.date), FLOWS(dividends.amount - COALESCE(dividends.tax, 0), dividends.date), SUM(SUM(trades.value)), TODAY())" } }
        });
        Assert.True(xirr.GetProperty("data")[0].GetProperty("xirr").GetDouble() > 0);

        // The time-weighted index from the end of June (empty depot), by Modified Dietz: money counts for the part of the month it was in.
        // July: 1000 on the 15th (17 of 31 days) → (1020 − 1000 + 548.39) / 548.39; August: 500 on the 10th (22 of 31) → (1470 − 500 + 354.84) / (1020 + 354.84);
        // September: 12.50 paid out on the 15th (16 of 30) → (1650 + 12.50 − 6.67) / (1470 − 6.67). The benchmark (a dashboard variable) from its first price.
        var index = await AggregationQueryTests.QueryAsync(host, new
        {
            source = "accounts",
            asOf = new { bucket = "month", from = "2026-07-01", to = "2026-09-30", @as = "month" },
            variables = new Dictionary<string, object> { ["benchmark"] = "IE00B4L5Y983" },
            measures = new[]
            {
                new { name = "depot", formula = "TWR(SUM(SUM(trades.signed_shares * LOOKUP_ASOF(\"prices\", \"isin\", trades.isin, \"date\", BUCKET_END(), \"price\") WHERE trades.date <= BUCKET_END())), FLOWS(trades.invested, trades.date), FLOWS(COALESCE(dividends.tax, 0) - dividends.amount, dividends.date), 100)" },
                new { name = "return", formula = "TWR(SUM(SUM(trades.signed_shares * LOOKUP_ASOF(\"prices\", \"isin\", trades.isin, \"date\", BUCKET_END(), \"price\") WHERE trades.date <= BUCKET_END())), FLOWS(trades.invested, trades.date), FLOWS(COALESCE(dividends.tax, 0) - dividends.amount, dividends.date))" },
                new { name = "benchmark", formula = "REBASE(LOOKUP_ASOF(\"prices\", \"isin\", benchmark, \"date\", BUCKET_END(), \"price\"))" }
            }
        });
        Assert.Equal(["2026-07-01 103.6470588235 0.0364705882 100", "2026-08-01 99.8776327049 -0.001223673 96.0784313725", "2026-09-01 113.016432907 0.1301643291 107.8431372549"],
            AggregationQueryTests.Rows(index, "month", "depot", "return", "benchmark"));

        // docs/user/dashboards.md, "A depot with its own cash": the depot holds the cash it gets (transfers with category Investing, id 5,
        // minus what the trades cost, plus net dividends). Money in is the transfers only, so the dividend counts as return:
        // July (1020 − 1000 + 1000 × 18/31) / (1000 × 18/31), August (1470 − 500 + 500 × 23/31) / (1020 + 500 × 23/31), September 1662.50 / 1470.
        var withCash = await AggregationQueryTests.QueryAsync(host, new
        {
            source = "accounts",
            asOf = new { bucket = "month", from = "2026-07-01", to = "2026-09-30", @as = "month" },
            measures = new[]
            {
                new { name = "depot", formula = "TWR(SUM(SUM(trades.signed_shares * LOOKUP_ASOF(\"prices\", \"isin\", trades.isin, \"date\", BUCKET_END(), \"price\") WHERE trades.date <= BUCKET_END())) - SUM(SUM(transactions.amount WHERE transactions.category_id = 5 AND transactions.date <= BUCKET_END())) - SUM(SUM(trades.invested WHERE trades.date <= BUCKET_END())) + SUM(SUM(dividends.amount - COALESCE(dividends.tax, 0) WHERE dividends.date <= BUCKET_END())), FLOWS(-transactions.amount, transactions.date WHERE transactions.category_id = 5), 100)" }
            }
        });
        Assert.Equal(["2026-07-01 103.4444444444", "2026-08-01 99.726010101", "2026-09-01 112.7853685666"], AggregationQueryTests.Rows(withCash, "month", "depot"));

        // A sale of 5 at 108 with 1 fee: by average cost (100 a share) it gains 540 − 1 − 500 = 39; 10 shares costing 1000 stay, worth 1100.
        (await host.Client.PostAsJsonAsync("/api/v1/data/trades", new { date = "2026-09-20", isin = "IE00B4L5Y983", name = "MSCI World ETF", kind = "sell", shares = 5, price = 108m, fees = 1m, account_id = 2 })).EnsureSuccessStatusCode();
        await host.GetService<StoredCustomValues>().RunAsync();
        var sold = await AggregationQueryTests.QueryAsync(host, new
        {
            source = "trades",
            groupBy = new[] { new { column = "name" } },
            measures = new[]
            {
                new { name = "shares", formula = "SUM(signed_shares)" },
                new { name = "cost", formula = "SUM(cost_change)" },
                new { name = "value", formula = "SUM(value)" },
                new { name = "gain", formula = "SUM(value - cost_change)" },
                new { name = "realized", formula = "SUM(realized)" }
            }
        });
        Assert.Equal(["MSCI World ETF 10 1000 1100 100 39"], AggregationQueryTests.Rows(sold, "name", "shares", "cost", "value", "gain", "realized"));
    }

    [Fact]
    public async Task Template_OnSqlite()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?> { ["Backend:AuthMode"] = "open", ["Schema:AutoMigrateOnStartup"] = "false" });
        await ApplyAsync(host);
        await SeedAsync(host);
        await Scenario(host);
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresFinanceTemplateTests
{
    [PostgresFact]
    public async Task Template_OnPostgres_MatchesSqlite()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await FinanceTemplateTests.ApplyAsync(host);
        await FinanceTemplateTests.SeedAsync(host);
        await FinanceTemplateTests.Scenario(host);
    }
}
