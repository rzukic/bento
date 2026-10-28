using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Middleware;
using Bento.Api.Models;
using Bento.Api.Services;
using Bento.Api.Services.Apps;

namespace Bento.Api.Controllers;

/// <summary>
/// CalDAV (RFC 4791) for calendar apps (Step 5.4 part B): Apple Calendar on
/// Mac and iPhone, Thunderbird, DAVx5. A device signs in with HTTP Basic — any
/// user name, its app password — and sees that app's calendars:
/// <list type="bullet">
/// <item><c>/dav/</c> → its principal <c>/dav/principal/</c> → the calendars <c>/dav/calendars/</c>;</item>
/// <item><c>/dav/calendars/{calendar}/</c> a calendar, <c>…/{event}.ics</c> an event.</item>
/// </list>
/// Sync by the calendar's getctag and the events' ETags (no sync-collection:
/// Bento keeps no history of deletions). PUT and DELETE change rows.
/// </summary>
[ApiController]
[Route("dav")]
public sealed class DavController(AppService apps, CalDavService calDav, AuthRateLimiter limiter, Services.Workspaces.WorkspaceService workspaces) : ControllerBase
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace CS = "http://calendarserver.org/ns/";
    private static readonly XNamespace A = "http://apple.com/ns/ical/";
    private const string Root = "/dav/";
    private const string Principal = "/dav/principal/";
    private const string Home = "/dav/calendars/";
    private const string Methods = "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, PROPPATCH, REPORT";
    /// <summary>Requests per minute and address; a sync takes a few dozen.</summary>
    public const int RequestsPerMinute = 600;

    [AcceptVerbs("OPTIONS", "GET", "HEAD", "PUT", "DELETE", "PROPFIND", "PROPPATCH", "REPORT", "MKCALENDAR", "MKCOL", "MOVE", "COPY", "LOCK", "UNLOCK", "POST")]
    [Route("")]
    [Route("{**path}")]
    public async Task<IActionResult> Handle(string? path)
    {
        var method = Request.Method.ToUpperInvariant();
        Response.Headers["DAV"] = "1, 3, calendar-access";
        if (method == "OPTIONS")
        {
            Response.Headers.Allow = Methods;
            return StatusCode(StatusCodes.Status200OK);
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var limit = limiter.CheckRequest($"apps:{ip}", RequestsPerMinute, DateTimeOffset.UtcNow);
        if (!limit.Allowed)
        {
            Response.Headers.RetryAfter = limit.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Text(StatusCodes.Status429TooManyRequests, "Too many requests; try again in a minute.");
        }

        var app = await apps.AuthenticateAsync(BasicPassword());
        if (app is null)
        {
            Response.Headers.WWWAuthenticate = "Basic realm=\"Bento\", charset=\"UTF-8\"";
            return Text(StatusCodes.Status401Unauthorized, "Sign in with the device's app password (Bento → Apps).");
        }

        // A device works in its app's workspace: its tables, model and settings.
        var workspace = workspaces.Find(app.WorkspaceId) ?? (await workspaces.ReloadAsync()).FirstOrDefault(w => w.Id == app.WorkspaceId);
        if (workspace is null)
            return Text(StatusCodes.Status401Unauthorized, "This app's workspace is gone.");
        using var inWorkspace = Services.Workspaces.WorkspaceContext.Use(workspace);

        if (method is "MKCALENDAR" or "MKCOL")
            return Text(StatusCodes.Status403Forbidden, "Calendars are set up in Bento (Apps).");
        if (method is "MOVE" or "COPY" or "LOCK" or "UNLOCK" or "POST")
            return StatusCode(StatusCodes.Status405MethodNotAllowed);

        var segments = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        try
        {
            return segments switch
            {
                [] => await CollectionAsync(method, Root, app, null),
                ["principal"] => await CollectionAsync(method, Principal, app, null),
                ["calendars"] => await CollectionAsync(method, Home, app, null),
                ["calendars", var slug] => app.Calendars.FirstOrDefault(c => c.Slug == slug) is { } calendar
                    ? await CalendarAsync(method, app, calendar)
                    : NotFound(),
                ["calendars", var slug, var name] => app.Calendars.FirstOrDefault(c => c.Slug == slug) is { } calendar
                    ? await EventAsync(method, app, calendar, name)
                    : NotFound(),
                _ => NotFound()
            };
        }
        catch (InvalidOperationException e)
        {
            // The table behind a calendar is gone, or its columns changed.
            return Text(StatusCodes.Status409Conflict, e.Message);
        }
    }

    /// <summary>The password of an HTTP Basic sign-in; the user name does not matter.</summary>
    private string? BasicPassword()
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) || header.Parameter is null)
            return null;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
            var colon = text.IndexOf(':');
            return colon < 0 ? null : text[(colon + 1)..];
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ── The root, the principal and the list of calendars ────────────────

    private async Task<IActionResult> CollectionAsync(string method, string href, AppService.App app, AppCalendar? _)
    {
        if (method is "GET" or "HEAD")
            return Text(StatusCodes.Status200OK, "Bento CalDAV. Add this address as a CalDAV account in your calendar app.");
        if (method == "PROPPATCH")
            return await PropPatchAsync(href);
        if (method != "PROPFIND")
            return StatusCode(StatusCodes.Status405MethodNotAllowed);

        var (requested, all) = await PropfindAsync();
        var responses = new List<XElement> { Answer(href, CollectionProps(href, app), requested, all) };
        if (href == Home && Depth() > 0)
        {
            foreach (var calendar in app.Calendars)
            {
                var resources = await calDav.ResourcesAsync(app, calendar);
                responses.Add(Answer(CalendarHref(calendar), CalendarProps(app, calendar, resources), requested, all));
            }
        }
        return MultiStatus(responses);
    }

    private Dictionary<XName, Func<object?>> CollectionProps(string href, AppService.App app)
    {
        var props = Common(app);
        props[D + "resourcetype"] = () => href == Principal
            ? new object[] { new XElement(D + "collection"), new XElement(D + "principal") }
            : new XElement(D + "collection");
        props[D + "displayname"] = () => href == Home ? "Calendars" : app.Name;
        if (href == Principal)
        {
            props[D + "principal-URL"] = () => Href(Principal);
            props[C + "calendar-user-address-set"] = () => Href(Principal);
        }
        return props;
    }

    private Dictionary<XName, Func<object?>> Common(AppService.App app) => new()
    {
        [D + "current-user-principal"] = () => Href(Principal),
        [D + "principal-URL"] = () => Href(Principal),
        [D + "owner"] = () => Href(Principal),
        [C + "calendar-home-set"] = () => Href(Home),
        [D + "current-user-privilege-set"] = () => Privileges(),
        [D + "supported-report-set"] = () => new[] { "calendar-multiget", "calendar-query" }
            .Select(r => new XElement(D + "supported-report", new XElement(D + "report", new XElement(C + r)))).ToArray<object>()
    };

    private static object[] Privileges() =>
        new[] { "read", "write", "write-properties", "write-content", "bind", "unbind", "read-current-user-privilege-set" }
            .Select(p => new XElement(D + "privilege", new XElement(D + p))).ToArray<object>();

    // ── A calendar ───────────────────────────────────────────────────────

    private static string CalendarHref(AppCalendar calendar) => $"{Home}{Uri.EscapeDataString(calendar.Slug!)}/";

    private Dictionary<XName, Func<object?>> CalendarProps(AppService.App app, AppCalendar calendar, IReadOnlyList<CalDavService.Resource> resources)
    {
        var ctag = CalDavService.CTag(resources);
        var props = Common(app);
        props[D + "resourcetype"] = () => new object[] { new XElement(D + "collection"), new XElement(C + "calendar") };
        props[D + "displayname"] = () => calendar.Name;
        props[CS + "getctag"] = () => ctag;
        props[D + "getetag"] = () => $"\"{ctag}\"";
        props[C + "supported-calendar-component-set"] = () => new XElement(C + "comp", new XAttribute("name", "VEVENT"));
        props[C + "supported-calendar-data"] = () => new XElement(C + "calendar-data", new XAttribute("content-type", "text/calendar"), new XAttribute("version", "2.0"));
        props[C + "calendar-description"] = () => $"Rows of {calendar.Table} in Bento";
        if (calendar.Color is { } color)
            props[A + "calendar-color"] = () => color + "FF";
        return props;
    }

    private async Task<IActionResult> CalendarAsync(string method, AppService.App app, AppCalendar calendar)
    {
        switch (method)
        {
            case "PROPFIND":
            {
                var (requested, all) = await PropfindAsync();
                var resources = await calDav.ResourcesAsync(app, calendar);
                var responses = new List<XElement> { Answer(CalendarHref(calendar), CalendarProps(app, calendar, resources), requested, all) };
                if (Depth() > 0)
                    responses.AddRange(resources.Select(r => Answer(EventHref(calendar, r), EventProps(calendar, r), requested, all)));
                return MultiStatus(responses);
            }
            case "REPORT":
                return await ReportAsync(app, calendar);
            case "GET" or "HEAD":
            {
                // The whole calendar as one file (some programs read it so).
                var resources = await calDav.ResourcesAsync(app, calendar);
                return Content(IcsWriter.Calendar(calendar.Name!, calendar.Color, "UTC", resources.Select(r => r.Event), DateTimeOffset.UtcNow), IcsWriter.ContentType, Encoding.UTF8);
            }
            case "PROPPATCH":
                return await PropPatchAsync(CalendarHref(calendar));
            case "MKCALENDAR" or "MKCOL":
                return Text(StatusCodes.Status403Forbidden, "Calendars are set up in Bento (Apps).");
            default:
                return StatusCode(StatusCodes.Status405MethodNotAllowed);
        }
    }

    private async Task<IActionResult> ReportAsync(AppService.App app, AppCalendar calendar)
    {
        var body = await ReadXmlAsync();
        if (body?.Root is not { } report)
            return Text(StatusCodes.Status400BadRequest, "A REPORT needs a body.");
        var requested = report.Element(D + "prop")?.Elements().Select(e => e.Name).ToList() ?? [D + "getetag", C + "calendar-data"];
        var resources = await calDav.ResourcesAsync(app, calendar);

        if (report.Name == C + "calendar-multiget")
        {
            var responses = new List<XElement>();
            foreach (var href in report.Elements(D + "href").Select(h => h.Value.Trim()))
            {
                var name = Uri.UnescapeDataString(href.TrimEnd('/').Split('/').Last());
                var found = resources.FirstOrDefault(r => r.Href == name);
                responses.Add(found is null
                    ? new XElement(D + "response", new XElement(D + "href", href), new XElement(D + "status", "HTTP/1.1 404 Not Found"))
                    : Answer(EventHref(calendar, found), EventProps(calendar, found), requested, false));
            }
            return MultiStatus(responses);
        }
        if (report.Name == C + "calendar-query")
        {
            var range = report.Descendants(C + "time-range").FirstOrDefault();
            var start = Moment(range?.Attribute("start")?.Value);
            var end = Moment(range?.Attribute("end")?.Value);
            var matching = resources.Where(r => Overlaps(r, start, end));
            return MultiStatus(matching.Select(r => Answer(EventHref(calendar, r), EventProps(calendar, r), requested, false)).ToList());
        }
        return DavError(StatusCodes.Status403Forbidden, new XElement(D + "supported-report"));
    }

    private static DateTimeOffset? Moment(string? value) =>
        value is not null && DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : null;

    /// <summary>Whether an event touches [start, end); repeating ones always count (their dates are the device's to work out).</summary>
    private static bool Overlaps(CalDavService.Resource resource, DateTimeOffset? start, DateTimeOffset? end)
    {
        if (resource.Event.Recurrence is not null || resource.Raw?.Contains("RRULE", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        DateTimeOffset From(CalendarEvents.EventTime t) => t.Moment ?? new DateTimeOffset(t.Day!.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(-14);
        DateTimeOffset To(CalendarEvents.EventTime t) => t.Moment ?? new DateTimeOffset(t.Day!.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(14);
        return (end is null || From(resource.Event.Start) < end) && (start is null || To(resource.Event.End) > start);
    }

    // ── An event ─────────────────────────────────────────────────────────

    private static string EventHref(AppCalendar calendar, CalDavService.Resource resource) => CalendarHref(calendar) + Uri.EscapeDataString(resource.Href);

    private static Dictionary<XName, Func<object?>> EventProps(AppCalendar calendar, CalDavService.Resource resource) => new()
    {
        [D + "getetag"] = () => resource.ETag,
        [D + "getcontenttype"] = () => "text/calendar; charset=utf-8; component=vevent",
        [D + "resourcetype"] = () => null,
        [C + "calendar-data"] = () => CalDavService.Ics(resource, calendar, DateTimeOffset.UtcNow)
    };

    private async Task<IActionResult> EventAsync(string method, AppService.App app, AppCalendar calendar, string name)
    {
        switch (method)
        {
            case "GET" or "HEAD" or "PROPFIND":
            {
                var resource = (await calDav.ResourcesAsync(app, calendar)).FirstOrDefault(r => r.Href == name);
                if (resource is null)
                    return NotFound();
                if (method == "PROPFIND")
                {
                    var (requested, all) = await PropfindAsync();
                    return MultiStatus([Answer(EventHref(calendar, resource), EventProps(calendar, resource), requested, all)]);
                }
                Response.Headers.ETag = resource.ETag;
                return Content(CalDavService.Ics(resource, calendar, DateTimeOffset.UtcNow), IcsWriter.ContentType, Encoding.UTF8);
            }
            case "PUT":
            {
                string body;
                using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
                    body = await reader.ReadToEndAsync();
                var ifMatch = Request.Headers.IfMatch.FirstOrDefault()?.Trim();
                var ifNoneMatch = Request.Headers.IfNoneMatch.Any(v => v?.Trim() == "*");
                var result = await calDav.PutAsync(app, calendar, name, body, ifMatch, ifNoneMatch);
                switch (result.Outcome)
                {
                    case CalDavService.PutOutcome.PreconditionFailed:
                        return Text(StatusCodes.Status412PreconditionFailed, result.Message ?? "Precondition failed.");
                    case CalDavService.PutOutcome.Invalid:
                        return DavError(StatusCodes.Status403Forbidden, new XElement(C + "valid-calendar-data"), result.Message);
                    default:
                        if (result.ETag is not null)
                            Response.Headers.ETag = result.ETag;
                        return StatusCode(result.Outcome == CalDavService.PutOutcome.Created ? StatusCodes.Status201Created : StatusCodes.Status204NoContent);
                }
            }
            case "DELETE":
            {
                var ifMatch = Request.Headers.IfMatch.FirstOrDefault()?.Trim();
                return await calDav.DeleteAsync(app, calendar, name, ifMatch) switch
                {
                    CalDavService.DeleteOutcome.Deleted => NoContent(),
                    CalDavService.DeleteOutcome.PreconditionFailed => Text(StatusCodes.Status412PreconditionFailed, "The event was changed in the meantime."),
                    _ => NotFound()
                };
            }
            default:
                return StatusCode(StatusCodes.Status405MethodNotAllowed);
        }
    }

    // ── WebDAV plumbing ──────────────────────────────────────────────────

    private int Depth() => Request.Headers["Depth"].FirstOrDefault()?.Trim().ToLowerInvariant() switch
    {
        "0" => 0,
        null => 0,
        _ => 1
    };

    private async Task<XDocument?> ReadXmlAsync()
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            // No DTDs, no external entities.
            using var xml = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(xml);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>The properties a PROPFIND asks for; none named (or allprop) means the usual ones.</summary>
    private async Task<(IReadOnlyList<XName> Requested, bool All)> PropfindAsync()
    {
        var body = await ReadXmlAsync();
        var prop = body?.Root?.Element(D + "prop");
        if (prop is null)
            return ([], true);
        return (prop.Elements().Select(e => e.Name).ToList(), false);
    }

    /// <summary>One resource's answer: the properties it has (200), and those it lacks (404).</summary>
    private static XElement Answer(string href, Dictionary<XName, Func<object?>> props, IReadOnlyList<XName> requested, bool all)
    {
        var names = all ? props.Keys.Where(k => k != C + "calendar-data").ToList() : requested.ToList();
        var found = new XElement(D + "prop");
        var missing = new XElement(D + "prop");
        foreach (var name in names)
        {
            if (props.TryGetValue(name, out var value))
                found.Add(new XElement(name, value()));
            else
                missing.Add(new XElement(name));
        }
        var response = new XElement(D + "response", new XElement(D + "href", href));
        if (found.HasElements || !missing.HasElements)
            response.Add(new XElement(D + "propstat", found, new XElement(D + "status", "HTTP/1.1 200 OK")));
        if (missing.HasElements)
            response.Add(new XElement(D + "propstat", missing, new XElement(D + "status", "HTTP/1.1 404 Not Found")));
        return response;
    }

    private static XElement Href(string href) => new(D + "href", href);

    /// <summary>
    /// Names and colours set on the device are accepted but not kept — they are
    /// set in Bento — so the device does not report an error.
    /// </summary>
    private async Task<IActionResult> PropPatchAsync(string href)
    {
        var body = await ReadXmlAsync();
        var names = body?.Root?.Descendants(D + "prop").SelectMany(p => p.Elements()).Select(e => new XElement(e.Name)).ToList() ?? [];
        return MultiStatus([new XElement(D + "response", Href(href), new XElement(D + "propstat", new XElement(D + "prop", names), new XElement(D + "status", "HTTP/1.1 200 OK")))]);
    }

    private ContentResult MultiStatus(List<XElement> responses)
    {
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(D + "multistatus",
                new XAttribute(XNamespace.Xmlns + "d", D), new XAttribute(XNamespace.Xmlns + "c", C),
                new XAttribute(XNamespace.Xmlns + "cs", CS), new XAttribute(XNamespace.Xmlns + "ical", A),
                responses));
        return new ContentResult { StatusCode = 207, Content = document.Declaration + "\n" + document.Root, ContentType = "application/xml; charset=utf-8" };
    }

    private static ContentResult DavError(int status, XElement condition, string? message = null)
    {
        var error = new XElement(D + "error", new XAttribute(XNamespace.Xmlns + "d", D), new XAttribute(XNamespace.Xmlns + "c", C), condition);
        if (message is not null)
            error.Add(new XComment(" " + message.Replace("--", "-") + " "));
        return new ContentResult { StatusCode = status, Content = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + error, ContentType = "application/xml; charset=utf-8" };
    }

    private static ContentResult Text(int status, string text) =>
        new() { StatusCode = status, Content = text, ContentType = "text/plain; charset=utf-8" };
}
