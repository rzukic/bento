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
    /// <summary>A status column: how its values show on devices, and which of them are invitations to answer.</summary>
    public AppStatus? Status { get; init; }
}

/// <summary>
/// A calendar's status column (e.g. proposed, confirmed, cancelled). <see cref="Values"/>
/// sets the iCal STATUS devices show. With <see cref="Pending"/>, rows in those
/// states go out as invitations the user answers on the device: accepting writes
/// <see cref="Accepted"/>, declining <see cref="Declined"/>, "maybe"
/// <see cref="Tentative"/> (or leaves the row as it is), and
/// <see cref="AnsweredAt"/> gets the time of the answer. An answer changes only
/// these columns — never the times, the title or other rows.
/// </summary>
public sealed record AppStatus
{
    public string? Column { get; init; }
    /// <summary>Value → TENTATIVE, CONFIRMED or CANCELLED.</summary>
    public IReadOnlyDictionary<string, string>? Values { get; init; }
    /// <summary>The values that are invitations still to be answered.</summary>
    public IReadOnlyList<string>? Pending { get; init; }
    public string? Accepted { get; init; }
    public string? Declined { get; init; }
    public string? Tentative { get; init; }
    /// <summary>A date-time column that gets the moment of each answer.</summary>
    public string? AnsweredAt { get; init; }
}

/// <summary>
/// Who answers the invitations (the address the calendar app knows as its
/// user's own — in DAVx⁵ the account name) and who sends them.
/// </summary>
public sealed record AppInvitations
{
    public string? Attendee { get; init; }
    public string? AttendeeName { get; init; }
    /// <summary>Default bento@bento.invalid: an address no mail is sent to.</summary>
    public string? Organizer { get; init; }
    /// <summary>Default "Bento".</summary>
    public string? OrganizerName { get; init; }
}

/// <param name="Op">eq, neq, gt, gte, lt, lte, like, in, null, notnull — as the list endpoint takes them.</param>
public sealed record AppFilter(string? Column, string? Op, string? Value);

public sealed record AppConfig
{
    public IReadOnlyList<AppCalendar>? Calendars { get; init; }
    /// <summary>Needed when a calendar's status has invitations.</summary>
    public AppInvitations? Invitations { get; init; }
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
