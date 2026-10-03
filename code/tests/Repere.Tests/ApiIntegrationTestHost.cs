using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.Sqlite;
using Npgsql;
using Repere.Api;
using Repere.Api.Db;
using Repere.Api.Db.Schema;
using Repere.Api.Services;
using System.Data;

namespace Repere.Tests;

public sealed class ApiIntegrationTestHost : IAsyncDisposable
{
    private readonly string _dataRootPath;
    private readonly WebApplication _app;
    private readonly DatabaseProvider _provider;
    private readonly Func<Task<IDbConnection>> _openSystemConnectionAsync;
    private readonly Func<Task<IDbConnection>> _openDataConnectionAsync;
    private readonly Func<ValueTask> _cleanupAsync;

    private ApiIntegrationTestHost(
        DatabaseProvider provider,
        string dataRootPath,
        WebApplication app,
        HttpClient client,
        Func<Task<IDbConnection>> openSystemConnectionAsync,
        Func<Task<IDbConnection>> openDataConnectionAsync,
        Func<ValueTask> cleanupAsync)
    {
        _provider = provider;
        _dataRootPath = dataRootPath;
        _app = app;
        _openSystemConnectionAsync = openSystemConnectionAsync;
        _openDataConnectionAsync = openDataConnectionAsync;
        _cleanupAsync = cleanupAsync;
        Client = client;
    }

    public HttpClient Client { get; }
    public DatabaseProvider Provider => _provider;

    public string SystemDatabasePath =>
        _provider == DatabaseProvider.Sqlite
            ? Path.Combine(_dataRootPath, "system.db")
            : throw new InvalidOperationException("SystemDatabasePath is only available for SQLite-backed test hosts.");

