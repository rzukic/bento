using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bento.Api.Db;
using Bento.Api.Services;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bento.Tests;

/// <summary>
/// The workspace's calendar (Settings → Workspace): the time zone TODAY() and
/// schedules are in, the day weeks start on and the month the (fiscal) year
/// starts in — for query buckets, periods and labels.
/// </summary>
public sealed class WorkspaceCalendarTests
{
    private const string Schema = """
        { "tables": [
            { "name": "events", "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "day", "type": "date", "nullable": false },
                { "name": "n", "type": "integer", "nullable": false },
                { "name": "today", "type": "computed", "formula": "TODAY()", "resultType": "date" } ] }
          ] }
        """;

    private static Dictionary<string, string?> Settings(string secret) => new()
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = secret,
        ["Schema:AutoMigrateOnStartup"] = "false"
    };

    [Fact]
    public void Calendar_StartsWeeksQuartersAndYearsWhereTheWorkspaceSays()
    {
        var monday = WorkspaceCalendar.Default;
        var sunday = new WorkspaceCalendar(TimeZoneInfo.Utc, "sunday");
        var saturday = new WorkspaceCalendar(TimeZoneInfo.Utc, "saturday");
        var april = new WorkspaceCalendar(TimeZoneInfo.Utc, YearStart: 4);
        var saturday10 = new DateOnly(2026, 10, 10);

        Assert.Equal(new DateOnly(2026, 10, 5), monday.Start("week", saturday10));
        Assert.Equal(new DateOnly(2026, 10, 4), sunday.Start("week", saturday10));
        Assert.Equal(new DateOnly(2026, 10, 11), sunday.Start("week", new DateOnly(2026, 10, 11)));
        Assert.Equal(saturday10, saturday.Start("week", saturday10));

        Assert.Equal(new DateOnly(2026, 1, 1), april.Start("quarter", new DateOnly(2026, 3, 31)));
        Assert.Equal(new DateOnly(2026, 4, 1), april.Start("quarter", new DateOnly(2026, 4, 1)));
        Assert.Equal(new DateOnly(2025, 4, 1), april.Start("year", new DateOnly(2026, 3, 31)));
        Assert.Equal(new DateOnly(2026, 4, 1), april.Start("year", new DateOnly(2026, 12, 31)));
        Assert.Equal(("Q4 2025/26", "Q1 2026/27", "2026/27"),
            (april.QuarterName(new DateOnly(2026, 1, 1)), april.QuarterName(new DateOnly(2026, 4, 1)), april.YearName(new DateOnly(2026, 4, 1))));
        Assert.Equal(("Q3 2026", "2026"), (monday.QuarterName(new DateOnly(2026, 7, 1)), monday.YearName(new DateOnly(2026, 1, 1))));

        // Rule periods and report labels follow.
        Assert.Equal((new DateOnly(2026, 10, 4), new DateOnly(2026, 10, 11)),
            Bento.Api.Services.Notifications.NotificationRuleService.PeriodRange("this-week", saturday10, sunday));
        Assert.Equal((new DateOnly(2026, 4, 1), new DateOnly(2027, 4, 1)),
            Bento.Api.Services.Notifications.NotificationRuleService.PeriodRange("this-year", saturday10, april));
        var fiscal = Bento.Api.Services.Notifications.ReportBuilder.Periods("year", "last", "previous", new DateOnly(2026, 4, 1), april);
        Assert.Equal(("2025/26", "2024/25"), (fiscal.Current.Label, fiscal.Compare!.Label));
        var week = Bento.Api.Services.Notifications.ReportBuilder.Periods("week", "last", "none", new DateOnly(2026, 10, 11), sunday);
        Assert.Equal("Week of 4–10 Oct 2026", week.Current.Label);
    }

    [Fact]
    public async Task Calendar_OnSqlite() => await CalendarAsync(await ApiIntegrationTestHost.StartAsync(Settings("sqlite-test-secret")));

    internal static async Task CalendarAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        await host.ApplySchemaAsync(Schema);
        foreach (var (day, n) in new[] { ("2026-10-10", 1), ("2026-10-11", 2), ("2026-03-31", 4), ("2026-04-01", 8) })
            (await host.Client.PostAsJsonAsync("/api/v1/data/events", new { day, n })).EnsureSuccessStatusCode();

        // A new workspace has no time zone of its own: the server's.
        var status = await GetJsonAsync(host, "/api/v1/setup/status");
        Assert.False(status.GetProperty("time_zone_set").GetBoolean());
        Assert.Equal(("monday", 1), (status.GetProperty("week_start").GetString(), status.GetProperty("year_start").GetInt32()));

        Assert.Contains("not a time zone", await FailAsync(host, new { currency = "EUR", timeZone = "Mars/Olympus" }));
        Assert.Contains("monday, sunday or saturday", await FailAsync(host, new { currency = "EUR", weekStart = "friday" }));
        Assert.Contains("1 (January) to 12", await FailAsync(host, new { currency = "EUR", yearStart = 13 }));

        await PutAsync(host, new { currency = "EUR", timeZone = "America/New_York", weekStart = "sunday", yearStart = 4 });
        status = await GetJsonAsync(host, "/api/v1/setup/status");
        Assert.Equal(("America/New_York", true, "sunday", 4),
            (status.GetProperty("time_zone").GetString(), status.GetProperty("time_zone_set").GetBoolean(), status.GetProperty("week_start").GetString(), status.GetProperty("year_start").GetInt32()));
        // Saving locale and currency alone keeps the calendar.
        await PutAsync(host, new { locale = "de-DE", currency = "EUR" });
        Assert.Equal("sunday", (await GetJsonAsync(host, "/api/v1/setup/status")).GetProperty("week_start").GetString());

        Assert.Equal(["2026-03-29 12", "2026-10-04 1", "2026-10-11 2"], await BucketsAsync(host, "week"));
        Assert.Equal(["2026-01-01 4", "2026-04-01 8", "2026-10-01 3"], await BucketsAsync(host, "quarter"));
        Assert.Equal(["2025-04-01 4", "2026-04-01 11"], await BucketsAsync(host, "year"));

        // Values at the end of each (fiscal) year.
        var asOf = await PostJsonAsync(host, "/api/v1/query", new
        {
            source = "events",
            measures = new[] { new { name = "total", formula = "SUM(n WHERE day <= BUCKET_END())" } },
            asOf = new { bucket = "year", from = "2025-06-01", to = "2026-10-11", @as = "year" }
        });
        Assert.Equal(["2025-04-01 4", "2026-04-01 15"], asOf.GetProperty("data").EnumerateArray().Select(r => $"{r.GetProperty("year")} {r.GetProperty("total")}").ToList());

        // TODAY() is the workspace's day: 26 hours apart, a day or two apart.
        foreach (var zone in new[] { "Pacific/Kiritimati", "Etc/GMT+12" })
        {
            await PutAsync(host, new { currency = "EUR", timeZone = zone });
            var expected = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime);
            var rows = await GetJsonAsync(host, "/api/v1/data/events?limit=1");
            Assert.Equal(expected.ToString("yyyy-MM-dd"), rows.GetProperty("data")[0].GetProperty("today").GetString()![..10]);
        }
    }

    [Fact]
    public async Task ExistingWorkspace_KeepsTheZoneItRanInBefore()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(Settings("sqlite-test-secret"));
        await PostJsonAsync(host, "/api/v1/auth/register", new { username = "owner", password = "supersecure" }, data: false);
        var pool = host.GetService<ISystemPool>();
        using (var conn = await pool.GetConnectionAsync())
            await conn.ExecuteAsync("DELETE FROM workspace_settings WHERE key = 'calendar_version'");

        // The next start of a workspace from before the setting: it has users, so Europe/Berlin.
        var bootstrap = host.GetService<IEnumerable<IHostedService>>().OfType<DatabaseBootstrap>().Single();
        await bootstrap.StartAsync(CancellationToken.None);
        var status = await GetJsonAsync(host, "/api/v1/setup/status");
        Assert.Equal(("Europe/Berlin", true), (status.GetProperty("time_zone").GetString(), status.GetProperty("time_zone_set").GetBoolean()));

        // Once only: a zone chosen later stays.
        await PutAsync(host, new { currency = "EUR", timeZone = "Asia/Tokyo" });
        await bootstrap.StartAsync(CancellationToken.None);
        Assert.Equal("Asia/Tokyo", (await GetJsonAsync(host, "/api/v1/setup/status")).GetProperty("time_zone").GetString());
    }

    private static async Task<List<string>> BucketsAsync(ApiIntegrationTestHost host, string bucket)
    {
        var result = await PostJsonAsync(host, "/api/v1/query", new
        {
            source = "events",
            groupBy = new[] { new { column = "day", bucket, @as = "period" } },
            measures = new[] { new { name = "total", fn = "sum", column = "n" } },
            sort = new[] { "period" }
        });
        return result.GetProperty("data").EnumerateArray().Select(r => $"{r.GetProperty("period")} {r.GetProperty("total")}").ToList();
    }

    private static async Task<JsonElement> GetJsonAsync(ApiIntegrationTestHost host, string url)
    {
        var text = await host.Client.GetStringAsync(url);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> PostJsonAsync(ApiIntegrationTestHost host, string url, object body, bool data = true)
    {
        using var response = await host.Client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return data ? JsonDocument.Parse(text).RootElement.Clone() : default;
    }

    private static async Task PutAsync(ApiIntegrationTestHost host, object body)
    {
        using var response = await host.Client.PutAsJsonAsync("/api/v1/admin/settings", body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> FailAsync(ApiIntegrationTestHost host, object body)
    {
        using var response = await host.Client.PutAsJsonAsync("/api/v1/admin/settings", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresWorkspaceCalendarTests
{
    [PostgresFact]
    public async Task Calendar_OnPostgres() => await WorkspaceCalendarTests.CalendarAsync(await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "postgres-test-secret",
        ["Schema:AutoMigrateOnStartup"] = "false"
    }));
}
