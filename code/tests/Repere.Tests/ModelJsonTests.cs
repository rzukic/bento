using Microsoft.Extensions.Logging.Abstractions;
using Repere.Api.Db;
using Repere.Api.Db.Schema;

namespace Repere.Tests;

public class ModelJsonTests
{
    [Fact]
    public void Parse_SampleSchema_DeserializesTablesColumnsRelationsAndIndexes()
    {
        var model = ModelJsonSerializer.Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "displayName": "Customers",
                  "description": "CRM accounts",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false },
                    { "name": "email", "type": "text", "unique": true },
                    { "name": "tier", "type": "enum", "values": ["free", "pro", "enterprise"] }
                  ],
                  "indexes": [
                    { "name": "idx_customers_email", "columns": ["email"], "unique": true }
                  ]
                },
                {
                  "name": "orders",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "customer", "type": "reference", "references": "customers" },
                    { "name": "total", "type": "currency" },
                    { "name": "status", "type": "enum", "values": ["draft", "paid", "shipped"] }
                  ]
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "customer" }
              ]
            }
            """);

        Assert.Equal(1, model.Version);
        Assert.Equal(2, model.Tables.Count);
        Assert.Single(model.Relations);

        var customers = model.Tables[0];
        Assert.Equal("customers", customers.Name);
        Assert.Equal("Customers", customers.DisplayName);
        Assert.Equal(4, customers.Columns.Count);
        Assert.Single(customers.Indexes);
        Assert.Equal(ColumnType.Enum, customers.Columns[3].Type);
        Assert.Equal(["free", "pro", "enterprise"], customers.Columns[3].Values);

        var orders = model.Tables[1];
        Assert.Equal(ColumnType.Reference, orders.Columns[1].Type);
        Assert.Equal("customers", orders.Columns[1].References);
        Assert.Equal(ColumnType.Currency, orders.Columns[2].Type);

        var relation = model.Relations[0];
        Assert.Equal(RelationType.OneToMany, relation.Type);
        Assert.Equal("customers", relation.From);
        Assert.Equal("orders", relation.To);
        Assert.Equal("customer", relation.Column);
    }

    [Fact]
    public void Serialize_RoundTrip_IsStable_AndPreservesUnknownFields()
    {
        const string json = """
            {
              "version": 7,
              "uiColor": "teal",
              "tables": [
                {
                  "name": "projects",
                  "icon": "folder",
                  "columns": [
                    {
                      "name": "title",
                      "type": "text",
                      "nullable": false,
                      "widget": {
                        "kind": "title",
                        "size": "xl"
                      }
                    }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """;

        var firstRoundTrip = ModelJsonSerializer.Serialize(ModelJsonSerializer.Parse(json));
        var secondRoundTrip = ModelJsonSerializer.Serialize(ModelJsonSerializer.Parse(firstRoundTrip));

        Assert.Equal(firstRoundTrip, secondRoundTrip);

        var parsed = ModelJsonSerializer.Parse(firstRoundTrip);
        Assert.NotNull(parsed.AdditionalData);
        Assert.NotNull(parsed.Tables[0].AdditionalData);
        Assert.NotNull(parsed.Tables[0].Columns[0].AdditionalData);
        Assert.Equal("teal", parsed.AdditionalData!["uiColor"].GetString());
        Assert.Equal("folder", parsed.Tables[0].AdditionalData!["icon"].GetString());
        Assert.Equal("title", parsed.Tables[0].Columns[0].AdditionalData!["widget"].GetProperty("kind").GetString());
    }

    [Fact]
    public void Parse_Preserves_DefaultSql_AsUnknownMetadata_WithoutCreatingExecutableDefault()
    {
        var model = ModelJsonSerializer.Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "events",
                  "columns": [
                    {
                      "name": "created_at",
                      "type": "datetime",
                      "defaultSql": "CURRENT_TIMESTAMP"
                    }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var column = Assert.Single(model.Tables[0].Columns);
        Assert.Null(column.InternalDefaultSql);
        Assert.NotNull(column.AdditionalData);
        Assert.Equal("CURRENT_TIMESTAMP", column.AdditionalData!["defaultSql"].GetString());

        var roundTrip = ModelJsonSerializer.Serialize(model);
        Assert.Contains("\"defaultSql\": \"CURRENT_TIMESTAMP\"", roundTrip, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AllColumnTypes_AreSupported()
    {
        var model = ModelJsonSerializer.Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "all_types",
                  "columns": [
                    { "name": "text_col", "type": "text" },
                    { "name": "int_col", "type": "integer" },
                    { "name": "float_col", "type": "float" },
                    { "name": "bool_col", "type": "boolean" },
                    { "name": "date_col", "type": "date" },
                    { "name": "datetime_col", "type": "datetime" },
                    { "name": "json_col", "type": "json" },
                    { "name": "ref_col", "type": "reference", "references": "all_types" },
                    { "name": "computed_col", "type": "computed", "formula": "price * quantity" },
                    { "name": "currency_col", "type": "currency" },
                    { "name": "enum_col", "type": "enum", "values": ["a", "b"] },
                    { "name": "file_col", "type": "file" }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var types = model.Tables[0].Columns.Select(c => c.Type).ToArray();

        Assert.Equal(
        [
            ColumnType.Text,
            ColumnType.Integer,
            ColumnType.Float,
            ColumnType.Boolean,
            ColumnType.Date,
            ColumnType.DateTime,
            ColumnType.Json,
            ColumnType.Reference,
            ColumnType.Computed,
            ColumnType.Currency,
            ColumnType.Enum,
            ColumnType.File
        ], types);
    }

    [Fact]
    public void Parse_Relations_Supports_OneToMany_ManyToMany_AndSelfReference()
    {
        var model = ModelJsonSerializer.Parse("""
            {
              "version": 3,
              "tables": [
                { "name": "employees", "columns": [{ "name": "manager_id", "type": "reference", "references": "employees" }], "indexes": [] },
                { "name": "teams", "columns": [{ "name": "name", "type": "text" }], "indexes": [] },
                { "name": "projects", "columns": [{ "name": "name", "type": "text" }], "indexes": [] }
              ],
              "relations": [
                { "name": "employee_manager", "type": "1:N", "from": "employees", "to": "employees", "column": "manager_id" },
                { "name": "team_projects", "type": "N:M", "from": "teams", "to": "projects" }
              ]
            }
            """);

        Assert.Equal(2, model.Relations.Count);
        Assert.Equal(RelationType.OneToMany, model.Relations[0].Type);
        Assert.Equal("employees", model.Relations[0].From);
        Assert.Equal("employees", model.Relations[0].To);
        Assert.Equal(RelationType.ManyToMany, model.Relations[1].Type);
        Assert.Equal("teams", model.Relations[1].From);
        Assert.Equal("projects", model.Relations[1].To);
    }

    [Fact]
    public void Parse_EmptySchema_IsAllowed()
    {
        var model = ModelJsonSerializer.Parse("""
            {
              "version": 1,
              "tables": [],
              "relations": []
            }
            """);

        Assert.Empty(model.Tables);
        Assert.Empty(model.Relations);
    }

    [Fact]
    public async Task Store_SaveAndRetrieve_PersistsSchemaVersions_AndIncrementsVersion()
    {
        using var env = new TemporarySchemaStoreEnvironment();

        var first = await env.Store.SaveAsync(ModelJsonSerializer.Parse("""
            {
              "version": 0,
              "tables": [
                {
                  "name": "customers",
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

        var second = await env.Store.SaveAsync(ModelJsonSerializer.Parse("""
            {
              "version": 0,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text", "nullable": false },
                    { "name": "email", "type": "text", "unique": true }
                  ],
                  "indexes": [
                    { "name": "idx_customers_email", "columns": ["email"], "unique": true }
                  ]
                }
              ],
              "relations": []
            }
            """));

        var current = await env.Store.GetCurrentAsync();
        var versionOne = await env.Store.GetVersionAsync(1);

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.NotNull(current);
        Assert.NotNull(versionOne);
        Assert.Equal(2, current!.Version);
        Assert.Equal(3, current.Tables[0].Columns.Count);
        Assert.Equal(1, versionOne!.Version);
        Assert.Equal(2, versionOne.Tables[0].Columns.Count);
    }

    [Fact]
    public async Task Store_ConcurrentSaves_AllocateUniqueVersions()
    {
        using var env = new TemporarySchemaStoreEnvironment();

        var saves = Enumerable.Range(0, 8)
            .Select(i => env.Store.SaveAsync(ModelJsonSerializer.Parse($$"""
                {
                  "version": 0,
                  "tables": [
                    {
                      "name": "table_{{i}}",
                      "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true, "nullable": false }
                      ],
                      "indexes": []
                    }
                  ],
                  "relations": []
                }
                """)))
            .ToArray();

        var results = await Task.WhenAll(saves);
        var versions = results.Select(x => x.Version).OrderBy(x => x).ToArray();

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], versions);

        var current = await env.Store.GetCurrentAsync();
        Assert.NotNull(current);
        Assert.Equal(8, current!.Version);
    }

    private sealed class TemporarySchemaStoreEnvironment : IDisposable
    {
        private readonly string _rootPath;

        public SystemPool SystemPool { get; }
        public ModelJsonStore Store { get; }

        public TemporarySchemaStoreEnvironment()
        {
            _rootPath = Path.Combine(Path.GetTempPath(), "repere-schema-tests", Guid.NewGuid().ToString("N"));

            var config = new DatabaseConfig
            {
                Provider = DatabaseProvider.Sqlite,
                SystemConnection = _rootPath,
                DataConnection = _rootPath
            };

            SystemPool = new SystemPool(config, NullLogger<SystemPool>.Instance);

            var bootstrap = new DatabaseBootstrap(SystemPool, NullLogger<DatabaseBootstrap>.Instance);
            bootstrap.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            Store = new ModelJsonStore(SystemPool);
        }

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
