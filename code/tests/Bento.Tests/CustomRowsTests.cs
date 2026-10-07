using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bento.Api.Formula;
using Bento.Api.Formula.Sandbox;
using Bento.Api.Workers;

namespace Bento.Tests;

/// <summary>
/// Step 4.3b: functions over a group of rows (<c>CUSTOM_ROWS</c>) and the cost
/// basis templates. Trades of one security, by hand:
/// buy 10 @ 100 + 1, buy 10 @ 120 + 1, sell 5 @ 130 − 2, sell 15 @ 90.
/// Average cost: 2202 for 20 shares (110.10 each); the first sale gains
/// 650 − 2 − 550.50 = 97.50, the second 1350 − 1651.50 = −301.50.
/// FIFO: the first sale uses 5 shares of the first lot (100.10 each), gaining
/// 147.50; the second 5 × 100.10 + 10 × 120.10, losing 351.50. Both lose 204 in all.
/// </summary>
public sealed class CustomRowsTests
{
    private static readonly double[] Shares = [10, 10, -5, -15];
    private static readonly double[] Prices = [100, 120, 130, 90];
    private static readonly double[] Fees = [1, 1, 2, 0];

    private static IReadOnlyList<object?> Template(string id, string what)
    {
        var template = FunctionTemplates.All.Single(t => t.Id == id);
        var definition = new CustomFunctionDef(template.Name, null, template.Arguments, template.ResultType, template.Code, 1) { Mode = template.Mode };
        var (function, error) = JsFunction.Create(definition);
        Assert.True(function is not null, error);
        using (function)
        {
            var columns = new List<IReadOnlyList<object?>>
            {
                Shares.Select(s => (object?)(decimal)s).ToList(),
                Prices.Select(p => (object?)(decimal)p).ToList(),
                Fees.Select(f => (object?)(decimal)f).ToList(),
                Shares.Select(_ => (object?)what).ToList()
            };
            var (values, failure, _) = function!.CallRows(columns, Shares.Length);
            Assert.True(values is not null, failure);
            return values!;
        }
    }

