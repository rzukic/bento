namespace Bento.Api.Models;

// ── Apps: doors from other programs to Bento's tables (Step 5.4) ─────────

/// <summary>
/// One calendar of a calendar app: the rows of <see cref="Table"/> (narrowed by
/// <see cref="Filters"/>) as events — <see cref="Title"/>, <see cref="Start"/>,
/// optionally <see cref="End"/>, <see cref="AllDay"/>, <see cref="Description"/>,
/// <see cref="Location"/>, each a column of the table.
/// </summary>
public sealed record AppCalendar
{
    public string? Name { get; init; }
    /// <summary>The calendar's part of its addresses, from the name; kept when the name changes.</summary>
    public string? Slug { get; init; }
    public string? Table { get; init; }
    public string? Title { get; init; }
    /// <summary>A date column (all-day events) or a date-time column.</summary>
    public string? Start { get; init; }
    public string? End { get; init; }
    /// <summary>A yes/no column that makes an event all-day; without it, date columns are all-day.</summary>
    public string? AllDay { get; init; }
    public string? Description { get; init; }
    public string? Location { get; init; }
    /// <summary>A text column with the event's repetition (an iCal RRULE such as FREQ=WEEKLY;BYDAY=MO).</summary>
    public string? Recurrence { get; init; }
    /// <summary>Events without an end, in minutes (timed events; default 60).</summary>
    public int? Duration { get; init; }
    /// <summary>#RRGGBB, shown by calendar apps that support it.</summary>
    public string? Color { get; init; }
    public IReadOnlyList<AppFilter>? Filters { get; init; }
}

/// <param name="Op">eq, neq, gt, gte, lt, lte, like, in, null, notnull — as the list endpoint takes them.</param>
public sealed record AppFilter(string? Column, string? Op, string? Value);

public sealed record AppConfig
{
    public IReadOnlyList<AppCalendar>? Calendars { get; init; }
}

public sealed record AppRequest
{
    public string? Name { get; init; }
    /// <summary><c>calendar</c>.</summary>
    public string? Kind { get; init; }
    public AppConfig? Config { get; init; }
    public bool? Enabled { get; init; }
}

/// <param name="Prefix">The first characters of the password, to tell devices apart.</param>
public sealed record AppPasswordResponse(long Id, string Name, string Prefix, string CreatedAt, string? LastUsedAt);

public sealed record AppResponse(
    long Id,
    string Name,
    string Kind,
    AppConfig Config,
    bool Enabled,
    IReadOnlyList<AppPasswordResponse> Passwords,
    string CreatedAt,
    string UpdatedAt);

/// <summary>The address a device uses: <c>caldav</c> (the server of a CalDAV account).</summary>
public sealed record AppLink(string Calendar, string Kind, string Url);

public sealed record AppPasswordRequest
{
    /// <summary>The device, e.g. "iPhone" or "Thunderbird at work".</summary>
    public string? Name { get; init; }
}

/// <param name="Secret">Shown once: the password the device signs in with.</param>
public sealed record AppPasswordCreated(AppPasswordResponse Password, string Secret, IReadOnlyList<AppLink> Links);
