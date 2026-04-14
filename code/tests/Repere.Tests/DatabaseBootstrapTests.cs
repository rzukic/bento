using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Repere.Api.Db;

namespace Repere.Tests;

public class DatabaseBootstrapTests
{
    private static readonly string[] ExpectedSystemTables =
    [
        "api_keys",
        "auth_sessions",
        "credentials",
        "dashboards",
        "jobs",
        "migrations",
        "schema_version_counters",
        "schema_versions",
        "system_views",
        "users"
    ];

    [Fact]
    public async Task Bootstrap_CanRunTwice_WithoutChangingSystemTableSet()
    {
        using var env = new TemporarySqliteEnvironment();
        var bootstrap = new DatabaseBootstrap(env.SystemPool, NullLogger<DatabaseBootstrap>.Instance);

        await bootstrap.StartAsync(CancellationToken.None);
        await bootstrap.StartAsync(CancellationToken.None);

        await using var conn = (SqliteConnection)await env.SystemPool.GetConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN (
                'schema_versions',
                'migrations',
                'users',
                'api_keys',
                'auth_sessions',
                'dashboards',
                'credentials',
                'jobs',
                'schema_version_counters',
                'system_views'
              )
            ORDER BY name;
            """;

        var actualTables = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actualTables.Add(reader.GetString(0));
        }

        Assert.Equal(ExpectedSystemTables, actualTables);
    }

    [Fact]
    public async Task DataPool_CannotAccessSystemTables_InSqliteMode()
    {
        using var env = new TemporarySqliteEnvironment();
        var bootstrap = new DatabaseBootstrap(env.SystemPool, NullLogger<DatabaseBootstrap>.Instance);

        await bootstrap.StartAsync(CancellationToken.None);

        await using (var systemConn = (SqliteConnection)await env.SystemPool.GetConnectionAsync())
        {
            using var insert = systemConn.CreateCommand();
            insert.CommandText = """
                INSERT INTO schema_versions (model_json, version)
                VALUES ($modelJson, $version);
                """;
            insert.Parameters.AddWithValue("$modelJson", "{}");
            insert.Parameters.AddWithValue("$version", 1);
            await insert.ExecuteNonQueryAsync();
        }

        await using var dataConn = (SqliteConnection)await env.DataPool.GetConnectionAsync();
        using var cmd = dataConn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_versions;";

        var ex = await Assert.ThrowsAsync<SqliteException>(async () => await cmd.ExecuteScalarAsync());
        Assert.Contains("no such table", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SchemaVersions_VersionColumn_IsUniqueAtDatabaseLevel()
    {
        using var env = new TemporarySqliteEnvironment();
        var bootstrap = new DatabaseBootstrap(env.SystemPool, NullLogger<DatabaseBootstrap>.Instance);

        await bootstrap.StartAsync(CancellationToken.None);

        await using var conn = (SqliteConnection)await env.SystemPool.GetConnectionAsync();

        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO schema_versions (model_json, version)
                VALUES ($modelJson, $version);
                """;
            insert.Parameters.AddWithValue("$modelJson", "{}");
            insert.Parameters.AddWithValue("$version", 1);
            await insert.ExecuteNonQueryAsync();
        }

        using var duplicate = conn.CreateCommand();
        duplicate.CommandText = """
            INSERT INTO schema_versions (model_json, version)
            VALUES ($modelJson, $version);
            """;
        duplicate.Parameters.AddWithValue("$modelJson", "{}");
        duplicate.Parameters.AddWithValue("$version", 1);

        var ex = await Assert.ThrowsAsync<SqliteException>(async () => await duplicate.ExecuteNonQueryAsync());
        Assert.Contains("UNIQUE constraint failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TemporarySqliteEnvironment : IDisposable
    {
        private readonly string _rootPath;

        public SystemPool SystemPool { get; }
        public DataPool DataPool { get; }

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
            DataPool = new DataPool(config, NullLogger<DataPool>.Instance);
        }

        public void Dispose()
        {
            SystemPool.Dispose();
            DataPool.Dispose();

            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }
}
