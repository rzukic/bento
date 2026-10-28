using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Bento.Api.Services.Apps;

namespace Bento.Tests;

/// <summary>
/// Proposals as invitations: rows in a pending state go out with an organizer
/// and the user as attendee, so the calendar app offers accept, maybe and
/// decline; the answer comes back as the row's status — and nothing else.
/// </summary>
public sealed class CalDavInvitationTests
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";

    private const string Schema = """
        { "tables": [
            { "name": "tasks", "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "title", "type": "text", "nullable": false },
                { "name": "stage", "type": "text" } ] },
            { "name": "blocks", "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "title", "type": "text", "nullable": false },
                { "name": "start_at", "type": "datetime", "nullable": false },
                { "name": "end_at", "type": "datetime" },
                { "name": "status", "type": "enum", "values": ["confirmed", "tentative", "proposed", "done", "cancelled"], "nullable": false },
                { "name": "answered_at", "type": "datetime" },
                { "name": "task_id", "type": "reference", "references": "tasks" } ] }
          ] }
        """;

    internal static Dictionary<string, string?> Settings(string secret) => new()
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = secret,
        ["Backend:PublicUrl"] = "https://bento.example",
        ["Schema:AutoMigrateOnStartup"] = "false"
    };

    [Fact]
    public async Task Proposals_AreAnsweredOnTheDevice() => await InvitationsAsync(await ApiIntegrationTestHost.StartAsync(Settings("sqlite-test-secret")));

    internal static async Task InvitationsAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        await host.ApplySchemaAsync(Schema);
        (await host.Client.PutAsJsonAsync("/api/v1/admin/settings", new { currency = "EUR", timeZone = "Europe/Berlin" })).EnsureSuccessStatusCode();
        await PostAsync(host, "/api/v1/data/tasks", new { title = "Write thesis chapter", stage = "planned" });
        foreach (var (title, day, status) in new[] { ("Focus: chapter 2", "03", "proposed"), ("Lecture", "04", "confirmed"), ("Focus: chapter 3", "05", "proposed"), ("Focus: reading", "06", "proposed"), ("Focus: notes", "09", "proposed") })
            await PostAsync(host, "/api/v1/data/blocks", new { title, start_at = $"2026-11-{day}T09:00:00", end_at = $"2026-11-{day}T11:00:00", status, task_id = 1 });

        var calendar = new
        {
            name = "Study",
            table = "blocks",
            title = "title",
            start = "start_at",
            end = "end_at",
            color = "#16A085",
            filters = new[] { new { column = "status", op = "neq", value = "cancelled" } },
            status = new { column = "status", values = new Dictionary<string, string> { ["done"] = "CONFIRMED" }, pending = new[] { "proposed" }, accepted = "confirmed", declined = "cancelled", answeredAt = "answered_at" }
        };
        // Invitations need the address the device knows as its user's.
        using (var missing = await host.Client.PostAsJsonAsync("/api/v1/admin/apps", new { name = "Planner", config = new { calendars = new[] { calendar } } }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Contains("DAVx", await missing.Content.ReadAsStringAsync());
        }
        var app = await PostAsync(host, "/api/v1/admin/apps", new { name = "Planner", config = new { calendars = new[] { calendar }, invitations = new { attendee = "me@example.com", attendeeName = "Ruben" } } });
        var invitations = app.GetProperty("config").GetProperty("invitations");
        Assert.Equal(("me@example.com", "bento@bento.invalid", "Bento"), (invitations.GetProperty("attendee").GetString(), invitations.GetProperty("organizer").GetString(), invitations.GetProperty("organizerName").GetString()));
        var secret = (await PostAsync(host, $"/api/v1/admin/apps/{app.GetProperty("id").GetInt64()}/passwords", new { name = "S21" })).GetProperty("secret").GetString()!;

        // The device learns its user's address from the principal.
        var principal = await MultiStatusAsync(host, CalDavTests.Dav("PROPFIND", "/dav/principal/", secret, new XElement(D + "propfind", new XElement(D + "prop", new XElement(C + "calendar-user-address-set"))), "0"));
        Assert.Equal("mailto:me@example.com", principal.Descendants(C + "calendar-user-address-set").Single().Elements(D + "href").First().Value);

        // A proposal: tentative, from Bento, waiting for the user's answer.
        var (proposal, proposalTag) = await CalDavTests.GetEventAsync(host, "/dav/calendars/study/blocks-1.ics", secret);
        Assert.Contains("UID:blocks-1@bento", proposal);
        Assert.Contains("STATUS:TENTATIVE", proposal);
        Assert.Contains("ORGANIZER;CN=\"Bento\":mailto:bento@bento.invalid", proposal);
        Assert.Contains("ATTENDEE;CN=\"Ruben\";CUTYPE=INDIVIDUAL;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:me@example.com", Unfold(proposal));
        // A booking made confirmed is a plain event.
        var (lecture, _) = await CalDavTests.GetEventAsync(host, "/dav/calendars/study/blocks-2.ics", secret);
        Assert.Contains("STATUS:CONFIRMED", lecture);
        Assert.DoesNotContain("ORGANIZER", lecture);
        Assert.DoesNotContain("ATTENDEE", lecture);

        // Accepted on the phone (as DAVx⁵ sends it) — with a start the device moved in the meantime, which an answer does not carry over.
        var accepted = Answer(proposal, "ACCEPTED").Replace("DTSTART:20261103T080000Z", "DTSTART:20261103T120000Z");
        using (var put = await host.Client.SendAsync(CalDavTests.Ics("PUT", "/dav/calendars/study/blocks-1.ics", secret, accepted, ifMatch: proposalTag)))
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var block = await CalDavTests.RowAsync(host, "blocks", 1);
        Assert.Equal("confirmed", block.GetProperty("status").GetString());
        Assert.StartsWith("2026-11-03T09:00:00", block.GetProperty("start_at").GetString());
        Assert.False(string.IsNullOrEmpty(block.GetProperty("answered_at").GetString()));
        // Still Bento's invitation, now accepted: same address, same UID, no second event.
        var (acceptedBack, _) = await CalDavTests.GetEventAsync(host, "/dav/calendars/study/blocks-1.ics", secret);
        Assert.Contains("UID:blocks-1@bento", acceptedBack);
        Assert.Contains("STATUS:CONFIRMED", acceptedBack);
        Assert.Contains("PARTSTAT=ACCEPTED", Unfold(acceptedBack));
        Assert.Single(Unfold(acceptedBack).Split('\n'), l => l.StartsWith("ATTENDEE"));
        Assert.Single(Unfold(acceptedBack).Split('\n'), l => l.StartsWith("ORGANIZER"));
        Assert.Equal(5, await CountAsync(host, secret));

        // Maybe: the row keeps waiting, the device keeps its answer.
        var (third, thirdTag) = await CalDavTests.GetEventAsync(host, "/dav/calendars/study/blocks-3.ics", secret);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(CalDavTests.Ics("PUT", "/dav/calendars/study/blocks-3.ics", secret, Answer(third, "TENTATIVE"), ifMatch: thirdTag))).StatusCode);
        Assert.Equal("proposed", (await CalDavTests.RowAsync(host, "blocks", 3)).GetProperty("status").GetString());
        Assert.Contains("PARTSTAT=TENTATIVE", Unfold((await CalDavTests.GetEventAsync(host, "/dav/calendars/study/blocks-3.ics", secret)).Ics));

        // Declined: the block is cancelled and leaves the calendar; the task and the other blocks stay as they are.
        var (fourth, fourthTag) = await CalDavTests.GetEventAsync(host, "/dav/calendars/study/blocks-4.ics", secret);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(CalDavTests.Ics("PUT", "/dav/calendars/study/blocks-4.ics", secret, Answer(fourth, "DECLINED"), ifMatch: fourthTag))).StatusCode);
        var declined = await CalDavTests.RowAsync(host, "blocks", 4);
        Assert.Equal(("cancelled", "2026-11-06T09:00:00"), (declined.GetProperty("status").GetString(), declined.GetProperty("start_at").GetString()![..19]));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.SendAsync(CalDavTests.Dav("GET", "/dav/calendars/study/blocks-4.ics", secret, null))).StatusCode);
        var task = await CalDavTests.RowAsync(host, "tasks", 1);
        Assert.Equal(("Write thesis chapter", "planned"), (task.GetProperty("title").GetString(), task.GetProperty("stage").GetString()));
        Assert.Equal("confirmed", (await CalDavTests.RowAsync(host, "blocks", 1)).GetProperty("status").GetString());
        Assert.StartsWith("2026-11-04T09:00:00", (await CalDavTests.RowAsync(host, "blocks", 2)).GetProperty("start_at").GetString());

        // Deleting a proposal on the device declines it: the row stays.
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(CalDavTests.Dav("DELETE", "/dav/calendars/study/blocks-5.ics", secret, null))).StatusCode);
        Assert.Equal("cancelled", (await CalDavTests.RowAsync(host, "blocks", 5)).GetProperty("status").GetString());

        // Accepted in Bento after the device answered "maybe": the device's invitation shows the acceptance.
        (await host.Client.PatchAsJsonAsync("/api/v1/data/blocks/3", new { status = "confirmed" })).EnsureSuccessStatusCode();
        var (inBento, _) = await CalDavTests.GetEventAsync(host, "/dav/calendars/study/blocks-3.ics", secret);
        Assert.Contains("STATUS:CONFIRMED", inBento);
        Assert.Contains("PARTSTAT=ACCEPTED:mailto:me@example.com", Unfold(inBento));
        // A proposal the device never answered, confirmed in Bento: a plain event again.
        await PostAsync(host, "/api/v1/data/blocks", new { title = "Focus: review", start_at = "2026-11-10T09:00:00", end_at = "2026-11-10T10:00:00", status = "proposed", task_id = 1 });
        Assert.Contains("PARTSTAT=NEEDS-ACTION", Unfold((await CalDavTests.GetEventAsync(host, "/dav/calendars/study/blocks-6.ics", secret)).Ics));
        (await host.Client.PatchAsJsonAsync("/api/v1/data/blocks/6", new { status = "confirmed" })).EnsureSuccessStatusCode();
        var (plain, _) = await CalDavTests.GetEventAsync(host, "/dav/calendars/study/blocks-6.ics", secret);
        Assert.Contains("STATUS:CONFIRMED", plain);
        Assert.DoesNotContain("ORGANIZER", plain);
        Assert.DoesNotContain("mailto:me@example.com", plain);
        Assert.Equal(4, await CountAsync(host, secret));
    }

    [Fact]
    public void Merge_DropsTheInvitationWhenTheRowIsNoLongerOne()
    {
        const string raw = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:blocks-1@bento\r\nDTSTART:20261103T080000Z\r\nSUMMARY:Focus\r\nSTATUS:CONFIRMED\r\n" +
                           "ORGANIZER;CN=Bento:mailto:bento@bento.invalid\r\nATTENDEE;PARTSTAT=ACCEPTED:mailto:me@example.com\r\nATTENDEE;PARTSTAT=ACCEPTED:mailto:anna@example.com\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var e = new CalendarEvents.Event("1", "blocks-1@bento", "Focus", new CalendarEvents.EventTime(null, new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero)),
            new CalendarEvents.EventTime(null, new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero)), null, null, "t", State: "done", Status: "CONFIRMED");
        var merged = IcsWriter.Merge(raw, e, DateTimeOffset.UtcNow, recurrenceMapped: false, new Bento.Api.Models.AppInvitations { Attendee = "me@example.com", Organizer = "bento@bento.invalid" });

        Assert.DoesNotContain("ORGANIZER", merged);
        Assert.DoesNotContain("mailto:me@example.com", merged);
        Assert.Contains("ATTENDEE;PARTSTAT=ACCEPTED:mailto:anna@example.com", merged);
        Assert.Single(merged.Split("STATUS:").Skip(1));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>The event as a calendar app sends it back after the user answered.</summary>
    private static string Answer(string ics, string partStat) =>
        Unfold(ics).Replace("PARTSTAT=NEEDS-ACTION;RSVP=TRUE", $"PARTSTAT={partStat}");

    private static string Unfold(string ics) => string.Join("\n", IcsReader.Unfold(ics));

    private static async Task<int> CountAsync(ApiIntegrationTestHost host, string secret) =>
        (await MultiStatusAsync(host, CalDavTests.Dav("PROPFIND", "/dav/calendars/study/", secret, new XElement(D + "propfind", new XElement(D + "prop", new XElement(D + "getetag"))), "1")))
        .Elements(D + "response").Count(r => r.Element(D + "href")!.Value.EndsWith(".ics"));

    private static async Task<XElement> MultiStatusAsync(ApiIntegrationTestHost host, HttpRequestMessage request)
    {
        using var response = await host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == 207, $"{(int)response.StatusCode}: {text}");
        return XDocument.Parse(text).Root!;
    }

    private static Task<JsonElement> PostAsync(ApiIntegrationTestHost host, string url, object body) => ImportSourceTests.PostAsync(host, url, body);
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresCalDavInvitationTests
{
    [PostgresFact]
    public async Task Proposals_OnPostgres() =>
        await CalDavInvitationTests.InvitationsAsync(await ApiIntegrationTestHost.StartPostgresAsync(CalDavInvitationTests.Settings("postgres-test-secret")));
}
