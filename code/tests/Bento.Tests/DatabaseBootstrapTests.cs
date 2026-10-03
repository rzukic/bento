using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Bento.Api.Db;

namespace Bento.Tests;

public class DatabaseBootstrapTests
{
    private static readonly string[] ExpectedSystemTables =
    [
        "api_keys",
        "auth_sessions",
        "credentials",
        "dashboards",
        "import_profiles",
        "jobs",
        "migrations",
        "saved_queries",
        "schema_version_counters",
        "schema_versions",
        "system_views",
        "user_table_explorer_preferences",
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
                'import_profiles',
                'saved_queries',
                'credentials',
                'jobs',
                'schema_version_counters',
                'system_views',
                'user_table_explorer_preferences'
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

    [Fact]
    public async Task Bootstrap_AddsOwnerAndSharingToAnExistingDashboardsTable()
    {
        using var env = new TemporarySqliteEnvironment();
        await using (var conn = (SqliteConnection)await env.SystemPool.GetConnectionAsync())
        {
            await conn.ExecuteAsync("""
                CREATE TABLE dashboards (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    dashboard_json TEXT NOT NULL,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                INSERT INTO dashboards (name, dashboard_json) VALUES ('Old', '{"widgets":[]}');
                """);
        }

        var bootstrap = new DatabaseBootstrap(env.SystemPool, NullLogger<DatabaseBootstrap>.Instance);
        await bootstrap.StartAsync(CancellationToken.None);
        await bootstrap.StartAsync(CancellationToken.None);

        await using var check = (SqliteConnection)await env.SystemPool.GetConnectionAsync();
        var columns = (await check.QueryAsync<string>("SELECT name FROM pragma_table_info('dashboards')")).ToList();
        Assert.Contains("owner_user_id", columns);
        Assert.Contains("is_shared", columns);
        Assert.Equal(0, await check.ExecuteScalarAsync<long>("SELECT is_shared FROM dashboards WHERE name = 'Old'"));
    }

    private sealed class TemporarySqliteEnvironment : IDisposable
    {
        private readonly string _rootPath;

        public SystemPool SystemPool { get; }
        public DataPool DataPool { get; }

        public TemporarySqliteEnvironment()
        {
            _rootPath = Path.Combine(Path.GetTempPath(), "bento-tests", Guid.NewGuid().ToString("N"));

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
