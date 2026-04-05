using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Repere.Api.Configuration;
using Repere.Api.Db;
using Repere.Api.Exceptions;
using Repere.Api.Models;
using Repere.Api.Services;

namespace Repere.Tests;

public sealed class AuthServiceTests
{
    [Fact]
    public async Task RegisterAsync_AllowsAdminManagedCreation_AfterBootstrapUserExists()
    {
        using var env = new TemporarySqliteEnvironment();
        var service = new AuthService(env.SystemPool, CreateRuntimeOptions());

        var bootstrap = await service.RegisterAsync(
            new RegisterUserRequest("bootstrap-admin", "supersecure", null),
            new ClaimsPrincipal(new ClaimsIdentity()));
        Assert.True(bootstrap.BootstrapAdmin);

        var adminPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "admin")],
            authenticationType: "test"));

        var created = await service.RegisterAsync(
            new RegisterUserRequest("viewer-user", "anotherpass", "viewer"),
            adminPrincipal);

        Assert.False(created.BootstrapAdmin);
        Assert.Equal("viewer", created.Role);

        await using var conn = (SqliteConnection)await env.SystemPool.GetConnectionAsync();
        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users;");
        var storedRole = await conn.ExecuteScalarAsync<string>(
            "SELECT role FROM users WHERE username = $username;",
            new { username = "viewer-user" });

        Assert.Equal(2, count);
        Assert.Equal("viewer", storedRole);
    }

    [Fact]
    public async Task RegisterAsync_RejectsDuplicateUsername_WithConflict()
    {
        using var env = new TemporarySqliteEnvironment();
        var service = new AuthService(env.SystemPool, CreateRuntimeOptions());

        await service.RegisterAsync(
            new RegisterUserRequest("duplicate-user", "supersecure", null),
            new ClaimsPrincipal(new ClaimsIdentity()));

        var ex = await Assert.ThrowsAsync<ApiConflictException>(() => service.RegisterAsync(
            new RegisterUserRequest("duplicate-user", "anotherpass", "viewer"),
            CreateAdminPrincipal()));

        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoginAndRefreshAsync_PreserveNonBootstrapRole_InPayloadClaimsAndSession()
    {
        using var env = new TemporarySqliteEnvironment();
        var service = new AuthService(env.SystemPool, CreateRuntimeOptions());

        await service.RegisterAsync(
            new RegisterUserRequest("bootstrap-admin", "supersecure", null),
            new ClaimsPrincipal(new ClaimsIdentity()));

        var registered = await service.RegisterAsync(
            new RegisterUserRequest("editor-user", "anotherpass", "editor"),
            CreateAdminPrincipal());

        var session = await service.LoginAsync(new LoginRequest("editor-user", "anotherpass"));

        Assert.False(string.IsNullOrWhiteSpace(session.Response.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(session.RefreshToken));
        Assert.Equal("Bearer", session.Response.TokenType);
        Assert.Equal(registered.Id, session.Response.User.Id);
        Assert.Equal("editor-user", session.Response.User.Username);
        Assert.Equal("editor", session.Response.User.Role);

        var claims = ParseJwtPayload(session.Response.AccessToken);
        Assert.Equal(registered.Id.ToString(), claims.GetProperty("sub").GetString());
        Assert.Equal("editor-user", claims.GetProperty("username").GetString());
        Assert.Equal("editor", claims.GetProperty("role").GetString());
        Assert.True(claims.TryGetProperty("iat", out _));
        Assert.True(claims.TryGetProperty("exp", out _));

        await using var conn = (SqliteConnection)await env.SystemPool.GetConnectionAsync();
        var stored = await conn.QuerySingleAsync<(int UserId, string Hash)>(
            """
            SELECT user_id AS UserId, refresh_token_hash AS Hash
            FROM auth_sessions
            WHERE id = $id;
            """,
            new { id = session.SessionId });

        Assert.Equal(registered.Id, stored.UserId);
        Assert.NotEqual(session.RefreshToken, stored.Hash);

        var refreshed = await service.RefreshAsync(session.RefreshToken);

        Assert.NotEqual(session.RefreshToken, refreshed.RefreshToken);
        Assert.NotEqual(session.Response.AccessToken, refreshed.Response.AccessToken);
        Assert.Equal(registered.Id, refreshed.Response.User.Id);
        Assert.Equal("editor-user", refreshed.Response.User.Username);
        Assert.Equal("editor", refreshed.Response.User.Role);

        var refreshedClaims = ParseJwtPayload(refreshed.Response.AccessToken);
        Assert.Equal(registered.Id.ToString(), refreshedClaims.GetProperty("sub").GetString());
        Assert.Equal("editor-user", refreshedClaims.GetProperty("username").GetString());
        Assert.Equal("editor", refreshedClaims.GetProperty("role").GetString());
    }

    [Fact]
    public async Task RefreshAsync_RotatesSession_AndRevokesPriorRefreshToken()
    {
        using var env = new TemporarySqliteEnvironment();
        var service = new AuthService(env.SystemPool, CreateRuntimeOptions());

        await service.RegisterAsync(
            new RegisterUserRequest("bootstrap-admin", "supersecure", null),
            new ClaimsPrincipal(new ClaimsIdentity()));

        var initial = await service.LoginAsync(new LoginRequest("bootstrap-admin", "supersecure"));
        var refreshed = await service.RefreshAsync(initial.RefreshToken);

        Assert.NotEqual(initial.RefreshToken, refreshed.RefreshToken);
        Assert.NotEqual(initial.Response.AccessToken, refreshed.Response.AccessToken);

        var reusedEx = await Assert.ThrowsAsync<ApiUnauthorizedException>(
            () => service.RefreshAsync(initial.RefreshToken));
        Assert.Contains("invalid", reusedEx.Message, StringComparison.OrdinalIgnoreCase);

        await using var conn = (SqliteConnection)await env.SystemPool.GetConnectionAsync();
        var revokedAt = await conn.ExecuteScalarAsync<string?>(
            "SELECT revoked_at FROM auth_sessions WHERE id = $id;",
            new { id = initial.SessionId });
        var replacement = await conn.ExecuteScalarAsync<long?>(
            "SELECT replaced_by_session_id FROM auth_sessions WHERE id = $id;",
            new { id = initial.SessionId });

        Assert.False(string.IsNullOrWhiteSpace(revokedAt));
        Assert.Equal(refreshed.SessionId, Assert.IsType<long>(replacement));
    }

    private static BackendRuntimeOptions CreateRuntimeOptions()
    {
        return new BackendRuntimeOptions
        {
            AuthMode = BackendAuthMode.Standard,
            Jwt = new JwtRuntimeOptions
            {
                Secret = "super-secret",
                Issuer = "repere-tests",
                Audience = "repere-clients",
                AccessTokenLifetimeMinutes = 15,
                RefreshTokenLifetimeDays = 7
            }
        };
    }

    private static ClaimsPrincipal CreateAdminPrincipal()
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("role", "admin")],
            authenticationType: "test"));
    }

    private sealed class TemporarySqliteEnvironment : IDisposable
    {
        private readonly string _rootPath;

        public TemporarySqliteEnvironment()
        {
            _rootPath = Path.Combine(Path.GetTempPath(), "repere-tests", Guid.NewGuid().ToString("N"));

            var config = new DatabaseConfig
            {
                Provider = DatabaseProvider.Sqlite,
                SystemConnection = _rootPath,
                DataConnection = _rootPath
            };

            SystemPool = new SystemPool(config, NullLogger<SystemPool>.Instance);
            var bootstrap = new DatabaseBootstrap(SystemPool, NullLogger<DatabaseBootstrap>.Instance);
            bootstrap.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public SystemPool SystemPool { get; }

        public void Dispose()
        {
            SystemPool.Dispose();

            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }

    private static JsonElement ParseJwtPayload(string token)
    {
        var segments = token.Split('.');
        Assert.Equal(3, segments.Length);

        var padded = segments[1]
            .Replace('-', '+')
            .Replace('_', '/');

        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        var bytes = Convert.FromBase64String(padded);

        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }
}
