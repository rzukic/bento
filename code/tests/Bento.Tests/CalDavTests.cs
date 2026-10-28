using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Bento.Api.Services.Apps;

namespace Bento.Tests;

/// <summary>
/// Step 5.4 part B: CalDAV as calendar apps speak it — discovery, the
/// calendars and their events, events made, moved and deleted on the device
/// becoming rows, rows changed in Bento showing on the device, and no
/// overwriting of changes a device has not seen.
/// </summary>
public sealed class CalDavTests
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace CS = "http://calendarserver.org/ns/";

    private const string Schema = """
        { "tables": [
            { "name": "appointments", "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "title", "type": "text" },
                { "name": "starts_at", "type": "datetime", "nullable": false },
                { "name": "ends_at", "type": "datetime" },
                { "name": "place", "type": "text" },
                { "name": "repeat", "type": "text" } ] },
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

    [Fact]
    public async Task CalDav_LikeACalendarApp() => await CalDavAsync(await ApiIntegrationTestHost.StartAsync(Settings("sqlite-test-secret")));

    internal static async Task CalDavAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        await host.ApplySchemaAsync(Schema);
        (await host.Client.PutAsJsonAsync("/api/v1/admin/settings", new { currency = "EUR", timeZone = "Europe/Berlin" })).EnsureSuccessStatusCode();
        await PostAsync(host, "/api/v1/data/appointments", new { title = "Dentist", starts_at = "2026-10-23T10:00:00", ends_at = "2026-10-23T11:00:00", place = "Main St 1" });
        await PostAsync(host, "/api/v1/data/deadlines", new { what = "Tax return", due = "2026-10-31", done = false });
        var app = await PostAsync(host, "/api/v1/admin/apps", new
        {
            name = "My calendar",
            config = new
            {
                calendars = new object[]
                {
                    new { name = "Termine", table = "appointments", title = "title", start = "starts_at", end = "ends_at", location = "place", recurrence = "repeat", color = "#3B82F6" },
                    new { name = "Fristen", table = "deadlines", title = "what", start = "due", filters = new[] { new { column = "done", op = "eq", value = "false" } } }
                }
            }
        });
        var id = app.GetProperty("id").GetInt64();
        var created = await PostAsync(host, $"/api/v1/admin/apps/{id}/passwords", new { name = "iPhone" });
        var secret = created.GetProperty("secret").GetString()!;
        Assert.Contains(created.GetProperty("links").EnumerateArray(), l => l.GetProperty("kind").GetString() == "caldav" && l.GetProperty("url").GetString() == "https://bento.example/dav/");

        // Discovery: OPTIONS needs no sign-in, everything else does.
        using (var options = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/dav/")))
            Assert.Contains("calendar-access", options.Headers.GetValues("DAV").Single());
        using (var anonymous = await host.Client.SendAsync(Dav("PROPFIND", "/dav/", null, null)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.StartsWith("Basic", anonymous.Headers.WwwAuthenticate.Single().Scheme);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(Dav("PROPFIND", "/dav/", "bento_app_wrong", null))).StatusCode);
        // The test client follows the redirect, as calendar apps do.
        using (var wellKnown = await host.Client.SendAsync(Dav("PROPFIND", "/.well-known/caldav", null, null)))
            Assert.Equal("/dav/", wellKnown.RequestMessage!.RequestUri!.AbsolutePath);

        var root = await MultiStatusAsync(host, Dav("PROPFIND", "/dav/", secret, Propfind(D + "current-user-principal"), "0"));
        Assert.Equal("/dav/principal/", root.Descendants(D + "current-user-principal").Single().Element(D + "href")!.Value);
        var principal = await MultiStatusAsync(host, Dav("PROPFIND", "/dav/principal/", secret, Propfind(C + "calendar-home-set", D + "displayname"), "0"));
        Assert.Equal("/dav/calendars/", principal.Descendants(C + "calendar-home-set").Single().Element(D + "href")!.Value);
        Assert.Equal("My calendar", principal.Descendants(D + "displayname").Single().Value);

        var home = await MultiStatusAsync(host, Dav("PROPFIND", "/dav/calendars/", secret, Propfind(D + "resourcetype", D + "displayname", CS + "getctag", D + "unknown-thing"), "1"));
        var calendars = home.Elements(D + "response").Where(r => r.Descendants(C + "calendar").Any()).ToList();
        Assert.Equal(["/dav/calendars/termine/", "/dav/calendars/fristen/"], calendars.Select(r => r.Element(D + "href")!.Value));
        Assert.Equal("Termine", calendars[0].Descendants(D + "displayname").Single().Value);
        // A property Bento does not have is a 404 propstat, not an error.
        Assert.Contains(home.Descendants(D + "propstat"), p => p.Element(D + "status")!.Value.Contains("404") && p.Descendants(D + "unknown-thing").Any());
        var ctag = calendars[0].Descendants(CS + "getctag").Single().Value;

        // The calendar's events with their ETags, then their data.
        var listing = await MultiStatusAsync(host, Dav("PROPFIND", "/dav/calendars/termine/", secret, Propfind(D + "getetag"), "1"));
        var dentist = listing.Elements(D + "response").Single(r => r.Element(D + "href")!.Value.EndsWith(".ics"));
        Assert.Equal("/dav/calendars/termine/appointments-1.ics", dentist.Element(D + "href")!.Value);
        var multiget = await MultiStatusAsync(host, Dav("REPORT", "/dav/calendars/termine/", secret,
            new XElement(C + "calendar-multiget", new XElement(D + "prop", new XElement(D + "getetag"), new XElement(C + "calendar-data")),
                new XElement(D + "href", "/dav/calendars/termine/appointments-1.ics"), new XElement(D + "href", "/dav/calendars/termine/gone.ics")), "1"));
        var data = multiget.Descendants(C + "calendar-data").Single().Value;
        Assert.Contains("SUMMARY:Dentist", data);
        Assert.Contains("DTSTART:20261023T080000Z", data);
        Assert.Contains(multiget.Elements(D + "response"), r => r.Element(D + "href")!.Value.EndsWith("gone.ics") && r.Element(D + "status")!.Value.Contains("404"));

        // Made on the iPhone: Berlin time, an alarm, a weekly repetition.
        const string phoneEvent = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Apple Inc.//iOS 26.0//EN
            BEGIN:VTIMEZONE
            TZID:Europe/Berlin
            END:VTIMEZONE
            BEGIN:VEVENT
            UID:5F1B2C3D-AAAA-4BBB-8CCC-1234567890AB
            DTSTAMP:20261021T090000Z
            DTSTART;TZID=Europe/Berlin:20261027T183000
            DTEND;TZID=Europe/Berlin:20261027T200000
            SUMMARY:Choir\, Tuesday
            LOCATION:Church hall
            RRULE:FREQ=WEEKLY;BYDAY=TU
            BEGIN:VALARM
            ACTION:DISPLAY
            DESCRIPTION:Reminder
            TRIGGER:-PT30M
            END:VALARM
            END:VEVENT
            END:VCALENDAR
            """;
        const string phoneHref = "/dav/calendars/termine/5F1B2C3D-AAAA-4BBB-8CCC-1234567890AB.ics";
        using (var put = await host.Client.SendAsync(Ics("PUT", phoneHref, secret, phoneEvent, ifNoneMatch: true)))
        {
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
            Assert.NotNull(put.Headers.ETag);
        }
        var row = await RowAsync(host, "appointments", 2);
        Assert.Equal(("Choir, Tuesday", "Church hall", "FREQ=WEEKLY;BYDAY=TU"), (row.GetProperty("title").GetString(), row.GetProperty("place").GetString(), row.GetProperty("repeat").GetString()));
        // Stored as the wall time Bento shows.
        Assert.StartsWith("2026-10-27T18:30:00", row.GetProperty("starts_at").GetString());
        Assert.StartsWith("2026-10-27T20:00:00", row.GetProperty("ends_at").GetString());
        // Again with If-None-Match: * — it exists already.
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await host.Client.SendAsync(Ics("PUT", phoneHref, secret, phoneEvent, ifNoneMatch: true))).StatusCode);

        // The device reads it back under its own address and UID, with its alarm.
        var (back, etag) = await GetEventAsync(host, phoneHref, secret);
        Assert.Contains("UID:5F1B2C3D-AAAA-4BBB-8CCC-1234567890AB", back);
        Assert.Contains("BEGIN:VALARM\r\nACTION:DISPLAY\r\nDESCRIPTION:Reminder\r\nTRIGGER:-PT30M\r\nEND:VALARM", back);
        Assert.Contains("DTSTART:20261027T173000Z", back);
        Assert.Contains("RRULE:FREQ=WEEKLY;BYDAY=TU", back);
        Assert.Single(back.Split("SUMMARY:").Skip(1));
        var newCtag = (await MultiStatusAsync(host, Dav("PROPFIND", "/dav/calendars/termine/", secret, Propfind(CS + "getctag"), "0"))).Descendants(CS + "getctag").Single().Value;
        Assert.NotEqual(ctag, newCtag);

        // Moved on the device: with the ETag it saw.
        var moved = phoneEvent.Replace("20261027T183000", "20261027T190000").Replace("20261027T200000", "20261027T203000");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await host.Client.SendAsync(Ics("PUT", phoneHref, secret, moved, ifMatch: "\"stale\""))).StatusCode);
        using (var update = await host.Client.SendAsync(Ics("PUT", phoneHref, secret, moved, ifMatch: etag)))
            Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.StartsWith("2026-10-27T19:00:00", (await RowAsync(host, "appointments", 2)).GetProperty("starts_at").GetString());

        // Changed in Bento (or by Claude): the device sees it, the alarm stays.
        (await host.Client.PatchAsJsonAsync("/api/v1/data/appointments/2", new { title = "Choir rehearsal" })).EnsureSuccessStatusCode();
        var (changed, changedTag) = await GetEventAsync(host, phoneHref, secret);
        Assert.Contains("SUMMARY:Choir rehearsal", changed);
        Assert.Contains("TRIGGER:-PT30M", changed);
        Assert.NotEqual(etag, changedTag);

        // Only what overlaps the asked range.
        var query = await MultiStatusAsync(host, Dav("REPORT", "/dav/calendars/termine/", secret,
            new XElement(C + "calendar-query", new XElement(D + "prop", new XElement(D + "getetag")),
                new XElement(C + "filter", new XElement(C + "comp-filter", new XAttribute("name", "VCALENDAR"),
                    new XElement(C + "comp-filter", new XAttribute("name", "VEVENT"),
                        new XElement(C + "time-range", new XAttribute("start", "20261022T000000Z"), new XAttribute("end", "20261024T000000Z")))))), "1"));
        // The dentist, and the repeating choir (its dates are the device's to work out).
        Assert.Equal(2, query.Elements(D + "response").Count());
        var later = await MultiStatusAsync(host, Dav("REPORT", "/dav/calendars/termine/", secret,
            new XElement(C + "calendar-query", new XElement(D + "prop", new XElement(D + "getetag")),
                new XElement(C + "filter", new XElement(C + "comp-filter", new XAttribute("name", "VCALENDAR"),
                    new XElement(C + "comp-filter", new XAttribute("name", "VEVENT"),
                        new XElement(C + "time-range", new XAttribute("start", "20261101T000000Z")))))), "1"));
        Assert.Single(later.Elements(D + "response"));

        // An all-day deadline made on the device: a date, and the calendar's filter filled in.
        const string deadline = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:deadline-from-mac
            DTSTART;VALUE=DATE:20261105
            DTEND;VALUE=DATE:20261106
            SUMMARY:Insurance
            END:VEVENT
            END:VCALENDAR
            """;
        Assert.Equal(HttpStatusCode.Created, (await host.Client.SendAsync(Ics("PUT", "/dav/calendars/fristen/deadline-from-mac.ics", secret, deadline))).StatusCode);
        var insurance = await RowAsync(host, "deadlines", 2);
        var done = insurance.GetProperty("done");
        Assert.Equal(("Insurance", "2026-11-05", false), (insurance.GetProperty("what").GetString(), insurance.GetProperty("due").GetString(),
            done.ValueKind == JsonValueKind.Number ? done.GetInt32() != 0 : done.GetBoolean()));
        Assert.Contains("DTSTART;VALUE=DATE:20261105\r\nDTEND;VALUE=DATE:20261106", (await GetEventAsync(host, "/dav/calendars/fristen/deadline-from-mac.ics", secret)).Ics);
        // Marked done in Bento: it leaves the calendar.
        (await host.Client.PatchAsJsonAsync("/api/v1/data/deadlines/2", new { done = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.SendAsync(Dav("GET", "/dav/calendars/fristen/deadline-from-mac.ics", secret, null))).StatusCode);

        // Not an event, and a calendar made on the device: refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(Ics("PUT", "/dav/calendars/termine/x.ics", secret, "BEGIN:VCALENDAR\r\nBEGIN:VTODO\r\nUID:x\r\nEND:VTODO\r\nEND:VCALENDAR\r\n"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(Dav("MKCALENDAR", "/dav/calendars/new/", secret, null))).StatusCode);

        // Deleted on the device: the row goes.
        var (_, current) = await GetEventAsync(host, phoneHref, secret);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await host.Client.SendAsync(Dav("DELETE", phoneHref, secret, null, ifMatch: "\"stale\""))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(Dav("DELETE", phoneHref, secret, null, ifMatch: current))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/v1/data/appointments/2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.SendAsync(Dav("DELETE", phoneHref, secret, null))).StatusCode);
    }

    [Fact]
    public void Merge_KeepsWhatTheTableDoesNotHold()
    {
        const string raw = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:a\r\nDTSTART:20261001T080000Z\r\nSUMMARY:Old\r\nRRULE:FREQ=DAILY\r\nATTENDEE:mailto:anna@example.com\r\n" +
                           "BEGIN:VALARM\r\nACTION:DISPLAY\r\nDESCRIPTION:Ping\r\nTRIGGER:-PT5M\r\nEND:VALARM\r\nEND:VEVENT\r\n" +
                           "BEGIN:VEVENT\r\nUID:a\r\nRECURRENCE-ID:20261002T080000Z\r\nDTSTART:20261002T090000Z\r\nSUMMARY:Moved once\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var e = new CalendarEvents.Event("1", "a", "New", new CalendarEvents.EventTime(null, new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero)),
            new CalendarEvents.EventTime(null, new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero)), null, null, "t");
        var merged = IcsWriter.Merge(raw, e, new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), recurrenceMapped: false);

        Assert.Contains("SUMMARY:New", merged);
        Assert.DoesNotContain("SUMMARY:Old", merged);
        Assert.Contains("DTSTART:20261001T090000Z\r\nDTEND:20261001T100000Z", merged);
        // Not mapped: the repetition, the attendee, the alarm (with its DESCRIPTION) and the exception stay.
        Assert.Contains("RRULE:FREQ=DAILY", merged);
        Assert.Contains("ATTENDEE:mailto:anna@example.com", merged);
        Assert.Contains("DESCRIPTION:Ping", merged);
        Assert.Contains("RECURRENCE-ID:20261002T080000Z\r\nDTSTART:20261002T090000Z\r\nSUMMARY:Moved once", merged);
        var parsed = IcsReader.Parse(merged)!;
        Assert.Equal(2, parsed.All("VEVENT").Count());
    }

    [Fact]
    public void Reader_UnfoldsAndReadsTimes()
    {
        var text = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:A long\r\n  title\\, folded\r\nDTSTART;TZID=\"W. Europe Standard Time\":20261023T100000\r\nDURATION:PT1H30M\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var vevent = IcsReader.Parse(text)!.All("VEVENT").Single();
        Assert.Equal("A long title, folded", IcsReader.Unescape(vevent.First("SUMMARY")!.Value));
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero), IcsReader.Time(vevent.First("DTSTART"), berlin)!.Value.Moment);
        Assert.Equal(TimeSpan.FromMinutes(90), IcsReader.Duration(vevent.First("DURATION")!.Value));
        Assert.Equal(TimeSpan.FromDays(-7), IcsReader.Duration("-P1W"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    internal static HttpRequestMessage Dav(string method, string url, string? secret, XElement? body, string? depth = null, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (secret is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("ruben:" + secret)));
        if (depth is not null)
            request.Headers.Add("Depth", depth);
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (body is not null)
            request.Content = new StringContent(new XDocument(body).ToString(), Encoding.UTF8, "application/xml");
        return request;
    }

    internal static HttpRequestMessage Ics(string method, string url, string secret, string ics, bool ifNoneMatch = false, string? ifMatch = null)
    {
        var request = Dav(method, url, secret, null, ifMatch: ifMatch);
        if (ifNoneMatch)
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        request.Content = new StringContent(ics.Replace("\r\n", "\n").Replace("\n", "\r\n"), Encoding.UTF8, "text/calendar");
        return request;
    }

    private static XElement Propfind(params XName[] names) => new(D + "propfind", new XElement(D + "prop", names.Select(n => new XElement(n))));

    private static async Task<XElement> MultiStatusAsync(ApiIntegrationTestHost host, HttpRequestMessage request)
    {
        using var response = await host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == 207, $"{(int)response.StatusCode}: {text}");
        return XDocument.Parse(text).Root!;
    }

    internal static async Task<(string Ics, string ETag)> GetEventAsync(ApiIntegrationTestHost host, string url, string secret)
    {
        using var response = await host.Client.SendAsync(Dav("GET", url, secret, null));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return (text, response.Headers.ETag!.ToString());
    }

    internal static async Task<JsonElement> RowAsync(ApiIntegrationTestHost host, string table, int id)
    {
        var text = await host.Client.GetStringAsync($"/api/v1/data/{table}/{id}");
        var root = JsonDocument.Parse(text).RootElement;
        return (root.TryGetProperty("data", out var data) ? data : root).Clone();
    }

    private static Task<JsonElement> PostAsync(ApiIntegrationTestHost host, string url, object body) => ImportSourceTests.PostAsync(host, url, body);
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresCalDavTests
{
    [PostgresFact]
    public async Task CalDav_OnPostgres() => await CalDavTests.CalDavAsync(await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "postgres-test-secret",
        ["Backend:PublicUrl"] = "https://bento.example",
        ["Schema:AutoMigrateOnStartup"] = "false"
    }));
}
