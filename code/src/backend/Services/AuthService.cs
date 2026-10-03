using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
using Bento.Api.Configuration;
using Bento.Api.Db;
using Bento.Api.Exceptions;
using Bento.Api.Models;

namespace Bento.Api.Services;

public sealed class AuthService
{
    private static readonly HashSet<string> AllowedRoles = new(StringComparer.Ordinal)
    {
        "admin",
        "editor",
        "viewer",
        "api"
    };

    private readonly ISystemPool _systemPool;
    private readonly BackendRuntimeOptions _runtimeOptions;

    public AuthService(ISystemPool systemPool, BackendRuntimeOptions runtimeOptions)
    {
        _systemPool = systemPool;
        _runtimeOptions = runtimeOptions;
    }

    public async Task<bool> IsBootstrapSelfRegistrationOpenAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_runtimeOptions.IsOpenAuthMode)
        {
            return true;
        }

        using var conn = await _systemPool.GetConnectionAsync();
        var userCount = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM {GetUsersTableName()};");

        return userCount == 0;
    }

    public async Task<AuthSessionResult> LoginAsync(
        LoginRequest? request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedUsername = NormalizeUsername(request?.Username);
        var password = RequirePassword(request?.Password);
        EnsureJwtConfigured();

        using var conn = await _systemPool.GetConnectionAsync();
        var user = await conn.QuerySingleOrDefaultAsync<AuthUserRecord>(
            $"""
            SELECT id, username, password_hash AS PasswordHash, role
            FROM {GetUsersTableName()}
            WHERE username = @Username;
            """,
            new { Username = normalizedUsername });

        if (user is null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            throw InvalidCredentials();
        }

        return await CreateSessionAsync(conn, user, cancellationToken);
    }

    public async Task<AuthSessionResult> RefreshAsync(
        string? refreshToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rawRefreshToken = RequireRefreshToken(refreshToken);
        EnsureJwtConfigured();

        using var conn = await _systemPool.GetConnectionAsync();
        using var tx = conn.BeginTransaction();

        try
        {
            var session = await conn.QuerySingleOrDefaultAsync<AuthSessionRecord>(
                $"""
                SELECT
                    s.id,
                    s.user_id AS UserId,
                    u.username,
                    u.role,
                    s.refresh_token_hash AS RefreshTokenHash,
                    CAST(s.expires_at AS TEXT) AS ExpiresAtRaw,
                    CAST(s.last_used_at AS TEXT) AS LastUsedAtRaw,
                    CAST(s.revoked_at AS TEXT) AS RevokedAtRaw,
                    s.replaced_by_session_id AS ReplacedBySessionId
                FROM {GetAuthSessionsTableName()} s
                INNER JOIN {GetUsersTableName()} u ON u.id = s.user_id
                WHERE s.refresh_token_hash = @RefreshTokenHash;
                """,
                new { RefreshTokenHash = ComputeRefreshTokenHash(rawRefreshToken) },
                tx);

            var now = DateTimeOffset.UtcNow;
            if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now)
            {
                throw InvalidRefreshSession();
            }

            var refreshed = await CreateSessionAsync(conn, tx, session.ToUserRecord(), now, cancellationToken);

            await conn.ExecuteAsync(
                $"""
                UPDATE {GetAuthSessionsTableName()}
                SET revoked_at = @RevokedAt,
                    last_used_at = @LastUsedAt,
                    replaced_by_session_id = @ReplacedBySessionId
                WHERE id = @Id;
                """,
                new
                {
                    Id = session.Id,
                    RevokedAt = ToDbValue(now),
                    LastUsedAt = ToDbValue(now),
                    ReplacedBySessionId = refreshed.SessionId
                },
                tx);

            tx.Commit();
            return refreshed;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Revokes the refresh session behind <paramref name="refreshToken"/> so a
    /// later <c>POST /auth/refresh</c> with the same cookie fails. Idempotent:
    /// a missing, unknown, expired or already revoked token is not an error.
    /// Returns <c>true</c> when a live session was revoked.
    /// </summary>
    public async Task<bool> LogoutAsync(string? refreshToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return false;
        }

        using var conn = await _systemPool.GetConnectionAsync();
        var now = DateTimeOffset.UtcNow;
        var affected = await conn.ExecuteAsync(
            $"""
            UPDATE {GetAuthSessionsTableName()}
            SET revoked_at = @RevokedAt,
                last_used_at = @LastUsedAt
            WHERE refresh_token_hash = @RefreshTokenHash
              AND revoked_at IS NULL;
            """,
            new
            {
                RefreshTokenHash = ComputeRefreshTokenHash(refreshToken),
                RevokedAt = ToDbValue(now),
                LastUsedAt = ToDbValue(now)
            });

        return affected > 0;
    }

    public async Task<RegisterUserResponse> RegisterAsync(
        RegisterUserRequest? request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(request?.Username);
        var password = ValidatePassword(request?.Password);
        var requestedRole = NormalizeRequestedRole(request?.Role);

        using var conn = await _systemPool.GetConnectionAsync();
        using var tx = conn.BeginTransaction();

        try
        {
            var userCount = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {GetUsersTableName()};",
                transaction: tx);

            var isBootstrapRegistration = userCount == 0;
            if (!isBootstrapRegistration && !_runtimeOptions.IsOpenAuthMode && !IsAdminPrincipal(principal))
            {
                throw new ApiForbiddenException(
                    "Self-registration is closed after the initial account is created.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["requiresAdmin"] = true
                    });
            }

            var role = isBootstrapRegistration ? "admin" : requestedRole ?? "viewer";
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(password);

            var id = await InsertUserAsync(conn, tx, normalizedUsername, passwordHash, role);
            tx.Commit();

            return new RegisterUserResponse(
                Id: id,
                Username: normalizedUsername,
                Role: role,
                BootstrapAdmin: isBootstrapRegistration);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static string NormalizeUsername(string? username)
    {
        var normalized = username?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw Validation("username", "Username is required.");
        }

        if (normalized.Length > 255)
        {
            throw Validation("username", "Username must be 255 characters or fewer.");
        }

        return normalized;
    }

    private static string ValidatePassword(string? password)
    {
        password = RequirePassword(password);

        if (password.Length < 8)
        {
            throw Validation("password", "Password must be at least 8 characters long.");
        }

        return password;
    }

    private static string RequirePassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw Validation("password", "Password is required.");
        }

        return password;
    }

    private static string? NormalizeRequestedRole(string? role)
    {
        if (role is null)
            return null;

        var normalized = role.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (!AllowedRoles.Contains(normalized))
        {
            throw Validation("role", "Role must be one of: admin, editor, viewer, api.");
        }

        return normalized;
    }

    private static ApiValidationException Validation(string field, string message)
    {
        return new ApiValidationException(
            message,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["field"] = field
            });
    }

    private static ApiUnauthorizedException InvalidCredentials()
    {
        return new ApiUnauthorizedException(
            "Invalid username or password.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["field"] = "credentials"
            });
    }

    private static ApiUnauthorizedException InvalidRefreshSession()
    {
        return new ApiUnauthorizedException(
            "Refresh session is invalid, expired, or revoked.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["field"] = "refresh_token"
            });
    }

    private static string RequireRefreshToken(string? refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw InvalidRefreshSession();
        }

        return refreshToken.Trim();
    }

    private async Task<int> InsertUserAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        string username,
        string passwordHash,
        string role)
    {
        try
        {
            if (_systemPool.Provider == DatabaseProvider.Postgres)
            {
                return await conn.ExecuteScalarAsync<int>(
                    $"""
                    INSERT INTO {GetUsersTableName()} (username, password_hash, role)
                    VALUES (@Username, @PasswordHash, @Role)
                    RETURNING id;
                    """,
                    new
                    {
                        Username = username,
                        PasswordHash = passwordHash,
                        Role = role
                    },
                    tx);
            }

            await conn.ExecuteAsync(
                $"""
                INSERT INTO {GetUsersTableName()} (username, password_hash, role)
                VALUES (@Username, @PasswordHash, @Role);
                """,
                new
                {
                    Username = username,
                    PasswordHash = passwordHash,
                    Role = role
                },
                tx);

            return await conn.ExecuteScalarAsync<int>("SELECT last_insert_rowid();", transaction: tx);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            throw new ApiConflictException(
                "A user with that username already exists.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["field"] = "username"
                });
        }
    }

    private async Task<AuthSessionResult> CreateSessionAsync(
        System.Data.IDbConnection conn,
        AuthUserRecord user,
        CancellationToken cancellationToken)
    {
        using var tx = conn.BeginTransaction();

        try
        {
            var result = await CreateSessionAsync(conn, tx, user, DateTimeOffset.UtcNow, cancellationToken);
            tx.Commit();
            return result;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private async Task<AuthSessionResult> CreateSessionAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        AuthUserRecord user,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var refreshToken = GenerateRefreshToken();
        var refreshExpiresAt = now.AddDays(_runtimeOptions.Jwt.RefreshTokenLifetimeDays);
        var sessionId = await InsertAuthSessionAsync(conn, tx, user.Id, refreshToken, refreshExpiresAt);
        var accessExpiresAt = now.AddMinutes(_runtimeOptions.Jwt.AccessTokenLifetimeMinutes);
        var accessToken = CreateAccessToken(user, sessionId, now, accessExpiresAt);

        return new AuthSessionResult(
            new AuthTokenResponse(
                AccessToken: accessToken,
                TokenType: "Bearer",
                ExpiresAt: accessExpiresAt.ToUniversalTime().ToString("O"),
                User: new AuthenticatedUserResponse(user.Id, user.Username, user.Role)),
            refreshToken,
            refreshExpiresAt,
            sessionId);
    }

    private async Task<int> InsertAuthSessionAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        int userId,
        string refreshToken,
        DateTimeOffset expiresAt)
    {
        var refreshTokenHash = ComputeRefreshTokenHash(refreshToken);
        var expiresAtValue = ToDbValue(expiresAt);

        if (_systemPool.Provider == DatabaseProvider.Postgres)
        {
            return await conn.ExecuteScalarAsync<int>(
                $"""
                INSERT INTO {GetAuthSessionsTableName()} (user_id, refresh_token_hash, expires_at)
                VALUES (@UserId, @RefreshTokenHash, @ExpiresAt)
                RETURNING id;
                """,
                new
                {
                    UserId = userId,
                    RefreshTokenHash = refreshTokenHash,
                    ExpiresAt = expiresAtValue
                },
                tx);
        }

        await conn.ExecuteAsync(
            $"""
            INSERT INTO {GetAuthSessionsTableName()} (user_id, refresh_token_hash, expires_at)
            VALUES (@UserId, @RefreshTokenHash, @ExpiresAt);
            """,
            new
            {
                UserId = userId,
                RefreshTokenHash = refreshTokenHash,
                ExpiresAt = expiresAtValue
            },
            tx);

        return await conn.ExecuteScalarAsync<int>("SELECT last_insert_rowid();", transaction: tx);
    }

    private string CreateAccessToken(
        AuthUserRecord user,
        int sessionId,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        var header = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        };

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sub"] = user.Id.ToString(CultureInfo.InvariantCulture),
            ["username"] = user.Username,
            ["role"] = user.Role,
            ["sid"] = sessionId.ToString(CultureInfo.InvariantCulture),
            ["iss"] = _runtimeOptions.Jwt.Issuer,
            ["aud"] = _runtimeOptions.Jwt.Audience,
            ["iat"] = issuedAt.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds()
        };

        var encodedHeader = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        var encodedPayload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = $"{encodedHeader}.{encodedPayload}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_runtimeOptions.Jwt.Secret!));
        var signature = Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));

        return $"{signingInput}.{signature}";
    }

    private void EnsureJwtConfigured()
    {
        if (string.IsNullOrWhiteSpace(_runtimeOptions.Jwt.Secret))
        {
            throw new InvalidOperationException(
                "JWT_SECRET must be configured before login or refresh endpoints can issue tokens.");
        }
    }

    private static string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    private static string ComputeRefreshTokenHash(string refreshToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));
        return Convert.ToHexString(hash);
    }

    private object ToDbValue(DateTimeOffset value)
    {
        return _systemPool.Provider == DatabaseProvider.Postgres
            ? value.UtcDateTime
            : value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private bool IsAdminPrincipal(ClaimsPrincipal principal)
    {
        if (_runtimeOptions.IsOpenAuthMode)
            return true;

        return principal.IsInRole("admin")
            || string.Equals(principal.FindFirstValue(ClaimTypes.Role), "admin", StringComparison.Ordinal)
            || string.Equals(principal.FindFirstValue("role"), "admin", StringComparison.Ordinal);
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        return exception switch
        {
            PostgresException postgresException when postgresException.SqlState == PostgresErrorCodes.UniqueViolation => true,
            SqliteException sqliteException when sqliteException.SqliteErrorCode == 19 => true,
            DbException dbException when dbException.InnerException is not null => IsUniqueViolation(dbException.InnerException),
            _ when exception.InnerException is not null => IsUniqueViolation(exception.InnerException),
            _ => false
        };
    }

    private string GetAuthSessionsTableName() =>
        _systemPool.Provider == DatabaseProvider.Postgres ? "_system.auth_sessions" : "auth_sessions";

    private string GetUsersTableName() =>
        _systemPool.Provider == DatabaseProvider.Postgres ? "_system.users" : "users";

    private sealed class AuthUserRecord
    {
        public int Id { get; init; }
        public string Username { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
    }

    private sealed class AuthSessionRecord
    {
        public int Id { get; init; }
        public int UserId { get; init; }
        public string Username { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public string RefreshTokenHash { get; init; } = string.Empty;
        public string ExpiresAtRaw { get; init; } = string.Empty;
        public string? LastUsedAtRaw { get; init; }
        public string? RevokedAtRaw { get; init; }
        public int? ReplacedBySessionId { get; init; }

        public DateTimeOffset ExpiresAt => ParseTimestamp(ExpiresAtRaw);
        public DateTimeOffset? LastUsedAt => ParseTimestampOrNull(LastUsedAtRaw);
        public DateTimeOffset? RevokedAt => ParseTimestampOrNull(RevokedAtRaw);

        public AuthUserRecord ToUserRecord() => new()
        {
            Id = UserId,
            Username = Username,
            Role = Role
        };

        private static DateTimeOffset ParseTimestamp(string value)
        {
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        }

        private static DateTimeOffset? ParseTimestampOrNull(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : ParseTimestamp(value);
        }
    }
}

public sealed record AuthSessionResult(
    AuthTokenResponse Response,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt,
    int SessionId);
