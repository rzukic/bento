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

/// <summary>
/// What is said: an event name for machines, a title and text for people, an
/// optional link and facts; a report (Step 5.3) adds sections, each with its own facts.
/// </summary>
public sealed record Notification(string Event, string Title, string Text, string? Link = null, IReadOnlyList<NotificationFact>? Facts = null, IReadOnlyList<NotificationSection>? Sections = null);

/// <param name="Detail">A second line, e.g. the change against the period before.</param>
/// <param name="Tone"><c>good</c> or <c>bad</c> for a change; colours it in emails.</param>
/// <param name="Number">The value as a number, for webhooks.</param>
/// <param name="Previous">The value of the period compared with, for webhooks.</param>
public sealed record NotificationFact(string Label, string Value, string? Detail = null, string? Tone = null, decimal? Number = null, decimal? Previous = null);

public sealed record NotificationSection(string? Title, string? Text = null, IReadOnlyList<NotificationFact>? Facts = null);

// ── Rules: when Bento says something (Step 5.3) ─────────────────────────

/// <summary>
/// What a rule watches. By <c>kind</c>:
/// <c>condition</c> — <see cref="Source"/>, <see cref="Formula"/> (one value, e.g. <c>-SUM(amount WHERE amount &lt; 0)</c>),
/// optionally <see cref="Period"/> on <see cref="DateColumn"/>, then <see cref="Op"/> and <see cref="Threshold"/>;
/// <c>target</c> — the target widget <see cref="WidgetId"/> on dashboard <see cref="DashboardId"/>: each group at <see cref="Percent"/> of its target or limit;
/// <c>event</c> — <see cref="Event"/> (<c>import.failed</c>, <c>price.failed</c>);
/// <c>report</c> — <see cref="Blocks"/> sent <see cref="Every"/> day, week, month, quarter or year
/// on <see cref="Day"/> at <see cref="At"/>, for the <see cref="Range"/> period, against <see cref="Compare"/>.
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
    /// <summary>Conditions: hour (default) or day. Reports: day, week, month, quarter or year.</summary>
    public string? Every { get; init; }
    /// <summary>HH:mm for a daily check or a report.</summary>
    public string? At { get; init; }
    public long? DashboardId { get; init; }
    public string? WidgetId { get; init; }
    public decimal? Percent { get; init; }
    public string? Event { get; init; }

    /// <summary>Report: the weekday (1 = Monday … 7) for weekly ones, the day of the month (1–28) otherwise.</summary>
    public int? Day { get; init; }
    /// <summary>Report: <c>last</c> (the period just over, default) or <c>current</c> (the period so far).</summary>
    public string? Range { get; init; }
    /// <summary>Report: <c>previous</c> (default), <c>year</c> (a year earlier) or <c>none</c>.</summary>
    public string? Compare { get; init; }
    /// <summary>Report: text at the top.</summary>
    public string? Intro { get; init; }
    /// <summary>Report: the AI writes a few sentences about the numbers (when AI is set up).</summary>
    public bool? Ai { get; init; }
    /// <summary>Report: what the AI should look at or how it should write, e.g. "in German, the biggest changes first".</summary>
    public string? AiInstructions { get; init; }
    public IReadOnlyList<ReportBlock>? Blocks { get; init; }
}

/// <summary>
/// One part of a report. By <c>type</c>:
/// <c>value</c> — <see cref="Formula"/> over <see cref="Source"/> (rows of the period on <see cref="DateColumn"/>), against the period compared with;
/// <c>top</c> — the same per value of <see cref="GroupBy"/>, the <see cref="Limit"/> biggest (or smallest with <see cref="Order"/> asc);
/// <c>dashboard</c> — the key figures (kpi widgets) of dashboard <see cref="DashboardId"/>, all or <see cref="Widgets"/>, for the period;
/// <c>rules</c> — the notification rules that hold now;
/// <c>text</c> — <see cref="Text"/> as it is.
/// </summary>
public sealed record ReportBlock
{
    public string? Type { get; init; }
    public string? Label { get; init; }
    public string? Source { get; init; }
    public string? Formula { get; init; }
    public string? DateColumn { get; init; }
    /// <summary>auto (from the formula), currency, number, integer or percent.</summary>
    public string? Format { get; init; }
    /// <summary>Whether a rise is good news (default) or bad (spending, errors).</summary>
    public bool? HigherIsBetter { get; init; }
    public string? GroupBy { get; init; }
    public int? Limit { get; init; }
    /// <summary>desc (default) or asc.</summary>
    public string? Order { get; init; }
    public long? DashboardId { get; init; }
    public IReadOnlyList<string>? Widgets { get; init; }
    public string? Text { get; init; }
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

/// <summary>A report as it would look now, before it is saved (the editor's preview).</summary>
public sealed record ReportPreviewRequest
{
    public string? Name { get; init; }
    public NotificationRuleConfig? Config { get; init; }
}
