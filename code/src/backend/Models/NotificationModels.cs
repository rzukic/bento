namespace Bento.Api.Models;

/// <summary>
/// Where a notification goes (Step 5.2). Fields by kind:
/// <c>email</c> — to, from, smtpHost, smtpPort, smtpSecurity (starttls, ssl, none), smtpUser, smtpPassword;
/// <c>slack</c> — url (an incoming webhook);
/// <c>webhook</c> — url, secret (signs the body).
/// smtpPassword, url and secret are write-only: stored encrypted, never sent back.
/// Leave one out to keep it, send "" to remove it.
/// </summary>
public sealed record NotificationChannelConfig
{
    public IReadOnlyList<string>? To { get; init; }
    public string? From { get; init; }
    public string? SmtpHost { get; init; }
    public int? SmtpPort { get; init; }
    public string? SmtpSecurity { get; init; }
    public string? SmtpUser { get; init; }
    public string? SmtpPassword { get; init; }
    public string? Url { get; init; }
    public string? Secret { get; init; }
}

public sealed record NotificationChannelRequest
{
    public string? Name { get; init; }
    public string? Kind { get; init; }
    public NotificationChannelConfig? Config { get; init; }
    public bool? Enabled { get; init; }
}

/// <param name="Config">Without the secrets.</param>
/// <param name="Target">Where it goes, readable: the addresses, or the webhook's host.</param>
public sealed record NotificationChannelResponse(
    long Id,
    string Name,
    string Kind,
    NotificationChannelConfig Config,
    string Target,
    bool HasPassword,
    bool HasUrl,
    bool HasSecret,
    bool Enabled,
    NotificationDelivery? LastDelivery,
    string CreatedAt,
    string UpdatedAt);

/// <param name="Status"><c>sent</c> or <c>failed</c>.</param>
public sealed record NotificationDelivery(long Id, string Event, string Title, string Status, string? Error, int DurationMs, string CreatedAt);

/// <summary>What is said: an event name for machines, a title and text for people, an optional link and facts.</summary>
public sealed record Notification(string Event, string Title, string Text, string? Link = null, IReadOnlyList<NotificationFact>? Facts = null);

public sealed record NotificationFact(string Label, string Value);

// ── Rules: when Bento says something (Step 5.3) ─────────────────────────

/// <summary>
/// What a rule watches. By <c>kind</c>:
/// <c>condition</c> — <see cref="Source"/>, <see cref="Formula"/> (one value, e.g. <c>-SUM(amount WHERE amount &lt; 0)</c>),
/// optionally <see cref="Period"/> on <see cref="DateColumn"/>, then <see cref="Op"/> and <see cref="Threshold"/>;
/// <c>budget</c> — the budget widget <see cref="WidgetId"/> on dashboard <see cref="DashboardId"/>, at <see cref="Percent"/> of the limit;
/// <c>event</c> — <see cref="Event"/> (<c>import.failed</c>, <c>price.failed</c>).
/// Conditions are checked every hour or daily <see cref="At"/>, and after a scheduled import brings rows.
/// </summary>
public sealed record NotificationRuleConfig
{
    public string? Source { get; init; }
    public string? Formula { get; init; }
    /// <summary>today, this-week, this-month, this-year — on <see cref="DateColumn"/>; none for all rows.</summary>
    public string? Period { get; init; }
    public string? DateColumn { get; init; }
    /// <summary>&gt;, &gt;=, &lt;, &lt;=, =, &lt;&gt;</summary>
    public string? Op { get; init; }
    public decimal? Threshold { get; init; }
    /// <summary>hour (default) or day.</summary>
    public string? Every { get; init; }
    /// <summary>HH:mm for a daily check.</summary>
    public string? At { get; init; }
    public long? DashboardId { get; init; }
    public string? WidgetId { get; init; }
    public decimal? Percent { get; init; }
    public string? Event { get; init; }
}

public sealed record NotificationRuleRequest
{
    public string? Name { get; init; }
    public string? Kind { get; init; }
    public NotificationRuleConfig? Config { get; init; }
    public IReadOnlyList<long>? Channels { get; init; }
    public bool? Enabled { get; init; }
}

/// <summary>Where a rule stands: whether its condition holds, the value last seen, when it last spoke.</summary>
public sealed record NotificationRuleState
{
    public bool Active { get; init; }
    public string? Value { get; init; }
    public string? CheckedAt { get; init; }
    public string? FiredAt { get; init; }
    public string? Error { get; init; }
    /// <summary>Budget: the period (yyyy-MM) and the categories already reported in it. Events: the sources failing now.</summary>
    public string? Period { get; init; }
    public IReadOnlyList<string>? Keys { get; init; }
}

public sealed record NotificationRuleResponse(
    long Id,
    string Name,
    string Kind,
    NotificationRuleConfig Config,
    IReadOnlyList<long> Channels,
    bool Enabled,
    NotificationRuleState State,
    string Summary,
    string CreatedAt,
    string UpdatedAt);

/// <summary>A check run by hand: what it found and whether it would speak (or spoke).</summary>
/// <param name="Notification">What it said or would say; null when nothing to say.</param>
public sealed record NotificationRuleCheck(NotificationRuleState State, bool Fired, Notification? Notification, IReadOnlyList<NotificationDelivery> Deliveries);
