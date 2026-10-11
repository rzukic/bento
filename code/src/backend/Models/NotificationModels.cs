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
