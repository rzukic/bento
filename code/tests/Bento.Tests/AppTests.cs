using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bento.Api.Services.Apps;

namespace Bento.Tests;

/// <summary>
/// Step 5.4 part A: apps — a calendar made of a table's rows, each device with
/// its own password, and the iCal subscription calendar programs read.
/// </summary>
public sealed class AppTests
{
    private const string Path = "/api/v1/admin/apps";

    private const string Schema = """
        { "tables": [
            { "name": "appointments", "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "title", "type": "text" },
                { "name": "starts_at", "type": "datetime", "nullable": false },
                { "name": "ends_at", "type": "datetime" },
                { "name": "place", "type": "text" },
                { "name": "notes", "type": "text" },
                { "name": "private", "type": "boolean" } ] },
            { "name": "deadlines", "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "what", "type": "text", "nullable": false },
                { "name": "due", "type": "date", "nullable": false },
                { "name": "done", "type": "boolean" } ] }
          ] }
        """;

    private static Dictionary<string, string?> Settings(string secret) => new()
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = secret,
        ["Backend:PublicUrl"] = "https://bento.example",
        ["Schema:AutoMigrateOnStartup"] = "false"
    };

    // ── iCalendar ────────────────────────────────────────────────────────

    [Fact]
    public void Ics_EscapesFoldsAndWritesDaysAndMoments()
    {
        var events = new[]
        {
            new CalendarEvents.Event("1", "appointments-1@bento", "Dentist; Dr. Müller, room 2", new CalendarEvents.EventTime(null, new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero)),
                new CalendarEvents.EventTime(null, new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero)), "Bring the card\nand the form", null, "t1"),
            new CalendarEvents.Event("2", "deadlines-2@bento", new string('ä', 60), new CalendarEvents.EventTime(new DateOnly(2026, 10, 31), null),
                new CalendarEvents.EventTime(new DateOnly(2026, 11, 1), null), null, "Office", "t2")
        };
        var text = IcsWriter.Calendar("Termine", "#3B82F6", "Europe/Berlin", events, new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n", text);
        Assert.EndsWith("END:VCALENDAR\r\n", text);
        Assert.Contains("\r\nSUMMARY:Dentist\\; Dr. Müller\\, room 2\r\n", text);
        Assert.Contains("\r\nDESCRIPTION:Bring the card\\nand the form\r\n", text);
        Assert.Contains("\r\nDTSTART:20261023T080000Z\r\nDTEND:20261023T090000Z\r\n", text);
        Assert.Contains("\r\nDTSTART;VALUE=DATE:20261031\r\nDTEND;VALUE=DATE:20261101\r\n", text);
        Assert.Contains("\r\nDTSTAMP:20261021T120000Z\r\n", text);
        Assert.Contains("X-WR-CALNAME:Termine", text);
        // No line over 75 octets; a folded line goes on after CRLF and a space.
        foreach (var line in text.Split("\r\n"))
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line);
        var summary = text[text.IndexOf("SUMMARY:ää", StringComparison.Ordinal)..];
        Assert.Contains("\r\n ä", summary);
        Assert.Equal(new string('ä', 60), string.Concat(summary.Split("\r\n")[..2].Select((l, i) => i == 0 ? l["SUMMARY:".Length..] : l[1..])));
    }

    [Fact]
    public void Times_AreDaysForDatesAndWallTimeInTheWorkspaceZone()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        Assert.Equal(new DateOnly(2026, 10, 31), CalendarEvents.Time("2026-10-31", berlin, null)!.Value.Day);
        // Wall time in Berlin (CEST in October, CET from 25 Oct).
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero), CalendarEvents.Time("2026-10-23T10:00:00", berlin, null)!.Value.Moment);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero), CalendarEvents.Time("2026-10-26 10:00:00", berlin, null)!.Value.Moment);
        // A Z (Postgres hands date-times back so) is still the wall time Bento shows; an offset is that moment.
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero), CalendarEvents.Time("2026-10-23T10:00:00Z", berlin, null)!.Value.Moment);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero), CalendarEvents.Time(new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc), berlin, null)!.Value.Moment);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero), CalendarEvents.Time("2026-10-23T12:00:00+02:00", berlin, null)!.Value.Moment);
        // A yes/no column decides: a date-time all-day is its day, a date not all-day starts at midnight.
        Assert.Equal(new DateOnly(2026, 10, 23), CalendarEvents.Time("2026-10-23T23:30:00", TimeZoneInfo.Utc, true)!.Value.Day);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 22, 0, 0, TimeSpan.Zero), CalendarEvents.Time("2026-10-23", berlin, false)!.Value.Moment);
        Assert.Null(CalendarEvents.Time("soon", berlin, null));
    }

    // ── Apps and subscriptions ───────────────────────────────────────────

    [Fact]
    public async Task Calendar_FromTables_SubscribedWithADevicePassword() =>
        await SubscriptionAsync(await ApiIntegrationTestHost.StartAsync(Settings("sqlite-test-secret")));

    internal static async Task SubscriptionAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        await host.ApplySchemaAsync(Schema);
        (await host.Client.PutAsJsonAsync("/api/v1/admin/settings", new { currency = "EUR", timeZone = "Europe/Berlin" })).EnsureSuccessStatusCode();
        await PostAsync(host, "/api/v1/data/appointments", new { title = "Dentist", starts_at = "2026-10-23T10:00:00", ends_at = "2026-10-23T11:00:00", place = "Main St 1", notes = "Bring the card" });
        await PostAsync(host, "/api/v1/data/appointments", new { title = "Call with Anna", starts_at = "2026-10-24T09:30:00" });
        await PostAsync(host, "/api/v1/data/appointments", new { title = "Therapy", starts_at = "2026-10-25T15:00:00", @private = true });
        await PostAsync(host, "/api/v1/data/deadlines", new { what = "Tax return", due = "2026-10-31", done = false });
        await PostAsync(host, "/api/v1/data/deadlines", new { what = "Old one", due = "2026-09-30", done = true });

        var app = await PostAsync(host, Path, new
        {
            name = "My calendar",
            kind = "calendar",
            config = new
            {
                calendars = new object[]
                {
                    new { name = "Termine", table = "appointments", title = "title", start = "starts_at", end = "ends_at", location = "place", description = "notes", color = "#3b82f6",
                          filters = new[] { new { column = "private", op = "neq", value = "true" } } },
                    new { name = "Fristen & Abgaben", table = "deadlines", title = "what", start = "due", filters = new[] { new { column = "done", op = "eq", value = "false" } } }
                }
            }
        });
        var id = app.GetProperty("id").GetInt64();
        var calendars = app.GetProperty("config").GetProperty("calendars");
        Assert.Equal(("termine", "fristen-abgaben", "#3B82F6"),
            (calendars[0].GetProperty("slug").GetString(), calendars[1].GetProperty("slug").GetString(), calendars[0].GetProperty("color").GetString()));

        var created = await PostAsync(host, $"{Path}/{id}/passwords", new { name = "iPhone" });
        var secret = created.GetProperty("secret").GetString()!;
        Assert.StartsWith(AppService.PasswordPrefix, secret);
        var links = created.GetProperty("links");
        Assert.Equal($"https://bento.example/feeds/{secret}/termine.ics", links.EnumerateArray().First(l => l.GetProperty("kind").GetString() == "ical").GetProperty("url").GetString());
        // Listed without the secret.
        var listed = (await GetJsonAsync(host, Path)).GetProperty("data")[0].GetProperty("passwords")[0];
        Assert.Equal("iPhone", listed.GetProperty("name").GetString());
        Assert.DoesNotContain(secret, (await host.Client.GetStringAsync(Path)));

        using (var feed = await host.Client.GetAsync($"/feeds/{secret}/termine.ics"))
        {
            Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
            Assert.Equal("text/calendar", feed.Content.Headers.ContentType!.MediaType);
            var text = await feed.Content.ReadAsStringAsync();
            Assert.Contains("X-WR-CALNAME:Termine", text);
            Assert.Contains("X-WR-TIMEZONE:Europe/Berlin", text);
            Assert.Contains("SUMMARY:Dentist\r\nDESCRIPTION:Bring the card\r\nLOCATION:Main St 1", text);
            // 10:00 in Berlin is 08:00 UTC; without an end, an hour.
            Assert.Contains("DTSTART:20261023T080000Z\r\nDTEND:20261023T090000Z", text);
            Assert.Contains("DTSTART:20261024T073000Z\r\nDTEND:20261024T083000Z", text);
            Assert.Contains("UID:appointments-1@bento", text);
            Assert.DoesNotContain("Therapy", text);

            // Unchanged: 304 for the same tag.
            using var again = new HttpRequestMessage(HttpMethod.Get, $"/feeds/{secret}/termine.ics");
            again.Headers.IfNoneMatch.Add(feed.Headers.ETag!);
            Assert.Equal(HttpStatusCode.NotModified, (await host.Client.SendAsync(again)).StatusCode);
        }

        var deadlines = await host.Client.GetStringAsync($"/feeds/{secret}/fristen-abgaben.ics");
        Assert.Contains("SUMMARY:Tax return\r\nEND:VEVENT", deadlines.Replace("\r\nLOCATION", ""));
        Assert.Contains("DTSTART;VALUE=DATE:20261031\r\nDTEND;VALUE=DATE:20261101", deadlines);
        Assert.DoesNotContain("Old one", deadlines);

        // Claude is told which tables are calendars.
        using (var initialize = await host.Client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18" } }))
        {
            var instructions = JsonDocument.Parse(await initialize.Content.ReadAsStringAsync()).RootElement.GetProperty("result").GetProperty("instructions").GetString();
            Assert.Contains("“Termine”: table `appointments`, title `title`, start `starts_at`, end `ends_at`", instructions);
            Assert.Contains("only rows with done eq false", instructions);
        }

        // A wrong password, an unknown calendar, a revoked password, an app turned off: nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/feeds/{AppService.PasswordPrefix}nope/termine.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/feeds/{secret}/other.ics")).StatusCode);
        var second = (await PostAsync(host, $"{Path}/{id}/passwords", new { name = "Mac" })).GetProperty("secret").GetString()!;
        (await host.Client.PutAsJsonAsync($"{Path}/{id}", new { enabled = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/feeds/{second}/termine.ics")).StatusCode);
        (await host.Client.PutAsJsonAsync($"{Path}/{id}", new { enabled = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync($"/feeds/{second}/termine.ics")).StatusCode);
        var passwordId = created.GetProperty("password").GetProperty("id").GetInt64();
        (await host.Client.DeleteAsync($"{Path}/{id}/passwords/{passwordId}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/feeds/{secret}/termine.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync($"/feeds/{second}/termine.ics")).StatusCode);

        // Renamed, the calendar keeps its address.
        var config = JsonSerializer.Deserialize<JsonElement>((await GetJsonAsync(host, $"{Path}/{id}")).GetProperty("data").GetProperty("config").GetRawText());
        var renamed = JsonDocument.Parse(config.GetRawText().Replace("\"name\":\"Termine\"", "\"name\":\"Appointments\"")).RootElement;
        (await host.Client.PutAsJsonAsync($"{Path}/{id}", new { config = renamed })).EnsureSuccessStatusCode();
        Assert.Contains("X-WR-CALNAME:Appointments", await host.Client.GetStringAsync($"/feeds/{second}/termine.ics"));
    }

    [Fact]
    public async Task App_SaysWhatIsWrong()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(Settings("sqlite-test-secret"));
        await host.ApplySchemaAsync(Schema);

        async Task<string> FailAsync(object calendar)
        {
            using var response = await host.Client.PostAsJsonAsync(Path, new { name = "Cal", config = new { calendars = new[] { calendar } } });
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
            return body;
        }

        Assert.Contains("Choose the table", await FailAsync(new { name = "A", table = "nope", title = "title", start = "starts_at" }));
        Assert.Contains("config.calendars[0].title", await FailAsync(new { name = "A", table = "appointments", start = "starts_at" }));
        Assert.Contains("not a date or date-time column", await FailAsync(new { name = "A", table = "appointments", title = "title", start = "place" }));
        Assert.Contains("not a yes/no column", await FailAsync(new { name = "A", table = "appointments", title = "title", start = "starts_at", allDay = "notes" }));
        Assert.Contains("#3B82F6", await FailAsync(new { name = "A", table = "appointments", title = "title", start = "starts_at", color = "blue" }));
        Assert.Contains("Filter with", await FailAsync(new { name = "A", table = "appointments", title = "title", start = "starts_at", filters = new[] { new { column = "place", op = "~", value = "x" } } }));
        using var none = await host.Client.PostAsJsonAsync(Path, new { name = "Cal", config = new { calendars = Array.Empty<object>() } });
        Assert.Contains("Add a calendar", await none.Content.ReadAsStringAsync());
        using var nameless = await host.Client.PostAsJsonAsync($"{Path}/1/passwords", new { name = "" });
        Assert.Equal(HttpStatusCode.NotFound, nameless.StatusCode);
    }

    private static async Task<JsonElement> GetJsonAsync(ApiIntegrationTestHost host, string url) =>
        JsonDocument.Parse(await host.Client.GetStringAsync(url)).RootElement.Clone();

    private static Task<JsonElement> PostAsync(ApiIntegrationTestHost host, string url, object body) => ImportSourceTests.PostAsync(host, url, body);
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresAppTests
{
    [PostgresFact]
    public async Task Subscription_OnPostgres() => await AppTests.SubscriptionAsync(await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "postgres-test-secret",
        ["Backend:PublicUrl"] = "https://bento.example",
        ["Schema:AutoMigrateOnStartup"] = "false"
    }));
}
