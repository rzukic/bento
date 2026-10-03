using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Repere.Api.Configuration;
using Repere.Api.Db;

namespace Repere.Tests;

public sealed class BackendRuntimeConfigurationTests
{
    [Fact]
    public void BuildEnvironmentOverrides_MapsRuntimeSettings_AndDualSqlitePaths()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["BACKEND_PORT"] = "7001",
            ["LOG_LEVEL"] = "warn",
            ["ALLOWED_ORIGINS"] = "https://app.example.com, https://admin.example.com",
            ["MAX_BODY_SIZE"] = "20mb",
            ["AUTH_MODE"] = "standard",
            ["JWT_SECRET"] = "super-secret",
            ["JWT_ISSUER"] = "repere-tests",
            ["JWT_AUDIENCE"] = "repere-clients",
            ["JWT_ACCESS_TOKEN_LIFETIME_MINUTES"] = "20",
            ["JWT_REFRESH_TOKEN_LIFETIME_DAYS"] = "14",
            ["AUTH_REFRESH_COOKIE_NAME"] = "repere_rt",
            ["AUTH_REFRESH_COOKIE_SECURE"] = "false",
            ["AUTH_REFRESH_COOKIE_SAME_SITE"] = "Lax",
            ["AUTH_REFRESH_COOKIE_PATH"] = "/api/v1/auth",
            ["AUTH_RATE_LIMIT_PER_MINUTE"] = "9",
            ["AUTH_SESSION_RATE_LIMIT_PER_MINUTE"] = "240",
            ["DATA_DIR"] = "/srv/repere"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:Port"] = "5000",
                ["Backend:LogLevel"] = "Information",
                ["Backend:MaxBodySize"] = "10mb",
                ["Backend:AuthMode"] = "standard",
                ["Backend:Jwt:Issuer"] = "repere-api",
                ["Backend:Jwt:Audience"] = "repere-clients",
                ["Backend:Jwt:AccessTokenLifetimeMinutes"] = "15",
                ["Backend:Jwt:RefreshTokenLifetimeDays"] = "7",
                ["Backend:AuthCookie:RefreshCookieName"] = "repere_refresh",
                ["Backend:AuthCookie:HttpOnly"] = "true",
                ["Backend:AuthCookie:RequireSecure"] = "true",
                ["Backend:AuthCookie:SameSite"] = "Strict",
                ["Backend:AuthCookie:Path"] = "/api/v1/auth",
                ["Backend:AuthRateLimitPerMinute"] = "5",
                ["Backend:DataDir"] = "./data",
                ["Database:Provider"] = nameof(DatabaseProvider.Sqlite),
                ["Database:SystemConnection"] = "./data",
                ["Database:DataConnection"] = "./data"
            })
            .AddInMemoryCollection(BackendRuntimeConfiguration.BuildEnvironmentOverrides(GetEnv))
            .Build();

        var runtimeOptions = BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(
            configuration,
            new FakeHostEnvironment("Development"));

        var dbConfig = new DatabaseConfig();
        configuration.GetSection("Database").Bind(dbConfig);
        BackendRuntimeConfiguration.ApplyDatabaseOverrides(dbConfig, runtimeOptions);

        Assert.Equal(7001, runtimeOptions.Port);
        Assert.Equal("Warning", runtimeOptions.LogLevel);
        Assert.Equal(["https://app.example.com", "https://admin.example.com"], runtimeOptions.AllowedOrigins);
        Assert.Equal("20mb", runtimeOptions.MaxBodySize);
        Assert.Equal(BackendAuthMode.Standard, runtimeOptions.AuthMode);
        Assert.Equal("super-secret", runtimeOptions.Jwt.Secret);
        Assert.Equal("repere-tests", runtimeOptions.Jwt.Issuer);
        Assert.Equal("repere-clients", runtimeOptions.Jwt.Audience);
        Assert.Equal(20, runtimeOptions.Jwt.AccessTokenLifetimeMinutes);
        Assert.Equal(14, runtimeOptions.Jwt.RefreshTokenLifetimeDays);
        Assert.Equal("repere_rt", runtimeOptions.AuthCookie.RefreshCookieName);
        Assert.True(runtimeOptions.AuthCookie.HttpOnly);
        Assert.False(runtimeOptions.AuthCookie.RequireSecure);
        Assert.False(runtimeOptions.AuthCookie.EffectiveRequireSecure);
        Assert.Equal("Lax", runtimeOptions.AuthCookie.SameSite);
        Assert.Equal("/api/v1/auth", runtimeOptions.AuthCookie.Path);
        Assert.Equal(9, runtimeOptions.AuthRateLimitPerMinute);
        Assert.Equal(240, runtimeOptions.AuthSessionRateLimitPerMinute);
        Assert.Equal("/srv/repere", runtimeOptions.DataDir);
        Assert.Equal(20L * 1024L * 1024L, runtimeOptions.GetMaxBodySizeBytes());
        Assert.Equal("/srv/repere", dbConfig.SystemConnection);
        Assert.Equal("/srv/repere", dbConfig.DataConnection);

        string? GetEnv(string name) => env.TryGetValue(name, out var value) ? value : null;
    }

    [Fact]
    public void ApplyDatabaseOverrides_UsesSqliteDatabaseUrl_AsSharedDataRoot()
    {
        var dbConfig = new DatabaseConfig
        {
            Provider = DatabaseProvider.Sqlite,
            SystemConnection = "./data",
            DataConnection = "./data"
        };

        var runtimeOptions = new BackendRuntimeOptions
        {
            Port = 5000,
            LogLevel = "Information",
            MaxBodySize = "10mb",
            AuthMode = BackendAuthMode.Standard,
            DataDir = "./data",
            DatabaseUrl = "sqlite:///var/lib/repere/system.db"
        };

        runtimeOptions.AuthCookie.EffectiveRequireSecure = false;
        BackendRuntimeConfiguration.Validate(runtimeOptions);
        BackendRuntimeConfiguration.ApplyDatabaseOverrides(dbConfig, runtimeOptions);

        Assert.Equal(DatabaseProvider.Sqlite, dbConfig.Provider);
        Assert.Equal("/var/lib/repere", dbConfig.SystemConnection);
        Assert.Equal("/var/lib/repere", dbConfig.DataConnection);
    }

    [Fact]
    public void ApplyDatabaseOverrides_RejectsSinglePostgresDatabaseUrl()
    {
        var dbConfig = new DatabaseConfig
        {
            Provider = DatabaseProvider.Postgres,
            SystemConnection = "Host=localhost;Database=repere;Username=repere_system;Password=secret",
            DataConnection = "Host=localhost;Database=repere;Username=repere_data;Password=secret"
        };

        var runtimeOptions = new BackendRuntimeOptions
        {
            Port = 5000,
            LogLevel = "Information",
            MaxBodySize = "10mb",
            AuthMode = BackendAuthMode.Standard,
            DataDir = "./data",
            DatabaseUrl = "postgresql://admin:pass@db:5432/repere"
        };

        runtimeOptions.AuthCookie.EffectiveRequireSecure = false;
        BackendRuntimeConfiguration.Validate(runtimeOptions);

        var ex = Assert.Throws<InvalidOperationException>(
            () => BackendRuntimeConfiguration.ApplyDatabaseOverrides(dbConfig, runtimeOptions));

        Assert.Contains("DATABASE_SYSTEM_CONNECTION", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DATABASE_DATA_CONNECTION", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BindAndValidateRuntimeOptions_RejectsCommittedPostgresSecrets()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:Port"] = "5000",
                ["Backend:LogLevel"] = "Information",
                ["Backend:MaxBodySize"] = "10mb",
                ["Backend:AuthMode"] = "standard",
                ["Backend:Jwt:Issuer"] = "repere-api",
                ["Backend:Jwt:Audience"] = "repere-clients",
                ["Backend:AuthCookie:RefreshCookieName"] = "repere_refresh",
                ["Backend:AuthCookie:SameSite"] = "Strict",
                ["Backend:AuthCookie:Path"] = "/api/v1/auth",
                ["Backend:DataDir"] = "./data",
                ["Backend:PostgresPackaging:SuperuserPassword"] = "repere_dev",
                ["Database:Provider"] = nameof(DatabaseProvider.Postgres),
                ["Database:SystemConnection"] = "Host=localhost;Port=5432;Database=repere;Username=repere_system;Password=repere_system_dev",
                ["Database:DataConnection"] = "Host=localhost;Port=5432;Database=repere;Username=repere_data;Password=repere_data_dev"
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(
                configuration,
                new FakeHostEnvironment("Production")));

        Assert.Contains("POSTGRES_SUPERUSER_PASSWORD", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BindAndValidateRuntimeOptions_AllowsNonDefaultPostgresSecrets()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:Port"] = "5000",
                ["Backend:LogLevel"] = "Information",
                ["Backend:MaxBodySize"] = "10mb",
                ["Backend:AuthMode"] = "standard",
                ["Backend:Jwt:Issuer"] = "repere-api",
                ["Backend:Jwt:Audience"] = "repere-clients",
                ["Backend:AuthCookie:RefreshCookieName"] = "repere_refresh",
                ["Backend:AuthCookie:SameSite"] = "Strict",
                ["Backend:AuthCookie:Path"] = "/api/v1/auth",
                ["Backend:DataDir"] = "./data",
                ["Backend:PostgresPackaging:SuperuserPassword"] = "superuser-secret-123",
                ["Database:Provider"] = nameof(DatabaseProvider.Postgres),
                ["Database:SystemConnection"] = "Host=localhost;Port=5432;Database=repere;Username=repere_system;Password=system-secret-123",
                ["Database:DataConnection"] = "Host=localhost;Port=5432;Database=repere;Username=repere_data;Password=data-secret-123"
            })
            .Build();

        var options = BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(
            configuration,
            new FakeHostEnvironment("Production"));

        Assert.Equal(BackendAuthMode.Standard, options.AuthMode);
    }

    [Fact]
    public void BindAndValidateRuntimeOptions_RejectsInvalidAuthMode()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:Port"] = "5000",
                ["Backend:LogLevel"] = "Information",
                ["Backend:MaxBodySize"] = "10mb",
                ["Backend:AuthMode"] = "bad-mode",
                ["Backend:DataDir"] = "./data"
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(
                configuration,
                new FakeHostEnvironment("Development")));

        Assert.Contains("AUTH_MODE", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BindAndValidateRuntimeOptions_RejectsVaultMode_UntilLaterPhase()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:Port"] = "5000",
                ["Backend:LogLevel"] = "Information",
                ["Backend:MaxBodySize"] = "10mb",
                ["Backend:AuthMode"] = "vault",
                ["Backend:Jwt:Issuer"] = "repere-api",
                ["Backend:Jwt:Audience"] = "repere-clients",
                ["Backend:AuthCookie:RefreshCookieName"] = "repere_refresh",
                ["Backend:AuthCookie:SameSite"] = "Strict",
                ["Backend:AuthCookie:Path"] = "/api/v1/auth",
                ["Backend:DataDir"] = "./data"
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(
                configuration,
                new FakeHostEnvironment("Development")));

        Assert.Contains("not implemented yet", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseByteSize_ParsesHumanReadableValues()
    {
        Assert.Equal(512L, BackendRuntimeConfiguration.ParseByteSize("512"));
        Assert.Equal(1024L, BackendRuntimeConfiguration.ParseByteSize("1kb"));
        Assert.Equal(10L * 1024L * 1024L, BackendRuntimeConfiguration.ParseByteSize("10mb"));
    }

    [Fact]
    public void BindAndValidateRuntimeOptions_DoesNotForceRefreshCookieSecurity_InAnyEnvironment()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:Port"] = "5000",
                ["Backend:LogLevel"] = "Information",
                ["Backend:MaxBodySize"] = "10mb",
                ["Backend:AuthMode"] = "standard",
                ["Backend:Jwt:Issuer"] = "repere-api",
                ["Backend:Jwt:Audience"] = "repere-clients",
                ["Backend:AuthCookie:RefreshCookieName"] = "repere_refresh",
                ["Backend:AuthCookie:SameSite"] = "Strict",
                ["Backend:AuthCookie:Path"] = "/api/v1/auth",
                ["Backend:DataDir"] = "./data"
            })
            .Build();

        var developmentOptions = BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(
            configuration,
            new FakeHostEnvironment("Development"));
        var productionOptions = BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(
            configuration,
            new FakeHostEnvironment("Production"));

        // Unset → the cookie follows the request scheme at runtime; nothing is forced
        // even in Production, otherwise plain-HTTP packaged stacks (http://<lan-ip>)
        // would issue a Secure cookie that browsers drop.
        Assert.Null(developmentOptions.AuthCookie.RequireSecure);
        Assert.False(developmentOptions.AuthCookie.EffectiveRequireSecure);
        Assert.Null(productionOptions.AuthCookie.RequireSecure);
        Assert.False(productionOptions.AuthCookie.EffectiveRequireSecure);
    }

    [Fact]
    public void BuildEnvironmentOverrides_MapsTrustedProxies_AsIndexedList()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["TRUSTED_PROXIES"] = "172.18.0.0/16, 10.0.0.5 ,fd00::/8"
        };

        var overrides = BackendRuntimeConfiguration.BuildEnvironmentOverrides(
            name => env.TryGetValue(name, out var value) ? value : null);

        Assert.Equal("172.18.0.0/16", overrides["Backend:TrustedProxies:0"]);
        Assert.Equal("10.0.0.5", overrides["Backend:TrustedProxies:1"]);
        Assert.Equal("fd00::/8", overrides["Backend:TrustedProxies:2"]);
        Assert.DoesNotContain("Backend:TrustedProxies:3", overrides.Keys);

        var emptyOverrides = BackendRuntimeConfiguration.BuildEnvironmentOverrides(_ => null);
        Assert.DoesNotContain(emptyOverrides.Keys, key => key.StartsWith("Backend:TrustedProxies", StringComparison.Ordinal));
    }

    [Fact]
    public void BindAndValidateRuntimeOptions_ParsesTrustedProxies_AndDefaultsToNone()
    {
        var withProxies = BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(
            BuildMinimalConfiguration(new Dictionary<string, string?>
            {
                ["Backend:TrustedProxies:0"] = "172.18.0.0/16",
                ["Backend:TrustedProxies:1"] = "10.0.0.5"
            }),
            new FakeHostEnvironment("Production"));

        Assert.True(withProxies.HasTrustedProxies);
        Assert.Equal(["172.18.0.0/16", "10.0.0.5"], withProxies.TrustedProxies);

        var withoutProxies = BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(
            BuildMinimalConfiguration(),
            new FakeHostEnvironment("Production"));

        Assert.False(withoutProxies.HasTrustedProxies);
        Assert.Empty(withoutProxies.TrustedProxies);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/abc")]
    [InlineData("fd00::/129")]
    [InlineData("proxy")]
    public void BindAndValidateRuntimeOptions_RejectsInvalidTrustedProxyEntry(string entry)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(
                BuildMinimalConfiguration(new Dictionary<string, string?>
                {
                    ["Backend:TrustedProxies:0"] = "10.0.0.5",
                    ["Backend:TrustedProxies:1"] = entry
                }),
                new FakeHostEnvironment("Development")));

        Assert.Contains("TRUSTED_PROXIES", ex.Message, StringComparison.Ordinal);
        Assert.Contains(entry, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedProxyConfiguration_BuildsForwardedHeaderOptions_WithoutLoopbackDefaults()
    {
        var options = TrustedProxyConfiguration.CreateForwardedHeadersOptions(["172.18.0.0/16", "10.0.0.5", "fd00::/8"]);

        Assert.Equal(
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost,
            options.ForwardedHeaders);

        var proxy = Assert.Single(options.KnownProxies);
        Assert.Equal(System.Net.IPAddress.Parse("10.0.0.5"), proxy);
        Assert.DoesNotContain(System.Net.IPAddress.IPv6Loopback, options.KnownProxies);

        Assert.Equal(2, options.KnownNetworks.Count);
        Assert.Contains(options.KnownNetworks, network => network.Contains(System.Net.IPAddress.Parse("172.18.5.9")));
        Assert.Contains(options.KnownNetworks, network => network.Contains(System.Net.IPAddress.Parse("fd00::1")));
        Assert.DoesNotContain(options.KnownNetworks, network => network.Contains(System.Net.IPAddress.Loopback));

        Assert.Throws<InvalidOperationException>(() => TrustedProxyConfiguration.CreateForwardedHeadersOptions([]));
    }

    private static IConfiguration BuildMinimalConfiguration(IReadOnlyDictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Backend:Port"] = "5000",
            ["Backend:LogLevel"] = "Information",
            ["Backend:MaxBodySize"] = "10mb",
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Issuer"] = "repere-api",
            ["Backend:Jwt:Audience"] = "repere-clients",
            ["Backend:AuthCookie:RefreshCookieName"] = "repere_refresh",
            ["Backend:AuthCookie:SameSite"] = "Strict",
            ["Backend:AuthCookie:Path"] = "/api/v1/auth",
            ["Backend:DataDir"] = "./data"
        };

        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                values[pair.Key] = pair.Value;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void BuildPersistentRuntimeOverrides_GeneratesJwtSecret_File_WhenMissing()
    {
        using var tempDir = new TemporaryDirectory();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DataDir"] = tempDir.Path
            })
            .Build();

        var overrides = PersistentRuntimeConfigBootstrap.BuildOverrides(configuration);

        var configPath = Assert.IsType<string>(overrides["Backend:PersistentConfigPath"]);
        var secret = Assert.IsType<string>(overrides["Backend:Jwt:Secret"]);

        Assert.Equal(Path.Combine(tempDir.Path, "config.json"), configPath);
        Assert.False(string.IsNullOrWhiteSpace(secret));
        Assert.True(File.Exists(configPath));

        var persisted = File.ReadAllText(configPath);
        Assert.Contains("\"Secret\"", persisted, StringComparison.Ordinal);
        Assert.Contains(secret, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPersistentRuntimeOverrides_OnPosix_CreatesSecureModes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var tempDir = new TemporaryDirectory();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DataDir"] = tempDir.Path
            })
            .Build();

        _ = PersistentRuntimeConfigBootstrap.BuildOverrides(configuration);

        var configPath = Path.Combine(tempDir.Path, "config.json");
        var fileMode = File.GetUnixFileMode(configPath);
        var directoryMode = File.GetUnixFileMode(tempDir.Path);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            fileMode);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            directoryMode);
    }

    [Fact]
    public void BuildPersistentRuntimeOverrides_ReusesPersistedJwtSecret()
    {
        using var tempDir = new TemporaryDirectory();
        var configPath = Path.Combine(tempDir.Path, "config.json");
        File.WriteAllText(configPath, """
            {
              "Backend": {
                "Jwt": {
                  "Secret": "persisted-secret"
                }
              }
            }
            """);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DataDir"] = tempDir.Path
            })
            .Build();

        var overrides = PersistentRuntimeConfigBootstrap.BuildOverrides(configuration);

        Assert.Equal("persisted-secret", overrides["Backend:Jwt:Secret"]);
        Assert.Equal(configPath, overrides["Backend:PersistentConfigPath"]);
    }

    [Fact]
    public void BuildPersistentRuntimeOverrides_OnPosix_WarnsOnceForOverPermissiveConfig()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var tempDir = new TemporaryDirectory();
        var configPath = Path.Combine(tempDir.Path, "config.json");
        File.WriteAllText(configPath, """
            {
              "Backend": {
                "Jwt": {
                  "Secret": "persisted-secret"
                }
              }
            }
            """);
        File.SetUnixFileMode(
            configPath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.GroupRead |
            UnixFileMode.OtherRead);

        var logger = new TestLogger();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DataDir"] = tempDir.Path
            })
            .Build();

        var first = PersistentRuntimeConfigBootstrap.BuildOverrides(configuration, logger);
        var second = PersistentRuntimeConfigBootstrap.BuildOverrides(configuration, logger);

        Assert.Equal("persisted-secret", first["Backend:Jwt:Secret"]);
        Assert.Equal("persisted-secret", second["Backend:Jwt:Secret"]);
        Assert.Single(logger.WarningMessages);
        Assert.Contains("over-permissive mode", logger.WarningMessages[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("644", logger.WarningMessages[0], StringComparison.Ordinal);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "Repere.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(AppContext.BaseDirectory);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "repere-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class TestLogger : ILogger
    {
        public List<string> WarningMessages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                WarningMessages.Add(formatter(state, exception));
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    [Fact]
    public void BindAndValidateRuntimeOptions_DefaultsSessionRateLimit_And_RejectsNonPositiveValues()
    {
        IConfigurationRoot Build(string? sessionLimit) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:Port"] = "5000",
                ["Backend:LogLevel"] = "Information",
                ["Backend:MaxBodySize"] = "10mb",
                ["Backend:AuthMode"] = "standard",
                ["Backend:Jwt:Issuer"] = "repere-api",
                ["Backend:Jwt:Audience"] = "repere-clients",
                ["Backend:AuthCookie:RefreshCookieName"] = "repere_refresh",
                ["Backend:AuthCookie:SameSite"] = "Strict",
                ["Backend:AuthCookie:Path"] = "/api/v1/auth",
                ["Backend:DataDir"] = "./data",
                ["Backend:AuthSessionRateLimitPerMinute"] = sessionLimit
            })
            .Build();

        var defaults = BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(
            Build(null),
            new FakeHostEnvironment("Development"));
        Assert.Equal(120, defaults.AuthSessionRateLimitPerMinute);
        Assert.Equal(5, defaults.AuthRateLimitPerMinute);

        var ex = Assert.Throws<InvalidOperationException>(
            () => BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(
                Build("0"),
                new FakeHostEnvironment("Development")));
        Assert.Contains("AUTH_SESSION_RATE_LIMIT_PER_MINUTE", ex.Message, StringComparison.Ordinal);
    }

}
