using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Step 3.2 acceptance: monthly income and expenses, totals by category
/// (named by their record label), running balances with and without the
/// rows before the date range, date buckets, validation and saved queries.
/// The same scenario runs on SQLite and Postgres (<see cref="PostgresAggregationQueryTests"/>).
/// </summary>
public sealed class AggregationQueryTests
{
    internal const string Schema = """
        {
            "tables": [
                {
                    "name": "categories",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false, "unique": true }
                    ]
                },
                {
                    "name": "transactions",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "booked_on", "type": "date", "nullable": false },
                        { "name": "recorded_at", "type": "datetime" },
                        { "name": "counterparty", "type": "text" },
                        { "name": "amount", "type": "currency", "nullable": false },
                        { "name": "quantity", "type": "integer" },
                        { "name": "category_id", "type": "reference", "references": "categories" }
                    ]
                }
            ],
            "relations": [
                { "name": "category_transactions", "type": "1:N", "from": "categories", "to": "transactions", "column": "category_id" }
            ]
        }
        """;

    internal static async Task SeedAsync(ApiIntegrationTestHost host)
    {
        foreach (var name in new[] { "Gehalt", "Miete", "Lebensmittel" })
            (await host.Client.PostAsJsonAsync("/api/v1/data/categories", new { name })).EnsureSuccessStatusCode();

        object[] rows =
        [
            new { booked_on = "2026-08-01", amount = 3000.00m, category_id = 1, counterparty = "Arbeitgeber", quantity = 1 },
            new { booked_on = "2026-08-03", amount = -1000.00m, category_id = 2, counterparty = "Vermieter", quantity = 1 },
            new { booked_on = "2026-08-15", amount = -50.10m, category_id = 3, counterparty = "REWE", quantity = 3 },
            new { booked_on = "2026-09-01", amount = 3000.00m, category_id = 1, counterparty = "Arbeitgeber", quantity = 1 },
            new { booked_on = "2026-09-02", amount = -1000.00m, category_id = 2, counterparty = "Vermieter", quantity = 1 },
            new { booked_on = "2026-09-10", amount = -45.90m, category_id = 3, counterparty = "REWE", quantity = 2, recorded_at = "2026-09-30T23:30:00" },
            new { booked_on = "2026-09-11", amount = -3.20m, category_id = 3, counterparty = "Bäckerei", quantity = 1, recorded_at = "2026-10-01T00:15:00" },
            new { booked_on = "2026-10-01", amount = 3100.00m, category_id = 1, counterparty = "Arbeitgeber", quantity = 1 },
            new { booked_on = "2026-10-05", amount = -20.05m, category_id = 3, counterparty = "REWE", quantity = 2 },
            new { booked_on = "2026-10-20", amount = -10.00m, counterparty = "Kiosk", quantity = 1 }
        ];
        foreach (var row in rows)
            (await host.Client.PostAsJsonAsync("/api/v1/data/transactions", row)).EnsureSuccessStatusCode();
    }

    // ── Scenario shared with Postgres ──

    internal static async Task MonthlyExpenses(ApiIntegrationTestHost host)
    {
        var result = await QueryAsync(host, new
        {
            source = "transactions",
            filter = new { amount = new { lt = 0 } },
            groupBy = new[] { new { column = "booked_on", bucket = "month", @as = "month" } },
            measures = new[] { new { name = "spent", fn = "sum", column = "amount" }, new { name = "bookings", fn = "count", column = (string?)null } }
        });

        Assert.Equal(
            ["2026-08-01 -1050.1 2", "2026-09-01 -1049.1 3", "2026-10-01 -30.05 2"],
            Rows(result, "month", "spent", "bookings"));
        var columns = result.GetProperty("meta").GetProperty("columns").EnumerateArray()
            .Select(c => $"{c.GetProperty("name").GetString()}:{c.GetProperty("type").GetString()}:{c.GetProperty("role").GetString()}");
        Assert.Equal(["month:date:group", "spent:currency:measure", "bookings:integer:measure"], columns);
    }

