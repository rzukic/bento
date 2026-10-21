using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Bento.Api.Db;
using Bento.Api.Exceptions;

namespace Bento.Api.Services;

/// <summary>
/// API keys (Part 3 R3.3): <c>bento_</c> plus 32 random bytes, shown once on
/// creation and stored as a SHA-256 hash. A key acts as the user who made it,
/// limited by its scope: <c>read</c> (reads and queries) or <c>read-write</c>
/// (also writes data). Keys never reach admin routes (schema, users, keys).
/// </summary>
public sealed class ApiKeyService
{
    public const string KeyPrefix = "bento_";
    public const string ReadScope = "read";
    public const string ReadWriteScope = "read-write";

    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private readonly ISystemPool _systemPool;

    public ApiKeyService(ISystemPool systemPool)
    {
        _systemPool = systemPool;
    }

    /// <param name="Permissions">
    /// Keys made with chosen permissions (beta feedback: scripts for schema and price
    /// sources): what the key may do, like a connected app, capped by its user's role.
    /// Null for read and read-write keys.
    /// </param>
    /// <param name="Workspaces">The workspaces the key may use (Step 5.5); null: all.</param>
    /// <param name="Tables">The only tables the key may read and write rows of; null: all.</param>
    public sealed record ApiKeyIdentity(long KeyId, long UserId, string Username, string Scope, IReadOnlyList<string>? Permissions = null, string? UserRole = null,
        IReadOnlyList<long>? Workspaces = null, IReadOnlyList<string>? Tables = null)
    {
        /// <summary>
        /// A key never does more than its user in the workspace it is used in: a
        /// read-write key of a viewer only reads; the role caps chosen permissions.
        /// </summary>
        public ApiKeyIdentity As(string? role) => Permissions is null
            ? this with { Scope = Effective(Scope, role), UserRole = role }
            : this with { Permissions = Permissions.Intersect(McpPermissions.ForRole(role)).ToList(), UserRole = role };
    }

    private static string Effective(string scope, string? userRole) =>
        scope == ReadWriteScope && userRole is "admin" or "editor" ? ReadWriteScope : ReadScope;

    public sealed class ApiKeyRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string? Prefix { get; init; }
        public string Scopes { get; init; } = "[]";
        public long? UserId { get; init; }
        public string? Username { get; init; }
        public string CreatedAt { get; init; } = "";
        public string? ExpiresAt { get; init; }
        public string? LastUsedAt { get; init; }
        public string? RevokedAt { get; init; }
        public string? WorkspacesJson { get; init; }
        public string? TablesJson { get; init; }

        public string Scope => ReadScopes(Scopes);

        /// <summary>The only tables the key may use; null: all.</summary>
        public IReadOnlyList<string>? Tables => ReadTables(TablesJson);

        /// <summary>The workspaces the key may use; null: all.</summary>
        public IReadOnlyList<long>? Workspaces => Bento.Api.Services.Workspaces.WorkspaceAccess.ReadWorkspaces(WorkspacesJson);

