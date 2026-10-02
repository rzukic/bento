using System.Text.Json;
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
    public async Task SchemaService_ApplySchema_RemovesRenameHintsFromPersistedModel()
    {
        using var env = new SchemaTestEnvironment();
        await env.InitializeAsync();

        await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "full_name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """));

        var result = await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "accounts",
                  "renamedFrom": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "renamedFrom": "full_name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """));

        Assert.True(result.Applied);
        Assert.Contains(result.Changes, change => change.ChangeType == SchemaChangeType.RenameTable);
        Assert.Contains(result.Changes, change => change.ChangeType == SchemaChangeType.RenameColumn);
        Assert.Null(result.TargetModel.Tables[0].RenamedFrom);
        Assert.Null(result.TargetModel.Tables[0].Columns.Single(column => column.Name == "name").RenamedFrom);

        var current = await env.ModelJsonStore.GetCurrentAsync();
        Assert.NotNull(current);
        Assert.Equal("accounts", current!.Tables[0].Name);
        Assert.Null(current.Tables[0].RenamedFrom);
        Assert.Null(current.Tables[0].Columns.Single(column => column.Name == "name").RenamedFrom);

        await using var dataConn = await env.OpenDataAsync();
        Assert.True(await TableExistsAsync(dataConn, "accounts"));
        Assert.Equal(["id", "name"], await GetColumnNamesAsync(dataConn, "accounts"));
    }

    [Fact]
    public async Task SchemaService_ApplySchema_MigratesSavedViewsForTableAndColumnRenames()
    {
        using var env = new SchemaTestEnvironment();
        await env.InitializeAsync();

        await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "full_name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """));

        await using (var systemConn = await env.OpenSystemAsync())
        {
            var userId = await InsertSystemUserAsync(systemConn);
            using var view = systemConn.CreateCommand();
            view.CommandText = """
                INSERT INTO system_views
                    (name, table_name, owner_user_id, type, config, is_shared)
                VALUES
                    ($name, $table, $owner, $type, $config, 0);
                """;
            view.Parameters.AddWithValue("$name", "Customer Board");
            view.Parameters.AddWithValue("$table", "customers");
            view.Parameters.AddWithValue("$owner", userId);
            view.Parameters.AddWithValue("$type", "kanban");
            view.Parameters.AddWithValue("$config", """
                {
                  "type": "kanban",
                  "search": "",
                  "sort": [{ "column": "full_name", "dir": "asc" }],
                  "filters": [{ "column": "full_name", "op": "like", "value": "Acme" }],
                  "hiddenColumnNames": ["full_name"],
                  "columnOrder": ["id", "full_name"],
                  "kanbanColumn": "full_name",
                  "cardFieldNames": ["full_name"],
                  "hiddenLaneValues": []
                }
                """);
            await view.ExecuteNonQueryAsync();

            using var preferences = systemConn.CreateCommand();
            preferences.CommandText = """
                INSERT INTO user_table_explorer_preferences
                    (user_id, table_name, default_view_id, auto_save)
                VALUES
                    ($owner, 'customers', 1, 1);
                """;
            preferences.Parameters.AddWithValue("$owner", userId);
            await preferences.ExecuteNonQueryAsync();
        }

        var result = await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "accounts",
                  "renamedFrom": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "renamedFrom": "full_name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """));

        Assert.True(result.Applied);
        Assert.Contains(result.Changes, change => change.ChangeType == SchemaChangeType.RenameTable);
        Assert.Contains(result.Changes, change => change.ChangeType == SchemaChangeType.RenameColumn);

        await using var verifyConn = await env.OpenSystemAsync();
        using var viewQuery = verifyConn.CreateCommand();
        viewQuery.CommandText = "SELECT table_name, config FROM system_views WHERE name = 'Customer Board';";
        await using var reader = await viewQuery.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("accounts", reader.GetString(0));

        using var configDocument = JsonDocument.Parse(reader.GetString(1));
        var config = configDocument.RootElement;
        Assert.Equal("name", config.GetProperty("sort")[0].GetProperty("column").GetString());
        Assert.Equal("name", config.GetProperty("filters")[0].GetProperty("column").GetString());
        Assert.Equal("name", config.GetProperty("hiddenColumnNames")[0].GetString());
        Assert.Equal("name", config.GetProperty("columnOrder")[1].GetString());
        Assert.Equal("name", config.GetProperty("kanbanColumn").GetString());
        Assert.Equal("name", config.GetProperty("cardFieldNames")[0].GetString());

        using var preferencesQuery = verifyConn.CreateCommand();
        preferencesQuery.CommandText = "SELECT table_name FROM user_table_explorer_preferences WHERE user_id = 1;";
        Assert.Equal("accounts", await preferencesQuery.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SchemaService_ApplySchema_CascadesTableRenameHintsToReferencesAndRelations()
    {
        using var env = new SchemaTestEnvironment();
        await env.InitializeAsync();

        await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                },
                {
                  "name": "orders",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "customer_id", "type": "reference", "references": "customers", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "customer_id" }
              ]
            }
            """));

        var result = await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "accounts",
                  "renamedFrom": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                },
                {
                  "name": "orders",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "customer_id", "type": "reference", "references": "customers", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "customer_id" }
              ]
            }
            """));

        Assert.True(result.Applied);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Changes, change => change.ChangeType == SchemaChangeType.RenameTable);

        var current = await env.ModelJsonStore.GetCurrentAsync();
        Assert.NotNull(current);
        Assert.Equal("accounts", current!.Tables[0].Name);
        Assert.Equal(
            "accounts",
            current.Tables.Single(table => table.Name == "orders")
                .Columns.Single(column => column.Name == "customer_id")
                .References);
        Assert.Equal("accounts", current.Relations.Single().From);
        Assert.Equal("orders", current.Relations.Single().To);
        Assert.Null(current.Tables[0].RenamedFrom);

        await using var dataConn = await env.OpenDataAsync();
        Assert.True(await TableExistsAsync(dataConn, "accounts"));
        Assert.True(await TableExistsAsync(dataConn, "orders"));
    }

    [Fact]
    public async Task SchemaService_PreviewSchema_BlocksPrimaryKeyColumnRenames()
    {
        using var env = new SchemaTestEnvironment();
        await env.InitializeAsync();

        await ApplyReferencedCustomerOrderSchemaAsync(env);

        var preview = await env.SchemaService.PreviewSchemaAsync(Parse("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "account_id", "renamedFrom": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                },
                {
                  "name": "orders",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "customer_id", "type": "reference", "references": "customers", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "customer_id" }
              ]
            }
            """));

        var error = Assert.Single(preview.Errors);
        Assert.Equal("primary_key_rename_unsupported", error.Code);
        Assert.Equal("tables[0].columns[0].name", error.Path);
        Assert.Contains("Keep the primary key column named 'id'", error.Message);
        Assert.Null(preview.Plan);
        Assert.Contains(preview.Changes, change => change.ChangeType == SchemaChangeType.RenameColumn);
    }

    [Fact]
    public async Task SchemaService_PreviewSchema_BlocksPrimaryKeyFlagChangesEvenWhenDiffWouldBeEmpty()
    {
        using var env = new SchemaTestEnvironment();
        await env.InitializeAsync();

        await ApplyReferencedCustomerOrderSchemaAsync(env);

        var preview = await env.SchemaService.PreviewSchemaAsync(Parse("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "nullable": false },
                    { "name": "name", "type": "text", "primaryKey": true, "nullable": false }
                  ],
                  "indexes": []
                },
                {
                  "name": "orders",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "customer_id", "type": "reference", "references": "customers", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "customer_id" }
              ]
            }
            """));

        var error = Assert.Single(preview.Errors);
        Assert.Equal("primary_key_must_be_id", error.Code);
        Assert.Equal("tables[0].columns[1].primaryKey", error.Path);
        Assert.Empty(preview.Changes);
        Assert.Null(preview.Plan);
    }

    [Fact]
    public async Task SchemaService_PreviewSchema_BlocksMultiplePrimaryKeys()
    {
        using var env = new SchemaTestEnvironment();
        await env.InitializeAsync();

        await ApplyReferencedCustomerOrderSchemaAsync(env);

        var preview = await env.SchemaService.PreviewSchemaAsync(Parse("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "primaryKey": true, "nullable": false }
                  ],
                  "indexes": []
                },
                {
                  "name": "orders",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "customer_id", "type": "reference", "references": "customers", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "customer_id" }
              ]
            }
            """));

        var error = Assert.Single(preview.Errors);
        Assert.Equal("multiple_primary_keys", error.Code);
        Assert.Equal("tables[0].columns", error.Path);
        Assert.Empty(preview.Changes);
        Assert.Null(preview.Plan);
    }

    [Fact]
    public async Task SchemaService_ApplySchema_BlocksPrimaryKeyColumnRenamesAndLeavesSchemaUntouched()
    {
        using var env = new SchemaTestEnvironment();
        await env.InitializeAsync();

        await ApplyReferencedCustomerOrderSchemaAsync(env);

        var result = await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "account_id", "renamedFrom": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                },
                {
                  "name": "orders",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "customer_id", "type": "reference", "references": "customers", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "customer_id" }
              ]
            }
            """));

        var error = Assert.Single(result.Errors);
        Assert.False(result.Applied);
        Assert.Equal("primary_key_rename_unsupported", error.Code);
        Assert.Null(result.Plan);

        await using var dataConn = await env.OpenDataAsync();
        Assert.Equal(["id", "name"], await GetColumnNamesAsync(dataConn, "customers"));

        await using var systemConn = await env.OpenSystemAsync();
        Assert.Equal(1, await CountRowsAsync(systemConn, "migrations"));
    }

    [Fact]
    public async Task SchemaService_ApplySchema_RenamesChildReferenceColumnAndPreservesValues()
    {
        using var env = new SchemaTestEnvironment();
        await env.InitializeAsync();

        await ApplyReferencedCustomerOrderSchemaAsync(env);

        await using (var dataConn = await env.OpenDataAsync())
        {
            using var insertCustomer = dataConn.CreateCommand();
            insertCustomer.CommandText = """INSERT INTO "customers" ("name") VALUES ('Acme');""";
            await insertCustomer.ExecuteNonQueryAsync();

            using var insertOrder = dataConn.CreateCommand();
            insertOrder.CommandText = """INSERT INTO "orders" ("customer_id") VALUES (1);""";
            await insertOrder.ExecuteNonQueryAsync();
        }

        var result = await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                },
                {
                  "name": "orders",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "account_id", "renamedFrom": "customer_id", "type": "reference", "references": "customers", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "account_id" }
              ]
            }
            """));

        Assert.True(result.Applied);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Changes, change => change.ChangeType == SchemaChangeType.RenameColumn && change.PreviousColumnName == "customer_id" && change.ColumnName == "account_id");

        await using var verifyConn = await env.OpenDataAsync();
        Assert.Equal(["id", "account_id"], await GetColumnNamesAsync(verifyConn, "orders"));

        using var query = verifyConn.CreateCommand();
        query.CommandText = """SELECT "account_id" FROM "orders" LIMIT 1;""";
        Assert.Equal(1L, (long)(await query.ExecuteScalarAsync() ?? 0L));
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
    public async Task SchemaService_ApplySchema_RebuildsReferencedParentTable_WhileChildRowsExist()
    {
        using var env = new SchemaTestEnvironment();
        await env.InitializeAsync();

        await ApplyReferencedCustomerOrderSchemaAsync(env);

        await using (var dataConn = await env.OpenDataAsync())
        {
            using var insertCustomer = dataConn.CreateCommand();
            insertCustomer.CommandText = """INSERT INTO "customers" ("name") VALUES ('Acme'), ('Globex');""";
            await insertCustomer.ExecuteNonQueryAsync();

            using var insertOrder = dataConn.CreateCommand();
            insertOrder.CommandText = """INSERT INTO "orders" ("customer_id") VALUES (1), (2), (2);""";
            await insertOrder.ExecuteNonQueryAsync();
        }

        // Making customers.name unique forces a SQLite table rebuild of the
        // parent table (create temp, copy, DROP old, rename). With foreign keys
        // enforced the DROP used to fail because orders still reference it.
        var result = await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false, "unique": true }
                  ],
                  "indexes": []
                },
                {
                  "name": "orders",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "customer_id", "type": "reference", "references": "customers", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "customer_id" }
              ]
            }
            """));

        Assert.True(result.Applied, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Contains(result.Changes, change => change.ChangeType == SchemaChangeType.AlterColumnUnique);

        await using var verifyConn = await env.OpenDataAsync();
        Assert.Equal(2, await CountRowsAsync(verifyConn, "customers"));
        Assert.Equal(3, await CountRowsAsync(verifyConn, "orders"));

        using var orphanCheck = verifyConn.CreateCommand();
        orphanCheck.CommandText = "PRAGMA foreign_key_check;";
        await using (var reader = await orphanCheck.ExecuteReaderAsync())
        {
            Assert.False(await reader.ReadAsync(), "foreign_key_check reported violations after the rebuild");
        }

        using var joined = verifyConn.CreateCommand();
        joined.CommandText = """SELECT COUNT(*) FROM "orders" o JOIN "customers" c ON c."id" = o."customer_id";""";
        Assert.Equal(3L, (long)(await joined.ExecuteScalarAsync() ?? 0L));

        // Enforcement is back on for regular connections: an orphan insert must fail.
        using var orphanInsert = verifyConn.CreateCommand();
        orphanInsert.CommandText = """INSERT INTO "orders" ("customer_id") VALUES (999);""";
        await Assert.ThrowsAsync<SqliteException>(() => orphanInsert.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task MigrationRunner_Rollback_WhenPreviousVersionIsMissing_LeavesDataSchemaUntouched()
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

        // Simulate a corrupted history: the version we would roll back to is gone.
        await using (var systemConn = await env.OpenSystemAsync())
        {
            await using var delete = systemConn.CreateCommand();
            delete.CommandText = "DELETE FROM schema_versions WHERE version = 1;";
            await delete.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => env.SchemaService.RollbackLastAsync());
        Assert.Contains("schema version 1 no longer exists", error.Message);

        // The failure must happen before any down SQL ran, so data schema and
        // Model JSON still agree on version 2.
        var current = await env.ModelJsonStore.GetCurrentAsync();
        Assert.NotNull(current);
        Assert.Equal(2, current!.Version);

        await using var dataConn = await env.OpenDataAsync();
        Assert.Equal(["id", "name", "sku"], await GetColumnNamesAsync(dataConn, "items"));
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

    private static async Task ApplyReferencedCustomerOrderSchemaAsync(SchemaTestEnvironment env)
    {
        await env.SchemaService.ApplySchemaAsync(Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                },
                {
                  "name": "orders",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "customer_id", "type": "reference", "references": "customers", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "customer_id" }
              ]
            }
            """));
    }

    private static async Task<int> CountRowsAsync(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\";";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<int> InsertSystemUserAsync(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (username, password_hash, role)
            VALUES ('saved-view-user', 'hash', 'admin');
            SELECT last_insert_rowid();
            """;
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
            SchemaService = new SchemaService(ModelJsonStore, validator, diff, generator, MigrationRunner, DataPool);
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
