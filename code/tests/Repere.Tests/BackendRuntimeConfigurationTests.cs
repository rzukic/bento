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
            ["AUTH_MODE"] = "vault",
            ["JWT_SECRET"] = "super-secret",
            ["DATA_DIR"] = "/srv/repere"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:Port"] = "5000",
                ["Backend:LogLevel"] = "Information",
                ["Backend:MaxBodySize"] = "10mb",
                ["Backend:AuthMode"] = "standard",
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
        Assert.Equal("vault", runtimeOptions.AuthMode);
        Assert.Equal("super-secret", runtimeOptions.JwtSecret);
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
            AuthMode = "standard",
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
            AuthMode = "standard",
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
    public void ParseByteSize_ParsesHumanReadableValues()
    {
        Assert.Equal(512L, BackendRuntimeConfiguration.ParseByteSize("512"));
        Assert.Equal(1024L, BackendRuntimeConfiguration.ParseByteSize("1kb"));
        Assert.Equal(10L * 1024L * 1024L, BackendRuntimeConfiguration.ParseByteSize("10mb"));
    }
}