    internal static async Task TotalsByCategory(ApiIntegrationTestHost host)
    {
        var result = await QueryAsync(host, new
        {
            source = "transactions",
            groupBy = new[] { new { column = "category_id" } },
            measures = new[] { new { name = "total", fn = "sum", column = "amount" }, new { name = "avg", fn = "avg", column = "amount" } },
            sort = new[] { "-total" }
        });

        Assert.Equal(
            ["1 Gehalt 9100 3033.3333333333", "  -10 -10", "3 Lebensmittel -119.25 -29.8125", "2 Miete -2000 -1000"],
            Rows(result, "category_id", "category_id_label", "total", "avg"));
    }

    /// <summary>Step 3.6c: median and percentiles (interpolated like Postgres' percentile_cont), as quick functions and in formulas.</summary>
    internal static async Task MedianAndPercentiles(ApiIntegrationTestHost host)
    {
        var quick = await QueryAsync(host, new
        {
            source = "transactions",
            groupBy = new[] { new { column = "category_id" } },
            measures = new object[]
            {
                new { name = "median", fn = "median", column = "amount" },
                new { name = "p90", fn = "percentile", column = "amount", percentile = 90 },
                new { name = "items", fn = "median", column = "quantity" }
            },
            sort = new[] { "median" }
        });
        // Lebensmittel: -50.10, -45.90, -20.05, -3.20 → median halfway between the middle two, p90 at 2.7 of 3.
        Assert.Equal(
            ["2 Miete -1000 -1000 1", "3 Lebensmittel -32.975 -8.255 2", "  -10 -10 1", "1 Gehalt 3000 3080 1"],
            Rows(quick, "category_id", "category_id_label", "median", "p90", "items"));
        var types = quick.GetProperty("meta").GetProperty("columns").EnumerateArray()
            .Where(c => c.GetProperty("role").GetString() == "measure")
            .Select(c => $"{c.GetProperty("name").GetString()}:{c.GetProperty("type").GetString()}");
        Assert.Equal(["median:currency", "p90:currency", "items:float"], types);

        var formula = await QueryAsync(host, new
        {
            source = "transactions",
            groupBy = new[] { new { column = "booked_on", bucket = "month", @as = "month" } },
            measures = new[]
            {
                new { name = "typical_expense", formula = "MEDIAN(amount WHERE amount < 0)" },
                new { name = "small_expense", formula = "PERCENTILE(amount, 90 WHERE amount < 0)" }
            }
        });
        Assert.Equal(
            ["2026-08-01 -525.05 -145.09", "2026-09-01 -45.9 -11.74", "2026-10-01 -15.025 -11.005"],
            Rows(formula, "month", "typical_expense", "small_expense"));

        // Over related rows: the median per category, added up per group (one category each).
        var related = await QueryAsync(host, new
        {
            source = "categories",
            groupBy = new[] { new { column = "name" } },
            measures = new[] { new { name = "median", formula = "SUM(MEDIAN(transactions.amount))" } }
        });
        Assert.Equal(["Gehalt 3000", "Lebensmittel -32.975", "Miete -1000"], Rows(related, "name", "median"));
    }

