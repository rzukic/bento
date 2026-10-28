using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Step 5.8: a saved query as a table — read-only, filled from the query
/// whenever the data changes, usable like any table.
/// </summary>
public sealed class QueryTableTests
{
    private const string Schema = """
        { "tables": [ { "name": "transactions", "columns": [
            { "name": "id", "type": "integer", "primaryKey": true },
            { "name": "amount", "type": "currency" },
            { "name": "booked_on", "type": "date" },
            { "name": "category", "type": "text" } ] } ] }
        """;

    [Fact]
    public async Task SavedQuery_AsTable() => await QueryTableAsync(await ShareTests.HostAsync(Schema));

    internal static async Task QueryTableAsync(ApiIntegrationTestHost host)
    {
        await using var disposable = host;
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        foreach (var (amount, day) in new[] { (-10.0, "2026-01-05"), (-30.0, "2026-01-20"), (-5.0, "2026-02-03"), (100.0, "2026-02-10") })
            await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/data/transactions", admin, new { amount, booked_on = day, category = "food" }));
        var query = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/queries", admin, new
        {
            name = "Monthly spending",
            query = new
            {
                source = "transactions",
                filter = new { amount = new { lt = 0 } },
                groupBy = new[] { new { column = "booked_on", bucket = "month", @as = "month" } },
                measures = new[] { new { name = "spent", fn = "sum", column = "amount" } }
            }
        }));
        var id = query.GetProperty("id").GetInt64();

        var made = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, $"/api/v1/queries/{id}/table", admin, new { }));
        Assert.Equal("monthly_spending", made.GetProperty("table").GetString());
        Assert.Equal(2, made.GetProperty("rows").GetInt32());
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Post, $"/api/v1/queries/{id}/table", admin, new { name = "monthly_spending" }), HttpStatusCode.BadRequest, "already");

        // Its rows, and the table in another query and in a formula.
        var rows = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/data/monthly_spending?sort=month", admin));
        Assert.Equal(-40m, rows[0].GetProperty("spent").GetDecimal());
        Assert.Equal(-5m, rows[1].GetProperty("spent").GetDecimal());
        var average = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/query", admin, new
        {
            source = "monthly_spending",
            measures = new[] { new { name = "average", fn = "avg", column = "spent" } }
        }));
        Assert.Equal(-22.5m, average[0].GetProperty("average").GetDecimal());
        var variables = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/query/variables", admin, new
        {
            variables = new[] { new { name = "worst", source = "monthly_spending", formula = "MIN(spent)" } }
        }));
        Assert.Equal(-40m, variables[0].GetProperty("value").GetDecimal());

        // New data: read again, it is current.
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/data/transactions", admin, new { amount = -60, booked_on = "2026-03-01", category = "rent" }));
        rows = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/data/monthly_spending?sort=month", admin));
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal(-60m, rows[2].GetProperty("spent").GetDecimal());

        // Read-only; its query cannot go while the table is there.
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/data/monthly_spending", admin, new { spent = 1 }), HttpStatusCode.Forbidden, "made from a saved query");
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Delete, $"/api/v1/queries/{id}", admin), HttpStatusCode.Conflict, "monthly_spending");

        // The query changes: the table follows with a new column.
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Put, $"/api/v1/queries/{id}", admin, new
        {
            query = new
            {
                source = "transactions",
                filter = new { amount = new { lt = 0 } },
                groupBy = new[] { new { column = "booked_on", bucket = "month", @as = "month" } },
                measures = new object[] { new { name = "spent", fn = "sum", column = "amount" }, new { name = "bookings", fn = "count" } }
            }
        }));
        rows = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/data/monthly_spending?sort=month", admin));
        Assert.Equal(2, rows[0].GetProperty("bookings").GetInt32());
        var status = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/query-tables/monthly_spending/refresh", admin));
        Assert.True(status.GetProperty("current").GetBoolean());
        Assert.Equal(3, status.GetProperty("rows").GetInt32());
    }

    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.RequestUri} {(int)response.StatusCode}: {text}");
            return JsonDocument.Parse(text).RootElement.GetProperty("data").Clone();
        }
    }

    private static async Task Ok(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.RequestUri} {(int)response.StatusCode}: {text}");
        }
    }

    private static async Task Status(HttpResponseMessage response, HttpStatusCode expected, string? contains = null)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.RequestUri}: expected {(int)expected}, got {(int)response.StatusCode}: {text}");
            if (contains is not null)
                Assert.Contains(contains, text);
        }
    }
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresQueryTableTests
{
    [PostgresFact]
    public async Task SavedQuery_AsTable_OnPostgres()
    {
        var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync("""
            { "tables": [ { "name": "transactions", "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "amount", "type": "currency" },
                { "name": "booked_on", "type": "date" },
                { "name": "category", "type": "text" } ] } ] }
            """);
        await QueryTableTests.QueryTableAsync(host);
    }
}