    public static async Task<ApiIntegrationTestHost> StartAsync(
        IReadOnlyDictionary<string, string?>? overrides = null,
        string environmentName = "Development",
        Action<IServiceCollection>? configureTestServices = null)
    {
        var dataRootPath = Path.Combine(Path.GetTempPath(), "repere-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRootPath);

        var configOverrides = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Backend:DataDir"] = dataRootPath,
            ["Database:Provider"] = nameof(DatabaseProvider.Sqlite),
            ["Database:SystemConnection"] = dataRootPath,
            ["Database:DataConnection"] = dataRootPath
        };

        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                configOverrides[pair.Key] = pair.Value;
            }
        }

        return await StartCoreAsync(
            DatabaseProvider.Sqlite,
            dataRootPath,
            configOverrides,
            environmentName,
            configureTestServices,
            openSystemConnectionAsync: async () =>
            {
                var conn = new SqliteConnection($"Data Source={Path.Combine(dataRootPath, "system.db")}");
                await conn.OpenAsync();
                return conn;
            },
            openDataConnectionAsync: async () =>
            {
                var conn = new SqliteConnection($"Data Source={Path.Combine(dataRootPath, "data.db")}");
                await conn.OpenAsync();
                return conn;
            },
            cleanupAsync: () =>
            {
                if (Directory.Exists(dataRootPath))
                {
                    Directory.Delete(dataRootPath, recursive: true);
                }

                return ValueTask.CompletedTask;
            });
    }

    public static async Task<ApiIntegrationTestHost> StartPostgresAsync(
        IReadOnlyDictionary<string, string?>? overrides = null,
        string environmentName = "Development",
        Action<IServiceCollection>? configureTestServices = null)
    {
        var dataRootPath = Path.Combine(Path.GetTempPath(), "repere-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRootPath);

        PostgresTestDatabase? database = null;

        try
        {
            database = await PostgresTestDatabase.CreateAsync(PostgresTestSettings.FromEnvironment());

            var configOverrides = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Backend:DataDir"] = dataRootPath,
                ["Database:Provider"] = nameof(DatabaseProvider.Postgres),
                ["Database:SystemConnection"] = database.SystemConnectionString,
                ["Database:DataConnection"] = database.DataConnectionString
            };

            if (overrides is not null)
            {
                foreach (var pair in overrides)
                {
                    configOverrides[pair.Key] = pair.Value;
                }
            }

            return await StartCoreAsync(
                DatabaseProvider.Postgres,
                dataRootPath,
                configOverrides,
                environmentName,
                configureTestServices,
                openSystemConnectionAsync: async () =>
                {
                    var conn = new NpgsqlConnection(database.SystemConnectionString);
                    await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SET search_path TO _system, public;";
                    await cmd.ExecuteNonQueryAsync();
                    return conn;
                },
                openDataConnectionAsync: async () =>
                {
                    var conn = new NpgsqlConnection(database.DataConnectionString);
                    await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SET search_path TO public;";
                    await cmd.ExecuteNonQueryAsync();
                    return conn;
                },
                cleanupAsync: async () =>
                {
                    if (Directory.Exists(dataRootPath))
                    {
                        Directory.Delete(dataRootPath, recursive: true);
                    }

                    await database.DisposeAsync();
                });
        }
        catch
        {
            if (database is not null)
            {
                await database.DisposeAsync();
            }

            if (Directory.Exists(dataRootPath))
            {
                Directory.Delete(dataRootPath, recursive: true);
            }

            throw;
        }
    }

    private static async Task<ApiIntegrationTestHost> StartCoreAsync(
        DatabaseProvider provider,
        string dataRootPath,
        IReadOnlyDictionary<string, string?> configOverrides,
        string environmentName,
        Action<IServiceCollection>? configureTestServices,
        Func<Task<IDbConnection>> openSystemConnectionAsync,
        Func<Task<IDbConnection>> openDataConnectionAsync,
        Func<ValueTask> cleanupAsync)
    {
        try
        {
            var app = await BuildAndStartAppAsync(dataRootPath, configOverrides, environmentName, configureTestServices);
            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
                ?? throw new InvalidOperationException("Server addresses were not available after startup.");
            var baseAddress = addresses.Single();

            var client = new HttpClient
            {
                BaseAddress = new Uri(baseAddress)
            };

            return new ApiIntegrationTestHost(
                provider,
                dataRootPath,
                app,
                client,
                openSystemConnectionAsync,
                openDataConnectionAsync,
                cleanupAsync);
        }
        catch
        {
            await cleanupAsync();
            throw;
        }
    }

    private static async Task<WebApplication> BuildAndStartAppAsync(
        string dataRootPath,
        IReadOnlyDictionary<string, string?> configOverrides,
        string environmentName,
        Action<IServiceCollection>? configureTestServices)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.FullName,
            EnvironmentName = environmentName,
            ContentRootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/backend"))
        });

        BackendHost.ConfigureConfiguration(builder);

        var mergedOverrides = new Dictionary<string, string?>(StringComparer.Ordinal)
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
            ["Backend:AuthCookie:SameSite"] = "Strict",
            ["Backend:AuthCookie:Path"] = "/api/v1/auth",
            ["Backend:AuthRateLimitPerMinute"] = "5",
            ["Backend:DataDir"] = dataRootPath,
            ["Schema:AutoMigrateOnStartup"] = "false",
            ["urls"] = "http://127.0.0.1:0"
        };

        foreach (var pair in configOverrides)
        {
            mergedOverrides[pair.Key] = pair.Value;
        }

        builder.Configuration.AddInMemoryCollection(mergedOverrides);
        BackendHost.ConfigureServices(builder.Services, builder.Configuration, builder.Environment);
        configureTestServices?.Invoke(builder.Services);

        var app = builder.Build();
        BackendHost.ConfigureMiddleware(app);
        BackendHost.ConfigureRoutes(app);

        await app.StartAsync();
        return app;
    }

    public async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        var response = await Client.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Expected JSON response body.");
    }

    public string DataDatabasePath =>
        _provider == DatabaseProvider.Sqlite
            ? Path.Combine(_dataRootPath, "data.db")
            : throw new InvalidOperationException("DataDatabasePath is only available for SQLite-backed test hosts.");

    public async Task<T> QuerySystemScalarAsync<T>(string sql, object? parameters = null)
        where T : notnull
    {
        using var conn = await _openSystemConnectionAsync();
        var result = await conn.ExecuteScalarAsync<T>(sql, parameters);
        return result ?? throw new InvalidOperationException("Expected scalar result.");
    }

    public async Task<T> QueryDataScalarAsync<T>(string sql, object? parameters = null)
        where T : notnull
    {
        using var conn = await _openDataConnectionAsync();
        var result = await conn.ExecuteScalarAsync<T>(sql, parameters);
        return result ?? throw new InvalidOperationException("Expected scalar result.");
    }

    /// <summary>
    /// Execute arbitrary SQL against the data database (useful for inserting junction table rows).
    /// </summary>
    public async Task ExecuteDataSqlAsync(string sql, object? parameters = null)
    {
        using var conn = await _openDataConnectionAsync();
        await conn.ExecuteAsync(sql, parameters);
    }

    /// <summary>
    /// Apply a Model JSON schema and reload the schema cache so data routes become available.
    /// </summary>
    public async Task ApplySchemaAsync(string modelJson)
    {
        var schemaService = _app.Services.GetRequiredService<SchemaService>();
        var schemaCache = _app.Services.GetRequiredService<SchemaCache>();

        var model = ModelJsonSerializer.Parse(modelJson);
        var result = await schemaService.ApplySchemaAsync(model, confirmDestructive: true);

        if (result.Errors.Count > 0)
        {
            var errors = string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Message}"));
            throw new InvalidOperationException($"Schema apply failed: {errors}");
        }

        await schemaCache.ReloadAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _cleanupAsync();
    }
}
