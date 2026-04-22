using Repere.Api.Db;
using Repere.Api.Db.Schema;

namespace Repere.Tests;

public class SchemaDiffTests
{
    private readonly SchemaValidator _validator = new();
    private readonly SchemaDiffEngine _diff = new();

    [Fact]
    public void Diff_IdenticalSchemas_ReturnsNoChanges()
    {
        var model = Normalize("""
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
                }
              ],
              "relations": []
            }
            """);

        var changes = _diff.Diff(model, model);

        Assert.Empty(changes);
    }

    [Fact]
    public void Diff_EmptyToSchema_FlagsAllTablesAsAdded()
    {
        var oldModel = Normalize("""
            {
              "version": 1,
              "tables": [],
              "relations": []
            }
            """);

        var newModel = Normalize("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var changes = _diff.Diff(oldModel, newModel);

        var addTable = Assert.Single(changes);
        Assert.Equal(SchemaChangeType.AddTable, addTable.ChangeType);
        Assert.Equal("customers", addTable.TableName);
        Assert.IsType<TableDef>(addTable.NewValue);
    }

    [Fact]
    public void Diff_DetectsTableAndColumnRenames_UsingRenamedFrom()
    {
        var oldModel = Normalize("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "full_name", "type": "text", "nullable": false }
                  ],
                  "indexes": [
                    { "name": "idx_customers_full_name", "columns": ["full_name"], "unique": false }
                  ]
                }
              ],
              "relations": []
            }
            """);

        var newModel = Normalize("""
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
                  "indexes": [
                    { "name": "idx_accounts_name", "renamedFrom": "idx_customers_full_name", "columns": ["name"], "unique": false }
                  ]
                }
              ],
              "relations": []
            }
            """);

        var changes = _diff.Diff(oldModel, newModel).ToList();

        Assert.Contains(changes, c => c.ChangeType == SchemaChangeType.RenameTable && c.PreviousTableName == "customers" && c.TableName == "accounts");
        Assert.Contains(changes, c => c.ChangeType == SchemaChangeType.RenameColumn && c.PreviousColumnName == "full_name" && c.ColumnName == "name" && c.TableName == "accounts");
        Assert.Contains(changes, c => c.ChangeType == SchemaChangeType.RenameIndex && Equals(c.OldValue, "idx_customers_full_name") && Equals(c.NewValue, "idx_accounts_name"));
        Assert.Contains(changes, c => c.ChangeType == SchemaChangeType.AlterIndex && c.IndexName == "idx_accounts_name");
    }

    [Fact]
    public void Diff_DetectsColumnAlterations_AndClassifiesTypeChanges()
    {
        var oldModel = Normalize("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "orders",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "total", "type": "float", "nullable": true, "default": 0.0, "unique": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var newModel = Normalize("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "orders",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "total", "type": "integer", "nullable": false, "default": 1, "unique": true }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var changes = _diff.Diff(oldModel, newModel).ToList();

        var typeChange = Assert.Single(changes.Where(c => c.ChangeType == SchemaChangeType.AlterColumnType));
        Assert.Equal("orders", typeChange.TableName);
        Assert.Equal("total", typeChange.ColumnName);
        Assert.Equal(CoercionKind.Lossy, typeChange.CoercionKind);
        Assert.True(typeChange.IsDestructive);

        Assert.Contains(changes, c => c.ChangeType == SchemaChangeType.AlterColumnNullability && Equals(c.OldValue, true) && Equals(c.NewValue, false));
        Assert.Contains(changes, c => c.ChangeType == SchemaChangeType.AlterColumnDefault && Equals(c.OldValue, "0.0") && Equals(c.NewValue, "1"));
        Assert.Contains(changes, c => c.ChangeType == SchemaChangeType.AlterColumnUnique && Equals(c.OldValue, false) && Equals(c.NewValue, true));
    }

    [Fact]
    public void Diff_DetectsTableAndColumnMetadataChanges()
    {
        var oldModel = Normalize("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "displayName": "Customers",
                  "description": "Original table description",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "displayName": "Name", "description": "Original column description", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var newModel = Normalize("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "customers",
                  "displayName": "Accounts",
                  "description": "Updated table description",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "displayName": "Customer name", "description": "Updated column description", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var changes = _diff.Diff(oldModel, newModel).ToList();

        var tableMetadata = Assert.Single(changes.Where(c => c.ChangeType == SchemaChangeType.AlterTableMetadata));
        Assert.Equal("customers", tableMetadata.TableName);
        Assert.False(tableMetadata.IsDestructive);

        var columnMetadata = Assert.Single(changes.Where(c => c.ChangeType == SchemaChangeType.AlterColumnMetadata));
        Assert.Equal("customers", columnMetadata.TableName);
        Assert.Equal("name", columnMetadata.ColumnName);
        Assert.False(columnMetadata.IsDestructive);
    }

    [Fact]
    public void Diff_DetectsAddsDropsRelationsAndIndexChanges_InOnePass()
    {
        var oldModel = Normalize("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text" },
                    { "name": "legacy_code", "type": "text" }
                  ],
                  "indexes": []
                },
                {
                  "name": "orders",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "customer_id", "type": "reference", "references": "customers" }
                  ],
                  "indexes": [
                    { "name": "idx_orders_customer", "columns": ["customer_id"], "unique": false }
                  ]
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "customer_id" }
              ]
            }
            """);

        var newModel = Normalize("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text" }
                  ],
                  "indexes": []
                },
                {
                  "name": "orders",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "customer_id", "type": "reference", "references": "customers" },
                    { "name": "status", "type": "enum", "values": ["draft", "paid"] }
                  ],
                  "indexes": [
                    { "name": "idx_orders_customer", "columns": ["customer_id"], "unique": true }
                  ]
                },
                {
                  "name": "invoices",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "customer_id", "type": "reference", "references": "customers" }
                  ],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "customer_id" },
                { "name": "customer_invoices", "type": "1:N", "from": "customers", "to": "invoices", "column": "customer_id" }
              ]
            }
            """);

        var changes = _diff.Diff(oldModel, newModel).ToList();

        Assert.Contains(changes, c => c.ChangeType == SchemaChangeType.DropColumn && c.TableName == "customers" && c.ColumnName == "legacy_code" && c.IsDestructive);
        Assert.Contains(changes, c => c.ChangeType == SchemaChangeType.AddColumn && c.TableName == "orders" && c.ColumnName == "status");
        Assert.Contains(changes, c => c.ChangeType == SchemaChangeType.AlterIndex && c.TableName == "orders" && c.IndexName == "idx_orders_customer");
        Assert.Contains(changes, c => c.ChangeType == SchemaChangeType.AddTable && c.TableName == "invoices");
        Assert.Contains(changes, c => c.ChangeType == SchemaChangeType.AddRelation && c.RelationName == "customer_invoices");
    }

    [Fact]
    public void Diff_DetectsRemovedTablesAndRelations_AsDestructiveOnlyForTableDrop()
    {
        var oldModel = Normalize("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false }
                  ],
                  "indexes": []
                },
                {
                  "name": "tags",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "customer_tags", "type": "N:M", "from": "customers", "to": "tags" }
              ]
            }
            """);

        var newModel = Normalize("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "tags",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var changes = _diff.Diff(oldModel, newModel).ToList();

        var dropTable = Assert.Single(changes.Where(c => c.ChangeType == SchemaChangeType.DropTable));
        Assert.True(dropTable.IsDestructive);

        var dropRelation = Assert.Single(changes.Where(c => c.ChangeType == SchemaChangeType.DropRelation));
        Assert.False(dropRelation.IsDestructive);
    }

    [Fact]
    public void Diff_DetectsRelationMetadataChanges_WhenOnlyAccessorsChange()
    {
        var oldModel = Normalize("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "employees",
                  "columns": [],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "employee_peers", "type": "N:M", "from": "employees", "to": "employees" }
              ]
            }
            """);

        var newModel = Normalize("""
            {
              "version": 2,
              "tables": [
                {
                  "name": "employees",
                  "columns": [],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "employee_peers", "type": "N:M", "from": "employees", "to": "employees", "fromAccessor": "mentors", "toAccessor": "mentees" }
              ]
            }
            """);

        var changes = _diff.Diff(oldModel, newModel).ToList();

        var alterRelation = Assert.Single(changes.Where(c => c.ChangeType == SchemaChangeType.AlterRelation));
        Assert.Equal("employee_peers", alterRelation.RelationName);
    }

    private ModelJson Normalize(string json)
    {
        var parsed = ModelJsonSerializer.Parse(json);
        var validation = _validator.Validate(parsed);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors.Select(e => e.Message)));
        return validation.NormalizedModel;
    }
}