    /// <summary>Step 3.6b: the N largest (or smallest) groups, the rest as "Other".</summary>
    internal static async Task TopValues(ApiIntegrationTestHost host)
    {
        var withOther = await QueryAsync(host, new
        {
            source = "transactions",
            groupBy = new[] { new { column = "counterparty", top = 2, other = true, rank = "asc" } },
            measures = new[] { new { name = "total", fn = "sum", column = "amount" } },
            sort = new[] { "-total" }
        });
        Assert.Equal([" true 9086.8", "REWE false -116.05", "Vermieter false -2000"], Rows(withOther, "counterparty", "counterparty_other", "total"));
        Assert.Contains(withOther.GetProperty("meta").GetProperty("columns").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "counterparty_other" && c.GetProperty("role").GetString() == "other");

        // Without "other" the rest is left out; ranking by a formula and per category label.
        var top = await QueryAsync(host, new
        {
            source = "transactions",
            groupBy = new[] { new { column = "category_id", top = 1, other = false, rank = (string?)null } },
            measures = new[] { new { name = "bookings", formula = "COUNT()" } },
            sort = new[] { "-bookings" }
        });
        Assert.Equal(["3 Lebensmittel 4", "  1"], Rows(top, "category_id", "category_id_label", "bookings"));

        var topPerCategoryLabel = await QueryAsync(host, new
        {
            source = "transactions",
            groupBy = new[] { new { column = "category_id", top = 1, other = true, rank = "desc" } },
            measures = new[] { new { name = "total", fn = "sum", column = "amount" } },
            sort = new[] { "-total" }
        });
        Assert.Equal(["1 Gehalt false 9100", "  false -10", "  true -2119.25"], Rows(topPerCategoryLabel, "category_id", "category_id_label", "category_id_other", "total"));
    }

    internal static async Task RunningBalance(ApiIntegrationTestHost host)
    {
        var all = await QueryAsync(host, new
        {
            source = "transactions",
            groupBy = new[] { new { column = "booked_on", bucket = "month", @as = "month" } },
            measures = new object[]
            {
                new { name = "net", fn = "sum", column = "amount" },
                new { name = "balance", fn = "sum", column = "amount", running = true }
            }
        });
        Assert.Equal(["2026-08-01 1949.9 1949.9", "2026-09-01 1950.9 3900.8", "2026-10-01 3069.95 6970.75"], Rows(all, "month", "net", "balance"));

        // From September on: within the range the balance restarts at zero;
        // with carryIn it starts from what August left.
        var fromSeptember = await QueryAsync(host, new
        {
            source = "transactions",
            filter = new { booked_on = new { gte = "2026-09-01" } },
            groupBy = new[] { new { column = "booked_on", bucket = "month", @as = "month" } },
            measures = new object[]
            {
                new { name = "within", fn = "sum", column = "amount", running = true },
                new { name = "balance", fn = "sum", column = "amount", running = true, carryIn = true }
            },
            sort = new[] { "-month" }
        });
        Assert.Equal(["2026-10-01 5020.85 6970.75", "2026-09-01 1950.9 3900.8"], Rows(fromSeptember, "month", "within", "balance"));
    }

    internal static async Task RunningTotalsPerCategory(ApiIntegrationTestHost host)
    {
        var result = await QueryAsync(host, new
        {
            source = "transactions",
            filter = new { category_id = new { @in = new[] { 1, 3 } }, booked_on = new { gte = "2026-09-01" } },
            groupBy = new object[] { new { column = "category_id" }, new { column = "booked_on", bucket = "month", @as = "month" } },
            measures = new[] { new { name = "cumulative", fn = "sum", column = "amount", running = true, carryIn = true } },
            sort = new[] { "category_id", "month" }
        });

        Assert.Equal(
            ["1 2026-09-01 6000", "1 2026-10-01 9100", "3 2026-09-01 -99.2", "3 2026-10-01 -119.25"],
            Rows(result, "category_id", "month", "cumulative"));
    }

    internal static async Task Buckets(ApiIntegrationTestHost host)
    {
        async Task<string[]> Bucketed(string column, string bucket) => Rows(await QueryAsync(host, new
        {
            source = "transactions",
            filter = new Dictionary<string, object> { [column] = new { notnull = true } },
            groupBy = new[] { new { column, bucket, @as = "start" } },
            measures = new[] { new { name = "n", fn = "count", column = (string?)null } }
        }), "start", "n");

        Assert.Equal(["2026-07-01 7", "2026-10-01 3"], await Bucketed("booked_on", "quarter"));
        Assert.Equal(["2026-01-01 10"], await Bucketed("booked_on", "year"));
        // 10 and 11 September 2026 are Thursday and Friday: the week starts Monday the 7th.
        Assert.Contains("2026-09-07 2", await Bucketed("booked_on", "week"));
        Assert.Equal(["2026-09-30 1", "2026-10-01 1"], await Bucketed("recorded_at", "day"));
        // Date-times are bucketed as written: 23:30 on 30 September stays in September.
        Assert.Equal(["2026-09-01 1", "2026-10-01 1"], await Bucketed("recorded_at", "month"));
    }

