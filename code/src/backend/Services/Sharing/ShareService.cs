using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bento.Api.Db;
using Bento.Api.Exceptions;
using Bento.Api.Services.Workspaces;
using Dapper;

namespace Bento.Api.Services.Sharing;

/// <summary>
/// Share links (Step 5.6): a dashboard (later a form) for anyone with the
/// link, without an account. The link is <c>/s/&lt;token&gt;</c>; only a hash
/// of the token is kept. A link belongs to a workspace and acts as the one
/// who made it, read-only and only for what the dashboard shows
/// (<see cref="ShareGuard"/>); it can expire, be revoked and fix filter values.
/// </summary>
public sealed class ShareService
{
    public const string TokenPrefix = "bento_s_";
    public const string HeaderName = "X-Bento-Share";
    public static readonly string[] Kinds = ["dashboard"];

    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ISystemPool _systemPool;

    public ShareService(ISystemPool systemPool)
    {
        _systemPool = systemPool;
    }

    private bool Pg => _systemPool.Provider == DatabaseProvider.Postgres;
    private string Table => Pg ? "_system.share_links" : "share_links";
    private string Users => Pg ? "_system.users" : "users";
    private string Ts(string column) => SystemSqlDialect.TimestampText(_systemPool.Provider, column);
    private string TsParam(string name) => SystemSqlDialect.TimestampParam(_systemPool.Provider, name);

    /// <param name="Filters">Fixed filter values by column: what the viewer sees is always filtered so.</param>
    /// <param name="Embed">May be shown inside another site (an iframe).</param>
    public sealed record ShareOptions(Dictionary<string, List<JsonElement>>? Filters = null, bool Embed = false);

    public sealed record ShareLink(long Id, long WorkspaceId, string Kind, long TargetId, string Name, string Prefix, ShareOptions Options,
        long CreatedBy, string? CreatedByName, string CreatedAt, string? ExpiresAt, string? RevokedAt, string? LastUsedAt, int Views)
    {
        public string Status => RevokedAt is not null ? "revoked"
            : ExpiresAt is not null && DateTime.Parse(ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) <= DateTime.UtcNow ? "expired"
            : "active";
    }

    /// <summary>The link a request carries, with the installation role of the one who made it.</summary>
    public sealed record Visitor(ShareLink Link, string CreatorRole);

    private sealed class Row
    {
        public long Id { get; init; }
        public long WorkspaceId { get; init; }
        public string Kind { get; init; } = "";
        public long TargetId { get; init; }
        public string Name { get; init; } = "";
        public string Prefix { get; init; } = "";
        public string Options { get; init; } = "{}";
        public long CreatedBy { get; init; }
        public string? CreatedByName { get; init; }
        public string? CreatorRole { get; init; }
        public string CreatedAt { get; init; } = "";
        public string? ExpiresAt { get; init; }
        public string? RevokedAt { get; init; }
        public string? LastUsedAt { get; init; }
        public int Views { get; init; }

        public ShareLink ToLink() => new(Id, WorkspaceId, Kind, TargetId, Name, Prefix, ReadOptions(Options), CreatedBy, CreatedByName, CreatedAt, ExpiresAt,
            RevokedAt, LastUsedAt, Views);
    }

    private string Columns =>
        $"s.id, s.workspace_id AS WorkspaceId, s.kind, s.target_id AS TargetId, s.name, s.prefix, s.options, s.created_by AS CreatedBy, " +
        $"u.username AS CreatedByName, u.role AS CreatorRole, {Ts("s.created_at")} AS CreatedAt, {Ts("s.expires_at")} AS ExpiresAt, " +
        $"{Ts("s.revoked_at")} AS RevokedAt, {Ts("s.last_used_at")} AS LastUsedAt, s.views";

