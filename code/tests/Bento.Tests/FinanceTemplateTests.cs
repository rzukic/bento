using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Step 3.9: the finance template's tables and the formulas of its
/// dashboards (net worth at each month end from balances plus trades ×
/// price, depot value, gains, positions), with its example rows. Mirrors
/// <c>features/dashboards/financeTemplate.ts</c> (no prefix); the same
/// scenario runs on Postgres.
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
                        { "name": "value", "type": "computed", "formula": "signed_shares * LOOKUP_ASOF(\"prices\", \"isin\", isin, \"date\", TODAY(), \"price\")" }
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
                new { name = "invested", formula = "SUM(invested)" },
                new { name = "value", formula = "SUM(value)" },
                new { name = "gain", formula = "SUM(value - invested)" }
            }
        });
        Assert.Equal(["MSCI World ETF 15 1500 1650 150"], AggregationQueryTests.Rows(positions, "name", "shares", "invested", "value", "gain"));
    }

    [Fact]
    public async Task Template_OnSqlite()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?> { ["Backend:AuthMode"] = "open", ["Schema:AutoMigrateOnStartup"] = "false" });
        await host.ApplySchemaAsync(Schema);
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
        await host.ApplySchemaAsync(FinanceTemplateTests.Schema);
        await FinanceTemplateTests.SeedAsync(host);
        await FinanceTemplateTests.Scenario(host);
    }
}
