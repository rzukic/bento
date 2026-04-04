using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Repere.Api.Db;
using Repere.Api.Db.Migrations;
using Repere.Api.Db.Schema;

namespace Repere.Tests;

public class MigrationRunnerTests
{
    [Fact]
    public async Task SchemaService_ApplySchema_CreatesTablesAndRecordsHistory()
    {
        using var env = new SchemaTestEnvironment();
        await env.InitializeAsync();

        var result = await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "name", "type": "text", "nullable": false }
                  ],
                  "indexes": [
                    { "name": "idx_customers_name", "columns": ["name"], "unique": false }
                  ]
                }
              ],
              "relations": []
            }
            """));

        Assert.True(result.Applied);
        Assert.NotNull(result.Migration);
        Assert.Equal(0, result.Migration!.VersionFrom);
        Assert.Equal(1, result.Migration.VersionTo);

        await using var dataConn = await env.OpenDataAsync();
        Assert.True(await TableExistsAsync(dataConn, "customers"));
        Assert.Equal(["id", "name"], await GetColumnNamesAsync(dataConn, "customers"));
        Assert.True(await IndexExistsAsync(dataConn, "idx_customers_name"));

        await using var systemConn = await env.OpenSystemAsync();
        Assert.Equal(1, await CountRowsAsync(systemConn, "schema_versions"));
        Assert.Equal(1, await CountRowsAsync(systemConn, "migrations"));

        var history = await env.SchemaService.GetHistoryAsync();
        Assert.Single(history);
        Assert.Equal(0, history[0].VersionFrom);
        Assert.Equal(1, history[0].VersionTo);
    }

    [Fact]
    public async Task SchemaService_ApplySchema_ModifyThenRollback_RestoresPreviousSchema()
    {
        using var env = new SchemaTestEnvironment();
        await env.InitializeAsync();

        await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "products",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """));

        await using (var dataConn = await env.OpenDataAsync())
        {
            using var insert = dataConn.CreateCommand();
            insert.CommandText = """
                INSERT INTO "products" ("name")
                VALUES ('Widget');
                """;
            await insert.ExecuteNonQueryAsync();
        }

        var applyResult = await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "products",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false },
                    { "name": "status", "type": "text", "nullable": false, "default": "draft" }
                  ],
                  "indexes": [
                    { "name": "idx_products_status", "columns": ["status"], "unique": false }
                  ]
                }
              ],
              "relations": []
            }
            """));

        Assert.True(applyResult.Applied);

        await using (var dataConn = await env.OpenDataAsync())
        {
            Assert.Equal(["id", "name", "status"], await GetColumnNamesAsync(dataConn, "products"));

            using var query = dataConn.CreateCommand();
            query.CommandText = """
                SELECT "status"
                FROM "products"
                WHERE "name" = 'Widget'
                LIMIT 1;
                """;
            var status = (string?)await query.ExecuteScalarAsync();
            Assert.Equal("draft", status);
        }

        var rollback = await env.SchemaService.RollbackLastAsync();
        Assert.NotNull(rollback);
        Assert.Equal(2, rollback!.VersionFrom);
        Assert.Equal(1, rollback.VersionTo);

        await using (var dataConn = await env.OpenDataAsync())
        {
            Assert.Equal(["id", "name"], await GetColumnNamesAsync(dataConn, "products"));

            using var count = dataConn.CreateCommand();
            count.CommandText = """SELECT COUNT(*) FROM "products";""";
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync() ?? 0L));
        }

        var current = await env.ModelJsonStore.GetCurrentAsync();
        Assert.NotNull(current);
        Assert.Equal(1, current!.Version);

        var history = await env.SchemaService.GetHistoryAsync();
        Assert.Equal(3, history.Count);
        Assert.Equal((2, 1), (history[0].VersionFrom, history[0].VersionTo));
    }

    [Fact]
    public async Task SchemaService_DestructiveChange_RequiresConfirmation()
    {
        using var env = new SchemaTestEnvironment();
        await env.InitializeAsync();

        await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "products",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false },
                    { "name": "legacy_code", "type": "text" }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """));

        var blocked = await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "products",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """));

        Assert.False(blocked.Applied);
        Assert.True(blocked.RequiresConfirmation);
        Assert.NotNull(blocked.Plan);
        Assert.True(blocked.Plan!.IsDestructive);

        await using (var dataConn = await env.OpenDataAsync())
        {
            Assert.Contains("legacy_code", await GetColumnNamesAsync(dataConn, "products"));
        }

        await using (var systemConn = await env.OpenSystemAsync())
        {
            Assert.Equal(1, await CountRowsAsync(systemConn, "migrations"));
        }

        var confirmed = await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "products",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """), confirmDestructive: true);

        Assert.True(confirmed.Applied);

        await using var verifyConn = await env.OpenDataAsync();
        Assert.DoesNotContain("legacy_code", await GetColumnNamesAsync(verifyConn, "products"));
    }

    [Fact]
    public async Task MigrationRunner_FailedMigration_RollsBackTransactionAndHistory()
    {
        using var env = new SchemaTestEnvironment();
        await env.InitializeAsync();

        var plan = new MigrationPlan
        {
            SqlUp =
            [
                """CREATE TABLE "broken" ("id" INTEGER PRIMARY KEY AUTOINCREMENT);""",
                "THIS IS NOT VALID SQL;"
            ],
            SqlDown =
            [
                """DROP TABLE "broken";"""
            ],
            IsDestructive = false,
            Changes = []
        };

        await Assert.ThrowsAsync<SqliteException>(() => env.MigrationRunner.ApplyAsync(
            new ModelJson { Version = 0 },
            Parse("""
                {
                  "version": 1,
                  "tables": [
                    {
                      "name": "broken",
                      "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true, "nullable": false }
                      ],
                      "indexes": []
                    }
                  ],
                  "relations": []
                }
                """),
            plan));

        await using var dataConn = await env.OpenDataAsync();
        Assert.False(await TableExistsAsync(dataConn, "broken"));

        await using var systemConn = await env.OpenSystemAsync();
        Assert.Equal(0, await CountRowsAsync(systemConn, "schema_versions"));
        Assert.Equal(0, await CountRowsAsync(systemConn, "migrations"));
    }

    [Fact]
    public async Task MigrationRunner_RollbackAsync_CanRollbackMultipleSteps()
    {
        using var env = new SchemaTestEnvironment();
        await env.InitializeAsync();

        await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "items",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """));

        await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "items",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false },
                    { "name": "sku", "type": "text" }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """));

        await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 3,
              "tables": [
                {
                  "name": "items",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false },
                    { "name": "sku", "type": "text" },
                    { "name": "price", "type": "float" }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """));

        var rollbacks = await env.SchemaService.RollbackAsync(2);

        Assert.Equal(2, rollbacks.Count);

        var current = await env.ModelJsonStore.GetCurrentAsync();
        Assert.NotNull(current);
        Assert.Equal(1, current!.Version);

        await using var dataConn = await env.OpenDataAsync();
        Assert.Equal(["id", "name"], await GetColumnNamesAsync(dataConn, "items"));
    }

    [Fact]
    public async Task SchemaAutoMigrationService_StartAsync_AppliesConfiguredSchema()
    {
        using var env = new SchemaTestEnvironment();
        await env.InitializeAsync();

        var schemaDirectory = Path.Combine(env.ContentRootPath, "schema");
        Directory.CreateDirectory(schemaDirectory);
        var schemaPath = Path.Combine(schemaDirectory, "model.json");

        await File.WriteAllTextAsync(schemaPath, ModelJsonSerializer.Serialize(Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "notes",
                  "columns": [
                    { "name": "body", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """)));

        var autoMigration = new SchemaAutoMigrationService(
            new SchemaRuntimeOptions
            {
                AutoMigrateOnStartup = true,
                ModelJsonPath = "./schema/model.json"
            },
            env.SchemaService,
            new TestHostEnvironment { ContentRootPath = env.ContentRootPath },
            NullLogger<SchemaAutoMigrationService>.Instance);

        await autoMigration.StartAsync(CancellationToken.None);

        var current = await env.ModelJsonStore.GetCurrentAsync();
        Assert.NotNull(current);
        Assert.Equal(1, current!.Version);

        await using var dataConn = await env.OpenDataAsync();
        Assert.True(await TableExistsAsync(dataConn, "notes"));
    }

    private static ModelJson Parse(string json) => ModelJsonSerializer.Parse(json);

    private static async Task<int> CountRowsAsync(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\";";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name = $name;
            """;
        cmd.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection conn, string indexName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'index'
              AND name = $name;
            """;
        cmd.Parameters.AddWithValue("$name", indexName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }

    private static async Task<IReadOnlyList<string>> GetColumnNamesAsync(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\");";

        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private sealed class SchemaTestEnvironment : IDisposable
    {
        public string ContentRootPath { get; }
        public DatabaseConfig Config { get; }
        public SystemPool SystemPool { get; }
        public DataPool DataPool { get; }
        public ModelJsonStore ModelJsonStore { get; }
        public MigrationRunner MigrationRunner { get; }
        public SchemaService SchemaService { get; }

        public SchemaTestEnvironment()
        {
            ContentRootPath = Path.Combine(Path.GetTempPath(), "repere-tests", Guid.NewGuid().ToString("N"));

            Config = new DatabaseConfig
            {
                Provider = DatabaseProvider.Sqlite,
                SystemConnection = ContentRootPath,
                DataConnection = ContentRootPath
            };

            SystemPool = new SystemPool(Config, NullLogger<SystemPool>.Instance);
            DataPool = new DataPool(Config, NullLogger<DataPool>.Instance);
            ModelJsonStore = new ModelJsonStore(SystemPool);

            var validator = new SchemaValidator();
            var diff = new SchemaDiffEngine();
            var generator = new MigrationGenerator(Config);
            MigrationRunner = new MigrationRunner(DataPool, SystemPool, ModelJsonStore);
            SchemaService = new SchemaService(ModelJsonStore, validator, diff, generator, MigrationRunner);
        }

        public async Task InitializeAsync()
        {
            var bootstrap = new DatabaseBootstrap(SystemPool, NullLogger<DatabaseBootstrap>.Instance);
            await bootstrap.StartAsync(CancellationToken.None);
        }

        public async Task<SqliteConnection> OpenDataAsync() => (SqliteConnection)await DataPool.GetConnectionAsync();

        public async Task<SqliteConnection> OpenSystemAsync() => (SqliteConnection)await SystemPool.GetConnectionAsync();

        public void Dispose()
        {
            SystemPool.Dispose();
            DataPool.Dispose();

            if (Directory.Exists(ContentRootPath))
            {
                Directory.Delete(ContentRootPath, recursive: true);
            }
        }
    }
}
