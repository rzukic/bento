using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Bento.Api.Db;
using Bento.Api.Exceptions;

namespace Bento.Api.Services;

/// <summary>
/// Workspace-wide settings an admin chooses: the locale numbers and dates are
/// written in (null: each browser's language), the currency of amounts, and the
/// calendar — the time zone "today" and every schedule are in, the day weeks
/// start on and the month the (fiscal) year starts in. Kept in memory once read;
/// every change goes through <see cref="SetAsync"/>.
/// Per workspace (Step 5.5): the default workspace's values are the
/// installation's and every other workspace starts from them; a value set in
/// a workspace is kept under <c>key@id</c>.
/// </summary>
public sealed partial class WorkspaceSettings
{
    public const string DefaultCurrency = "EUR";
    public static readonly string[] WeekStarts = ["monday", "sunday", "saturday"];

    private readonly ISystemPool _systemPool;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Values> _current = new();

    public WorkspaceSettings(ISystemPool systemPool)
    {
        _systemPool = systemPool;
    }

    /// <param name="TimeZone">An IANA time zone (Europe/Berlin, America/New_York); null: the server's.</param>
    /// <param name="WeekStart">monday, sunday or saturday.</param>
    /// <param name="YearStart">The month the year starts in, 1 (January) to 12; quarters follow it.</param>
    public sealed record Values(string? Locale, string Currency, string? TimeZone = null, string WeekStart = "monday", int YearStart = 1)
    {
        /// <summary>The calendar these settings describe.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public WorkspaceCalendar Calendar => new(ResolveZone(TimeZone), WeekStart, YearStart);
    }

    /// <summary>The settings as last read or saved; the defaults before the first read.</summary>
    public Values Current => _current.GetValueOrDefault(Ws) ?? _current.GetValueOrDefault(Workspaces.Workspace.DefaultId) ?? new Values(null, DefaultCurrency);

    /// <summary>The calendar in effect now (time zone, week and year start).</summary>
    public WorkspaceCalendar Calendar => Current.Calendar;

    public async Task<Values> GetAsync()
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = (await conn.QueryAsync<(string Key, string? Value)>($"SELECT key, value FROM {Table}"))
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
        var suffix = Suffix;
        // The workspace's own value (even an empty one: "each browser's language"), else the installation's.
        string? Text(string key) =>
            (suffix.Length > 0 && rows.TryGetValue(key + suffix, out var own) ? own : rows.GetValueOrDefault(key)) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;
        var values = new Values(
            Text("locale"),
            Text("currency") ?? DefaultCurrency,
            Text("time_zone"),
            Text("week_start") is { } week && WeekStarts.Contains(week) ? week : "monday",
            int.TryParse(Text("year_start"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var month) && month is >= 1 and <= 12 ? month : 1);
        _current[Ws] = values;
        return values;
    }

    /// <summary>Reads the settings once, so <see cref="Current"/> holds them; a database not ready yet leaves the defaults.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_current.ContainsKey(Ws))
            return;
        try
        {
            await GetAsync();
        }
        catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException)
        {
            // Before the bootstrap made the table: the defaults until then.
        }
    }

    /// <summary>The calendar, read once and then kept.</summary>
    public async Task<WorkspaceCalendar> CalendarAsync()
    {
        await EnsureLoadedAsync();
        return Calendar;
    }

    /// <summary>Forgets what was read (the bootstrap changed the table).</summary>
    public void Invalidate() => _current.Clear();

    public sealed record Changes(string? Locale, string? Currency, string? TimeZone = null, string? WeekStart = null, int? YearStart = null);

    /// <summary>
    /// Validates and stores the settings. Locale and currency are always set
    /// (a null or empty locale means each browser's language); the calendar
    /// fields are kept when left out, and an empty time zone means the server's.
    /// </summary>
    public async Task<Values> SetAsync(Changes changes)
    {
        var existing = await GetAsync();
        var locale = string.IsNullOrWhiteSpace(changes.Locale) ? null : changes.Locale.Trim();
        if (locale is not null)
        {
            // The browser formats (Intl); the server only keeps the tag. It runs without
            // culture data (InvariantGlobalization), so the tag is checked by its form.
            var match = LocaleTag().Match(locale);
            if (!match.Success)
                throw Invalid("locale", $"'{locale}' is not a language and region like de-AT or en-US.");
            locale = match.Groups[2].Success
                ? $"{match.Groups[1].Value.ToLowerInvariant()}-{match.Groups[2].Value.ToUpperInvariant()}"
                : match.Groups[1].Value.ToLowerInvariant();
        }
        var code = string.IsNullOrWhiteSpace(changes.Currency) ? DefaultCurrency : changes.Currency.Trim().ToUpperInvariant();
        if (!CurrencyCode().IsMatch(code))
            throw Invalid("currency", $"'{changes.Currency}' is not a currency code like EUR or USD.");

        var timeZone = existing.TimeZone;
        if (changes.TimeZone is not null)
        {
            timeZone = string.IsNullOrWhiteSpace(changes.TimeZone) ? null : changes.TimeZone.Trim();
            // IANA names only: they are also handed to Postgres (AT TIME ZONE).
            if (timeZone is not null && (!ZoneName().IsMatch(timeZone) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _)))
                throw Invalid("timeZone", $"'{timeZone}' is not a time zone like Europe/Berlin or America/New_York.");
        }
        var weekStart = changes.WeekStart?.Trim().ToLowerInvariant() ?? existing.WeekStart;
        if (!WeekStarts.Contains(weekStart))
            throw Invalid("weekStart", "Weeks start on monday, sunday or saturday.");
        var yearStart = changes.YearStart ?? existing.YearStart;
        if (yearStart is < 1 or > 12)
            throw Invalid("yearStart", "The year starts in a month from 1 (January) to 12 (December).");

        using var conn = await _systemPool.GetConnectionAsync();
        foreach (var (key, value) in new[]
                 {
                     ("locale", locale), ("currency", (string?)code), ("time_zone", timeZone), ("week_start", weekStart),
                     ("year_start", yearStart.ToString(CultureInfo.InvariantCulture))
                 })
        {
            await conn.ExecuteAsync($"DELETE FROM {Table} WHERE key = @Key", new { Key = key + Suffix });
            await conn.ExecuteAsync($"INSERT INTO {Table} (key, value) VALUES (@Key, @Value)", new { Key = key + Suffix, Value = value });
        }
        var values = new Values(locale, code, timeZone, weekStart, yearStart);
        if (Ws == Workspaces.Workspace.DefaultId)
            _current.Clear(); // other workspaces may start from these
        _current[Ws] = values;
        return values;
    }

    /// <summary>The zone of an IANA name; the server's own when none is set or it is unknown here.</summary>
    public static TimeZoneInfo ResolveZone(string? id) =>
        id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Local;

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();

    /// <summary>de, de-AT, en_us … a language, maybe a region (two letters or a UN M.49 number).</summary>
    [GeneratedRegex("^([A-Za-z]{2,3})(?:[-_]([A-Za-z]{2}|[0-9]{3}))?$")]
    private static partial Regex LocaleTag();

    /// <summary>Area/Location (America/Argentina/Buenos_Aires, Etc/GMT+5) or UTC.</summary>
    [GeneratedRegex(@"^(?:UTC|[A-Za-z]+(?:/[A-Za-z0-9_+\-]+){1,2})$")]
    private static partial Regex ZoneName();

    private static long Ws => Workspaces.WorkspaceContext.Id;

    /// <summary>"" for the default workspace (the installation's values), "@3" for workspace 3.</summary>
    private static string Suffix => Ws == Workspaces.Workspace.DefaultId ? "" : "@" + Ws.ToString(CultureInfo.InvariantCulture);

    private string Table => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.workspace_settings" : "workspace_settings";
}

