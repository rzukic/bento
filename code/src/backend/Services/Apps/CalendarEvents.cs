using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bento.Api.Models;

namespace Bento.Api.Services.Apps;

/// <summary>
/// A calendar's rows as events: the mapped columns read from the table (with
/// the calendar's filters), times in the workspace's time zone. Rows without a
/// title or a start are left out.
/// </summary>
public sealed class CalendarEvents
{
    /// <summary>The most events one calendar hands out.</summary>
    public const int MaxEvents = 10_000;
    private const int Page = 1000;

    private readonly SchemaCache _schemaCache;
    private readonly CrudService _crud;
    private readonly WorkspaceSettings _workspace;

    public CalendarEvents(SchemaCache schemaCache, CrudService crud, WorkspaceSettings workspace)
    {
        _schemaCache = schemaCache;
        _crud = crud;
        _workspace = workspace;
    }

    /// <param name="Start">A day (all-day) or a moment.</param>
    /// <param name="End">Exclusive: the day after an all-day event, the moment a timed one ends.</param>
    /// <param name="Tag">Changes whenever the event does (an ETag).</param>
    /// <param name="Recurrence">An RRULE value, when the calendar maps a column to it.</param>
    /// <param name="State">The row's value in the calendar's status column.</param>
    /// <param name="Status">The iCal STATUS it shows as: TENTATIVE, CONFIRMED or CANCELLED.</param>
    /// <param name="Invitation">When the event is an invitation: who asks, who answers, and the answer.</param>
    public sealed record Event(string RowId, string Uid, string Title, EventTime Start, EventTime End, string? Description, string? Location, string Tag, string? Recurrence = null,
        string? State = null, string? Status = null, Invitation? Invitation = null);

    /// <param name="PartStat">NEEDS-ACTION, ACCEPTED, DECLINED or TENTATIVE.</param>
    public sealed record Invitation(string Organizer, string OrganizerName, string Attendee, string? AttendeeName, string PartStat);

    /// <summary>A day of an all-day event, or a moment (UTC).</summary>
    public readonly record struct EventTime(DateOnly? Day, DateTimeOffset? Moment)
    {
        public bool AllDay => Day is not null;
    }

    /// <param name="invitations">Who answers the calendar's invitations, when its status has them.</param>
    public async Task<IReadOnlyList<Event>> ReadAsync(AppCalendar calendar, AppInvitations? invitations = null)
    {
        var table = _schemaCache.GetTable(calendar.Table!)
            ?? throw new InvalidOperationException($"The table {calendar.Table} no longer exists.");
        var key = table.Columns.FirstOrDefault(c => c.PrimaryKey)?.Name ?? "id";
        var zone = (await _workspace.CalendarAsync()).Zone;
        // "Not" keeps empty cells (private ≠ yes: also the rows where it is not set), which SQL's <> drops.
        var filters = (calendar.Filters ?? []).Where(f => f.Op != "neq").Select(f => new FilterClause(f.Column!, f.Op ?? "eq", f.Value)).ToList();
        var not = (calendar.Filters ?? []).Where(f => f.Op == "neq").ToList();
        var events = new List<Event>();
        for (var page = 1; events.Count < MaxEvents; page++)
        {
            var result = await _crud.ListAsync(table, new ListQueryParameters { Page = page, PerPage = Page, Filters = filters, Sorts = [new SortClause(calendar.Start!, false)] });
            foreach (var row in result.Data)
            {
                if (not.Any(f => Same(Get(row, f.Column), f.Value)))
                    continue;
                if (ToEvent(row, calendar, key, table.Name, zone, invitations) is { } found)
                    events.Add(found);
            }
            if (page >= result.TotalPages)
                break;
        }
        return events;
    }

