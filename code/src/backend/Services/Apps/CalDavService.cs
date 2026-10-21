using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Models;
using Dapper;

namespace Bento.Api.Services.Apps;

/// <summary>
/// The calendar side of CalDAV (Step 5.4 part B): a calendar's rows as
/// resources with stable addresses and ETags, and what devices send turned
/// into rows — created, changed and deleted through the same row service as
/// the app, so validation and computed columns apply. An event made on a
/// device keeps the address and UID the device gave it, and what the table
/// does not hold (alarms, attendees, exceptions) is kept as the device sent it.
/// </summary>
public sealed class CalDavService
{
    private readonly ISystemPool _systemPool;
    private readonly SchemaCache _schemaCache;
    private readonly CrudService _crud;
    private readonly CalendarEvents _events;
    private readonly WorkspaceSettings _workspace;
    private readonly ICustomValueRecalculation? _recalculation;

    public CalDavService(ISystemPool systemPool, SchemaCache schemaCache, CrudService crud, CalendarEvents events, WorkspaceSettings workspace,
        ICustomValueRecalculation? recalculation = null)
    {
        _systemPool = systemPool;
        _schemaCache = schemaCache;
        _crud = crud;
        _events = events;
        _workspace = workspace;
        _recalculation = recalculation;
    }

    private bool Pg => _systemPool.Provider == DatabaseProvider.Postgres;
    private string Links => Pg ? "_system.app_events" : "app_events";

    /// <param name="Href">The resource's name in the calendar, e.g. "appointments-4.ics".</param>
    /// <param name="Raw">The iCalendar the device last sent, when the event came from one.</param>
    public sealed record Resource(string Href, string Uid, CalendarEvents.Event Event, string? Raw, string ETag);

    private sealed class LinkRow
    {
        public long Id { get; init; }
        public string RowId { get; init; } = "";
        public string Href { get; init; } = "";
        public string Uid { get; init; } = "";
        public string? Raw { get; init; }
    }