    private static string[] Text(IReadOnlyList<object?> values) => values.Select(v => Convert.ToDecimal(v).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    [Fact]
    public void AverageCost_Template()
    {
        Assert.Equal(["0", "0", "97.5", "-301.5"], Text(Template("avg-cost", "realized")));
        Assert.Equal(["1001", "1201", "-550.5", "-1651.5"], Text(Template("avg-cost", "cost")));
        Assert.Equal(["10", "20", "15", "0"], Text(Template("avg-cost", "held")));
        Assert.Equal(["100.1", "110.1", "110.1", "0"], Text(Template("avg-cost", "average")));
    }

    [Fact]
    public void Fifo_Template()
    {
        Assert.Equal(["0", "0", "147.5", "-351.5"], Text(Template("fifo-cost", "realized")));
        Assert.Equal(["1001", "1201", "-500.5", "-1701.5"], Text(Template("fifo-cost", "cost")));
        Assert.Equal(["100.1", "110.1", "113.43", "0"], Text(Template("fifo-cost", "average")));
    }

    [Fact]
    public void RowsFunctions_ReturnAValuePerRow()
    {
        var definition = new CustomFunctionDef("f", null, [new("n", "float")], "float", "return n.slice(1);", 1) { Mode = "rows" };
        var (function, _) = JsFunction.Create(definition);
        using (function)
        {
            var (values, error, _) = function!.CallRows([new List<object?> { 1m, 2m, 3m }], 3);
            Assert.Null(values);
            Assert.Equal("The function returned 2 values for 3 rows; return a list with a value per row.", error);
        }
    }

    private const string Schema = """
        {
            "tables": [
                {
                    "name": "trades",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "account", "type": "text" },
                        { "name": "isin", "type": "text" },
                        { "name": "date", "type": "date" },
                        { "name": "shares", "type": "float" },
                        { "name": "price", "type": "currency" },
                        { "name": "fees", "type": "currency" },
                        { "name": "realized", "type": "computed", "formula": "CUSTOM_ROWS(\"avg_cost\", PARTITION(account, isin), ORDER(date, id), shares, price, COALESCE(fees, 0), \"realized\")" },
                        { "name": "cost_change", "type": "computed", "formula": "CUSTOM_ROWS(\"avg_cost\", PARTITION(account, isin), ORDER(date, id), shares, price, COALESCE(fees, 0), \"cost\")" },
                        { "name": "fifo", "type": "computed", "formula": "CUSTOM_ROWS(\"fifo_cost\", PARTITION(account, isin), ORDER(date, id), shares, price, COALESCE(fees, 0), \"realized\")" }
                    ]
                }
            ]
        }
        """;

    internal static async Task RunAsync(ApiIntegrationTestHost host)
    {
        foreach (var id in new[] { "avg-cost", "fifo-cost" })
        {
            var template = FunctionTemplates.All.Single(t => t.Id == id);
            var created = await SendAsync(host, HttpMethod.Post, "/api/v1/functions", new
            {
                template.Name,
                template.Arguments,
                template.ResultType,
                template.Code,
                template.Mode
            }, HttpStatusCode.Created);
            Assert.Equal("rows", created.GetProperty("mode").GetString());
        }
        await SendAsync(host, HttpMethod.Post, "/api/v1/functions", new
        {
            name = "double_it",
            arguments = new[] { new { name = "n", type = "float" } },
            resultType = "float",
            code = "return n * 2;"
        }, HttpStatusCode.Created);
        await host.ApplySchemaAsync(Schema);

        // Inserted out of order: the function sees each group by date, then id.
        var rows = new (string Account, string Isin, string Date, double Shares, double Price, double? Fees)[]
        {
            ("A", "X1", "2026-04-01", -15, 90, null),
            ("A", "X1", "2026-01-10", 10, 100, 1),
            ("B", "X1", "2026-02-01", 2, 50, 0),
            ("A", "X1", "2026-03-01", -5, 130, 2),
            ("A", "X1", "2026-02-10", 10, 120, 1),
        };
        foreach (var r in rows)
            await PostAsync(host, "trades", new { account = r.Account, isin = r.Isin, date = r.Date, shares = r.Shares, price = r.Price, fees = r.Fees });

        await host.GetService<StoredCustomValues>().RunAsync();
        Assert.Equal(
            ["A 2026-01-10 0 1001 0", "A 2026-02-10 0 1201 0", "A 2026-03-01 97.5 -550.5 147.5", "A 2026-04-01 -301.5 -1651.5 -351.5", "B 2026-02-01 0 100 0"],
            await TradesAsync(host));

        // Measures read the stored values: realised gains and the cost of what is held.
        var totals = await QueryAsync(host, new
        {
            source = "trades",
            groupBy = new[] { new { column = "account" } },
            measures = new object[] { new { name = "realized", formula = "SUM(realized)" }, new { name = "cost", formula = "SUM(cost_change)" } }
        });
        Assert.Equal(["A -204 0", "B 0 100"], Rows(totals, "account", "realized", "cost"));

        // A second run computes nothing new.
        await host.GetService<StoredCustomValues>().RunAsync();
        Assert.Equal(0, (await GetDataAsync(host, "/api/v1/functions/status")).GetProperty("lastUpdated").GetInt32());

        // A changed first buy changes the later sales of its group, not the other group.
        using (var patch = await host.Client.PatchAsJsonAsync("/api/v1/data/trades/2", new { price = 90 }))
            Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());
        await host.GetService<StoredCustomValues>().RunAsync();
        Assert.Equal(
            ["A 2026-01-10 0 901 0", "A 2026-02-10 0 1201 0", "A 2026-03-01 122.5 -525.5 197.5", "A 2026-04-01 -226.5 -1576.5 -301.5", "B 2026-02-01 0 100 0"],
            await TradesAsync(host));

        // A deleted sale: the group is computed again without it.
        using (var delete = await host.Client.DeleteAsync("/api/v1/data/trades/4"))
            Assert.True(delete.IsSuccessStatusCode);
        await host.GetService<StoredCustomValues>().RunAsync();
        Assert.Equal(
            ["A 2026-01-10 0 901 0", "A 2026-02-10 0 1201 0", "A 2026-04-01 -226.5 -1576.5 -151.5", "B 2026-02-01 0 100 0"],
            await TradesAsync(host));

        // The editor's preview runs the whole group of each shown row.
        var preview = await SendAsync(host, HttpMethod.Post, "/api/v1/formulas/preview", new
        {
            table = "trades",
            formula = "CUSTOM_ROWS(\"avg_cost\", PARTITION(account, isin), ORDER(date, id), shares, price, COALESCE(fees, 0), \"held\")",
            limit = 2
        });
        Assert.Equal(["5", "10"], preview.GetProperty("rows").EnumerateArray().Select(r => FormulaConformanceTests.Text(r.GetProperty("value"))));

        // The test run takes a list per argument.
        var test = await SendAsync(host, HttpMethod.Post, "/api/v1/functions/test", new
        {
            arguments = new[] { new { name = "n", type = "float" } },
            resultType = "float",
            mode = "rows",
            code = "let sum = 0;\nreturn n.map((x) => (sum += x));",
            values = new object[] { new[] { 1, 2, 3 } }
        });
        Assert.Equal([1, 3, 6], test.GetProperty("value").EnumerateArray().Select(v => v.GetInt32()));

        // Mistakes say how to call it.
        Assert.Equal("avg_cost runs over a group of rows; call it as CUSTOM_ROWS(\"avg_cost\", PARTITION(…), ORDER(…), …).",
            await ValidateAsync(host, "CUSTOM(\"avg_cost\", shares, price, fees, \"realized\")"));
        Assert.Equal("double_it runs on one row; call it as CUSTOM(\"double_it\", …), or let it run over a group of rows in its editor.",
            await ValidateAsync(host, "CUSTOM_ROWS(\"double_it\", PARTITION(isin), shares)"));
        Assert.Equal("avg_cost takes 4 arguments (shares, price, fees, what), not 2.",
            await ValidateAsync(host, "CUSTOM_ROWS(\"avg_cost\", PARTITION(isin), ORDER(date), shares, price)"));
        Assert.Equal("PARTITION(…) is part of CUSTOM_ROWS: CUSTOM_ROWS(\"avg_cost\", PARTITION(isin), ORDER(date), …).",
            await ValidateAsync(host, "PARTITION(isin)"));
        Assert.Equal("CUSTOM_ROWS(…) must be the whole formula. Use its result in another computed column.",
            await ValidateAsync(host, "1 + CUSTOM_ROWS(\"avg_cost\", PARTITION(isin), ORDER(date), shares, price, fees, \"cost\")"));

        using var measure = await host.Client.PostAsJsonAsync("/api/v1/query", new
        {
            source = "trades",
            measures = new[] { new { name = "x", formula = "CUSTOM_ROWS(\"avg_cost\", PARTITION(isin), ORDER(date), shares, price, fees, \"cost\")" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, measure.StatusCode);
        Assert.Equal("CUSTOM_ROWS works in computed columns: it gives each row a value from its group's rows.",
            (await measure.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("message").GetString());

        var network = await SendAsync(host, HttpMethod.Post, "/api/v1/functions", new
        {
            name = "fetching_rows",
            resultType = "float",
            mode = "rows",
            allowedHosts = new[] { "example.com" },
            code = "return [];"
        }, HttpStatusCode.BadRequest);
        Assert.Equal("A function over rows computes from the rows; it cannot call the internet.", network.GetProperty("message").GetString());

        // Both columns are listed as usages of the function.
        var function = await GetDataAsync(host, "/api/v1/functions/avg_cost");
        Assert.Equal(["trades.cost_change", "trades.realized"], function.GetProperty("usedBy").EnumerateArray()
            .Select(u => $"{u.GetProperty("table").GetString()}.{u.GetProperty("column").GetString()}").Order());
    }

    [Fact]
    public async Task CustomRows_OnSqlite()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?> { ["Backend:AuthMode"] = "open", ["Schema:AutoMigrateOnStartup"] = "false" });
        await RunAsync(host);
    }

    // ── Helpers ──

    private static async Task<string> ValidateAsync(ApiIntegrationTestHost host, string formula)
    {
        var result = await SendAsync(host, HttpMethod.Post, "/api/v1/formulas/validate", new { table = "trades", formula });
        return result.GetProperty("errors")[0].GetProperty("message").GetString()!;
    }

    private static async Task<string[]> TradesAsync(ApiIntegrationTestHost host) =>
        (await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/trades?sort=account,date&limit=50")).GetProperty("data").EnumerateArray()
            .Select(a => string.Join(" ", new[] { "account", "date", "realized", "cost_change", "fifo" }.Select(c => FormulaConformanceTests.Text(a.GetProperty(c)))))
            .ToArray();

    private static async Task<JsonElement> GetDataAsync(ApiIntegrationTestHost host, string path) =>
        (await host.Client.GetFromJsonAsync<JsonElement>(path)).GetProperty("data");

    private static async Task PostAsync(ApiIntegrationTestHost host, string table, object body)
    {
        using var response = await host.Client.PostAsJsonAsync($"/api/v1/data/{table}", body);
        Assert.True(response.IsSuccessStatusCode, $"{table}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<JsonElement> SendAsync(ApiIntegrationTestHost host, HttpMethod method, string path, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"{method} {path}: {(int)response.StatusCode} {text}");
        var json = JsonDocument.Parse(text).RootElement.Clone();
        return json.TryGetProperty("error", out var error) ? error : json.GetProperty("data");
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
public sealed class PostgresCustomRowsTests
{
    [PostgresFact]
    public async Task CustomRows_OnPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await CustomRowsTests.RunAsync(host);
    }
}
