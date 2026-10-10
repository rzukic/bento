using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Beta feedback: LAST/FIRST by a date, MIN/MAX on text, rolling windows —
/// over related rows (computed columns) and over a group's rows (measures), on both providers.
/// </summary>
public sealed class NewFunctionsTests
{
    private const string Schema = """
        {
            "tables": [
                { "name": "accounts", "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true },
                    { "name": "name", "type": "text" },
                    { "name": "last_amount", "type": "computed", "formula": "LAST(transactions.amount, transactions.booked_on)" },
                    { "name": "first_spend", "type": "computed", "formula": "FIRST(transactions.booked_on, transactions.booked_on WHERE transactions.amount < 0)" },
                    { "name": "last_payee", "type": "computed", "formula": "MAX(transactions.payee)" }
                ] },
                { "name": "transactions", "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true },
                    { "name": "account_id", "type": "reference", "references": "accounts" },
                    { "name": "booked_on", "type": "date" },
                    { "name": "amount", "type": "currency" },
                    { "name": "payee", "type": "text" }
                ] }
            ],
            "relations": [ { "name": "account_transactions", "type": "1:N", "from": "accounts", "to": "transactions", "column": "account_id" } ]
        }
        """;

    [Fact]
    public Task OnSqlite() => RunAsync(ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?> { ["Backend:AuthMode"] = "open", ["Backend:Jwt:Secret"] = "x" }));

    internal static async Task RunAsync(Task<ApiIntegrationTestHost> start)
    {
        await using var host = await start;
        await host.ApplySchemaAsync(Schema);
        await PostAsync(host, "accounts", new { name = "Giro" });
        await PostAsync(host, "accounts", new { name = "Savings" });
        foreach (var row in new object[]
        {
            new { account_id = 1, booked_on = "2026-01-10", amount = 100m, payee = "Zeta" },
            new { account_id = 1, booked_on = "2026-02-05", amount = -30m, payee = "alpha" },
            new { account_id = 1, booked_on = "2026-03-20", amount = -20m, payee = "Billa" },
            new { account_id = 2, booked_on = "2026-03-01", amount = 500m, payee = "Employer" }
        })
            await PostAsync(host, "transactions", row);

        // Related rows: the latest amount, the first spending day, the last payee in character order.
        var accounts = (await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/accounts?sort=id")).GetProperty("data");
        Assert.Equal(-20m, accounts[0].GetProperty("last_amount").GetDecimal());
        Assert.Equal("2026-02-05", accounts[0].GetProperty("first_spend").GetString());
        Assert.Equal("alpha", accounts[0].GetProperty("last_payee").GetString());
        Assert.Equal(500m, accounts[1].GetProperty("last_amount").GetDecimal());
        Assert.Equal(JsonValueKind.Null, accounts[1].GetProperty("first_spend").ValueKind);

        // A group's rows.
        var byAccount = await QueryAsync(host, new
        {
            source = "transactions",
            groupBy = new[] { new { column = "account_id" } },
            sort = new[] { "account_id" },
            measures = new object[]
            {
                new { name = "latest", formula = "LAST(amount, booked_on)" },
                new { name = "first_payee", formula = "FIRST(payee, booked_on WHERE amount < 0)" },
                new { name = "min_payee", formula = "MIN(payee)" }
            }
        });
        Assert.Equal(["-20|alpha|Billa", "500||Employer"],
            byAccount.Select(r => $"{r.GetProperty("latest").GetDecimal()}|{(r.GetProperty("first_payee").ValueKind == JsonValueKind.Null ? "" : r.GetProperty("first_payee").GetString())}|{r.GetProperty("min_payee").GetString()}"));

        // Rolling windows over the months: two months together, and their average.
        var monthly = await QueryAsync(host, new
        {
            source = "transactions",
            groupBy = new[] { new { column = "booked_on", bucket = "month", @as = "month" } },
            measures = new object[]
            {
                new { name = "net", formula = "SUM(amount)" },
                new { name = "two", formula = "ROLLING_SUM(SUM(amount), 2)" },
                new { name = "avg", formula = "ROLLING_AVG(SUM(amount), 2)" }
            }
        });
        Assert.Equal(["2026-01-01 100 100 100", "2026-02-01 -30 70 35", "2026-03-01 480 450 225"],
            monthly.Select(r => $"{r.GetProperty("month").GetString()} {r.GetProperty("net").GetDecimal()} {r.GetProperty("two").GetDecimal()} {r.GetProperty("avg").GetDecimal()}"));

        // Mistakes say how to write it.
        await ExpectErrorAsync(host, "ROLLING_SUM(SUM(amount))", "Write ROLLING_SUM(measure, periods), e.g. ROLLING_SUM(SUM(amount), 12).");
        await ExpectErrorAsync(host, "ROLLING_SUM(SUM(amount), 1)", "The periods are a whole number from 2 to 366, e.g. 12.");
        await ExpectErrorAsync(host, "LAST(amount)", "Write LAST(column, date) or LAST(column, date WHERE condition), e.g. LAST(trades.price, trades.date).");
    }

    private static async Task<List<JsonElement>> QueryAsync(ApiIntegrationTestHost host, object query)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/query", query);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        return body.GetProperty("data").EnumerateArray().ToList();
    }

    private static async Task ExpectErrorAsync(ApiIntegrationTestHost host, string formula, string message)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/query", new
        {
            source = "transactions",
            groupBy = new[] { new { column = "booked_on", bucket = "month", @as = "month" } },
            measures = new[] { new { name = "x", formula } }
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(response.IsSuccessStatusCode, body.ToString());
        Assert.Equal(message, body.GetProperty("error").GetProperty("message").GetString());
    }

    private static async Task PostAsync(ApiIntegrationTestHost host, string table, object row)
    {
        using var response = await host.Client.PostAsJsonAsync($"/api/v1/data/{table}", row);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresNewFunctionsTests
{
    [PostgresFact]
    public Task OnPostgres() => NewFunctionsTests.RunAsync(ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "x",
        ["Schema:AutoMigrateOnStartup"] = "false"
    }));
}
