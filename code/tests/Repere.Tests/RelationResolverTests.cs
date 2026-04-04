using Repere.Api.Db.Schema;

namespace Repere.Tests;

public class RelationResolverTests
{
    private readonly SchemaValidator _validator = new();
    private readonly RelationResolver _resolver = new();

    [Fact]
    public void Resolve_OrdersTablesByReferenceDependencies()
    {
        var model = Normalize("""
            {
              "version": 1,
              "tables": [
                {
                  "name": "orders",
                  "columns": [
                    { "name": "customer_id", "type": "reference", "references": "customers", "nullable": false }
                  ],
                  "indexes": []
                },
                {
                  "name": "customers",
                  "columns": [
                    { "name": "name", "type": "text", "nullable": false }
                  ],
                  "indexes": []
                }
              ],
              "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "customer_id" }
              ]
            }
            """);

        var result = _resolver.Resolve(model);

        Assert.Equal(["customers", "orders"], result.OrderedTables.Select(t => t.Name).ToArray());

        var relation = Assert.Single(result.Relations);
        Assert.Equal("customer", relation.ForwardAccessor);
        Assert.Equal("customer_orders", relation.ReverseAccessor);
        Assert.Equal("customer_id", relation.ForeignKeyColumn);
        Assert.False(relation.RequiresDeferredConstraint);
    }

    [Fact]
    public void Resolve_ManyToMany_GeneratesAlphabeticalJunctionTable()
    {
        var model = Normalize("""
            {
              "version": 1,
              "tables": [
                { "name": "tags", "columns": [], "indexes": [] },
                { "name": "products", "columns": [], "indexes": [] }
              ],
              "relations": [
                { "name": "product_tags", "type": "N:M", "from": "products", "to": "tags" }
              ]
            }
            """);

        var result = _resolver.Resolve(model);

        var relation = Assert.Single(result.Relations);
        Assert.Equal("_jt_products_tags", relation.JunctionTableName);
        Assert.NotNull(relation.JunctionTable);
        Assert.Equal(["product_id", "tag_id", "created_at"], relation.JunctionTable!.Columns.Select(c => c.Name).ToArray());
        Assert.Equal(2, relation.JunctionTable.Columns.Count(c => c.PrimaryKey));
    }

    [Fact]
    public void Resolve_SelfReference_DoesNotCreateCycleError_ButFlagsDeferredConstraint()
    {
        var model = Normalize("""
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
              "relations": [
                { "name": "manager_reports", "type": "1:N", "from": "employees", "to": "employees", "column": "manager_id" }
              ]
            }
            """);

        var result = _resolver.Resolve(model);

        Assert.Empty(result.Cycles);
        var relation = Assert.Single(result.Relations);
        Assert.True(relation.IsSelfReferential);
        Assert.True(relation.RequiresDeferredConstraint);
    }

    [Fact]
    public void Resolve_CircularDependencies_AreReported()
    {
        var model = ModelJsonSerializer.Parse("""
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

        var result = _resolver.Resolve(model);

        Assert.NotEmpty(result.Cycles);
        Assert.Contains(result.Cycles, cycle => cycle.SequenceEqual(["customers", "addresses", "customers"], StringComparer.OrdinalIgnoreCase)
            || cycle.SequenceEqual(["addresses", "customers", "addresses"], StringComparer.OrdinalIgnoreCase));
    }

    private ModelJson Normalize(string json)
    {
        var parsed = ModelJsonSerializer.Parse(json);
        var validation = _validator.Validate(parsed);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors.Select(e => e.Message)));
        return validation.NormalizedModel;
    }
}
