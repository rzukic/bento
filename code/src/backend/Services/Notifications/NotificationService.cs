using System.Globalization;
using System.Text.Json;
using Bento.Api.Db;
using Bento.Api.Exceptions;
using Bento.Api.Models;
using Dapper;
using MimeKit;

namespace Bento.Api.Services.Notifications;

/// <summary>
/// Notification channels (Step 5.2): set up by admins, their secrets encrypted
/// under the vault's key and never sent back, a test send, and a log of every
/// delivery. <see cref="SendAsync"/> is what triggers (Step 5.3) call.
/// </summary>
public sealed class NotificationService
{
    public static readonly string[] Kinds = ["email", "slack", "webhook"];
    private const int KeptDeliveries = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ISystemPool _systemPool;
    private readonly CredentialService _vault;
    private readonly NotificationSender _sender;
    private readonly ProblemLog _problems;
    private readonly ILogger<NotificationService>? _logger;

    public NotificationService(ISystemPool systemPool, CredentialService vault, NotificationSender sender, ProblemLog problems, ILogger<NotificationService>? logger = null)
    {
        _systemPool = systemPool;
        _vault = vault;
        _sender = sender;
        _problems = problems;
        _logger = logger;
    }

    private bool Pg => _systemPool.Provider == DatabaseProvider.Postgres;
    private string Channels => Pg ? "_system.notification_channels" : "notification_channels";
    private string Deliveries => Pg ? "_system.notification_deliveries" : "notification_deliveries";
    private string NowParam => SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now");
    private string Ts(string column) => SystemSqlDialect.TimestampText(_systemPool.Provider, column);

    // ── Channels ─────────────────────────────────────────────────────────

    /// <summary>The encrypted secrets of a channel, as stored.</summary>
    private sealed record Secrets(string? Password, string? Url, string? Secret);

    private sealed class ChannelRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Config { get; init; } = "{}";
        public string Secrets { get; init; } = "{}";
        public bool Enabled { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private sealed record Channel(long Id, string Name, string Kind, NotificationChannelConfig Config, Secrets Secrets, bool Enabled, string CreatedAt, string UpdatedAt);

    private async Task<List<Channel>> LoadAsync(long? id = null)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<ChannelRow>(
            $"""
            SELECT id, name, kind, CAST(config AS TEXT) AS Config, secrets, enabled, {Ts("created_at")} AS CreatedAt, {Ts("updated_at")} AS UpdatedAt
            FROM {Channels}{(id is null ? "" : " WHERE id = @Id")} ORDER BY name
            """, new { Id = id });
        return rows.Select(r => new Channel(r.Id, r.Name, r.Kind,
            JsonSerializer.Deserialize<NotificationChannelConfig>(r.Config, Json) ?? new NotificationChannelConfig(),
            JsonSerializer.Deserialize<Secrets>(r.Secrets, Json) ?? new Secrets(null, null, null),
            r.Enabled, r.CreatedAt, r.UpdatedAt)).ToList();
    }

    private async Task<Channel> RequireAsync(long id) =>
        (await LoadAsync(id)).SingleOrDefault() ?? throw new ApiNotFoundException($"Notification channel {id} not found.");

    public async Task<IReadOnlyList<NotificationChannelResponse>> ListAsync()
    {
        var result = new List<NotificationChannelResponse>();
        foreach (var channel in await LoadAsync())
            result.Add(await ToResponseAsync(channel));
        return result;
    }

    public async Task<NotificationChannelResponse> GetAsync(long id) => await ToResponseAsync(await RequireAsync(id));

