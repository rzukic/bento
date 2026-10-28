using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Bento.Tests;

/// <summary>
/// Indexes for the searches formulas make per row and period (related rows,
/// LOOKUP_ASOF), and the problem log of slow queries. A weekly portfolio chart
/// over three years made thousands of lookups, each reading the whole prices
/// table, and ran past the 30 s limit.
/// </summary>
public sealed class QueryIndexTests
{
    internal const string Schema = """
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

    internal const string Depot =
        "SUM(SUM(trades.shares * LOOKUP_ASOF(\"prices\", \"isin\", trades.isin, \"date\", BUCKET_END(), \"price\") WHERE trades.date <= BUCKET_END()))";

    internal static async Task SeedAsync(ApiIntegrationTestHost host)
    {
        await host.ApplySchemaAsync(Schema);
        await PostAsync(host, "securities", new { isin = "IE00B4L5Y983" });
        await PostAsync(host, "prices", new { isin = "IE00B4L5Y983", date = "2026-01-01", price = 10.0 });
        await PostAsync(host, "prices", new { isin = "IE00B4L5Y983", date = "2026-01-20", price = 20.0 });
        // Typed in lower case: the lookup still finds the prices.
        await PostAsync(host, "trades", new { date = "2026-01-02", security_id = 1, isin = "ie00b4l5y983", shares = 2.0 });
    }

    /// <summary>The value at the end of each week: 2 shares at 10, then at 20.</summary>
    internal static async Task<JsonElement> WeeklyDepotAsync(ApiIntegrationTestHost host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/query")
        {
            Content = JsonContent.Create(new
            {
                source = "securities",
                asOf = new { bucket = "week", from = "2026-01-05", to = "2026-02-01", @as = "week" },
                measures = new[] { new { name = "value", formula = Depot } }
            })
        };
        request.Headers.Add("X-Bento-Source", Uri.EscapeDataString("/dashboards/6?interval.net=week · Depotwert"));
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        Assert.Equal(
            ["2026-01-05 20", "2026-01-12 20", "2026-01-19 40", "2026-01-26 40"],
            body.GetProperty("data").EnumerateArray().Select(r => $"{r.GetProperty("week").GetString()} {r.GetProperty("value").GetDecimal()}"));
        return body;
    }

    /// <summary>
    /// A value per day over three years: more periods than SQLite joins with UNION ALL
    /// (500), and than the old limit (1000), all of them without asking for more
    /// rows. Over 5000 periods is still refused.
    /// </summary>
    internal static async Task DailyDepotAsync(ApiIntegrationTestHost host)
    {
        async Task<(HttpResponseMessage Response, JsonElement Body)> Daily(string from)
        {
            var response = await host.Client.PostAsJsonAsync("/api/v1/query", new
            {
                source = "securities",
                asOf = new { bucket = "day", from, to = "2026-10-01", @as = "day" },
                measures = new[] { new { name = "value", formula = Depot } }
            });
            return (response, await response.Content.ReadFromJsonAsync<JsonElement>());
        }

        var (ok, body) = await Daily("2023-10-01");
        Assert.True(ok.IsSuccessStatusCode, body.ToString());
        var days = body.GetProperty("data").EnumerateArray().ToDictionary(r => r.GetProperty("day").GetString()!, r => r.GetProperty("value").GetDecimal());
        Assert.False(body.GetProperty("meta").GetProperty("truncated").GetBoolean());
        Assert.Equal(1097, days.Count);
        Assert.Equal(0m, days["2026-01-01"]);
        Assert.Equal(20m, days["2026-01-19"]);
        Assert.Equal(40m, days["2026-01-20"]);
        Assert.Equal(40m, days["2026-10-01"]);

        var (refused, error) = await Daily("2000-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("At most 5000 periods", error.ToString());
    }

    internal static async Task PostAsync(ApiIntegrationTestHost host, string table, object row)
    {
        using var response = await host.Client.PostAsJsonAsync($"/api/v1/data/{table}", row);
        Assert.True(response.IsSuccessStatusCode, $"{table}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task OnSqlite_TheLinkGetsAnIndex_TheLowerCasedLookupDoesNot()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "sqlite-test-secret"
        });
        await SeedAsync(host);
        await WeeklyDepotAsync(host);

        Assert.Equal(1L, await host.QueryDataScalarAsync<long>("SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'bento_ref_trades_security_id'"));
        // bento_lower is Bento's own function: an index on it would break other programs writing the file.
        Assert.Equal(0L, await host.QueryDataScalarAsync<long>("SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name LIKE 'bento_lookup_%'"));
    }

    [Fact]
    public async Task DailyValues_OverYears()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "sqlite-test-secret"
        });
        await SeedAsync(host);
        await DailyDepotAsync(host);
    }

    [Fact]
    public async Task OnSqlite_AQueryPastTheLimit_IsStopped_AndSaysSo()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "sqlite-test-secret",
            ["Database:QueryTimeoutSeconds"] = "1"
        });
        await SeedAsync(host);
        // Minutes of work: every week of 20 years, 2000 trades, each a lookup no index serves.
        await host.ExecuteDataSqlAsync("""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000)
            INSERT INTO trades (date, security_id, isin, shares) SELECT '2000-01-01', 1, 'X', i FROM n;
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 5000)
            INSERT INTO prices (isin, date, price) SELECT 'P' || i, '2000-01-01', i FROM n;
            """);

        var watch = Stopwatch.StartNew();
        using var response = await host.Client.PostAsJsonAsync("/api/v1/query", new
        {
            source = "securities",
            asOf = new { bucket = "week", from = "2007-01-01", to = "2026-01-01", @as = "week" },
            measures = new[] { new { name = "n", formula = "SUM(SUM(trades.shares * LOOKUP_ASOF(\"prices\", \"price\", trades.shares + 3000, \"date\", BUCKET_END(), \"price\")))" } }
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Contains("TIMEOUT", body);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");

        // The next query runs as usual.
        using var next = await host.Client.PostAsJsonAsync("/api/v1/query", new { source = "trades", measures = new[] { new { name = "n", fn = "count" } } });
        Assert.True(next.IsSuccessStatusCode, await next.Content.ReadAsStringAsync());
        Assert.Equal(2001, (await next.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")[0].GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task SlowQueries_AreLogged_WithWhereTheyCameFrom_ForAdmins()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "sqlite-test-secret",
            // Every query counts as slow here.
            ["Backend:SlowQueryMs"] = "-1"
        });
        await SeedAsync(host);
        await WeeklyDepotAsync(host);

        var list = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/problems");
        Assert.Equal(-1, list.GetProperty("meta").GetProperty("slowMs").GetInt32());
        var problem = list.GetProperty("data")[0];
        Assert.Equal("slow", problem.GetProperty("kind").GetString());
        Assert.Equal("/api/v1/query", problem.GetProperty("path").GetString());
        Assert.Equal("/dashboards/6?interval.net=week · Depotwert", problem.GetProperty("source").GetString());
        Assert.StartsWith("Query on securities took ", problem.GetProperty("message").GetString());
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Contains("\"source\":\"securities\"", detail);
        Assert.Contains("LOOKUP_ASOF", detail);
        Assert.Contains("SQL:", detail);

        using var cleared = await host.Client.DeleteAsync("/api/v1/admin/problems");
        Assert.True(cleared.IsSuccessStatusCode);
        Assert.Equal(0, (await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/problems")).GetProperty("data").GetArrayLength());
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresQueryIndexTests
{
    private static Task<ApiIntegrationTestHost> StartAsync() => ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "postgres-test-secret",
        ["Schema:AutoMigrateOnStartup"] = "false"
    });

    [PostgresFact]
    public async Task LookupAsOf_AndLinks_GetTheirIndexes_AndStillMatchIgnoringCase()
    {
        await using var host = await StartAsync();
        await QueryIndexTests.SeedAsync(host);
        Assert.Equal(0L, await IndexCountAsync(host));

        await QueryIndexTests.WeeklyDepotAsync(host);

        Assert.Equal(2L, await IndexCountAsync(host));
        var lookup = await host.QuerySystemScalarAsync<string>(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'bento_lookup_prices_isin_date'");
        Assert.Contains("lower(isin), date", lookup);
        var link = await host.QuerySystemScalarAsync<string>(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'bento_ref_trades_security_id'");
        Assert.Contains("(security_id)", link);
    }

    [PostgresFact]
    public async Task DailyValues_OverYears()
    {
        await using var host = await StartAsync();
        await QueryIndexTests.SeedAsync(host);
        await QueryIndexTests.DailyDepotAsync(host);
    }

    [PostgresFact]
    public async Task A_CancelledRequest_StopsItsQueryInTheDatabase()
    {
        await using var host = await StartAsync();
        await QueryIndexTests.SeedAsync(host);
        await host.ExecuteDataSqlAsync("""
            INSERT INTO trades (date, security_id, isin, shares) SELECT DATE '2000-01-01', 1, 'X', g FROM generate_series(1, 2000) g;
            INSERT INTO prices (isin, date, price) SELECT 'P' || g, DATE '2000-01-01', g FROM generate_series(1, 5000) g;
            """);

        // Each of ~1000 weeks, each of 2000 trades, a lookup by a number that no index serves (each trade a different one): minutes of work.
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        var watch = Stopwatch.StartNew();
        var request = host.Client.PostAsJsonAsync("/api/v1/query", new
        {
            source = "securities",
            asOf = new { bucket = "week", from = "2007-01-01", to = "2026-01-01", @as = "week" },
            measures = new[] { new { name = "n", formula = "SUM(SUM(trades.shares * LOOKUP_ASOF(\"prices\", \"price\", trades.shares + 3000, \"date\", BUCKET_END(), \"price\")))" } }
        }, cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => Assert.Fail(await (await request).Content.ReadAsStringAsync()));

        // The database stops too, instead of running on for nobody.
        long running = 1;
        while (watch.Elapsed < TimeSpan.FromSeconds(15) && (running = await RunningQueriesAsync(host)) > 0)
            await Task.Delay(200);
        Assert.Equal(0, running);
    }

    /// <summary>The data role's running queries on the prices table; the admin sees what other roles run.</summary>
    private static async Task<long> RunningQueriesAsync(ApiIntegrationTestHost host)
    {
        var database = await host.QuerySystemScalarAsync<string>("SELECT current_database()");
        await using var admin = new NpgsqlConnection(PostgresTestSettings.FromEnvironment().BuildAdminConnectionString(database));
        return await admin.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND state = 'active' AND pid <> pg_backend_pid() AND query LIKE '%FROM \"prices\"%'");
    }

    private static Task<long> IndexCountAsync(ApiIntegrationTestHost host) =>
        host.QuerySystemScalarAsync<long>("SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname LIKE 'bento\\_%'");
}
