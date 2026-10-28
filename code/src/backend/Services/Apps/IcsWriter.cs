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

    /// <summary>A whole calendar in one file: GET of a CalDAV calendar.</summary>
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

    /// <summary>
    /// An event as the device sent it, with what Bento keeps in the table put in:
    /// title, times, location, notes (and the repetition, when the calendar maps
    /// it) come from the row; alarms, attendees, URLs, exceptions and anything
    /// else stay as the device wrote them.
    /// </summary>
    public static string Merge(string raw, CalendarEvents.Event e, DateTimeOffset now, bool recurrenceMapped)
    {
        var replaced = new HashSet<string>(StringComparer.Ordinal) { "UID", "DTSTAMP", "DTSTART", "DTEND", "DURATION", "SUMMARY", "DESCRIPTION", "LOCATION" };
        if (recurrenceMapped)
            replaced.Add("RRULE");
        var lines = IcsReader.Unfold(raw);
        var text = new StringBuilder();
        var depth = 0;
        var mainDone = false;
        // The VEVENT being read: its lines, each with whether it is the event's own property (not an alarm's).
        List<(string Line, bool Own)>? current = null;
        var eventDepth = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;
            var upper = line.ToUpperInvariant();
            if (upper.StartsWith("BEGIN:", StringComparison.Ordinal))
            {
                depth++;
                if (current is null && upper == "BEGIN:VEVENT")
                {
                    current = [(line, false)];
                    eventDepth = depth;
                    continue;
                }
            }
            var ends = upper.StartsWith("END:", StringComparison.Ordinal);
            if (current is not null)
            {
                if (ends && depth == eventDepth && upper == "END:VEVENT")
                {
                    depth--;
                    // The first event that is not an exception (RECURRENCE-ID) is the one the row holds.
                    var isOverride = current.Any(l => l.Own && Name(l.Line.ToUpperInvariant()) == "RECURRENCE-ID");
                    if (mainDone || isOverride)
                    {
                        foreach (var kept in current)
                            Line(text, kept.Line);
                    }
                    else
                    {
                        mainDone = true;
                        Line(text, current[0].Line);
                        Line(text, "UID:" + Escape(e.Uid));
                        Line(text, "DTSTAMP:" + Utc(now));
                        Line(text, Property("DTSTART", e.Start));
                        Line(text, Property("DTEND", e.End));
                        Line(text, "SUMMARY:" + Escape(e.Title));
                        if (!string.IsNullOrWhiteSpace(e.Description))
                            Line(text, "DESCRIPTION:" + Escape(e.Description));
                        if (!string.IsNullOrWhiteSpace(e.Location))
                            Line(text, "LOCATION:" + Escape(e.Location));
                        if (recurrenceMapped && e.Recurrence is { } rule)
                            Line(text, "RRULE:" + rule);
                        foreach (var kept in current.Skip(1).Where(l => !(l.Own && replaced.Contains(Name(l.Line.ToUpperInvariant())))))
                            Line(text, kept.Line);
                    }
                    Line(text, line);
                    current = null;
                    continue;
                }
                current.Add((line, depth == eventDepth && !upper.StartsWith("BEGIN:", StringComparison.Ordinal) && !ends));
                if (ends)
                    depth--;
                continue;
            }
            if (ends)
                depth--;
            Line(text, line);
        }
        return mainDone ? text.ToString() : Single(e, now);
    }

    private static string Name(string line)
    {
        var end = line.IndexOfAny([';', ':']);
        return end < 0 ? line : line[..end];
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
        if (e.Recurrence is { } rule)
            Line(text, "RRULE:" + rule);
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