    public async Task<NotificationChannelResponse> CreateAsync(NotificationChannelRequest request, int? userId)
    {
        var name = RequireName(request.Name);
        var kind = request.Kind?.Trim().ToLowerInvariant() ?? "";
        if (!Kinds.Contains(kind))
            throw Invalid("kind", "The kind is email, slack or webhook.");
        var (config, secrets) = Validate(kind, request.Config ?? new NotificationChannelConfig(), new Secrets(null, null, null));
        using var conn = await _systemPool.GetConnectionAsync();
        await EnsureNameFreeAsync(conn, name, null);
        var id = await conn.ExecuteScalarAsync<long>(
            $"""
            INSERT INTO {Channels} (name, kind, config, secrets, enabled, created_by, created_at, updated_at)
            VALUES (@Name, @Kind, {SystemSqlDialect.JsonParam(_systemPool.Provider, "@Config")}, @Secrets, @Enabled, @CreatedBy, {NowParam}, {NowParam})
            RETURNING id
            """,
            new { Name = name, Kind = kind, Config = JsonSerializer.Serialize(config, Json), Secrets = JsonSerializer.Serialize(secrets, Json), Enabled = request.Enabled ?? true, CreatedBy = userId, Now = UtcNowIso() });
        return await GetAsync(id);
    }

    public async Task<NotificationChannelResponse> UpdateAsync(long id, NotificationChannelRequest request)
    {
        var existing = await RequireAsync(id);
        if (request.Kind is not null && !string.Equals(request.Kind.Trim(), existing.Kind, StringComparison.OrdinalIgnoreCase))
            throw Invalid("kind", "A channel keeps its kind; create a new channel for another one.");
        var name = request.Name is null ? existing.Name : RequireName(request.Name);
        var (config, secrets) = request.Config is null
            ? (existing.Config, existing.Secrets)
            : Validate(existing.Kind, request.Config, existing.Secrets);
        using var conn = await _systemPool.GetConnectionAsync();
        await EnsureNameFreeAsync(conn, name, id);
        await conn.ExecuteAsync(
            $"""
            UPDATE {Channels} SET name = @Name, config = {SystemSqlDialect.JsonParam(_systemPool.Provider, "@Config")}, secrets = @Secrets,
                   enabled = @Enabled, updated_at = {NowParam}
            WHERE id = @Id
            """,
            new { Id = id, Name = name, Config = JsonSerializer.Serialize(config, Json), Secrets = JsonSerializer.Serialize(secrets, Json), Enabled = request.Enabled ?? existing.Enabled, Now = UtcNowIso() });
        return await GetAsync(id);
    }

    public async Task DeleteAsync(long id)
    {
        await RequireAsync(id);
        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync($"DELETE FROM {Deliveries} WHERE channel_id = @Id", new { Id = id });
        await conn.ExecuteAsync($"DELETE FROM {Channels} WHERE id = @Id", new { Id = id });
    }

    public async Task<IReadOnlyList<NotificationDelivery>> DeliveriesAsync(long id, int limit = 20)
    {
        await RequireAsync(id);
        return await LoadDeliveriesAsync(id, limit);
    }

    private sealed class DeliveryRow
    {
        public long Id { get; init; }
        public string Event { get; init; } = "";
        public string Title { get; init; } = "";
        public string Status { get; init; } = "";
        public string? Error { get; init; }
        public int DurationMs { get; init; }
        public string CreatedAt { get; init; } = "";
    }