    private async Task<List<LinkRow>> LinksAsync(long appId, string calendar)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        return (await conn.QueryAsync<LinkRow>(
            $"SELECT id, row_id AS RowId, href, uid, raw FROM {Links} WHERE app_id = @AppId AND calendar = @Calendar",
            new { AppId = appId, Calendar = calendar })).ToList();
    }

    public async Task<IReadOnlyList<Resource>> ResourcesAsync(AppService.App app, AppCalendar calendar)
    {
        var events = await _events.ReadAsync(calendar);
        var links = (await LinksAsync(app.Id, calendar.Slug!)).GroupBy(l => l.RowId).ToDictionary(g => g.Key, g => g.Last());
        return events.Select(e => ToResource(e, links.GetValueOrDefault(e.RowId), calendar)).ToList();
    }

    private static Resource ToResource(CalendarEvents.Event e, LinkRow? link, AppCalendar calendar)
    {
        var uid = link?.Uid ?? e.Uid;
        var href = link?.Href ?? $"{calendar.Table}-{e.RowId}.ics";
        var tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(e.Tag + "\u001f" + (link?.Raw ?? "")))).ToLowerInvariant()[..24];
        return new Resource(href, uid, e with { Uid = uid }, link?.Raw, $"\"{tag}\"");
    }

    /// <summary>The calendar's version: changes whenever any event does (CalendarServer's getctag).</summary>
    public static string CTag(IEnumerable<Resource> resources) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', resources.Select(r => r.Href + r.ETag))))).ToLowerInvariant()[..32];

    /// <summary>The iCalendar of one resource: as the device sent it with the row's values put in, or written from the row.</summary>
    public static string Ics(Resource resource, AppCalendar calendar, DateTimeOffset now) =>
        resource.Raw is { } raw ? IcsWriter.Merge(raw, resource.Event, now, calendar.Recurrence is not null) : IcsWriter.Single(resource.Event, now);

    // ── Writes ───────────────────────────────────────────────────────────

    public enum PutOutcome { Created, Updated, PreconditionFailed, Invalid }

    public sealed record PutResult(PutOutcome Outcome, string? ETag, string? Message);

    /// <summary>
    /// A device stores an event at <paramref name="href"/>: a new row, or the
    /// row behind it changed. <paramref name="ifMatch"/> and
    /// <paramref name="ifNoneMatchAny"/> guard against overwriting a change the
    /// device has not seen.
    /// </summary>
    public async Task<PutResult> PutAsync(AppService.App app, AppCalendar calendar, string href, string body, string? ifMatch, bool ifNoneMatchAny)
    {
        var parsed = IcsReader.Parse(body);
        var vevent = parsed?.All("VEVENT").FirstOrDefault(e => e.First("RECURRENCE-ID") is null) ?? parsed?.All("VEVENT").FirstOrDefault();
        if (vevent is null)
            return new PutResult(PutOutcome.Invalid, null, "Only events (VEVENT) can be stored in this calendar.");
        var uid = vevent.First("UID")?.Value.Trim();
        if (string.IsNullOrEmpty(uid))
            return new PutResult(PutOutcome.Invalid, null, "The event has no UID.");

        var table = _schemaCache.GetTable(calendar.Table!) ?? throw new InvalidOperationException($"The table {calendar.Table} no longer exists.");
        var existing = (await ResourcesAsync(app, calendar)).FirstOrDefault(r => r.Href == href);
        if (ifNoneMatchAny && existing is not null)
            return new PutResult(PutOutcome.PreconditionFailed, existing.ETag, "The event exists already.");
        if (ifMatch is not null && ifMatch != "*" && existing?.ETag != ifMatch)
            return new PutResult(PutOutcome.PreconditionFailed, existing?.ETag, "The event was changed in the meantime.");
        if (ifMatch == "*" && existing is null)
            return new PutResult(PutOutcome.PreconditionFailed, null, "The event does not exist.");

        var zone = (await _workspace.CalendarAsync()).Zone;
        Dictionary<string, object?> values;
        try
        {
            values = Values(vevent, calendar, table, zone);
        }
        catch (FormatException e)
        {
            return new PutResult(PutOutcome.Invalid, null, e.Message);
        }

        string rowId;
        try
        {
            if (existing is not null)
            {
                rowId = existing.Event.RowId;
                await _crud.PatchAsync(table, rowId, JsonSerializer.SerializeToElement(values));
            }
            else
            {
                // A new event must stay in the calendar: the calendar's "is" filters are filled in.
                foreach (var filter in (calendar.Filters ?? []).Where(f => f.Op == "eq" && f.Value is not null && !values.ContainsKey(f.Column!)))
                    values[filter.Column!] = FilterValue(table, filter.Column!, filter.Value!);
                var created = await _crud.CreateAsync(table, JsonSerializer.SerializeToElement(values));
                var key = table.Columns.FirstOrDefault(c => c.PrimaryKey)?.Name ?? "id";
                rowId = Convert.ToString(created[key], CultureInfo.InvariantCulture)!;
            }
        }
        catch (ApiException e)
        {
            return new PutResult(PutOutcome.Invalid, null, e.Message);
        }
        await SaveLinkAsync(app.Id, calendar.Slug!, rowId, href, uid, body);
        if (_recalculation is not null)
            await _recalculation.RecalculateNowAsync(table);

        var stored = (await ResourcesAsync(app, calendar)).FirstOrDefault(r => r.Href == href);
        return new PutResult(existing is null ? PutOutcome.Created : PutOutcome.Updated, stored?.ETag, null);
    }

    public enum DeleteOutcome { Deleted, NotFound, PreconditionFailed }

    public async Task<DeleteOutcome> DeleteAsync(AppService.App app, AppCalendar calendar, string href, string? ifMatch)
    {
        var existing = (await ResourcesAsync(app, calendar)).FirstOrDefault(r => r.Href == href);
        if (existing is null)
            return DeleteOutcome.NotFound;
        if (ifMatch is not null && ifMatch != "*" && existing.ETag != ifMatch)
            return DeleteOutcome.PreconditionFailed;
        var table = _schemaCache.GetTable(calendar.Table!) ?? throw new InvalidOperationException($"The table {calendar.Table} no longer exists.");
        await _crud.DeleteAsync(table, existing.Event.RowId);
        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync($"DELETE FROM {Links} WHERE app_id = @AppId AND calendar = @Calendar AND row_id = @RowId",
            new { AppId = app.Id, Calendar = calendar.Slug, RowId = existing.Event.RowId });
        _recalculation?.Request();
        return DeleteOutcome.Deleted;
    }

    private async Task SaveLinkAsync(long appId, string calendar, string rowId, string href, string uid, string raw)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        // One address per row: a row moved to a new address (rare) leaves its old one.
        await conn.ExecuteAsync($"DELETE FROM {Links} WHERE app_id = @AppId AND calendar = @Calendar AND (row_id = @RowId OR href = @Href)",
            new { AppId = appId, Calendar = calendar, RowId = rowId, Href = href });
        await conn.ExecuteAsync(
            $"""
            INSERT INTO {Links} (app_id, calendar, row_id, href, uid, raw, updated_at)
            VALUES (@AppId, @Calendar, @RowId, @Href, @Uid, @Raw, {SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now")})
            """,
            new { AppId = appId, Calendar = calendar, RowId = rowId, Href = href, Uid = uid, Raw = raw, Now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) });
    }

    /// <summary>
    /// The row's values from the event: title, start and end as the columns
    /// keep them (a date column a day; a date-time column the wall time in the
    /// workspace's zone), all-day, location, notes, the repetition.
    /// </summary>
    public static Dictionary<string, object?> Values(IcsReader.Component vevent, AppCalendar calendar, TableDef table, TimeZoneInfo zone)
    {
        var start = IcsReader.Time(vevent.First("DTSTART"), zone) ?? throw new FormatException("The event has no start (DTSTART).");
        var end = IcsReader.Time(vevent.First("DTEND"), zone);
        if (end is null && IcsReader.Duration(vevent.First("DURATION")?.Value) is { } duration)
            end = start.Day is { } day
                ? new CalendarEvents.EventTime(day.AddDays(Math.Max(1, (int)duration.TotalDays)), null)
                : new CalendarEvents.EventTime(null, start.Moment!.Value + duration);

        ColumnType TypeOf(string column) => AppService.ValueType(table.Columns.First(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)));
        string Cell(CalendarEvents.EventTime time, string column, bool isEnd)
        {
            if (TypeOf(column) == ColumnType.Date)
            {
                // An all-day end is exclusive in iCal; the table keeps the last day.
                var day = time.Day ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.Moment!.Value, zone).DateTime);
                if (time.Day is not null && isEnd)
                    day = day.AddDays(-1);
                return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            var wall = time.Day is { } d
                ? d.AddDays(isEnd ? -1 : 0).ToDateTime(TimeOnly.MinValue)
                : TimeZoneInfo.ConvertTime(time.Moment!.Value, zone).DateTime;
            return wall.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [calendar.Title!] = IcsReader.Unescape(vevent.First("SUMMARY")?.Value ?? "").Trim() is { Length: > 0 } title ? title : "(no title)",
            [calendar.Start!] = Cell(start, calendar.Start!, false)
        };
        if (calendar.End is { } endColumn)
            values[endColumn] = end is { } e ? Cell(e, endColumn, true) : null;
        if (calendar.AllDay is { } allDay)
            values[allDay] = start.AllDay;
        if (calendar.Location is { } location)
            values[location] = vevent.First("LOCATION") is { } l ? IcsReader.Unescape(l.Value) : null;
        if (calendar.Description is { } description)
            values[description] = vevent.First("DESCRIPTION") is { } d2 ? IcsReader.Unescape(d2.Value) : null;
        if (calendar.Recurrence is { } recurrence)
            values[recurrence] = vevent.First("RRULE")?.Value.Trim();
        return values;
    }

    /// <summary>A filter's text as the column's value (yes/no, numbers), for a new row.</summary>
    private static object? FilterValue(TableDef table, string column, string value)
    {
        var type = AppService.ValueType(table.Columns.First(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)));
        return type switch
        {
            ColumnType.Boolean => value.Trim().ToLowerInvariant() is "true" or "1" or "yes",
            ColumnType.Integer when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            ColumnType.Float or ColumnType.Currency when decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => value
        };
    }
}
