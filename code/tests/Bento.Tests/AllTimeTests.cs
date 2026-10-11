using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Values at period ends over "all time" (no asOf.from) begin where the data
/// does: the first trade, not a year before today, and not the oldest price.
/// </summary>
public sealed class AllTimeTests
{
    [Fact]
    public async Task OnSqlite() => await RunAsync(await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "sqlite-test-secret"
    }));

    internal static async Task RunAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        await QueryIndexTests.SeedAsync(host);
        await QueryIndexTests.PostAsync(host, "trades", new { date = "2024-03-15", security_id = 1, isin = "IE00B4L5Y983", shares = 1.0 });
        await QueryIndexTests.PostAsync(host, "prices", new { isin = "IE00B4L5Y983", date = "2010-01-01", price = 5.0 });

        // Begins with the first trade's month; the older price does not count.
        var monthly = await QueryAsync(host, "month", QueryIndexTests.Depot);
        var months = Periods(monthly);
        Assert.Equal("2024-03-01", months[0]);
        Assert.Equal("2026-10-01", months[^1]);
        Assert.Equal(32, months.Count);
        Assert.False(monthly.GetProperty("meta").TryGetProperty("warnings", out var none) && none.ValueKind == JsonValueKind.Array && none.GetArrayLength() > 0);

        // A formula that compares no date with the period: the year before the end, as before.
        Assert.Equal("2025-10-01", Periods(await QueryAsync(host, "month", "SUM(SUM(trades.shares))"))[0]);

        // More days than a query takes: the newest, and a note.
        await QueryIndexTests.PostAsync(host, "trades", new { date = "1990-01-01", security_id = 1, isin = "IE00B4L5Y983", shares = 1.0 });
        var daily = await QueryAsync(host, "day", QueryIndexTests.Depot);
        var days = Periods(daily);
        Assert.Equal(5000, days.Count);
        Assert.Equal("2026-10-01", days[^1]);
        Assert.Contains("newest 5000", daily.GetProperty("meta").GetProperty("warnings")[0].GetString());
    }

    private static async Task<JsonElement> QueryAsync(ApiIntegrationTestHost host, string bucket, string formula)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/query", new
        {
            source = "securities",
            asOf = new { bucket, to = "2026-10-01", @as = "period" },
            measures = new[] { new { name = "value", formula } }
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        return body;
    }

    private static List<string> Periods(JsonElement body) =>
        body.GetProperty("data").EnumerateArray().Select(r => r.GetProperty("period").GetString()!).ToList();
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresAllTimeTests
{
    [PostgresFact]
    public async Task OnPostgres() => await AllTimeTests.RunAsync(await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "postgres-test-secret",
        ["Schema:AutoMigrateOnStartup"] = "false"
    }));
}