    private async Task<List<NotificationDelivery>> LoadDeliveriesAsync(long id, int limit)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<DeliveryRow>(
            $"""
            SELECT id, event, title, status, error, duration_ms AS DurationMs, {Ts("created_at")} AS CreatedAt
            FROM {Deliveries} WHERE channel_id = @Id ORDER BY id DESC LIMIT {Math.Clamp(limit, 1, KeptDeliveries)}
            """, new { Id = id });
        return rows.Select(r => new NotificationDelivery(r.Id, r.Event, r.Title, r.Status, r.Error, r.DurationMs, r.CreatedAt)).ToList();
    }

    private async Task<NotificationChannelResponse> ToResponseAsync(Channel channel)
    {
        var last = (await LoadDeliveriesAsync(channel.Id, 1)).FirstOrDefault();
        var target = channel.Kind == "email"
            ? string.Join(", ", channel.Config.To ?? [])
            : channel.Secrets.Url is not null && Uri.TryCreate(_vault.Unprotect(channel.Secrets.Url), UriKind.Absolute, out var url) ? url.Host : "";
        return new NotificationChannelResponse(channel.Id, channel.Name, channel.Kind, channel.Config, target,
            channel.Secrets.Password is not null, channel.Secrets.Url is not null, channel.Secrets.Secret is not null,
            channel.Enabled, last, channel.CreatedAt, channel.UpdatedAt);
    }

    // ── Validation ───────────────────────────────────────────────────────

    /// <summary>The settings to store (no secrets) and the secrets, encrypted: a left-out secret is kept, "" removes it.</summary>
    private (NotificationChannelConfig Config, Secrets Secrets) Validate(string kind, NotificationChannelConfig config, Secrets current)
    {
        string? Keep(string? sent, string? stored) => sent is null ? stored : sent.Length == 0 ? null : _vault.Protect(sent);

        switch (kind)
        {
            case "email":
            {
                var to = (config.To ?? []).Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (to.Count == 0)
                    throw Invalid("config.to", "Enter at least one address to send to.");
                foreach (var address in to)
                    if (!MailboxAddress.TryParse(address, out var parsed) || !parsed.Address.Contains('@'))
                        throw Invalid("config.to", $"'{address}' is not an email address.");
                if (string.IsNullOrWhiteSpace(config.SmtpHost))
                    throw Invalid("config.smtpHost", "Enter the SMTP server, e.g. smtp.example.com.");
                var security = config.SmtpSecurity?.Trim().ToLowerInvariant() ?? "starttls";
                if (security is not ("starttls" or "ssl" or "none"))
                    throw Invalid("config.smtpSecurity", "Security is starttls, ssl or none.");
                if (config.SmtpPort is { } port && port is < 1 or > 65535)
                    throw Invalid("config.smtpPort", "The port is between 1 and 65535.");
                var from = string.IsNullOrWhiteSpace(config.From) ? config.SmtpUser?.Trim() : config.From.Trim();
                if (string.IsNullOrEmpty(from) || !MailboxAddress.TryParse(from, out var sender) || !sender.Address.Contains('@'))
                    throw Invalid("config.from", "Enter the address the emails come from (or a user name that is an address).");
                var stored = new NotificationChannelConfig
                {
                    To = to,
                    From = string.IsNullOrWhiteSpace(config.From) ? null : config.From.Trim(),
                    SmtpHost = config.SmtpHost.Trim(),
                    SmtpPort = config.SmtpPort,
                    SmtpSecurity = security,
                    SmtpUser = string.IsNullOrWhiteSpace(config.SmtpUser) ? null : config.SmtpUser.Trim()
                };
                return (stored, current with { Password = Keep(config.SmtpPassword, current.Password), Url = null, Secret = null });
            }
            default:
            {
                var url = config.Url?.Trim();
                if (url is { Length: > 0 } && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http")))
                    throw Invalid("config.url", "Enter the full webhook address, starting with https://.");
                var secrets = current with { Password = null, Url = Keep(url, current.Url), Secret = kind == "webhook" ? Keep(config.Secret, current.Secret) : null };
                if (secrets.Url is null)
                    throw Invalid("config.url", kind == "slack"
                        ? "Enter the Slack webhook address (https://hooks.slack.com/services/…)."
                        : "Enter the address Bento posts to.");
                return (new NotificationChannelConfig(), secrets);
            }
        }
    }

    // ── Sending ──────────────────────────────────────────────────────────

    /// <summary>Sends a test over one channel and says how it went (also when it is turned off).</summary>
    public async Task<NotificationDelivery> TestAsync(long id, CancellationToken cancellationToken)
    {
        var channel = await RequireAsync(id);
        var notification = new Notification(
            "test",
            "Test from Bento",
            $"This is a test of the channel “{channel.Name}”. When it arrives, Bento can reach you here.",
            null,
            [new NotificationFact("Channel", channel.Name), new NotificationFact("Kind", channel.Kind)]);
        return await DeliverAsync(channel, notification, logProblem: false, cancellationToken);
    }

    /// <summary>
    /// Sends a notification over the given channels (turned-off ones are left
    /// out); each delivery is logged, a failed one also in the problem log.
    /// One channel failing does not stop the others.
    /// </summary>
    public async Task<IReadOnlyList<NotificationDelivery>> SendAsync(IEnumerable<long> channelIds, Notification notification, CancellationToken cancellationToken = default)
    {
        var wanted = channelIds.ToHashSet();
        var results = new List<NotificationDelivery>();
        foreach (var channel in (await LoadAsync()).Where(c => c.Enabled && wanted.Contains(c.Id)))
            results.Add(await DeliverAsync(channel, notification, logProblem: true, cancellationToken));
        return results;
    }

    private async Task<NotificationDelivery> DeliverAsync(Channel channel, Notification notification, bool logProblem, CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string? error = null;
        try
        {
            string? Open(string? stored, string what) => stored is null
                ? null
                : _vault.Unprotect(stored) ?? throw new NotificationSendException($"The channel's {what} cannot be read (the server's secret changed); enter it again.");
            var resolved = new ResolvedChannel(channel.Name, channel.Kind, channel.Config,
                Open(channel.Secrets.Password, "password"), Open(channel.Secrets.Url, "address"), Open(channel.Secrets.Secret, "signing secret"));
            await _sender.SendAsync(resolved, notification, cancellationToken);
        }
        catch (NotificationSendException e)
        {
            error = e.Message;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            error = $"Sending failed: {e.Message}";
            _logger?.LogWarning(e, "Notification over {Channel} failed.", channel.Name);
        }

        long id;
        using (var conn = await _systemPool.GetConnectionAsync())
        {
            id = await conn.ExecuteScalarAsync<long>(
                $"""
                INSERT INTO {Deliveries} (channel_id, event, title, status, error, duration_ms, created_at)
                VALUES (@Channel, @Event, @Title, @Status, @Error, @Duration, {NowParam})
                RETURNING id
                """,
                new { Channel = channel.Id, notification.Event, Title = Cut(notification.Title, 255), Status = error is null ? "sent" : "failed", Error = error, Duration = (int)watch.ElapsedMilliseconds, Now = UtcNowIso() });
            await conn.ExecuteAsync(
                $"DELETE FROM {Deliveries} WHERE channel_id = @Channel AND id NOT IN (SELECT id FROM {Deliveries} WHERE channel_id = @Channel ORDER BY id DESC LIMIT {KeptDeliveries})",
                new { Channel = channel.Id });
        }
        if (error is not null && logProblem)
            await _problems.RecordAsync("notify", $"The notification “{notification.Title}” could not be sent over {channel.Name}: {error}", null, watch.ElapsedMilliseconds);
        return (await LoadDeliveriesAsync(channel.Id, 1)).Single(d => d.Id == id);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task EnsureNameFreeAsync(System.Data.IDbConnection conn, string name, long? exceptId)
    {
        var taken = await conn.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {Channels} WHERE LOWER(name) = LOWER(@Name) AND (@ExceptId IS NULL OR id <> @ExceptId)",
            new { Name = name, ExceptId = exceptId });
        if (taken > 0)
            throw new ApiConflictException($"A channel named '{name}' already exists.", new Dictionary<string, object?> { ["field"] = "name" });
    }

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw Invalid("name", "Give the channel a name, e.g. My email.");
        if (trimmed.Length > 255)
            throw Invalid("name", "The name is too long.");
        return trimmed;
    }

    private static string Cut(string text, int length) => text.Length > length ? text[..length] : text;

    private static string UtcNowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}
