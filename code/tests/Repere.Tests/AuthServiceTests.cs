using System.Security.Claims;
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

    private static BackendRuntimeOptions CreateRuntimeOptions()
    {
        return new BackendRuntimeOptions
        {
            AuthMode = BackendAuthMode.Standard
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
}
