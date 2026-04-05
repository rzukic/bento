using System.Data.Common;
using System.Security.Claims;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
using Repere.Api.Configuration;
using Repere.Api.Db;
using Repere.Api.Exceptions;
using Repere.Api.Models;

namespace Repere.Api.Services;

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
        if (string.IsNullOrWhiteSpace(password))
        {
            throw Validation("password", "Password is required.");
        }

        if (password.Length < 8)
        {
            throw Validation("password", "Password must be at least 8 characters long.");
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

    private string GetUsersTableName() =>
        _systemPool.Provider == DatabaseProvider.Postgres ? "_system.users" : "users";
}
