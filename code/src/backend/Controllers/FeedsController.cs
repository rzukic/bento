using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Middleware;
using Bento.Api.Services;
using Bento.Api.Services.Apps;

namespace Bento.Api.Controllers;

/// <summary>
/// iCal subscriptions (Step 5.4): <c>/feeds/{password}/{calendar}.ics</c>, read
/// by Apple Calendar, Google Calendar, Outlook, Thunderbird. Calendar programs
/// cannot sign in to a subscription, so the device's app password is part of
/// the address; revoking it closes the address. Outside /api/v1: plain text
/// answers, no JSON errors.
/// </summary>
[ApiController]
[Route("feeds")]
public sealed class FeedsController(AppService apps, CalendarEvents events, WorkspaceSettings workspace, AuthRateLimiter limiter) : ControllerBase
{
    /// <summary>Requests per minute and address; calendar programs ask a few times an hour.</summary>
    public const int RequestsPerMinute = 120;

    [HttpGet("{secret}/{calendar}")]
    [HttpHead("{secret}/{calendar}")]
    public async Task<IActionResult> Calendar(string secret, string calendar)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var limit = limiter.CheckRequest($"apps:{ip}", RequestsPerMinute, DateTimeOffset.UtcNow);
        if (!limit.Allowed)
        {
            Response.Headers.RetryAfter = limit.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Plain(StatusCodes.Status429TooManyRequests, "Too many requests; try again in a minute.");
        }

        var app = await apps.AuthenticateAsync(secret);
        var slug = calendar.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ? calendar[..^4] : calendar;
        var found = app?.Calendars.FirstOrDefault(c => c.Slug == slug);
        // Unknown password and unknown calendar look the same.
        if (found is null)
            return Plain(StatusCodes.Status404NotFound, "No such calendar.");

        IReadOnlyList<CalendarEvents.Event> list;
        try
        {
            list = await events.ReadAsync(found);
        }
        catch (InvalidOperationException e)
        {
            return Plain(StatusCodes.Status409Conflict, e.Message);
        }
        var zone = (await workspace.CalendarAsync()).ZoneId;
        // The tag follows the events, not the moment (DTSTAMP), so unchanged calendars answer 304.
        var tag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', list.Select(e => e.Uid + e.Tag)) + found.Name + found.Color + zone))).ToLowerInvariant()[..32] + "\"";
        Response.Headers.ETag = tag;
        Response.Headers.CacheControl = "private, max-age=300";
        if (Request.Headers.IfNoneMatch.Any(v => v == tag))
            return StatusCode(StatusCodes.Status304NotModified);

        var body = IcsWriter.Calendar(found.Name!, found.Color, zone, list, DateTimeOffset.UtcNow);
        return Content(body, IcsWriter.ContentType, Encoding.UTF8);
    }

    private ContentResult Plain(int status, string text) =>
        new() { StatusCode = status, Content = text, ContentType = "text/plain; charset=utf-8" };
}