    /// <summary>The links of one dashboard of the current workspace, newest first.</summary>
    public async Task<IReadOnlyList<ShareLink>> ListAsync(string kind, long targetId)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<Row>(
            $"SELECT {Columns} FROM {Table} s LEFT JOIN {Users} u ON u.id = s.created_by " +
            "WHERE s.workspace_id = @Ws AND s.kind = @Kind AND s.target_id = @Target ORDER BY s.revoked_at IS NOT NULL, s.id DESC",
            new { Ws = WorkspaceContext.Id, Kind = kind, Target = targetId });
        return rows.Select(r => r.ToLink()).ToList();
    }

    public async Task<ShareLink> GetAsync(long id)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            $"SELECT {Columns} FROM {Table} s LEFT JOIN {Users} u ON u.id = s.created_by WHERE s.id = @Id AND s.workspace_id = @Ws",
            new { Id = id, Ws = WorkspaceContext.Id });
        return row?.ToLink() ?? throw new ApiNotFoundException($"Share link {id} not found.");
    }

    /// <summary>Makes a link; the token comes back once and is never stored.</summary>
    public async Task<(ShareLink Link, string Token)> CreateAsync(string kind, long targetId, string? name, int? expiresInDays, ShareOptions options, long userId)
    {
        if (!Kinds.Contains(kind))
            throw Invalid("kind", "A link shares a dashboard.");
        var label = string.IsNullOrWhiteSpace(name) ? "Link" : name.Trim();
        if (label.Length > 120)
            throw Invalid("name", "The name is too long.");
        if (expiresInDays is < 1 or > 3650)
            throw Invalid("expiresInDays", "A link lasts 1 to 3650 days, or has no end.");
        var token = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(24));
        var now = DateTime.UtcNow;
        using var conn = await _systemPool.GetConnectionAsync();
        var id = await conn.ExecuteScalarAsync<long>(
            $"""
            INSERT INTO {Table} (workspace_id, kind, target_id, name, token_hash, prefix, options, created_by, created_at, expires_at)
            VALUES (@Ws, @Kind, @Target, @Name, @Hash, @Prefix, @Options, @UserId, {TsParam("@Now")}, {(expiresInDays is null ? "NULL" : TsParam("@Expires"))})
            RETURNING id
            """,
            new
            {
                Ws = WorkspaceContext.Id, Kind = kind, Target = targetId, Name = label, Hash = Hash(token), Prefix = token[..(TokenPrefix.Length + 6)],
                Options = JsonSerializer.Serialize(options, Json), UserId = userId, Now = Iso(now),
                Expires = expiresInDays is { } days ? Iso(now.AddDays(days)) : null
            });
        return (await GetAsync(id), token);
    }

    public async Task RevokeAsync(long id)
    {
        await GetAsync(id);
        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync($"UPDATE {Table} SET revoked_at = {TsParam("@Now")} WHERE id = @Id AND revoked_at IS NULL",
            new { Id = id, Now = Iso(DateTime.UtcNow) });
    }

    /// <summary>
    /// The link behind a token, or null: unknown, revoked and expired tokens are
    /// all simply not found. Counts the visit (at most once a minute).
    /// </summary>
    public async Task<Visitor?> AuthenticateAsync(string? token, bool countView)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal) || token.Length > 100)
            return null;
        using var conn = await _systemPool.GetConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            $"SELECT {Columns} FROM {Table} s INNER JOIN {Users} u ON u.id = s.created_by WHERE s.token_hash = @Hash",
            new { Hash = Hash(token) });
        if (row is null)
            return null;
        var link = row.ToLink();
        if (link.Status != "active")
            return null;
        var now = DateTime.UtcNow;
        if (countView || link.LastUsedAt is null || now - Parse(link.LastUsedAt) >= LastUsedResolution)
        {
            await conn.ExecuteAsync(
                $"UPDATE {Table} SET last_used_at = {TsParam("@Now")}{(countView ? ", views = views + 1" : "")} WHERE id = @Id",
                new { link.Id, Now = Iso(now) });
        }
        return new Visitor(link, row.CreatorRole ?? "viewer");
    }

    private static ShareOptions ReadOptions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ShareOptions>(json, Json) ?? new ShareOptions();
        }
        catch (JsonException)
        {
            return new ShareOptions();
        }
    }

    /// <summary>A fixed filter as the dashboard writes it: one value is equals, several are "in".</summary>
    public static JsonNode ConditionOf(IReadOnlyList<JsonElement> values) =>
        values.Count == 1
            ? new JsonObject { ["eq"] = JsonNode.Parse(values[0].GetRawText()) }
            : new JsonObject { ["in"] = new JsonArray(values.Select(v => JsonNode.Parse(v.GetRawText())).ToArray()) };

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Iso(DateTime value) => value.ToString("o", CultureInfo.InvariantCulture);

    private static DateTime Parse(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}
