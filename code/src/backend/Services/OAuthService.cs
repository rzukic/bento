using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Bento.Api.Db;
using Bento.Api.Exceptions;

namespace Bento.Api.Services;

/// <summary>
/// A small OAuth 2.1 authorization server for MCP clients (Step 4.6): clients
/// register themselves (RFC 7591), the user approves them on Bento's consent
/// page, and the client trades the code (PKCE S256 only) for an access token
/// and a rotating refresh token. A grant acts as its user, limited to the
/// permissions the user ticked (<see cref="McpPermissions"/>) and capped by the
/// user's role. Codes and tokens are stored as SHA-256 hashes.
/// </summary>
public sealed class OAuthService
{
    public const string AccessTokenPrefix = "bento_at_";
    public const string RefreshTokenPrefix = "bento_rt_";
    private const string CodePrefix = "bento_ac_";

    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(60);
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);
    private const int MaxRedirectUris = 10;

    private readonly ISystemPool _systemPool;

    public OAuthService(ISystemPool systemPool)
    {
        _systemPool = systemPool;
    }

    public sealed record OAuthClient(string ClientId, string Name, IReadOnlyList<string> RedirectUris, string CreatedAt);

    public sealed record TokenSet(string AccessToken, string RefreshToken, int ExpiresIn, string Scope);

    /// <summary>The user behind a token, their role, and what the app may do (already capped by the role).</summary>
    public sealed record OAuthIdentity(long GrantId, long UserId, string Username, string Role, IReadOnlyList<string> Permissions, string ClientName);

    public sealed class GrantRow
    {
        public long Id { get; init; }
        public string ClientId { get; init; } = "";
        public string ClientName { get; init; } = "";
        public long UserId { get; init; }
        public string? Username { get; init; }
        public string Scope { get; init; } = ApiKeyService.ReadScope;
        public string? PermissionsJson { get; init; }
        public string CreatedAt { get; init; } = "";
        public string? LastUsedAt { get; init; }

        public IReadOnlyList<string> Permissions => ReadPermissions(PermissionsJson, Scope);
    }

    public static bool LooksLikeAccessToken(string? value) =>
        value is not null && value.StartsWith(AccessTokenPrefix, StringComparison.Ordinal) && value.Length > AccessTokenPrefix.Length + 8;

    /// <summary>Stored permissions; grants from before per-area permissions have only read or read-write.</summary>
    private static IReadOnlyList<string> ReadPermissions(string? json, string scope)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                return McpPermissions.Normalize(JsonSerializer.Deserialize<string[]>(json));
            }
            catch (JsonException)
            {
                // fall through to the scope
            }
        }
        return scope == ApiKeyService.ReadWriteScope
            ? [McpPermissions.Read, McpPermissions.WriteRows]
            : [McpPermissions.Read];
    }

    // --- Clients -------------------------------------------------------------------

    public async Task<OAuthClient> RegisterClientAsync(string? name, IReadOnlyList<string>? redirectUris)
    {
        if (redirectUris is null || redirectUris.Count == 0)
            throw new OAuthException("invalid_redirect_uri", "redirect_uris is required.");
        if (redirectUris.Count > MaxRedirectUris)
            throw new OAuthException("invalid_redirect_uri", $"At most {MaxRedirectUris} redirect_uris.");
        foreach (var uri in redirectUris)
            ValidateRedirectUri(uri);

        var clientName = string.IsNullOrWhiteSpace(name) ? "MCP client" : name.Trim();
        if (clientName.Length > 100) clientName = clientName[..100];
        var clientId = "bento_client_" + Base64Url(RandomNumberGenerator.GetBytes(16));

        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync(
            $"""
            INSERT INTO {Clients} (client_id, name, redirect_uris, created_at)
            VALUES (@ClientId, @Name, {SystemSqlDialect.JsonParam(_systemPool.Provider, "@Uris")}, {Ts("@Now")})
            """,
            new { ClientId = clientId, Name = clientName, Uris = JsonSerializer.Serialize(redirectUris), Now = Iso(DateTime.UtcNow) });

        return (await FindClientAsync(clientId))!;
    }

    public async Task<OAuthClient?> FindClientAsync(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return null;
        using var conn = await _systemPool.GetConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<ClientRow>(
            $"SELECT client_id AS ClientId, name, {JsonText("redirect_uris")} AS RedirectUris, {Text("created_at")} AS CreatedAt FROM {Clients} WHERE client_id = @ClientId",
            new { ClientId = clientId });
        if (row is null) return null;
        var uris = JsonSerializer.Deserialize<string[]>(row.RedirectUris) ?? [];
        return new OAuthClient(row.ClientId, row.Name, uris, row.CreatedAt);
    }

    /// <summary>The client and the redirect it asks for, checked before the consent page shows anything.</summary>
    public async Task<OAuthClient> ResolveAuthorizationAsync(string? clientId, string? redirectUri)
    {
        var client = await FindClientAsync(clientId)
            ?? throw new OAuthException("invalid_client", "Unknown client. Connect again from your MCP client.");
        if (string.IsNullOrWhiteSpace(redirectUri) || !client.RedirectUris.Any(registered => RedirectMatches(registered, redirectUri)))
            throw new OAuthException("invalid_request", "The redirect address is not registered for this client.");
        return client;
    }

    // --- Authorization code --------------------------------------------------------

    /// <summary>The signed-in user approved the client: a grant (reused per client and user) and a one-time code.</summary>
    public async Task<string> ApproveAsync(long userId, string clientId, string redirectUri, string codeChallenge, IReadOnlyList<string> permissions)
    {
        var granted = McpPermissions.Normalize(permissions);
        var scope = granted.Contains(McpPermissions.WriteRows) ? ApiKeyService.ReadWriteScope : ApiKeyService.ReadScope;
        var permissionsJson = JsonSerializer.Serialize(granted);
        await ResolveAuthorizationAsync(clientId, redirectUri);
        if (string.IsNullOrWhiteSpace(codeChallenge) || codeChallenge.Length is < 43 or > 128)
            throw new OAuthException("invalid_request", "A PKCE code_challenge (S256) is required.");

        var now = DateTime.UtcNow;
        using var conn = await _systemPool.GetConnectionAsync();
        var grantId = await conn.ExecuteScalarAsync<long?>(
            $"SELECT id FROM {Grants} WHERE client_id = @ClientId AND user_id = @UserId AND revoked_at IS NULL ORDER BY id DESC LIMIT 1",
            new { ClientId = clientId, UserId = userId });
        if (grantId is { } existing)
        {
            await conn.ExecuteAsync($"UPDATE {Grants} SET scope = @Scope, permissions = @Permissions WHERE id = @Id", new { Scope = scope, Permissions = permissionsJson, Id = existing });
        }
        else
        {
            grantId = await conn.ExecuteScalarAsync<long>(
                $"""
                INSERT INTO {Grants} (client_id, user_id, scope, permissions, created_at)
                VALUES (@ClientId, @UserId, @Scope, @Permissions, {Ts("@Now")})
                RETURNING id
                """,
                new { ClientId = clientId, UserId = userId, Scope = scope, Permissions = permissionsJson, Now = Iso(now) });
        }

        var code = CodePrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        await InsertTokenAsync(conn, grantId.Value, "code", code, now + CodeLifetime, redirectUri, codeChallenge);
        return code;
    }

    public async Task<TokenSet> ExchangeCodeAsync(string? clientId, string? code, string? redirectUri, string? codeVerifier)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(codeVerifier))
            throw new OAuthException("invalid_request", "code and code_verifier are required.");

        using var conn = await _systemPool.GetConnectionAsync();
        var token = await FindTokenAsync(conn, code, "code")
            ?? throw new OAuthException("invalid_grant", "The code is invalid or expired.");

        if (token.UsedAt is not null)
        {
            // A code used twice means it leaked: everything issued from it goes.
            await RevokeGrantCoreAsync(conn, token.GrantId);
            throw new OAuthException("invalid_grant", "The code was already used.");
        }
        if (ParseIso(token.ExpiresAt) <= DateTime.UtcNow || token.RevokedAt is not null)
            throw new OAuthException("invalid_grant", "The code is invalid or expired.");
        if (!string.Equals(token.ClientId, clientId, StringComparison.Ordinal))
            throw new OAuthException("invalid_grant", "The code was issued to another client.");
        if (!string.Equals(token.RedirectUri, redirectUri, StringComparison.Ordinal))
            throw new OAuthException("invalid_grant", "redirect_uri does not match the authorization request.");
        if (!FixedEquals(Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier))), token.CodeChallenge ?? ""))
            throw new OAuthException("invalid_grant", "code_verifier does not match the code_challenge.");

        await MarkUsedAsync(conn, token.Id);
        return await IssueTokensAsync(conn, token.GrantId, ReadPermissions(token.PermissionsJson, token.Scope));
    }

    public async Task<TokenSet> RefreshAsync(string? clientId, string? refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new OAuthException("invalid_request", "refresh_token is required.");

        using var conn = await _systemPool.GetConnectionAsync();
        var token = await FindTokenAsync(conn, refreshToken, "refresh")
            ?? throw new OAuthException("invalid_grant", "The refresh token is invalid or expired.");
        if (!string.Equals(token.ClientId, clientId, StringComparison.Ordinal))
            throw new OAuthException("invalid_grant", "The refresh token was issued to another client.");
        if (token.UsedAt is not null)
        {
            // Refresh tokens rotate; an old one coming back means it was copied.
            await RevokeGrantCoreAsync(conn, token.GrantId);
            throw new OAuthException("invalid_grant", "The refresh token was already used. Connect again.");
        }
        if (ParseIso(token.ExpiresAt) <= DateTime.UtcNow || token.RevokedAt is not null)
            throw new OAuthException("invalid_grant", "The refresh token is invalid or expired.");

        await MarkUsedAsync(conn, token.Id);
        return await IssueTokensAsync(conn, token.GrantId, ReadPermissions(token.PermissionsJson, token.Scope));
    }

    // --- Access tokens -------------------------------------------------------------

    /// <summary>The user and scope behind an access token, or 401 for anything unknown, expired or revoked.</summary>
    public async Task<OAuthIdentity> AuthenticateAsync(string accessToken)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<AccessLookupRow>(
            $"""
            SELECT g.id AS GrantId, g.user_id AS UserId, u.username, u.role AS UserRole, g.scope, g.permissions AS PermissionsJson,
                   c.name AS ClientName, {Text("t.expires_at")} AS ExpiresAt, {Text("g.revoked_at")} AS RevokedAt,
                   {Text("g.last_used_at")} AS LastUsedAt
            FROM {Tokens} t
            INNER JOIN {Grants} g ON g.id = t.grant_id
            INNER JOIN {Users} u ON u.id = g.user_id
            LEFT JOIN {Clients} c ON c.client_id = g.client_id
            WHERE t.token_hash = @Hash AND t.kind = 'access'
            """,
            new { Hash = Hash(accessToken) });

        var now = DateTime.UtcNow;
        if (row is null || row.RevokedAt is not null || ParseIso(row.ExpiresAt) <= now)
            throw new ApiUnauthorizedException("Access token is invalid, revoked or expired.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["scheme"] = "Bearer" });

        if (row.LastUsedAt is null || now - ParseIso(row.LastUsedAt) >= LastUsedResolution)
        {
            await conn.ExecuteAsync($"UPDATE {Grants} SET last_used_at = {Ts("@Now")} WHERE id = @Id", new { Id = row.GrantId, Now = Iso(now) });
        }

        var permissions = McpPermissions.Cap(ReadPermissions(row.PermissionsJson, row.Scope), row.UserRole);
        return new OAuthIdentity(row.GrantId, row.UserId, row.Username ?? "", row.UserRole ?? "viewer", permissions, row.ClientName ?? "MCP client");
    }

    // --- Grants ("connected apps") ------------------------------------------------

    /// <summary>Active grants: the user's own, or every user's for an admin.</summary>
    public async Task<IReadOnlyList<GrantRow>> ListGrantsAsync(long? userId)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<GrantRow>(
            $"""
            SELECT g.id, g.client_id AS ClientId, COALESCE(c.name, 'MCP client') AS ClientName, g.user_id AS UserId, u.username,
                   g.scope, g.permissions AS PermissionsJson, {Text("g.created_at")} AS CreatedAt, {Text("g.last_used_at")} AS LastUsedAt
            FROM {Grants} g
            LEFT JOIN {Clients} c ON c.client_id = g.client_id
            LEFT JOIN {Users} u ON u.id = g.user_id
            WHERE g.revoked_at IS NULL {(userId is null ? "" : "AND g.user_id = @UserId")}
            ORDER BY g.created_at DESC, g.id DESC
            """,
            new { UserId = userId });
        return rows.ToList();
    }

    public async Task RevokeGrantAsync(long grantId, long? ownerUserId)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var owner = await conn.ExecuteScalarAsync<long?>(
            $"SELECT user_id FROM {Grants} WHERE id = @Id AND revoked_at IS NULL", new { Id = grantId });
        if (owner is null || (ownerUserId is not null && owner != ownerUserId))
            throw new ApiNotFoundException($"Connection {grantId} not found.");
        await RevokeGrantCoreAsync(conn, grantId);
    }

    // --- Helpers -------------------------------------------------------------------

    /// <summary>
    /// https anywhere, http only on the loopback interface (native apps such as
    /// Claude Code listen there), or an app's own scheme; never script or data URLs.
    /// </summary>
    public static void ValidateRedirectUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > 2000 || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !string.IsNullOrEmpty(parsed.Fragment))
            throw new OAuthException("invalid_redirect_uri", $"'{uri}' is not a valid redirect address.");
        var scheme = parsed.Scheme.ToLowerInvariant();
        var ok = scheme switch
        {
            "https" => true,
            "http" => parsed.IsLoopback,
            "javascript" or "data" or "file" or "vbscript" or "ftp" or "ws" or "wss" => false,
            _ => true
        };
        if (!ok)
            throw new OAuthException("invalid_redirect_uri", $"'{uri}' is not allowed: use https, or http on localhost.");
    }

    /// <summary>Exact match; for loopback addresses the port may differ (RFC 8252 §7.3).</summary>
    private static bool RedirectMatches(string registered, string requested)
    {
        if (string.Equals(registered, requested, StringComparison.Ordinal)) return true;
        if (!Uri.TryCreate(registered, UriKind.Absolute, out var a) || !Uri.TryCreate(requested, UriKind.Absolute, out var b)) return false;
        return a.IsLoopback && b.IsLoopback && a.Scheme == "http" && b.Scheme == "http" &&
               string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.PathAndQuery, b.PathAndQuery, StringComparison.Ordinal);
    }

    private async Task<TokenSet> IssueTokensAsync(System.Data.IDbConnection conn, long grantId, IReadOnlyList<string> permissions)
    {
        var now = DateTime.UtcNow;
        var access = AccessTokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var refresh = RefreshTokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        await InsertTokenAsync(conn, grantId, "access", access, now + AccessTokenLifetime, null, null);
        await InsertTokenAsync(conn, grantId, "refresh", refresh, now + RefreshTokenLifetime, null, null);
        // Expired tokens are kept a day (to recognise reuse), then dropped.
        await conn.ExecuteAsync($"DELETE FROM {Tokens} WHERE expires_at < {Ts("@Cutoff")}", new { Cutoff = Iso(now.AddDays(-1)) });
        return new TokenSet(access, refresh, (int)AccessTokenLifetime.TotalSeconds, McpPermissions.ToScope(permissions));
    }

    private async Task InsertTokenAsync(System.Data.IDbConnection conn, long grantId, string kind, string token, DateTime expiresAt, string? redirectUri, string? codeChallenge)
    {
        await conn.ExecuteAsync(
            $"""
            INSERT INTO {Tokens} (grant_id, kind, token_hash, redirect_uri, code_challenge, created_at, expires_at)
            VALUES (@GrantId, @Kind, @Hash, @RedirectUri, @CodeChallenge, {Ts("@Now")}, {Ts("@Expires")})
            """,
            new { GrantId = grantId, Kind = kind, Hash = Hash(token), RedirectUri = redirectUri, CodeChallenge = codeChallenge, Now = Iso(DateTime.UtcNow), Expires = Iso(expiresAt) });
    }

    private async Task<TokenLookupRow?> FindTokenAsync(System.Data.IDbConnection conn, string token, string kind)
    {
        return await conn.QuerySingleOrDefaultAsync<TokenLookupRow>(
            $"""
            SELECT t.id, t.grant_id AS GrantId, t.redirect_uri AS RedirectUri, t.code_challenge AS CodeChallenge,
                   {Text("t.expires_at")} AS ExpiresAt, {Text("t.used_at")} AS UsedAt,
                   g.client_id AS ClientId, g.scope, g.permissions AS PermissionsJson, {Text("g.revoked_at")} AS RevokedAt
            FROM {Tokens} t
            INNER JOIN {Grants} g ON g.id = t.grant_id
            WHERE t.token_hash = @Hash AND t.kind = @Kind
            """,
            new { Hash = Hash(token), Kind = kind });
    }

    private async Task MarkUsedAsync(System.Data.IDbConnection conn, long tokenId) =>
        await conn.ExecuteAsync($"UPDATE {Tokens} SET used_at = {Ts("@Now")} WHERE id = @Id", new { Id = tokenId, Now = Iso(DateTime.UtcNow) });

    private async Task RevokeGrantCoreAsync(System.Data.IDbConnection conn, long grantId)
    {
        await conn.ExecuteAsync($"UPDATE {Grants} SET revoked_at = {Ts("@Now")} WHERE id = @Id AND revoked_at IS NULL", new { Id = grantId, Now = Iso(DateTime.UtcNow) });
        await conn.ExecuteAsync($"DELETE FROM {Tokens} WHERE grant_id = @Id", new { Id = grantId });
    }

    private sealed class ClientRow
    {
        public string ClientId { get; init; } = "";
        public string Name { get; init; } = "";
        public string RedirectUris { get; init; } = "[]";
        public string CreatedAt { get; init; } = "";
    }

    private sealed class TokenLookupRow
    {
        public long Id { get; init; }
        public long GrantId { get; init; }
        public string? RedirectUri { get; init; }
        public string? CodeChallenge { get; init; }
        public string ExpiresAt { get; init; } = "";
        public string? UsedAt { get; init; }
        public string ClientId { get; init; } = "";
        public string Scope { get; init; } = ApiKeyService.ReadScope;
        public string? PermissionsJson { get; init; }
        public string? RevokedAt { get; init; }
    }

    private sealed class AccessLookupRow
    {
        public long GrantId { get; init; }
        public long UserId { get; init; }
        public string? Username { get; init; }
        public string? UserRole { get; init; }
        public string Scope { get; init; } = ApiKeyService.ReadScope;
        public string? PermissionsJson { get; init; }
        public string? ClientName { get; init; }
        public string ExpiresAt { get; init; } = "";
        public string? RevokedAt { get; init; }
        public string? LastUsedAt { get; init; }
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string Hash(string value) => ApiKeyService.Hash(value);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Iso(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static DateTime ParseIso(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private string Ts(string parameter) => SystemSqlDialect.TimestampParam(_systemPool.Provider, parameter);
    private string Text(string column) => SystemSqlDialect.TimestampText(_systemPool.Provider, column);
    private string JsonText(string column) => _systemPool.Provider == DatabaseProvider.Postgres ? $"{column}::text" : column;
    private string Clients => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.oauth_clients" : "oauth_clients";
    private string Grants => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.oauth_grants" : "oauth_grants";
    private string Tokens => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.oauth_tokens" : "oauth_tokens";
    private string Users => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.users" : "users";
}

/// <summary>An OAuth protocol error, answered as <c>{ "error", "error_description" }</c> (RFC 6749 §5.2).</summary>
public sealed class OAuthException : Exception
{
    public OAuthException(string error, string description, int status = 400) : base(description)
    {
        Error = error;
        Status = status;
    }

    public string Error { get; }
    public int Status { get; }
}
