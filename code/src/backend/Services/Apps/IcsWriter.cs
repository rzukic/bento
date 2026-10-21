using System.Globalization;
using System.Text;

namespace Bento.Api.Services.Apps;

/// <summary>
/// iCalendar (RFC 5545) as calendar programs read it: CRLF lines folded at 75
/// octets, text escaped, all-day events as dates, timed ones in UTC.
/// </summary>
public static class IcsWriter
{
    public const string ContentType = "text/calendar; charset=utf-8";

    /// <summary>A whole calendar: what an iCal subscription downloads.</summary>
    public static string Calendar(string name, string? color, string timeZone, IEnumerable<CalendarEvents.Event> events, DateTimeOffset now)
    {
        var text = new StringBuilder();
        Begin(text, name, color, timeZone);
        foreach (var e in events)
            WriteEvent(text, e, now);
        Line(text, "END:VCALENDAR");
        return text.ToString();
    }

    /// <summary>One event in its own VCALENDAR: a CalDAV resource.</summary>
    public static string Single(CalendarEvents.Event e, DateTimeOffset now)
    {
        var text = new StringBuilder();
        Line(text, "BEGIN:VCALENDAR");
        Line(text, "VERSION:2.0");
        Line(text, "PRODID:-//Bento//Calendar//EN");
        WriteEvent(text, e, now);
        Line(text, "END:VCALENDAR");
        return text.ToString();
    }

    private static void Begin(StringBuilder text, string name, string? color, string timeZone)
    {
        Line(text, "BEGIN:VCALENDAR");
        Line(text, "VERSION:2.0");
        Line(text, "PRODID:-//Bento//Calendar//EN");
        Line(text, "CALSCALE:GREGORIAN");
        Line(text, "METHOD:PUBLISH");
        Line(text, "X-WR-CALNAME:" + Escape(name));
        Line(text, "NAME:" + Escape(name));
        Line(text, "X-WR-TIMEZONE:" + timeZone);
        if (color is not null)
        {
            Line(text, "X-APPLE-CALENDAR-COLOR:" + color);
            Line(text, "COLOR:" + color);
        }
        // Programs poll at their own pace; this asks for hourly (Apple, Outlook honour it).
        Line(text, "REFRESH-INTERVAL;VALUE=DURATION:PT1H");
        Line(text, "X-PUBLISHED-TTL:PT1H");
    }

    private static void WriteEvent(StringBuilder text, CalendarEvents.Event e, DateTimeOffset now)
    {
        Line(text, "BEGIN:VEVENT");
        Line(text, "UID:" + Escape(e.Uid));
        Line(text, "DTSTAMP:" + Utc(now));
        Line(text, Property("DTSTART", e.Start));
        Line(text, Property("DTEND", e.End));
        Line(text, "SUMMARY:" + Escape(e.Title));
        if (!string.IsNullOrWhiteSpace(e.Description))
            Line(text, "DESCRIPTION:" + Escape(e.Description));
        if (!string.IsNullOrWhiteSpace(e.Location))
            Line(text, "LOCATION:" + Escape(e.Location));
        Line(text, "END:VEVENT");
    }

    private static string Property(string name, CalendarEvents.EventTime time) =>
        time.Day is { } day
            ? $"{name};VALUE=DATE:{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}"
            : $"{name}:{Utc(time.Moment!.Value)}";

    public static string Utc(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>TEXT values: backslash, semicolon, comma and line breaks escaped.</summary>
    public static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    /// <summary>A content line, folded so no line is longer than 75 octets (never inside a UTF-8 character).</summary>
    private static void Line(StringBuilder text, string line)
    {
        var octets = 0;
        var first = true;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (octets + size > (first ? 75 : 74))
            {
                text.Append("\r\n ");
                octets = 0;
                first = false;
            }
            text.Append(rune.ToString());
            octets += size;
        }
        text.Append("\r\n");
    }
}
