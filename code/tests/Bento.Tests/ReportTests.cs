using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bento.Api.Db;
using Bento.Api.Models;
using Bento.Api.Services.Ai;
using Bento.Api.Services.Notifications;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bento.Tests;

/// <summary>
/// Step 5.3 part B: scheduled reports put together from blocks — values as
/// formulas, top lists, a dashboard's key figures, the rules that hold, text —
/// for the period just over against the one before. Nothing here is about money:
/// the data are support tickets.
/// </summary>
public sealed class ReportTests
{
    private const string Path = "/api/v1/admin/notifications/rules";

    private const string Schema = """
        { "tables": [
            { "name": "teams", "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "name", "type": "text", "nullable": false } ] },
            { "name": "tickets", "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "opened_on", "type": "date", "nullable": false },
                { "name": "minutes", "type": "integer", "nullable": false },
                { "name": "team_id", "type": "reference", "references": "teams" } ] }
          ],
          "relations": [ { "name": "team_tickets", "type": "1:N", "from": "teams", "to": "tickets", "column": "team_id" } ] }
        """;

    private static Dictionary<string, string?> Settings(string secret) => new()
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = secret,
        ["Backend:PublicUrl"] = "https://bento.example",
        ["Schema:AutoMigrateOnStartup"] = "false",
        ["Import:AllowPrivateNetwork"] = "true"
    };

    private static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
        TimeZoneInfo.FindSystemTimeZoneById(NotificationRuleService.TimeZoneId)).DateTime);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ── Periods ──────────────────────────────────────────────────────────

    [Fact]
    public void Periods_LastAndCurrent_AgainstThePeriodBeforeOrAYearEarlier()
    {
        var sunday = new DateOnly(2026, 10, 11);

        var month = ReportBuilder.Periods("month", "last", "previous", new DateOnly(2026, 10, 1));
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), "September 2026"), (month.Current.From, month.Current.To, month.Current.Label));
        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1), "August 2026"), (month.Compare!.From, month.Compare.To, month.Compare.Label));

        var week = ReportBuilder.Periods("week", "last", "previous", new DateOnly(2026, 10, 12));
        Assert.Equal("Week 41 (5–11 Oct 2026)", week.Current.Label);
        Assert.Equal("Week 40 (28 Sep – 4 Oct 2026)", week.Compare!.Label);

        // So far: the same number of days of the period before.
        var soFar = ReportBuilder.Periods("month", "current", "previous", sunday);
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 12), "October 2026 until 11 Oct"), (soFar.Current.From, soFar.Current.To, soFar.Current.Label));
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 12), "September 2026 until 11 Sep"), (soFar.Compare!.From, soFar.Compare.To, soFar.Compare.Label));

        var year = ReportBuilder.Periods("quarter", "last", "year", new DateOnly(2026, 10, 1));
        Assert.Equal(("Q3 2026", "Q3 2025"), (year.Current.Label, year.Compare!.Label));

        var day = ReportBuilder.Periods("day", "last", "none", sunday);
        Assert.Equal("Sat, 10 Oct 2026", day.Current.Label);
        Assert.Null(day.Compare);
    }

    [Fact]
    public void LastOccurrence_IsTheScheduledDayAtItsTime_InBerlin()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(NotificationRuleService.TimeZoneId);
        var monthly = new NotificationRuleConfig { Every = "month", Day = 1, At = "08:00" };
        // 1 Oct 2026, 07:30 in Berlin (CEST, UTC+2): the 1 September report was the last one.
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero),
            ReportBuilder.LastOccurrence(monthly, new DateTimeOffset(2026, 10, 1, 5, 30, 0, TimeSpan.Zero), zone).ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero),
            ReportBuilder.LastOccurrence(monthly, new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero), zone).ToUniversalTime());

        // Weekly on Monday at 07:00; Sunday 11 Oct → Monday 5 Oct.
        var weekly = new NotificationRuleConfig { Every = "week", Day = 1, At = "07:00" };
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 5, 0, 0, TimeSpan.Zero),
            ReportBuilder.LastOccurrence(weekly, new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero), zone).ToUniversalTime());

        // Winter time (CET, UTC+1).
        var daily = new NotificationRuleConfig { Every = "day", At = "08:00" };
        Assert.Equal(new DateTimeOffset(2026, 12, 2, 7, 0, 0, TimeSpan.Zero),
            ReportBuilder.LastOccurrence(daily, new DateTimeOffset(2026, 12, 2, 7, 30, 0, TimeSpan.Zero), zone).ToUniversalTime());
    }

    // ── Reports ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Report_BuildsItsBlocks_SendsOncePerPeriod() =>
        await BuildsAndSendsAsync(await ApiIntegrationTestHost.StartAsync(Settings("sqlite-test-secret")));

    internal static async Task BuildsAndSendsAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        await host.ApplySchemaAsync(Schema);
        using var receiver = new FakeReceiver();
        var channel = (await PostAsync(host, "/api/v1/admin/notifications/channels", new { name = "Hook", kind = "webhook", config = new { url = $"{receiver.Url}/hook" } })).GetProperty("id").GetInt64();
        await PostAsync(host, "/api/v1/data/teams", new { name = "Alpha" });
        await PostAsync(host, "/api/v1/data/teams", new { name = "Beta" });
        await PostAsync(host, "/api/v1/data/teams", new { name = "Gamma" });

        var thisMonth = new DateOnly(Today().Year, Today().Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);
        var monthBefore = thisMonth.AddMonths(-2);
        foreach (var (day, minutes, team) in new[]
                 {
                     (lastMonth, 30, 1), (lastMonth.AddDays(3), 20, 1), (lastMonth.AddDays(9), 10, 2),
                     (monthBefore, 40, 2), (monthBefore.AddDays(5), 10, 1),
                     (thisMonth, 5, 1),
                     // Nothing to say about Gamma: 0 minutes is left out of the top list.
                     (lastMonth.AddDays(2), 0, 3)
                 })
            await PostAsync(host, "/api/v1/data/tickets", new { opened_on = Iso(day), minutes, team_id = team });

        var dashboard = await PostAsync(host, "/api/v1/dashboards", new
        {
            name = "Support",
            dashboard = new
            {
                variables = new object[] { new { name = "avg_minutes", source = "tickets", formula = "AVG(minutes)", dateColumn = "opened_on" } },
                widgets = new object[]
                {
                    new
                    {
                        id = "opened", type = "kpi", title = "Opened",
                        position = new { x = 0, y = 0, w = 3, h = 2 },
                        data = new { query = new { source = "tickets", measures = new[] { new { name = "n", formula = "COUNT()" } } }, dateColumn = "opened_on" }
                    },
                    new
                    {
                        id = "avg", type = "kpi", title = "Average minutes",
                        position = new { x = 3, y = 0, w = 3, h = 2 },
                        data = new { variable = "avg_minutes" },
                        display = new { higherIsBetter = false }
                    },
                    new
                    {
                        // Its own range: not a figure of the report's period.
                        id = "forecast", type = "kpi", title = "Forecast",
                        position = new { x = 6, y = 0, w = 3, h = 2 },
                        data = new { query = new { source = "tickets", measures = new[] { new { name = "n", formula = "COUNT()" } } }, dateColumn = "opened_on", range = new { preset = "next-12-months" } }
                    },
                    new
                    {
                        id = "trend", type = "line", title = "Per month",
                        position = new { x = 0, y = 2, w = 8, h = 4 },
                        data = new { query = new { source = "tickets", groupBy = new[] { new { column = "opened_on", bucket = "month" } }, measures = new[] { new { name = "n", fn = "count" } } } }
                    }
                }
            }
        });
        var dashboardId = dashboard.GetProperty("id").GetInt64();

        var config = new
        {
            every = "month",
            day = 1,
            at = "00:00",
            intro = "The support month.",
            ai = true,
            blocks = new object[]
            {
                new { type = "value", label = "Tickets", source = "tickets", formula = "COUNT()", dateColumn = "opened_on" },
                new { type = "value", label = "Minutes spent", source = "tickets", formula = "SUM(minutes)", dateColumn = "opened_on", higherIsBetter = false },
                new { type = "value", label = "All tickets ever", source = "tickets", formula = "COUNT()" },
                new { type = "top", label = "Minutes by team", source = "tickets", formula = "SUM(minutes)", dateColumn = "opened_on", groupBy = "team_id", limit = 3 },
                new { type = "dashboard", dashboardId },
                new { type = "rules" },
                new { type = "text", label = "Next", text = "Review the open ones on Monday." }
            }
        };

        // The editor's preview, before saving.
        var preview = await PostAsync(host, $"{Path}/preview", new { name = "Support review", config });
        var current = lastMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        var before = monthBefore.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        Assert.Equal($"Support review · {current}", preview.GetProperty("title").GetString());
        // No AI in a preview: the period line.
        Assert.Equal($"The support month.\n\n{current}, compared with {before}.", preview.GetProperty("text").GetString());

        var sections = preview.GetProperty("sections").EnumerateArray().ToList();
        Assert.Equal(5, sections.Count);
        var values = sections[0].GetProperty("facts").EnumerateArray().ToList();
        Assert.Equal(("Tickets", "4", $"+2 (+100%) vs. {before}", "good"), Fact(values[0]));
        Assert.Equal(("Minutes spent", "60", $"+10 (+20%) vs. {before}", "bad"), Fact(values[1]));
        Assert.Equal("7", values[2].GetProperty("value").GetString());
        Assert.Null(Fact(values[2]).Item3);

        Assert.Equal("Minutes by team", sections[1].GetProperty("title").GetString());
        var teams = sections[1].GetProperty("facts").EnumerateArray().ToList();
        Assert.Equal(2, teams.Count);
        Assert.Equal(("Alpha", "50", $"+40 (+400%) vs. {before}", "good"), Fact(teams[0]));
        Assert.Equal(("Beta", "10", $"-30 (-75%) vs. {before}", "bad"), Fact(teams[1]));

        Assert.Equal("Support", sections[2].GetProperty("title").GetString());
        var kpis = sections[2].GetProperty("facts").EnumerateArray().ToList();
        Assert.Equal(2, kpis.Count);
        Assert.Equal(("Opened", "4", $"+2 (+100%) vs. {before}", "good"), Fact(kpis[0]));
        Assert.Equal(("Average minutes", "15", $"-10 (-40%) vs. {before}", "good"), Fact(kpis[1]));

        Assert.Equal("No rule holds right now.", sections[3].GetProperty("text").GetString());
        Assert.Equal(("Next", "Review the open ones on Monday."), (sections[4].GetProperty("title").GetString(), sections[4].GetProperty("text").GetString()));

        var report = await PostAsync(host, Path, new { name = "Support review", kind = "report", config, channels = new[] { channel } });
        var id = report.GetProperty("id").GetInt64();
        Assert.Equal("Monthly on the 1st at 00:00 · the last month against the month before · 7 blocks · AI text", report.GetProperty("summary").GetString());

        // Check now: put together, not sent.
        var check = await PostAsync(host, $"{Path}/{id}/check", new { });
        Assert.False(check.GetProperty("fired").GetBoolean());
        Assert.Equal($"Support review · {current}", check.GetProperty("notification").GetProperty("title").GetString());
        Assert.Empty(receiver.Requests);

        // Send test: AI is not set up, so the report goes out with its period line.
        await PostAsync(host, $"{Path}/{id}/test", new { });
        var test = JsonDocument.Parse(Assert.Single(receiver.Requests).Body).RootElement;
        Assert.Equal("report", test.GetProperty("event").GetString());
        Assert.Equal($"Test: Support review · {current}", test.GetProperty("title").GetString());
        Assert.Equal($"The support month.\n\n{current}, compared with {before}.", test.GetProperty("text").GetString());
        Assert.Equal($"https://bento.example/dashboards/{dashboardId}", test.GetProperty("link").GetString());
        var sent = test.GetProperty("sections")[0].GetProperty("facts")[0];
        Assert.Equal((4m, 2m), (sent.GetProperty("number").GetDecimal(), sent.GetProperty("previous").GetDecimal()));

        // Set up after this month's report time: it waits for the next one.
        var rules = host.GetService<NotificationRuleService>();
        Assert.DoesNotContain(id, await rules.DueAsync());

        // Made before it: due once, sent once.
        var pool = host.GetService<ISystemPool>();
        using (var conn = await pool.GetConnectionAsync())
        {
            if (pool.Provider == DatabaseProvider.Postgres)
                await conn.ExecuteAsync("UPDATE _system.notification_rules SET updated_at = @At WHERE id = @Id", new { Id = id, At = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            else
                await conn.ExecuteAsync("UPDATE notification_rules SET updated_at = @At WHERE id = @Id", new { Id = id, At = "2020-01-01T00:00:00.0000000Z" });
        }
        Assert.Contains(id, await rules.DueAsync());
        var scheduled = await rules.CheckScheduledAsync(id, CancellationToken.None);
        Assert.True(scheduled.Fired);
        Assert.Equal(2, receiver.Requests.Count);
        var monthly = JsonDocument.Parse(receiver.Requests.ElementAt(1).Body).RootElement;
        Assert.Equal($"Support review · {current}", monthly.GetProperty("title").GetString());
        Assert.StartsWith($"{current} · without AI text: ", scheduled.State.Value);

        Assert.DoesNotContain(id, await rules.DueAsync());
        Assert.False((await rules.CheckScheduledAsync(id, CancellationToken.None)).Fired);
        Assert.Equal(2, receiver.Requests.Count);
    }

    [Fact]
    public async Task Report_AiWritesTheOpening_FromTheFiguresOnly()
    {
        var model = new AiTests.ScriptedModel();
        await using var host = await ApiIntegrationTestHost.StartAsync(Settings("sqlite-test-secret"),
            configureTestServices: services => services.Replace(ServiceDescriptor.Singleton<IAiModel>(model)));
        await host.ApplySchemaAsync(Schema);
        using var receiver = new FakeReceiver();
        var channel = (await PostAsync(host, "/api/v1/admin/notifications/channels", new { name = "Hook", kind = "webhook", config = new { url = $"{receiver.Url}/hook" } })).GetProperty("id").GetInt64();
        (await host.Client.PutAsJsonAsync("/api/v1/ai/settings", new { apiKey = "sk-ant-test-123", monthlyLimitUsd = 5 })).EnsureSuccessStatusCode();
        var yesterday = Today().AddDays(-1);
        await PostAsync(host, "/api/v1/data/tickets", new { opened_on = Iso(yesterday), minutes = 12 });

        var report = await PostAsync(host, Path, new
        {
            name = "Daily tickets",
            kind = "report",
            config = new
            {
                every = "day",
                at = "07:00",
                ai = true,
                aiInstructions = "Auf Deutsch, kurz.",
                blocks = new object[] { new { type = "value", label = "Tickets", source = "tickets", formula = "COUNT()", dateColumn = "opened_on" } }
            },
            channels = new[] { channel }
        });
        model.Script.Enqueue(_ => new AiResponse([new AiText("Ein Ticket gestern, eins mehr als am Tag davor.")], "end_turn", new AiUsage(800, 40, 0, 0), "claude-opus-5-5"));

        await PostAsync(host, $"{Path}/{report.GetProperty("id").GetInt64()}/test", new { });
        var sent = JsonDocument.Parse(Assert.Single(receiver.Requests).Body).RootElement;
        Assert.Equal("Ein Ticket gestern, eins mehr als am Tag davor.", sent.GetProperty("text").GetString());

        var request = Assert.Single(model.Requests);
        Assert.Empty(request.Tools);
        var asked = ((AiText)request.Messages[0].Blocks[0]).Text;
        Assert.Contains("Auf Deutsch, kurz.", asked);
        Assert.Contains($"Tickets: 1 (+1 vs. {yesterday.AddDays(-1).ToString("ddd, d MMM yyyy", CultureInfo.InvariantCulture)})", asked);
    }

    [Fact]
    public async Task Report_SaysWhatIsWrong()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(Settings("sqlite-test-secret"));
        await host.ApplySchemaAsync(Schema);
        var channel = (await PostAsync(host, "/api/v1/admin/notifications/channels", new { name = "Hook", kind = "webhook", config = new { url = "http://127.0.0.1:9/hook" } })).GetProperty("id").GetInt64();
        var empty = await PostAsync(host, "/api/v1/dashboards", new { name = "Empty", dashboard = new { widgets = Array.Empty<object>() } });

        async Task<string> FailAsync(object config)
        {
            using var response = await host.Client.PostAsJsonAsync(Path, new { name = "R", kind = "report", config, channels = new[] { channel } });
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
            return body;
        }

        Assert.Contains("at least one block", await FailAsync(new { every = "month", blocks = Array.Empty<object>() }));
        Assert.Contains("1 to 28", await FailAsync(new { every = "month", day = 31, blocks = new object[] { new { type = "rules" } } }));
        Assert.Contains("every day, week, month", await FailAsync(new { every = "hour", blocks = new object[] { new { type = "rules" } } }));
        Assert.Contains("config.blocks[0].type", await FailAsync(new { blocks = new object[] { new { type = "chart" } } }));
        Assert.Contains("config.blocks[1].formula", await FailAsync(new { blocks = new object[] { new { type = "rules" }, new { type = "value", source = "tickets", formula = "SUM(nope)" } } }));
        Assert.Contains("config.blocks[0].groupBy", await FailAsync(new { blocks = new object[] { new { type = "top", source = "tickets", formula = "COUNT()" } } }));
        Assert.Contains("no key figures", await FailAsync(new { blocks = new object[] { new { type = "dashboard", dashboardId = empty.GetProperty("id").GetInt64() } } }));
        Assert.Contains("config.blocks[0].text", await FailAsync(new { blocks = new object[] { new { type = "text", text = " " } } }));
    }

    private static (string?, string?, string?, string?) Fact(JsonElement fact) =>
        (fact.GetProperty("label").GetString(), fact.GetProperty("value").GetString(),
         fact.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null,
         fact.TryGetProperty("tone", out var tone) && tone.ValueKind == JsonValueKind.String ? tone.GetString() : null);

    private static Task<JsonElement> PostAsync(ApiIntegrationTestHost host, string url, object body) => ImportSourceTests.PostAsync(host, url, body);
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresReportTests
{
    [PostgresFact]
    public async Task Report_OnPostgres() => await ReportTests.BuildsAndSendsAsync(await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "postgres-test-secret",
        ["Backend:PublicUrl"] = "https://bento.example",
        ["Schema:AutoMigrateOnStartup"] = "false",
        ["Import:AllowPrivateNetwork"] = "true"
    }));
}