    internal static async Task MinMaxDistinct(ApiIntegrationTestHost host)
    {
        var result = await QueryAsync(host, new
        {
            source = "transactions",
            measures = new object[]
            {
                new { name = "first", fn = "min", column = "booked_on" },
                new { name = "last", fn = "max", column = "booked_on" },
                new { name = "biggest", fn = "max", column = "amount" },
                new { name = "payees", fn = "count_distinct", column = "counterparty" },
                new { name = "items", fn = "sum", column = "quantity" }
            }
        });
        Assert.Equal(["2026-08-01 2026-10-20 3100 5 14"], Rows(result, "first", "last", "biggest", "payees", "items"));
    }

    [Fact]
    public async Task Scenario_OnSqlite()
    {
        await using var host = await CreateHostAsync();
        await MonthlyExpenses(host);
        await TotalsByCategory(host);
        await RunningBalance(host);
        await TopValues(host);
        await MedianAndPercentiles(host);
        await RunningTotalsPerCategory(host);
        await Buckets(host);
        await MinMaxDistinct(host);
    }

    [Theory]
    [InlineData("""{ "source": "nope", "measures": [{ "fn": "count" }] }""", 404, "source")]
    [InlineData("""{ "source": "transactions" }""", 400, "measures")]
    [InlineData("""{ "source": "transactions", "measures": [{ "fn": "sum", "column": "counterparty" }] }""", 400, "measures[0].column")]
    [InlineData("""{ "source": "transactions", "measures": [{ "fn": "mode", "column": "amount" }] }""", 400, "measures[0].fn")]
    [InlineData("""{ "source": "transactions", "measures": [{ "fn": "percentile", "column": "amount" }] }""", 400, "measures[0].percentile")]
    [InlineData("""{ "source": "transactions", "measures": [{ "fn": "percentile", "column": "amount", "percentile": 120 }] }""", 400, "measures[0].percentile")]
    [InlineData("""{ "source": "transactions", "measures": [{ "fn": "median", "column": "counterparty" }] }""", 400, "measures[0].column")]
    [InlineData("""{ "source": "transactions", "groupBy": [{ "column": "counterparty", "bucket": "month" }], "measures": [{ "fn": "count" }] }""", 400, "groupBy[0].bucket")]
    [InlineData("""{ "source": "transactions", "measures": [{ "fn": "sum", "column": "amount", "running": true }] }""", 400, "measures[0].running")]
    [InlineData("""{ "source": "transactions", "measures": [{ "fn": "count" }], "sort": ["nope"] }""", 400, "sort[0]")]
    [InlineData("""{ "source": "transactions", "filter": { "nope": 1 }, "measures": [{ "fn": "count" }] }""", 400, "filter.nope")]
    [InlineData("""{ "source": "transactions", "filter": { "amount": { "between": 1 } }, "measures": [{ "fn": "count" }] }""", 400, "filter.amount.between")]
    [InlineData("""{ "source": "transactions", "groupBy": [{ "column": "booked_on" }, { "column": "booked_on" }], "measures": [{ "fn": "count" }] }""", 400, "groupBy[1].as")]
    public async Task Invalid_QueriesNameTheField(string body, int status, string field)
    {
        await using var host = await CreateHostAsync();

        using var response = await host.Client.PostAsync("/api/v1/query", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(status, (int)response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(field, payload.GetProperty("error").GetProperty("details").GetProperty("field").GetString());
    }

    [Fact]
    public async Task Limit_TruncatesAndSays()
    {
        await using var host = await CreateHostAsync();

        var result = await QueryAsync(host, new
        {
            source = "transactions",
            groupBy = new[] { new { column = "booked_on" } },
            measures = new[] { new { name = "n", fn = "count", column = (string?)null } },
            limit = 3
        });

        Assert.Equal(3, result.GetProperty("data").GetArrayLength());
        Assert.True(result.GetProperty("meta").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task SavedQueries_RunWithExtraFilters_AndFollowRenames()
    {
        await using var host = await CreateHostAsync();
        var created = await PostAsync(host, "/api/v1/queries", new
        {
            name = "Monthly spending",
            query = new
            {
                source = "transactions",
                filter = new { amount = new { lt = 0 } },
                groupBy = new[] { new { column = "booked_on", bucket = "month", @as = "month" } },
                measures = new[] { new { name = "spent", fn = "sum", column = "amount" } }
            }
        }, HttpStatusCode.Created);
        var id = created.GetProperty("data").GetProperty("id").GetInt64();
        Assert.Equal("transactions", created.GetProperty("data").GetProperty("source").GetString());
        Assert.True(created.GetProperty("data").GetProperty("isOwner").GetBoolean());

        // A dashboard's date range comes in as an extra filter.
        var run = await PostAsync(host, $"/api/v1/queries/{id}/run", new { filter = new { booked_on = new { gte = "2026-09-01" } } }, HttpStatusCode.OK);
        Assert.Equal(["2026-09-01 -1049.1", "2026-10-01 -30.05"], Rows(run, "month", "spent"));

        var listed = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/queries?source=transactions");
        Assert.Equal("Monthly spending", Assert.Single(listed.GetProperty("data").EnumerateArray()).GetProperty("name").GetString());

        // Invalid queries are not stored.
        using var invalid = await host.Client.PostAsJsonAsync("/api/v1/queries", new { name = "Broken", query = new { source = "transactions", measures = new[] { new { fn = "sum", column = "counterparty" } } } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        // Renaming the table and a column in the schema keeps the query working.
        await host.ApplySchemaAsync(Schema
            .Replace("\"name\": \"transactions\",", "\"name\": \"bookings\", \"renamedFrom\": \"transactions\",")
            .Replace("\"to\": \"transactions\"", "\"to\": \"bookings\"")
            .Replace("{ \"name\": \"amount\", \"type\": \"currency\", \"nullable\": false }", "{ \"name\": \"value\", \"renamedFrom\": \"amount\", \"type\": \"currency\", \"nullable\": false }"));
        var renamed = await PostAsync(host, $"/api/v1/queries/{id}/run", new { }, HttpStatusCode.OK);
        Assert.Equal(["2026-08-01 -1050.1", "2026-09-01 -1049.1", "2026-10-01 -30.05"], Rows(renamed, "month", "spent"));
        var stored = await host.Client.GetFromJsonAsync<JsonElement>($"/api/v1/queries/{id}");
        Assert.Equal("bookings", stored.GetProperty("data").GetProperty("source").GetString());
        Assert.Equal("value", stored.GetProperty("data").GetProperty("query").GetProperty("measures")[0].GetProperty("column").GetString());

        using var deleted = await host.Client.DeleteAsync($"/api/v1/queries/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task SavedQueries_SharedOnesAreVisibleButOnlyTheOwnerChangesThem()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(Schema);
        (await host.Client.PostAsJsonAsync("/api/v1/auth/register", new { username = "bootstrap-admin", password = "supersecure" })).EnsureSuccessStatusCode();
        var admin = await LoginAsync(host, "bootstrap-admin", "supersecure");
        using (var createViewer = Request(HttpMethod.Post, "/api/v1/auth/register", admin, new { username = "viewer", password = "anotherpass", role = "viewer" }))
            (await host.Client.SendAsync(createViewer)).EnsureSuccessStatusCode();
        var viewer = await LoginAsync(host, "viewer", "anotherpass");

        var query = new { source = "transactions", measures = new[] { new { name = "n", fn = "count", column = (string?)null } } };
        long shared, mine;
        using (var create = Request(HttpMethod.Post, "/api/v1/queries", admin, new { name = "Shared", isShared = true, query }))
            shared = (await (await host.Client.SendAsync(create)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetInt64();
        using (var create = Request(HttpMethod.Post, "/api/v1/queries", admin, new { name = "Private", query }))
            mine = (await (await host.Client.SendAsync(create)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetInt64();

        using var list = Request(HttpMethod.Get, "/api/v1/queries", viewer);
        var visible = (await (await host.Client.SendAsync(list)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var only = Assert.Single(visible.EnumerateArray());
        Assert.Equal("Shared", only.GetProperty("name").GetString());
        Assert.False(only.GetProperty("isOwner").GetBoolean());

        using var runShared = Request(HttpMethod.Post, $"/api/v1/queries/{shared}/run", viewer, new { });
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(runShared)).StatusCode);
        using var runPrivate = Request(HttpMethod.Post, $"/api/v1/queries/{mine}/run", viewer, new { });
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.SendAsync(runPrivate)).StatusCode);
        using var deleteShared = Request(HttpMethod.Delete, $"/api/v1/queries/{shared}", viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(deleteShared)).StatusCode);
    }

    // ── Helpers ──

    internal static async Task<JsonElement> QueryAsync(ApiIntegrationTestHost host, object body) =>
        await PostAsync(host, "/api/v1/query", body, HttpStatusCode.OK);

    internal static async Task<JsonElement> PostAsync(ApiIntegrationTestHost host, string url, object body, HttpStatusCode expected)
    {
        using var response = await host.Client.PostAsJsonAsync(url, body);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {payload}");
        return payload;
    }

    /// <summary>Rows as "value value …" strings; numbers in their JSON form, null as empty.</summary>
    internal static string[] Rows(JsonElement result, params string[] columns) =>
        result.GetProperty("data").EnumerateArray()
            .Select(row => string.Join(" ", columns.Select(c => row.GetProperty(c) switch
            {
                { ValueKind: JsonValueKind.Null } => "",
                { ValueKind: JsonValueKind.String } value => value.GetString(),
                var value => value.GetRawText()
            })))
            .ToArray();

    private static HttpRequestMessage Request(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<string> LoginAsync(ApiIntegrationTestHost host, string username, string password)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Bento.Api.Models.AuthTokenResponse>())!.AccessToken;
    }

    private static async Task<ApiIntegrationTestHost> CreateHostAsync()
    {
        var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(Schema);
        await SeedAsync(host);
        return host;
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresAggregationQueryTests
{
    [PostgresFact]
    public async Task Scenario_OnPostgres_MatchesSqlite()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(AggregationQueryTests.Schema);
        await AggregationQueryTests.SeedAsync(host);

        await AggregationQueryTests.MonthlyExpenses(host);
        await AggregationQueryTests.TotalsByCategory(host);
        await AggregationQueryTests.RunningBalance(host);
        await AggregationQueryTests.TopValues(host);
        await AggregationQueryTests.MedianAndPercentiles(host);
        await AggregationQueryTests.RunningTotalsPerCategory(host);
        await AggregationQueryTests.Buckets(host);
        await AggregationQueryTests.MinMaxDistinct(host);

        // The list endpoint shares the filter coercion: number filters on
        // Postgres used to be sent as text ("numeric < text").
        var spent = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/transactions?filter[amount][lt]=0&per_page=1");
        Assert.Equal(7, spent.GetProperty("meta").GetProperty("total").GetInt64());
        var exact = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/transactions?filter[amount]=-45.90&per_page=1");
        Assert.Equal(1, exact.GetProperty("meta").GetProperty("total").GetInt64());
    }
}
