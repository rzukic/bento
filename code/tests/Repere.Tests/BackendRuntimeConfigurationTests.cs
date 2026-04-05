using Microsoft.Extensions.Configuration;
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

        var runtimeOptions = BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(configuration);

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
        Assert.Equal("Lax", runtimeOptions.AuthCookie.SameSite);
        Assert.Equal("/api/v1/auth", runtimeOptions.AuthCookie.Path);
        Assert.Equal(9, runtimeOptions.AuthRateLimitPerMinute);
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

        BackendRuntimeConfiguration.Validate(runtimeOptions);

        var ex = Assert.Throws<InvalidOperationException>(
            () => BackendRuntimeConfiguration.ApplyDatabaseOverrides(dbConfig, runtimeOptions));

        Assert.Contains("DATABASE_SYSTEM_CONNECTION", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DATABASE_DATA_CONNECTION", ex.Message, StringComparison.Ordinal);
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
            () => BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(configuration));

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
            () => BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(configuration));

        Assert.Contains("not implemented yet", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseByteSize_ParsesHumanReadableValues()
    {
        Assert.Equal(512L, BackendRuntimeConfiguration.ParseByteSize("512"));
        Assert.Equal(1024L, BackendRuntimeConfiguration.ParseByteSize("1kb"));
        Assert.Equal(10L * 1024L * 1024L, BackendRuntimeConfiguration.ParseByteSize("10mb"));
    }
}