    private static Event? ToEvent(IDictionary<string, object?> row, AppCalendar calendar, string key, string table, TimeZoneInfo zone, AppInvitations? invitations)
    {
        var id = Text(Get(row, key));
        var title = Text(Get(row, calendar.Title));
        if (id is null || string.IsNullOrWhiteSpace(title))
            return null;
        var allDayFlag = calendar.AllDay is { } flag ? Bool(Get(row, flag)) : null;
        if (Time(Get(row, calendar.Start), zone, allDayFlag) is not { } start)
            return null;
        var end = calendar.End is { } endColumn ? Time(Get(row, endColumn), zone, start.AllDay) : null;
        if (start.AllDay)
        {
            // An all-day end is the last day; iCal wants the day after.
            DateOnly lastDay;
            if (end?.Day is { } endDay)
                lastDay = endDay;
            else if (end?.Moment is { } moment)
                lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, zone).DateTime);
            else
                lastDay = start.Day!.Value;
            if (lastDay < start.Day!.Value)
                lastDay = start.Day.Value;
            end = new EventTime(lastDay.AddDays(1), null);
        }
        else if (end?.Moment is null || end.Value.Moment <= start.Moment)
        {
            end = new EventTime(null, start.Moment!.Value.AddMinutes(calendar.Duration ?? 60));
        }
        var description = Text(Get(row, calendar.Description));
        var location = Text(Get(row, calendar.Location));
        var recurrence = Text(Get(row, calendar.Recurrence))?.Trim();
        if (recurrence is not null && recurrence.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
            recurrence = recurrence[6..];
        var uid = $"{table}-{id}@bento";
        var state = calendar.Status is { Column: { } statusColumn } ? Text(Get(row, statusColumn))?.Trim() : null;
        var answer = Answer(calendar.Status, state);
        var invitation = answer is not null && invitations?.Attendee is { } attendee
            ? new Invitation(invitations.Organizer ?? AppService.DefaultOrganizer, invitations.OrganizerName ?? "Bento", attendee, invitations.AttendeeName, answer)
            : null;
        // The status only counts where a calendar maps one, so other calendars keep their ETags.
        var tagged = string.Join('\u001f', title, Stamp(start), Stamp(end.Value), description, location, recurrence) + (state is null ? "" : "\u001f" + state);
        var tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tagged))).ToLowerInvariant()[..16];
        return new Event(id, uid, title.Trim(), start, end.Value, description, location, tag, string.IsNullOrEmpty(recurrence) ? null : recurrence,
            state, Shown(calendar.Status, state), invitation);
    }

    /// <summary>The answer a status value stands for: NEEDS-ACTION while pending, then ACCEPTED, DECLINED or TENTATIVE; null for other values.</summary>
    public static string? Answer(AppStatus? status, string? state)
    {
        if (status?.Pending is not { Count: > 0 } pending || state is null)
            return null;
        if (pending.Contains(state, StringComparer.OrdinalIgnoreCase))
            return "NEEDS-ACTION";
        if (string.Equals(state, status.Accepted, StringComparison.OrdinalIgnoreCase))
            return "ACCEPTED";
        if (string.Equals(state, status.Declined, StringComparison.OrdinalIgnoreCase))
            return "DECLINED";
        if (string.Equals(state, status.Tentative, StringComparison.OrdinalIgnoreCase))
            return "TENTATIVE";
        return null;
    }

    /// <summary>The iCal STATUS of a status value: as mapped, else from the invitation it is (pending and maybe tentative, accepted confirmed, declined cancelled).</summary>
    private static string? Shown(AppStatus? status, string? state)
    {
        if (status is null || state is null)
            return null;
        if (status.Values?.FirstOrDefault(v => string.Equals(v.Key, state, StringComparison.OrdinalIgnoreCase)) is { Value: { } shown })
            return shown;
        return Answer(status, state) switch
        {
            "NEEDS-ACTION" or "TENTATIVE" => "TENTATIVE",
            "ACCEPTED" => "CONFIRMED",
            "DECLINED" => "CANCELLED",
            _ => null
        };
    }

    /// <summary>A cell equals a filter's text: yes/no as true/false (or 1/0), numbers and text as written, case aside.</summary>
    private static bool Same(object? cell, string? value)
    {
        if (cell is null)
            return value is null;
        if (value is null)
            return false;
        // Yes/no cells come as true/false or, from SQLite, as 1/0.
        if (cell is bool or long or int && value.Trim().ToLowerInvariant() is "true" or "false")
            return Bool(cell) == Bool(value.Trim().ToLowerInvariant());
        return string.Equals(Text(cell)?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string Stamp(EventTime time) =>
        time.Day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? time.Moment!.Value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    private static object? Get(IDictionary<string, object?> row, string? column) =>
        column is not null && row.TryGetValue(column, out var value) ? value : null;

    private static string? Text(object? value) => value switch
    {
        null => null,
        string s => s,
        DateTime d => d.ToString("o", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static bool? Bool(object? value) => value switch
    {
        bool b => b,
        long or int => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
        string s when bool.TryParse(s, out var b) => b,
        string s => s == "1",
        _ => null
    };

    /// <summary>
    /// A cell as an event time: a day for date values (or when the event is
    /// all-day), else a moment. Bento keeps date-times as the wall time they were
    /// written with and shows them so; Postgres hands them back with a Z. So the
    /// digits are wall time in the workspace's zone, Z or not — what the calendar
    /// shows is what Bento shows. Only an explicit offset (+02:00) is a moment.
    /// </summary>
    public static EventTime? Time(object? value, TimeZoneInfo zone, bool? allDay)
    {
        DateTimeOffset? moment = null;
        DateOnly? day = null;
        switch (value)
        {
            case null:
                return null;
            case DateOnly d:
                day = d;
                break;
            case DateTimeOffset o:
                moment = o;
                break;
            case DateTime dt:
                moment = Local(dt, zone);
                break;
            default:
            {
                var text = Text(value)!.Trim();
                if (text.Length == 10 && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDay))
                    day = parsedDay;
                else if (HasOffset(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
                    moment = withOffset;
                else if (DateTime.TryParse(text.TrimEnd('Z', 'z'), CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
                    moment = Local(wall, zone);
                else
                    return null;
                break;
            }
        }
        if (day is not null)
            return allDay == false ? new EventTime(null, Local(day.Value.ToDateTime(TimeOnly.MinValue), zone)) : new EventTime(day, null);
        if (allDay == true)
            return new EventTime(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment!.Value, zone).DateTime), null);
        return new EventTime(null, moment!.Value.ToUniversalTime());
    }

    private static bool HasOffset(string text) =>
        text.Length > 19 && (text[^6] is '+' or '-') && text[^3] == ':';

    private static DateTimeOffset Local(DateTime wall, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        // A time skipped by the clock change (02:30 in March) counts as the hour after.
        if (zone.IsInvalidTime(unspecified))
            unspecified = unspecified.AddHours(1);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }
}