/// <summary>
/// The workspace's calendar: the time zone of "today", the first day of a week
/// and the first month of a year (a fiscal year from April: quarters Apr–Jun …).
/// Periods everywhere — query buckets, rule and report periods, schedules —
/// come from here.
/// </summary>
public sealed record WorkspaceCalendar(TimeZoneInfo Zone, string WeekStart = "monday", int YearStart = 1)
{
    public static readonly WorkspaceCalendar Default = new(TimeZoneInfo.Utc);

    /// <summary>The IANA name of the zone (for Postgres and the browser).</summary>
    public string ZoneId => Zone.HasIanaId ? Zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(Zone.Id, out var iana) ? iana : "UTC";

    public DayOfWeek FirstDay => WeekStart switch { "sunday" => DayOfWeek.Sunday, "saturday" => DayOfWeek.Saturday, _ => DayOfWeek.Monday };

    /// <summary>Days from Monday to the week's first day: 0 Monday, 5 Saturday, 6 Sunday.</summary>
    public int WeekOffset => ((int)FirstDay + 6) % 7;

    /// <summary>Months from January to the year's first month.</summary>
    public int YearOffset => YearStart - 1;

    public DateTimeOffset Now(DateTimeOffset? utc = null) => TimeZoneInfo.ConvertTime(utc ?? DateTimeOffset.UtcNow, Zone);

    public DateOnly Today(DateTimeOffset? utc = null) => DateOnly.FromDateTime(Now(utc).DateTime);

    /// <summary>The first day of the day, week, month, quarter or year holding <paramref name="day"/>.</summary>
    public DateOnly Start(string unit, DateOnly day)
    {
        switch (unit)
        {
            case "day":
                return day;
            case "week":
                return day.AddDays(-(((int)day.DayOfWeek - (int)FirstDay + 7) % 7));
            case "month":
                return new DateOnly(day.Year, day.Month, 1);
            default:
            {
                // Quarters and years count from the year's first month.
                var months = day.Year * 12 + day.Month - 1 - YearOffset;
                var length = unit == "quarter" ? 3 : 12;
                months -= ((months % length) + length) % length;
                months += YearOffset;
                return new DateOnly(months / 12, months % 12 + 1, 1);
            }
        }
    }

    public DateOnly Add(string unit, DateOnly day, int count) => unit switch
    {
        "day" => day.AddDays(count),
        "week" => day.AddDays(7 * count),
        "month" => day.AddMonths(count),
        "quarter" => day.AddMonths(3 * count),
        _ => day.AddYears(count)
    };

    /// <summary>Q3 2026, or Q1 2026/27 in a year from April.</summary>
    public string QuarterName(DateOnly start)
    {
        var yearStart = Start("year", start);
        return $"Q{(start.Year * 12 + start.Month - yearStart.Year * 12 - yearStart.Month) / 3 + 1} {YearName(yearStart)}";
    }

    /// <summary>2026, or 2026/27 for a year from April 2026.</summary>
    public string YearName(DateOnly start) =>
        YearStart == 1 ? start.Year.ToString(CultureInfo.InvariantCulture) : $"{start.Year}/{(start.Year + 1) % 100:00}";
}
