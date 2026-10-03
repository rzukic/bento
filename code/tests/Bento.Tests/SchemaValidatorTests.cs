using Bento.Api.Db;
using Bento.Api.Db.Schema;

namespace Bento.Tests;

public class SchemaValidatorTests
{
    private readonly SchemaValidator _validator = new();

    [Fact]
    public void Validate_AutoAddsIdPrimaryKey_WhenMissing()
    {
        var model = ModelJsonSerializer.Parse("""
            {
              "version": 1,
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

        var result = _validator.Validate(model);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal("id", result.NormalizedModel.Tables[0].Columns[0].Name);
        Assert.Equal(ColumnType.Integer, result.NormalizedModel.Tables[0].Columns[0].Type);
        Assert.True(result.NormalizedModel.Tables[0].Columns[0].PrimaryKey);
        Assert.False(result.NormalizedModel.Tables[0].Columns[0].Nullable);
    }

    [Fact]
    public void Validate_RejectsMissingFields_AndDuplicateNames()
    {
        var model = ModelJsonSerializer.Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "",
                  "columns": [
                    { "name": "", "type": "text" },
                    { "name": "sku" },
                    { "name": "sku", "type": "text" }
                  ],
                  "indexes": []
                },
                {
                  "name": "products",
                  "columns": [],
                  "indexes": []
                },
                {
                  "name": "products",
                  "columns": [],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var result = _validator.Validate(model);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "missing_table_name");
        Assert.Contains(result.Errors, e => e.Code == "missing_column_name");
        Assert.Contains(result.Errors, e => e.Code == "missing_column_type");
        Assert.Contains(result.Errors, e => e.Code == "duplicate_column_name");
        Assert.Contains(result.Errors, e => e.Code == "duplicate_table_name");
    }

    [Fact]
    public void Validate_RejectsInvalidEnumReferenceComputedAndRelationDefinitions()
    {
        var model = ModelJsonSerializer.Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "orders",
                  "columns": [
                    { "name": "status", "type": "enum", "values": [] },
                    { "name": "customer_id", "type": "reference", "references": "customers" },
                    { "name": "total_with_tax", "type": "computed" }
                  ],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "missing_fk" },
                { "name": "project_tags", "type": "N:M", "from": "projects", "to": "tags" }
              ]
            }
            """);

        var result = _validator.Validate(model);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "enum_values_required");
        Assert.Contains(result.Errors, e => e.Code == "reference_target_missing");
        Assert.Contains(result.Errors, e => e.Code == "computed_formula_required");
        Assert.Contains(result.Errors, e => e.Code == "relation_from_missing");
        Assert.Contains(result.Errors, e => e.Code == "relation_to_missing");
        Assert.Contains(result.Errors, e => e.Code == "relation_column_missing");
    }

    [Fact]
    public void Validate_RejectsRelationColumnMismatch_AndDuplicateManyToManyPairs()
    {
        var model = ModelJsonSerializer.Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "columns": [],
                  "indexes": []
                },
                {
                  "name": "orders",
                  "columns": [
                    { "name": "customer_id", "type": "text" }
                  ],
                  "indexes": []
                },
                {
                  "name": "products",
                  "columns": [],
                  "indexes": []
                },
                {
                  "name": "tags",
                  "columns": [],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "customer_id" },
                { "name": "product_tags_primary", "type": "N:M", "from": "products", "to": "tags" },
                { "name": "product_tags_secondary", "type": "N:M", "from": "tags", "to": "products" }
              ]
            }
            """);

        var result = _validator.Validate(model);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "relation_column_not_reference");
        Assert.Contains(result.Errors, e => e.Code == "relation_reference_mismatch");
        Assert.Contains(result.Errors, e => e.Code == "duplicate_many_to_many_pair");
    }

    [Fact]
    public void Validate_RejectsRelationAccessorCollisions_OnSameTable()
    {
        var model = ModelJsonSerializer.Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "columns": [],
                  "indexes": []
                },
                {
                  "name": "orders",
                  "columns": [
                    { "name": "billing_customer_id", "type": "reference", "references": "customers" },
                    { "name": "shipping_customer_id", "type": "reference", "references": "customers" }
                  ],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "billing_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "billing_customer_id", "toAccessor": "customer" },
                { "name": "shipping_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "shipping_customer_id", "toAccessor": "customer" }
              ]
            }
            """);

        var result = _validator.Validate(model);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "relation_accessor_collision");
    }

    [Fact]
    public void Validate_AllowsSelfReferentialManyToMany_WithDistinctEffectiveColumns()
    {
        var model = ModelJsonSerializer.Parse("""
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

        var result = _validator.Validate(model);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Errors, e => e.Code == "many_to_many_junction_column_collision");
    }

    [Fact]
    public void Validate_RejectsSelfReferentialManyToMany_WithDuplicateExplicitJunctionColumns()
    {
        var model = ModelJsonSerializer.Parse("""
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
                { "name": "employee_peers", "type": "N:M", "from": "employees", "to": "employees", "fromColumn": "employee_id", "toColumn": "employee_id" }
              ]
            }
            """);

        var result = _validator.Validate(model);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "many_to_many_junction_column_collision");
    }

    [Fact]
    public void Validate_AllowsSelfReference_ButRejectsCircularDependencies()
    {
        var selfReferenceModel = ModelJsonSerializer.Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "employees",
                  "columns": [
                    { "name": "manager_id", "type": "reference", "references": "employees" }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var selfReferenceResult = _validator.Validate(selfReferenceModel);

        Assert.True(selfReferenceResult.IsValid);
        Assert.DoesNotContain(selfReferenceResult.Errors, e => e.Code == "circular_dependency");

        var cycleModel = ModelJsonSerializer.Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "address_id", "type": "reference", "references": "addresses" }
                  ],
                  "indexes": []
                },
                {
                  "name": "addresses",
                  "columns": [
                    { "name": "customer_id", "type": "reference", "references": "customers" }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var cycleResult = _validator.Validate(cycleModel);

        Assert.False(cycleResult.IsValid);
        Assert.Contains(cycleResult.Errors, e => e.Code == "circular_dependency");
    }

    [Fact]
    public void Validate_ReturnsWarnings_ForReservedWords_LongNames_AndIdOnlyTables()
    {
        var longName = new string('a', 64);
        var model = ModelJsonSerializer.Parse($$"""
            {
              "version": 1,
              "tables": [
                {
                  "name": "reports",
                  "columns": [
                    { "name": "select", "type": "text" },
                    { "name": "{{longName}}", "type": "text" }
                  ],
                  "indexes": []
                },
                {
                  "name": "{{longName}}",
                  "columns": [],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var result = _validator.Validate(model);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Code == "reserved_column_name");
        Assert.Contains(result.Warnings, w => w.Code == "long_column_name");
        Assert.Contains(result.Warnings, w => w.Code == "long_table_name");
        Assert.Contains(result.Warnings, w => w.Code == "id_only_table");
    }

    [Fact]
    public void Validate_RejectsIdColumnWithoutPrimaryKey()
    {
        var model = ModelJsonSerializer.Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer" },
                    { "name": "name", "type": "text" }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var result = _validator.Validate(model);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "missing_primary_key");
    }

    [Fact]
    public void Validate_RejectsMultiplePrimaryKeys()
    {
        var model = ModelJsonSerializer.Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                    { "name": "account_number", "type": "text", "primaryKey": true, "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var result = _validator.Validate(model);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e => e.Code == "multiple_primary_keys");
        Assert.Equal("tables[0].columns", error.Path);
    }

    [Fact]
    public void Validate_AllowsExplicitManyToManyJunctionTableWithCustomNameAndCompositePrimaryKey()
    {
        var model = ModelJsonSerializer.Parse("""
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
                  "name": "orders",
                  "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true, "nullable": false }
                  ],
                  "indexes": []
                },
                {
                  "name": "customer_order_links",
                  "columns": [
                    { "name": "customer_id", "type": "reference", "references": "customers", "primaryKey": true, "nullable": false },
                    { "name": "order_id", "type": "reference", "references": "orders", "primaryKey": true, "nullable": false },
                    { "name": "role", "type": "text" }
                  ],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "N:M", "from": "customers", "to": "orders", "fromColumn": "customer_id", "toColumn": "order_id", "junctionTable": "customer_order_links" }
              ]
            }
            """);

        var result = _validator.Validate(model);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Errors, error => error.Code == "multiple_primary_keys");
    }

    [Fact]
    public void Validate_RejectsPrimaryKeyMovedAwayFromId()
    {
        var model = ModelJsonSerializer.Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "integer", "nullable": false },
                    { "name": "account_number", "type": "text", "primaryKey": true, "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var result = _validator.Validate(model);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e => e.Code == "primary_key_must_be_id");
        Assert.Equal("tables[0].columns[1].primaryKey", error.Path);
    }

    [Fact]
    public void Validate_AcceptsUuidIdPrimaryKey()
    {
        var model = ModelJsonSerializer.Parse("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "customers",
                  "columns": [
                    { "name": "id", "type": "uuid", "primaryKey": true, "nullable": false },
                    { "name": "name", "type": "text" }
                  ],
                  "indexes": []
                }
              ],
              "relations": []
            }
            """);

        var result = _validator.Validate(model);

        Assert.True(result.IsValid);
    }
}