        /// <summary>The chosen permissions of a key made with them; null for read and read-write keys.</summary>
        public IReadOnlyList<string>? Permissions => ReadPermissions(Scopes);
    }

    public static bool LooksLikeKey(string? value) =>
        value is not null && value.StartsWith(KeyPrefix, StringComparison.Ordinal) && value.Length > KeyPrefix.Length + 8;

    /// <summary>Creates a key; the plain key is returned once and never stored.</summary>
    public async Task<(ApiKeyRow Row, string Key)> CreateAsync(string name, string scope, int? expiresInDays, long userId, IReadOnlyList<string>? permissions = null,
        IReadOnlyCollection<long>? workspaces = null, IReadOnlyCollection<string>? tables = null)
    {
        var key = KeyPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        var expires = expiresInDays is { } days ? now.AddDays(days) : (DateTime?)null;

        using var conn = await _systemPool.GetConnectionAsync();
        var id = await conn.ExecuteScalarAsync<long>(
            $"""
            INSERT INTO {Table} (name, key_hash, prefix, scopes, user_id, workspaces, tables, created_at, expires_at)
            VALUES (@Name, @Hash, @Prefix, {SystemSqlDialect.JsonParam(_systemPool.Provider, "@Scopes")}, @UserId, @Workspaces, @Tables,
                    {SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now")}, {ExpiresParam})
            RETURNING id
            """,
            new
            {
                Name = name,
                Hash = Hash(key),
                Prefix = key[..(KeyPrefix.Length + 6)],
                Scopes = JsonSerializer.Serialize(permissions is null ? ScopesFor(scope) : McpPermissions.Normalize(permissions).ToArray()),
                UserId = userId,
                Workspaces = Bento.Api.Services.Workspaces.WorkspaceAccess.WriteWorkspaces(workspaces),
                Tables = tables is null ? null : JsonSerializer.Serialize(tables.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()),
                Now = Iso(now),
                Expires = expires is null ? null : Iso(expires.Value)
            });

        return ((await ListAsync()).Single(row => row.Id == id), key);
    }

    public async Task<IReadOnlyList<ApiKeyRow>> ListAsync()
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<ApiKeyRow>(
            $"""
            SELECT k.id, k.name, k.prefix, k.scopes, k.user_id AS UserId, u.username,
                   {Text("k.created_at")} AS CreatedAt, {Text("k.expires_at")} AS ExpiresAt,
                   {Text("k.last_used_at")} AS LastUsedAt, {Text("k.revoked_at")} AS RevokedAt, k.workspaces AS WorkspacesJson, k.tables AS TablesJson
            FROM {Table} k
            LEFT JOIN {UsersTable} u ON u.id = k.user_id
            ORDER BY k.revoked_at IS NOT NULL, k.created_at DESC, k.id DESC
            """);
        return rows.ToList();
    }

    public async Task RevokeAsync(long id)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var updated = await conn.ExecuteAsync(
            $"UPDATE {Table} SET revoked_at = {SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now")} WHERE id = @Id AND revoked_at IS NULL",
            new { Id = id, Now = Iso(DateTime.UtcNow) });
        if (updated == 0 && (await ListAsync()).All(row => row.Id != id))
            throw new ApiNotFoundException($"API key {id} not found.");
    }

    /// <summary>
    /// The user a key acts for, or an exception: unknown, revoked and expired
    /// keys, and keys whose user is gone, are all rejected the same way.
    /// </summary>
    public async Task<ApiKeyIdentity> AuthenticateAsync(string key)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<KeyLookupRow>(
            $"""
            SELECT k.id, k.scopes, k.user_id AS UserId, u.username, u.role AS UserRole, k.workspaces AS WorkspacesJson, k.tables AS TablesJson,
                   {Text("k.expires_at")} AS ExpiresAt, {Text("k.revoked_at")} AS RevokedAt, {Text("k.last_used_at")} AS LastUsedAt
            FROM {Table} k
            INNER JOIN {UsersTable} u ON u.id = k.user_id
            WHERE k.key_hash = @Hash
            """,
            new { Hash = Hash(key) });

        var now = DateTime.UtcNow;
        if (row is null || row.RevokedAt is not null ||
            (row.ExpiresAt is not null && ParseIso(row.ExpiresAt) <= now))
            throw new ApiUnauthorizedException("API key is invalid, revoked or expired.");

        // Last use is recorded at minute resolution: no write on every request.
        if (row.LastUsedAt is null || now - ParseIso(row.LastUsedAt) >= LastUsedResolution)
        {
            await conn.ExecuteAsync(
                $"UPDATE {Table} SET last_used_at = {SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now")} WHERE id = @Id",
                new { row.Id, Now = Iso(now) });
        }

        // As stored, with the user's installation role; the role in the workspace it is
        // used in caps it (As), since a member may have another role there.
        return new ApiKeyIdentity(row.Id, row.UserId, row.Username ?? "", ReadScopes(row.Scopes), ReadPermissions(row.Scopes), row.UserRole,
            Bento.Api.Services.Workspaces.WorkspaceAccess.ReadWorkspaces(row.WorkspacesJson), ReadTables(row.TablesJson));
    }

    private sealed class KeyLookupRow
    {
        public long Id { get; init; }
        public string Scopes { get; init; } = "[]";
        public long UserId { get; init; }
        public string? Username { get; init; }
        public string? UserRole { get; init; }
        public string? WorkspacesJson { get; init; }
        public string? TablesJson { get; init; }
        public string? ExpiresAt { get; init; }
        public string? RevokedAt { get; init; }
        public string? LastUsedAt { get; init; }
    }

    public static string NormalizeScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        ReadScope => ReadScope,
        ReadWriteScope or "readwrite" or "write" => ReadWriteScope,
        _ => throw new ApiValidationException("scope must be 'read' or 'read-write'.", new Dictionary<string, object?> { ["field"] = "scope" })
    };

    private static string[] ScopesFor(string scope) => scope == ReadWriteScope ? ["read", "write"] : ["read"];

    private static string ReadScopes(string json)
    {
        try
        {
            var scopes = JsonSerializer.Deserialize<string[]>(json) ?? [];
            return scopes.Contains("write", StringComparer.OrdinalIgnoreCase) || scopes.Any(s => s != McpPermissions.Read && s.Contains('.'))
                ? ReadWriteScope
                : ReadScope;
        }
        catch (JsonException)
        {
            return ReadScope;
        }
    }

    /// <summary>Keys made with permissions store them ("data.read", "schema.write"); older keys store "read"/"write".</summary>
    private static IReadOnlyList<string>? ReadPermissions(string json)
    {
        try
        {
            var scopes = JsonSerializer.Deserialize<string[]>(json) ?? [];
            return scopes.Any(s => s.Contains('.')) ? McpPermissions.Normalize(scopes) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? ReadTables(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Iso(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static DateTime ParseIso(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private string Text(string column) => SystemSqlDialect.TimestampText(_systemPool.Provider, column);
    private string ExpiresParam => SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Expires");
    private string Table => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.api_keys" : "api_keys";
    private string UsersTable => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.users" : "users";
}
