using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Repere.Tests;

public sealed class AdminSchemaTests
{
    private const string SimpleSchema = """
        {
            "tables": [
                {
                    "name": "widgets",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false }
                    ]
                }
            ],
            "relations": []
        }
        """;

    private const string ExtendedSchema = """
        {
            "tables": [
                {
                    "name": "widgets",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false },
                        { "name": "color", "type": "text" }
                    ]
                }
            ],
            "relations": []
        }
        """;

    private const string TextScoreSchema = """
        {
            "tables": [
                {
                    "name": "widgets",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                        { "name": "name", "type": "text", "nullable": false },
                        { "name": "score", "type": "text" }
                    ],
                    "indexes": []
                }
            ],
            "relations": []
        }
        """;

    private const string IntegerScoreSchema = """
        {
            "tables": [
                {
                    "name": "widgets",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                        { "name": "name", "type": "text", "nullable": false },
                        { "name": "score", "type": "integer" }
                    ],
                    "indexes": []
                }
            ],
            "relations": []
        }
        """;

    private const string MissingTableNameSchema = """
        {
            "tables": [
                {
                    "name": "",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true }
                    ]
                }
            ],
            "relations": []
        }
        """;

    private const string ManyToManySchema = """
        {
            "tables": [
                {
                    "name": "products",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false }
                    ]
                },
                {
                    "name": "tags",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "label", "type": "text", "nullable": false }
                    ]
                }
            ],
            "relations": [
                { "name": "product_tags", "type": "N:M", "from": "products", "to": "tags" }
            ]
        }
        """;

    private const string ManyToManySchemaWithResolvedMetadata = """
        {
            "tables": [
                {
                    "name": "products",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false }
                    ]
                },
                {
                    "name": "tags",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "label", "type": "text", "nullable": false }
                    ]
                }
            ],
            "relations": [
                {
                    "name": "product_tags",
                    "type": "N:M",
                    "from": "products",
                    "to": "tags",
                    "resolvedFromAccessor": "tags",
                    "resolvedToAccessor": "products",
                    "resolvedJunctionTableName": "_jt_products_tags"
                }
            ]
        }
        """;

    private const string ManyToManySchemaWithoutRelation = """
        {
            "tables": [
                {
                    "name": "products",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false }
                    ]
                },
                {
                    "name": "tags",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "label", "type": "text", "nullable": false }
                    ]
                }
            ],
            "relations": []
        }
        """;

    private const string ManyToManySchemaWithExplicitJunctionColumns = """
        {
            "tables": [
                {
                    "name": "products",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false }
                    ]
                },
                {
                    "name": "tags",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "label", "type": "text", "nullable": false }
                    ]
                }
            ],
            "relations": [
                { "name": "product_tags", "type": "N:M", "from": "products", "to": "tags", "fromColumn": "product_ref", "toColumn": "tag_ref" }
            ]
        }
        """;

    private const string OneToManySchema = """
        {
            "tables": [
                {
                    "name": "customers",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text" }
                    ]
                },
                {
                    "name": "orders",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "customer_id", "type": "reference", "references": "customers", "nullable": true }
                    ]
                }
            ],
            "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "customer_id" }
            ]
        }
        """;

    private const string OneToManyRetargetedSchema = """
        {
            "tables": [
                {
                    "name": "customers",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text" }
                    ]
                },
                {
                    "name": "orders",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "customer_id", "type": "reference", "references": "customers", "nullable": true },
                        { "name": "buyer_id", "type": "reference", "references": "customers", "nullable": true }
                    ]
                }
            ],
            "relations": [
                { "name": "customer_orders", "type": "1:N", "from": "customers", "to": "orders", "column": "buyer_id" }
            ]
        }
        """;

    private const string OneToManyConvertedToVisibleManyToManySchema = """
        {
            "tables": [
                {
                    "name": "customers",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text" }
                    ]
                },
                {
                    "name": "orders",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true }
                    ]
                },
                {
                    "name": "_jt_customers_orders",
                    "columns": [
                        { "name": "customer_id", "type": "reference", "references": "customers", "primaryKey": true, "nullable": false },
                        { "name": "order_id", "type": "reference", "references": "orders", "primaryKey": true, "nullable": false },
                        { "name": "created_at", "type": "datetime" }
                    ]
                }
            ],
            "relations": [
                { "name": "customer_orders", "type": "N:M", "from": "customers", "to": "orders", "fromColumn": "customer_id", "toColumn": "order_id", "junctionTable": "_jt_customers_orders" }
            ]
        }
        """;

    private const string SchemaWithSecondTable = """
        {
            "tables": [
                {
                    "name": "widgets",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false }
                    ]
                },
                {
                    "name": "gadgets",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "label", "type": "text", "nullable": false }
                    ]
                }
            ],
            "relations": []
        }
        """;

    // ── GET /admin/schema ──

    [Fact]
    public async Task GetSchema_EmptyDatabase_ReturnsEmptyModelJson()
    {
        await using var host = await CreateHostAsync();

        using var response = await host.Client.GetAsync("/api/v1/admin/schema");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("version").GetInt32());
        Assert.Equal(0, json.GetProperty("tables").GetArrayLength());
    }

    [Fact]
    public async Task GetSchema_AfterApply_ReturnsCurrentSchema()
    {
        await using var host = await CreateHostAsync();

        // Apply a schema first
        await host.ApplySchemaAsync(SimpleSchema);

        using var response = await host.Client.GetAsync("/api/v1/admin/schema");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("version").GetInt32() >= 1);
        Assert.Equal(1, json.GetProperty("tables").GetArrayLength());
        Assert.Equal("widgets", json.GetProperty("tables")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetSchema_IncludesResolvedRelationAccessors()
    {
        await using var host = await CreateHostAsync();

        await host.ApplySchemaAsync(ManyToManySchema);

        using var response = await host.Client.GetAsync("/api/v1/admin/schema");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var relation = Assert.Single(json.GetProperty("relations").EnumerateArray());
        Assert.Equal("product_tags", relation.GetProperty("name").GetString());
        Assert.Equal("tags", relation.GetProperty("resolvedFromAccessor").GetString());
        Assert.Equal("products", relation.GetProperty("resolvedToAccessor").GetString());
        Assert.Equal("_jt_products_tags", relation.GetProperty("resolvedJunctionTableName").GetString());
    }

    [Fact]
    public async Task GetSchemaModel_ReturnsCanonicalModelJsonWithoutResolvedRelationMetadata()
    {
        await using var host = await CreateHostAsync();

        await host.ApplySchemaAsync(ManyToManySchema);

        using var response = await host.Client.GetAsync("/api/v1/admin/schema/model");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var relation = Assert.Single(json.GetProperty("relations").EnumerateArray());
        Assert.Equal("product_tags", relation.GetProperty("name").GetString());
        Assert.False(relation.TryGetProperty("resolvedFromAccessor", out _));
        Assert.False(relation.TryGetProperty("resolvedToAccessor", out _));
        Assert.False(relation.TryGetProperty("resolvedJunctionTableName", out _));
    }

    // ── POST /admin/schema/preview ──

    [Fact]
    public async Task PreviewSchema_ValidSchema_ReturnsPlanWithoutMutatingState()
    {
        await using var host = await CreateHostAsync();

        using var response = await host.Client.PostAsync(
            "/api/v1/admin/schema/preview",
            new StringContent(SimpleSchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("isValid").GetBoolean());
        Assert.True(json.GetProperty("hasChanges").GetBoolean());
        Assert.False(json.GetProperty("isDestructive").GetBoolean());
        Assert.False(json.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Equal(0, json.GetProperty("currentVersion").GetInt32());
        Assert.True(json.GetProperty("plan").GetProperty("sqlUp").GetArrayLength() > 0);
        Assert.Equal(1, json.GetProperty("normalizedModel").GetProperty("tables").GetArrayLength());
        Assert.Contains(
            json.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("changeType").GetString() == "AddTable"
                && change.GetProperty("table").GetString() == "widgets");

        using var schemaResponse = await host.Client.GetAsync("/api/v1/admin/schema");
        var schemaJson = await schemaResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, schemaJson.GetProperty("version").GetInt32());
        Assert.Equal(0, schemaJson.GetProperty("tables").GetArrayLength());

        using var migrationsResponse = await host.Client.GetAsync("/api/v1/admin/migrations");
        var migrationsJson = await migrationsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, migrationsJson.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task PutSchema_VisualManyToManyInspectorEditedSchema_AppliesWithValidJunctionReferences()
    {
        await using var host = await CreateHostAsync();
        const string schema = """
            {
                "tables": [
                    {
                        "name": "customers",
                        "columns": [
                            { "name": "id", "type": "integer", "primaryKey": true },
                            { "name": "name", "type": "text" }
                        ]
                    },
                    {
                        "name": "orders",
                        "columns": [
                            { "name": "id", "type": "integer", "primaryKey": true },
                            { "name": "number", "type": "text" }
                        ]
                    },
                    {
                        "name": "customer_order_links",
                        "columns": [
                            { "name": "source_customer_id", "type": "reference", "references": "customers", "primaryKey": true, "nullable": false },
                            { "name": "target_order_id", "type": "reference", "references": "orders", "primaryKey": true, "nullable": false },
                            { "name": "created_at", "type": "datetime" }
                        ]
                    }
                ],
                "relations": [
                    {
                        "name": "customer_orders",
                        "type": "N:M",
                        "from": "customers",
                        "to": "orders",
                        "fromColumn": "source_customer_id",
                        "toColumn": "target_order_id",
                        "junctionTable": "customer_order_links",
                        "fromAccessor": "orders",
                        "toAccessor": "customers"
                    }
                ]
            }
            """;

        using var response = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(schema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("applied").GetBoolean());
        Assert.Equal(
            1L,
            await host.QueryDataScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'customer_order_links';"));
        Assert.Equal(
            1L,
            await host.QueryDataScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('customer_order_links') WHERE name = 'source_customer_id';"));
        Assert.Equal(
            1L,
            await host.QueryDataScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('customer_order_links') WHERE name = 'target_order_id';"));
    }

    [Fact]
    public async Task PreviewSchema_InvalidSchema_ReturnsValidationIssues()
    {
        await using var host = await CreateHostAsync();

        using var response = await host.Client.PostAsync(
            "/api/v1/admin/schema/preview",
            new StringContent(MissingTableNameSchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("isValid").GetBoolean());
        Assert.False(json.GetProperty("hasChanges").GetBoolean());
        Assert.Equal("missing_table_name", json.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.False(json.TryGetProperty("plan", out _));
    }

    [Fact]
    public async Task PreviewSchema_DestructiveChange_ReturnsConfirmationMetadata()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(SimpleSchema);

        var emptySchema = """
            {
                "tables": [],
                "relations": []
            }
            """;

        using var response = await host.Client.PostAsync(
            "/api/v1/admin/schema/preview",
            new StringContent(emptySchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("hasChanges").GetBoolean());
        Assert.True(json.GetProperty("isDestructive").GetBoolean());
        Assert.True(json.GetProperty("requiresConfirmation").GetBoolean());
        Assert.True(json.GetProperty("plan").GetProperty("isDestructive").GetBoolean());
        Assert.True(json.GetProperty("plan").GetProperty("requiresConfirmation").GetBoolean());
        Assert.Contains(
            json.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("changeType").GetString() == "DropTable"
                && change.GetProperty("table").GetString() == "widgets"
                && change.GetProperty("isDestructive").GetBoolean());
    }

    [Fact]
    public async Task PreviewSchema_ManyToManyRelationDrop_ReturnsDestructiveConfirmationMetadata()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(ManyToManySchema);
        await host.ExecuteDataSqlAsync("""
            INSERT INTO "products" ("name") VALUES ('Widget');
            INSERT INTO "tags" ("label") VALUES ('Seasonal');
            INSERT INTO "_jt_products_tags" ("product_id", "tag_id") VALUES (1, 1);
            """);

        using var response = await host.Client.PostAsync(
            "/api/v1/admin/schema/preview",
            new StringContent(ManyToManySchemaWithoutRelation, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("hasChanges").GetBoolean());
        Assert.True(json.GetProperty("isDestructive").GetBoolean());
        Assert.True(json.GetProperty("requiresConfirmation").GetBoolean());
        Assert.True(json.GetProperty("plan").GetProperty("isDestructive").GetBoolean());
        Assert.True(json.GetProperty("plan").GetProperty("requiresConfirmation").GetBoolean());
        // The junction is a table in the model, so dropping the relation
        // without keeping that table drops the table (with its links).
        Assert.Contains(
            json.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("changeType").GetString() == "DropRelation"
                && change.TryGetProperty("relation", out var relation)
                && relation.GetString() == "product_tags");
        var dropChange = json.GetProperty("changes").EnumerateArray().Single(
            change => change.GetProperty("changeType").GetString() == "DropTable"
                && change.GetProperty("table").GetString() == "_jt_products_tags");
        Assert.True(dropChange.GetProperty("isDestructive").GetBoolean());
        var impact = dropChange.GetProperty("impact");
        Assert.Equal("table_drop", impact.GetProperty("kind").GetString());
        Assert.Equal("_jt_products_tags", impact.GetProperty("objectName").GetString());
        Assert.Equal(1L, impact.GetProperty("rowCount").GetInt64());
        Assert.Equal(1, impact.GetProperty("sampleRows").GetArrayLength());
        Assert.Equal(1L, impact.GetProperty("sampleRows")[0].GetProperty("product_id").GetInt64());
        Assert.Equal(1L, impact.GetProperty("sampleRows")[0].GetProperty("tag_id").GetInt64());
        Assert.Contains(
            json.GetProperty("plan").GetProperty("sqlUp").EnumerateArray().Select(statement => statement.GetString()),
            statement => statement is not null && statement.Contains("DROP TABLE \"_jt_products_tags\"", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetSchema_ManyToManyWithoutJunctionTable_ListsTheJunctionTableAndRoundTripsWithoutChanges()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(ManyToManySchema);

        using var getResponse = await host.Client.GetAsync("/api/v1/admin/schema");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var schemaText = await getResponse.Content.ReadAsStringAsync();
        var schema = JsonDocument.Parse(schemaText).RootElement;

        var junction = schema.GetProperty("tables").EnumerateArray().Single(
            table => table.GetProperty("name").GetString() == "_jt_products_tags");
        Assert.Equal(
            ["product_id", "tag_id", "created_at"],
            junction.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToArray());
        var relation = schema.GetProperty("relations").EnumerateArray().Single(
            r => r.GetProperty("name").GetString() == "product_tags");
        Assert.Equal("_jt_products_tags", relation.GetProperty("junctionTable").GetString());
        Assert.Equal("product_id", relation.GetProperty("fromColumn").GetString());
        Assert.Equal("tag_id", relation.GetProperty("toColumn").GetString());

        // Sending the model back (what the designer does) changes nothing.
        using var previewResponse = await host.Client.PostAsync(
            "/api/v1/admin/schema/preview",
            new StringContent(schemaText, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(preview.GetProperty("hasChanges").GetBoolean());

        // The junction rows are reachable like any table; created_at is filled
        // by the database.
        await host.ExecuteDataSqlAsync("""
            INSERT INTO "products" ("name") VALUES ('Widget');
            INSERT INTO "tags" ("label") VALUES ('Seasonal');
            """);
        using var createResponse = await host.Client.PostAsync(
            "/api/v1/data/_jt_products_tags",
            new StringContent("""{ "product_id": 1, "tag_id": 1 }""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(
            1L,
            await host.QueryDataScalarAsync<long>("SELECT COUNT(*) FROM \"_jt_products_tags\" WHERE \"created_at\" IS NOT NULL;"));
    }

    [Fact]
    public async Task PreviewSchema_ManyToManyRelationAlterThatRebuildsJunction_ReturnsDestructiveConfirmationMetadata()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(ManyToManySchema);
        await host.ExecuteDataSqlAsync("""
            INSERT INTO "products" ("name") VALUES ('Widget');
            INSERT INTO "tags" ("label") VALUES ('Seasonal');
            INSERT INTO "_jt_products_tags" ("product_id", "tag_id") VALUES (1, 1);
            """);

        using var response = await host.Client.PostAsync(
            "/api/v1/admin/schema/preview",
            new StringContent(ManyToManySchemaWithExplicitJunctionColumns, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("hasChanges").GetBoolean());
        Assert.True(json.GetProperty("isDestructive").GetBoolean());
        Assert.True(json.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Contains(
            json.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("changeType").GetString() == "AlterRelation"
                && change.TryGetProperty("relation", out var relation)
                && relation.GetString() == "product_tags"
                && change.GetProperty("isDestructive").GetBoolean());
        var alterChange = json.GetProperty("changes").EnumerateArray().Single(
            change => change.GetProperty("changeType").GetString() == "AlterRelation");
        var impact = alterChange.GetProperty("impact");
        Assert.Equal("many_to_many_junction_rebuild", impact.GetProperty("kind").GetString());
        Assert.Equal("_jt_products_tags", impact.GetProperty("objectName").GetString());
        Assert.Equal(1L, impact.GetProperty("rowCount").GetInt64());
        Assert.Equal(1, impact.GetProperty("sampleRows").GetArrayLength());
    }

    [Fact]
    public async Task PreviewSchema_OneToManyRelationRetarget_ReturnsNonDestructiveImpact()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(OneToManySchema);

        using var response = await host.Client.PostAsync(
            "/api/v1/admin/schema/preview",
            new StringContent(OneToManyRetargetedSchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("hasChanges").GetBoolean());
        Assert.False(json.GetProperty("isDestructive").GetBoolean());
        Assert.False(json.GetProperty("requiresConfirmation").GetBoolean());

        var alterChange = json.GetProperty("changes").EnumerateArray().Single(
            change => change.GetProperty("changeType").GetString() == "AlterRelation");
        Assert.False(alterChange.GetProperty("isDestructive").GetBoolean());
        var impact = alterChange.GetProperty("impact");
        Assert.Equal("one_to_many_relation_retarget", impact.GetProperty("kind").GetString());
        Assert.Equal("customer_orders", impact.GetProperty("objectName").GetString());
        Assert.Contains("Existing rows are preserved", impact.GetProperty("message").GetString());
    }

    [Fact]
    public async Task PreviewSchema_NoOpSchema_ReturnsStableEmptyPreview()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(SimpleSchema);

        using var response = await host.Client.PostAsync(
            "/api/v1/admin/schema/preview",
            new StringContent(SimpleSchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("isValid").GetBoolean());
        Assert.False(json.GetProperty("hasChanges").GetBoolean());
        Assert.False(json.GetProperty("metadataOnly").GetBoolean());
        Assert.False(json.GetProperty("isDestructive").GetBoolean());
        Assert.False(json.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Equal(0, json.GetProperty("changes").GetArrayLength());
        Assert.Equal(0, json.GetProperty("plan").GetProperty("sqlUp").GetArrayLength());
        Assert.Equal(0, json.GetProperty("plan").GetProperty("sqlDown").GetArrayLength());
    }

    [Fact]
    public async Task PreviewAndApplySchema_MetadataOnlyChange_IsApplicableWithoutSql()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(SimpleSchema);

        var metadataOnlySchema = """
            {
                "tables": [
                    {
                        "name": "widgets",
                        "displayName": "Widget catalog",
                        "description": "Inventory records managed by the workbench.",
                        "columns": [
                            { "name": "id", "type": "integer", "primaryKey": true },
                            {
                                "name": "name",
                                "displayName": "Widget name",
                                "description": "The customer-facing widget label.",
                                "type": "text",
                                "nullable": false
                            }
                        ]
                    }
                ],
                "relations": []
            }
            """;

        using var previewResponse = await host.Client.PostAsync(
            "/api/v1/admin/schema/preview",
            new StringContent(metadataOnlySchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);

        var previewJson = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(previewJson.GetProperty("isValid").GetBoolean());
        Assert.True(previewJson.GetProperty("hasChanges").GetBoolean());
        Assert.False(previewJson.GetProperty("isDestructive").GetBoolean());
        Assert.False(previewJson.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Equal(0, previewJson.GetProperty("plan").GetProperty("sqlUp").GetArrayLength());
        Assert.Equal(0, previewJson.GetProperty("plan").GetProperty("sqlDown").GetArrayLength());
        Assert.Contains(
            previewJson.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("changeType").GetString() == "AlterTableMetadata"
                && change.GetProperty("table").GetString() == "widgets");
        Assert.Contains(
            previewJson.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("changeType").GetString() == "AlterColumnMetadata"
                && change.GetProperty("table").GetString() == "widgets"
                && change.GetProperty("column").GetString() == "name");

        using var applyResponse = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(metadataOnlySchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);

        var applyJson = await applyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(applyJson.GetProperty("applied").GetBoolean());
        Assert.Contains(
            applyJson.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("changeType").GetString() == "AlterColumnMetadata"
                && change.GetProperty("table").GetString() == "widgets"
                && change.GetProperty("column").GetString() == "name");

        using var schemaResponse = await host.Client.GetAsync("/api/v1/admin/schema");
        var schemaJson = await schemaResponse.Content.ReadFromJsonAsync<JsonElement>();
        var widgets = Assert.Single(schemaJson.GetProperty("tables").EnumerateArray());
        Assert.Equal("Widget catalog", widgets.GetProperty("displayName").GetString());
        Assert.Equal("Inventory records managed by the workbench.", widgets.GetProperty("description").GetString());
        var nameColumn = widgets.GetProperty("columns")
            .EnumerateArray()
            .Single(column => column.GetProperty("name").GetString() == "name");
        Assert.Equal("Widget name", nameColumn.GetProperty("displayName").GetString());
        Assert.Equal("The customer-facing widget label.", nameColumn.GetProperty("description").GetString());
    }

    [Fact]
    public async Task ApplySchema_LayoutOnlyChange_PersistsMetadataInPlaceWithoutVersionBump()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(SimpleSchema);

        using var initialResponse = await host.Client.GetAsync("/api/v1/admin/schema");
        var initialJson = await initialResponse.Content.ReadFromJsonAsync<JsonElement>();
        var initialVersion = initialJson.GetProperty("version").GetInt32();

        var layoutSchema = """
            {
                "tables": [
                    {
                        "name": "widgets",
                        "layout": { "x": 240, "y": 96, "color": "#ff8800" },
                        "columns": [
                            { "name": "id", "type": "integer", "primaryKey": true },
                            { "name": "name", "type": "text", "nullable": false }
                        ]
                    }
                ],
                "relations": []
            }
            """;

        using var previewResponse = await host.Client.PostAsync(
            "/api/v1/admin/schema/preview",
            new StringContent(layoutSchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);

        var previewJson = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(previewJson.GetProperty("isValid").GetBoolean());
        Assert.False(previewJson.GetProperty("hasChanges").GetBoolean());
        Assert.True(previewJson.GetProperty("metadataOnly").GetBoolean());

        using var applyResponse = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(layoutSchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);

        var applyJson = await applyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(applyJson.GetProperty("applied").GetBoolean());
        Assert.True(applyJson.GetProperty("metadataOnly").GetBoolean());
        Assert.Equal(initialVersion, applyJson.GetProperty("version").GetInt32());
        Assert.Equal(0, applyJson.GetProperty("changes").GetArrayLength());

        using var refreshedResponse = await host.Client.GetAsync("/api/v1/admin/schema");
        var refreshedJson = await refreshedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(initialVersion, refreshedJson.GetProperty("version").GetInt32());
        var widgets = Assert.Single(refreshedJson.GetProperty("tables").EnumerateArray());
        var layout = widgets.GetProperty("layout");
        Assert.Equal(240, layout.GetProperty("x").GetInt32());
        Assert.Equal(96, layout.GetProperty("y").GetInt32());
        Assert.Equal("#ff8800", layout.GetProperty("color").GetString());
    }

    [Fact]
    public async Task ApplySchema_NoChangesAtAll_ReturnsAppliedFalseWithoutMetadataFlag()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(SimpleSchema);

        using var applyResponse = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(SimpleSchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);

        var applyJson = await applyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(applyJson.GetProperty("applied").GetBoolean());
        Assert.False(applyJson.GetProperty("metadataOnly").GetBoolean());
        Assert.Equal(0, applyJson.GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public async Task ApplySchema_RealSchemaChangeWithLayout_PersistsLayoutAlongsideMigration()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(SimpleSchema);

        var schemaWithExtraColumnAndLayout = """
            {
                "tables": [
                    {
                        "name": "widgets",
                        "layout": { "x": 320, "y": 80, "color": "#00bcd4" },
                        "columns": [
                            { "name": "id", "type": "integer", "primaryKey": true },
                            { "name": "name", "type": "text", "nullable": false },
                            { "name": "color", "type": "text" }
                        ]
                    }
                ],
                "relations": []
            }
            """;

        using var applyResponse = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(schemaWithExtraColumnAndLayout, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);

        var applyJson = await applyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(applyJson.GetProperty("applied").GetBoolean());
        Assert.False(applyJson.GetProperty("metadataOnly").GetBoolean());

        using var refreshedResponse = await host.Client.GetAsync("/api/v1/admin/schema");
        var refreshedJson = await refreshedResponse.Content.ReadFromJsonAsync<JsonElement>();
        var widgets = Assert.Single(refreshedJson.GetProperty("tables").EnumerateArray());
        var layout = widgets.GetProperty("layout");
        Assert.Equal(320, layout.GetProperty("x").GetInt32());
        Assert.Equal("#00bcd4", layout.GetProperty("color").GetString());
    }

    [Fact]
    public async Task PreviewSchema_LossyTypeChange_ReturnsDeterministicCastExamples()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(TextScoreSchema);

        await host.ExecuteDataSqlAsync("""
            INSERT INTO "widgets" ("name", "score")
            VALUES
                ('one', '1,0'),
                ('two', '2,0'),
                ('three', '2,1'),
                ('word', 'two'),
                ('four', '3,9'),
                ('overflow', '999');
            """);

        using var response = await host.Client.PostAsync(
            "/api/v1/admin/schema/preview",
            new StringContent(IntegerScoreSchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("isValid").GetBoolean());
        Assert.True(json.GetProperty("hasChanges").GetBoolean());
        Assert.True(json.GetProperty("isDestructive").GetBoolean());
        Assert.True(json.GetProperty("requiresConfirmation").GetBoolean());

        var typeChange = json.GetProperty("changes")
            .EnumerateArray()
            .Single(change =>
                change.GetProperty("changeType").GetString() == "AlterColumnType"
                && change.GetProperty("table").GetString() == "widgets"
                && change.GetProperty("column").GetString() == "score");

        Assert.Equal("Lossy", typeChange.GetProperty("coercionKind").GetString());

        var castExamples = typeChange.GetProperty("castExamples");
        Assert.Equal(5, castExamples.GetArrayLength());

        Assert.Equal("1,0", castExamples[0].GetProperty("sourceValue").GetString());
        Assert.Equal(1L, castExamples[0].GetProperty("castValue").GetInt64());
        Assert.True(castExamples[0].GetProperty("isValid").GetBoolean());

        Assert.Equal("2,0", castExamples[1].GetProperty("sourceValue").GetString());
        Assert.Equal(2L, castExamples[1].GetProperty("castValue").GetInt64());

        Assert.Equal("2,1", castExamples[2].GetProperty("sourceValue").GetString());
        Assert.Equal(2L, castExamples[2].GetProperty("castValue").GetInt64());

        Assert.Equal("two", castExamples[3].GetProperty("sourceValue").GetString());
        Assert.Equal(0L, castExamples[3].GetProperty("castValue").GetInt64());

        Assert.Equal("3,9", castExamples[4].GetProperty("sourceValue").GetString());
        Assert.Equal(3L, castExamples[4].GetProperty("castValue").GetInt64());
    }

    [Fact]
    public async Task PreviewSchema_DropColumn_ReturnsSampleValues()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(TextScoreSchema);

        await host.ExecuteDataSqlAsync("""
            INSERT INTO "widgets" ("name", "score")
            VALUES
                ('one', '10'),
                ('two', '20'),
                ('three', '30'),
                ('four', '40'),
                ('five', '50'),
                ('six', '60');
            """);

        var schemaWithoutScore = """
            {
                "tables": [
                    {
                        "name": "widgets",
                        "columns": [
                            { "name": "id", "type": "integer", "primaryKey": true, "nullable": false },
                            { "name": "name", "type": "text", "nullable": false }
                        ],
                        "indexes": []
                    }
                ],
                "relations": []
            }
            """;

        using var response = await host.Client.PostAsync(
            "/api/v1/admin/schema/preview",
            new StringContent(schemaWithoutScore, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("isDestructive").GetBoolean());

        var dropColumn = json.GetProperty("changes")
            .EnumerateArray()
            .Single(change =>
                change.GetProperty("changeType").GetString() == "DropColumn"
                && change.GetProperty("table").GetString() == "widgets"
                && change.GetProperty("column").GetString() == "score");

        var impact = dropColumn.GetProperty("impact");
        Assert.Equal("column_drop", impact.GetProperty("kind").GetString());
        Assert.Equal("widgets.score", impact.GetProperty("objectName").GetString());
        Assert.Equal(6L, impact.GetProperty("rowCount").GetInt64());
        var sampleValues = impact.GetProperty("sampleValues");
        Assert.Equal(5, sampleValues.GetArrayLength());
        Assert.Equal("10", sampleValues[0].GetString());
        Assert.Equal("50", sampleValues[4].GetString());
    }

    [Fact]
    public async Task PreviewSchema_AdditiveChangeAgainstExistingSchema_DoesNotCreateTableOrEnableRoute_UntilApply()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(SimpleSchema);

        var versionBeforePreview = await GetCurrentSchemaVersionAsync(host);
        var migrationsBeforePreview = await host.QuerySystemScalarAsync<long>("SELECT COUNT(*) FROM migrations;");
        var gadgetsTableBeforePreview = await host.QueryDataScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'gadgets';");

        using var routeBeforePreview = await host.Client.GetAsync("/api/v1/data/gadgets");
        Assert.Equal(HttpStatusCode.NotFound, routeBeforePreview.StatusCode);

        using var previewResponse = await host.Client.PostAsync(
            "/api/v1/admin/schema/preview",
            new StringContent(SchemaWithSecondTable, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);

        var previewJson = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(previewJson.GetProperty("isValid").GetBoolean());
        Assert.True(previewJson.GetProperty("hasChanges").GetBoolean());
        Assert.Equal(versionBeforePreview, previewJson.GetProperty("currentVersion").GetInt32());
        Assert.Contains(
            previewJson.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("changeType").GetString() == "AddTable"
                && change.GetProperty("table").GetString() == "gadgets"
                && !change.GetProperty("isDestructive").GetBoolean());
        Assert.Contains(
            previewJson.GetProperty("normalizedModel").GetProperty("tables").EnumerateArray(),
            table => table.GetProperty("name").GetString() == "gadgets");
        Assert.Contains(
            previewJson.GetProperty("plan").GetProperty("sqlUp").EnumerateArray().Select(statement => statement.GetString()),
            statement => statement is not null && statement.Contains("gadgets", StringComparison.OrdinalIgnoreCase));

        var versionAfterPreview = await GetCurrentSchemaVersionAsync(host);
        var migrationsAfterPreview = await host.QuerySystemScalarAsync<long>("SELECT COUNT(*) FROM migrations;");
        var gadgetsTableAfterPreview = await host.QueryDataScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'gadgets';");

        Assert.Equal(versionBeforePreview, versionAfterPreview);
        Assert.Equal(migrationsBeforePreview, migrationsAfterPreview);
        Assert.Equal(gadgetsTableBeforePreview, gadgetsTableAfterPreview);

        using var routeAfterPreview = await host.Client.GetAsync("/api/v1/data/gadgets");
        Assert.Equal(HttpStatusCode.NotFound, routeAfterPreview.StatusCode);

        var previewChanges = previewJson.GetProperty("changes")
            .EnumerateArray()
            .Select(change => new
            {
                ChangeType = change.GetProperty("changeType").GetString(),
                Table = change.GetProperty("table").GetString(),
                Column = change.TryGetProperty("column", out var column) ? column.GetString() : null,
                IsDestructive = change.GetProperty("isDestructive").GetBoolean()
            })
            .ToList();

        using var applyResponse = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(SchemaWithSecondTable, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);

        var applyJson = await applyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(applyJson.GetProperty("applied").GetBoolean());

        var applyChanges = applyJson.GetProperty("changes")
            .EnumerateArray()
            .Select(change => new
            {
                ChangeType = change.GetProperty("changeType").GetString(),
                Table = change.GetProperty("table").GetString(),
                Column = change.TryGetProperty("column", out var column) ? column.GetString() : null,
                IsDestructive = change.GetProperty("isDestructive").GetBoolean()
            })
            .ToList();

        Assert.Equal(previewChanges, applyChanges);

        var gadgetsTableAfterApply = await host.QueryDataScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'gadgets';");
        Assert.Equal(1, gadgetsTableAfterApply);

        using var routeAfterApply = await host.Client.GetAsync("/api/v1/data/gadgets");
        Assert.Equal(HttpStatusCode.OK, routeAfterApply.StatusCode);
    }

    // ── PUT /admin/schema ──

    [Fact]
    public async Task PutSchema_ValidSchema_AppliesMigration()
    {
        await using var host = await CreateHostAsync();

        using var response = await host.Client.PutAsync("/api/v1/admin/schema",
            new StringContent(SimpleSchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("applied").GetBoolean());
        Assert.True(json.GetProperty("version").GetInt32() >= 1);
    }

    [Fact]
    public async Task PutSchema_StripsResolvedRelationMetadataBeforePersistingModelJson()
    {
        await using var host = await CreateHostAsync();

        using var response = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(ManyToManySchemaWithResolvedMetadata, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var canonicalResponse = await host.Client.GetAsync("/api/v1/admin/schema/model");
        Assert.Equal(HttpStatusCode.OK, canonicalResponse.StatusCode);

        var canonicalJson = await canonicalResponse.Content.ReadFromJsonAsync<JsonElement>();
        var canonicalRelation = Assert.Single(canonicalJson.GetProperty("relations").EnumerateArray());
        Assert.False(canonicalRelation.TryGetProperty("resolvedFromAccessor", out _));
        Assert.False(canonicalRelation.TryGetProperty("resolvedToAccessor", out _));
        Assert.False(canonicalRelation.TryGetProperty("resolvedJunctionTableName", out _));

        using var enrichedResponse = await host.Client.GetAsync("/api/v1/admin/schema");
        Assert.Equal(HttpStatusCode.OK, enrichedResponse.StatusCode);

        var enrichedJson = await enrichedResponse.Content.ReadFromJsonAsync<JsonElement>();
        var enrichedRelation = Assert.Single(enrichedJson.GetProperty("relations").EnumerateArray());
        Assert.Equal("tags", enrichedRelation.GetProperty("resolvedFromAccessor").GetString());
        Assert.Equal("products", enrichedRelation.GetProperty("resolvedToAccessor").GetString());
        Assert.Equal("_jt_products_tags", enrichedRelation.GetProperty("resolvedJunctionTableName").GetString());
    }

    [Fact]
    public async Task PutSchema_ApplyThenExtend_ShowsChanges()
    {
        await using var host = await CreateHostAsync();

        // Apply initial schema
        await host.Client.PutAsync("/api/v1/admin/schema",
            new StringContent(SimpleSchema, Encoding.UTF8, "application/json"));

        // Extend with new column
        using var response = await host.Client.PutAsync("/api/v1/admin/schema",
            new StringContent(ExtendedSchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("applied").GetBoolean());

        var changes = json.GetProperty("changes");
        Assert.True(changes.GetArrayLength() > 0);
    }

    [Fact]
    public async Task PutSchema_InvalidSchema_Returns400()
    {
        await using var host = await CreateHostAsync();

        using var response = await host.Client.PutAsync("/api/v1/admin/schema",
            new StringContent(MissingTableNameSchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task PutSchema_WrongValueType_DescribesThePositionWithoutDotNetTypeNames()
    {
        await using var host = await CreateHostAsync();

        const string columnAsNumber = """{"version":1,"tables":[{"name":"t","columns":[42]}],"relations":[]}""";
        using var response = await host.Client.PutAsync("/api/v1/admin/schema",
            new StringContent(columnAsNumber, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.Equal("The schema could not be read: a value has the wrong format (table 1, column 1).",
            error.GetProperty("message").GetString());
        Assert.Equal("$.tables[0].columns[0]", error.GetProperty("details").GetProperty("path").GetString());
    }

    [Fact]
    public async Task PutSchema_DestructiveChange_Returns409WithoutConfirmation()
    {
        await using var host = await CreateHostAsync();

        // Apply initial schema
        await host.Client.PutAsync("/api/v1/admin/schema",
            new StringContent(SimpleSchema, Encoding.UTF8, "application/json"));

        // Try to apply schema that drops the table
        var emptySchema = """
            {
                "tables": [],
                "relations": []
            }
            """;

        using var response = await host.Client.PutAsync("/api/v1/admin/schema",
            new StringContent(emptySchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CONFIRMATION_REQUIRED", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task PutSchema_DestructiveChange_AppliesWithConfirmation()
    {
        await using var host = await CreateHostAsync();

        // Apply initial schema
        await host.Client.PutAsync("/api/v1/admin/schema",
            new StringContent(SimpleSchema, Encoding.UTF8, "application/json"));

        // Drop with confirmation
        var emptySchema = """
            {
                "tables": [],
                "relations": []
            }
            """;

        using var response = await host.Client.PutAsync(
            "/api/v1/admin/schema?confirm_destructive=true",
            new StringContent(emptySchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("applied").GetBoolean());
    }

    [Fact]
    public async Task PutSchema_ManyToManyRelationDrop_RequiresConfirmationAndDropsJunctionWhenConfirmed()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(ManyToManySchema);
        await host.ExecuteDataSqlAsync("""
            INSERT INTO "products" ("name") VALUES ('Widget');
            INSERT INTO "tags" ("label") VALUES ('Seasonal');
            INSERT INTO "_jt_products_tags" ("product_id", "tag_id") VALUES (1, 1);
            """);

        using var blockedResponse = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(ManyToManySchemaWithoutRelation, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Conflict, blockedResponse.StatusCode);

        var blockedJson = await blockedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CONFIRMATION_REQUIRED", blockedJson.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            1L,
            await host.QueryDataScalarAsync<long>("SELECT COUNT(*) FROM \"_jt_products_tags\";"));

        using var confirmedResponse = await host.Client.PutAsync(
            "/api/v1/admin/schema?confirm_destructive=true",
            new StringContent(ManyToManySchemaWithoutRelation, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, confirmedResponse.StatusCode);

        var confirmedJson = await confirmedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(confirmedJson.GetProperty("applied").GetBoolean());
        Assert.Contains(
            confirmedJson.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("changeType").GetString() == "DropRelation"
                && change.GetProperty("table").GetString() == "tags");
        Assert.Contains(
            confirmedJson.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("changeType").GetString() == "DropTable"
                && change.GetProperty("table").GetString() == "_jt_products_tags"
                && change.GetProperty("isDestructive").GetBoolean());
        Assert.Equal(
            0L,
            await host.QueryDataScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '_jt_products_tags';"));
    }

    [Fact]
    public async Task PutSchema_ConvertingOneToManyToVisibleManyToMany_CopiesExistingLinksIntoJunctionTable()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(OneToManySchema);
        await host.ExecuteDataSqlAsync("""
            INSERT INTO "customers" ("name") VALUES ('Acme'), ('Beta');
            INSERT INTO "orders" ("customer_id") VALUES (1), (NULL), (2);
            """);

        using var response = await host.Client.PutAsync(
            "/api/v1/admin/schema?confirm_destructive=true",
            new StringContent(OneToManyConvertedToVisibleManyToManySchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("applied").GetBoolean());

        Assert.Equal(
            2L,
            await host.QueryDataScalarAsync<long>("SELECT COUNT(*) FROM \"_jt_customers_orders\";"));
        Assert.Equal(
            0L,
            await host.QueryDataScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('orders') WHERE name = 'customer_id';"));

        Assert.Equal(
            1L,
            await host.QueryDataScalarAsync<long>("SELECT \"customer_id\" FROM \"_jt_customers_orders\" WHERE \"order_id\" = 1;"));
        Assert.Equal(
            2L,
            await host.QueryDataScalarAsync<long>("SELECT \"customer_id\" FROM \"_jt_customers_orders\" WHERE \"order_id\" = 3;"));
    }

    [Fact]
    public async Task PreviewSchema_ConvertingVisibleManyToManyToOneToMany_ShowsDroppedTableSamples()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(OneToManyConvertedToVisibleManyToManySchema);
        await host.ExecuteDataSqlAsync("""
            INSERT INTO "customers" ("name") VALUES ('Acme'), ('Beta');
            INSERT INTO "orders" DEFAULT VALUES;
            INSERT INTO "orders" DEFAULT VALUES;
            INSERT INTO "_jt_customers_orders" ("customer_id", "order_id") VALUES (1, 1), (2, 2);
            """);

        using var response = await host.Client.PostAsync(
            "/api/v1/admin/schema/preview",
            new StringContent(OneToManySchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("isDestructive").GetBoolean());

        var dropTable = json.GetProperty("changes").EnumerateArray().Single(
            change => change.GetProperty("changeType").GetString() == "DropTable"
                && change.GetProperty("table").GetString() == "_jt_customers_orders");
        var impact = dropTable.GetProperty("impact");
        Assert.Equal("table_drop", impact.GetProperty("kind").GetString());
        Assert.Equal("_jt_customers_orders", impact.GetProperty("objectName").GetString());
        Assert.Equal(2L, impact.GetProperty("rowCount").GetInt64());
        Assert.Equal(2, impact.GetProperty("sampleRows").GetArrayLength());
        Assert.Equal(1L, impact.GetProperty("sampleRows")[0].GetProperty("customer_id").GetInt64());
        Assert.Equal(1L, impact.GetProperty("sampleRows")[0].GetProperty("order_id").GetInt64());
    }

    [Fact]
    public async Task PutSchema_ConvertingVisibleManyToManyToOneToMany_CopiesExistingLinksBackToChildColumn()
    {
        await using var host = await CreateHostAsync();
        await host.ApplySchemaAsync(OneToManyConvertedToVisibleManyToManySchema);
        await host.ExecuteDataSqlAsync("""
            INSERT INTO "customers" ("name") VALUES ('Acme'), ('Beta');
            INSERT INTO "orders" DEFAULT VALUES;
            INSERT INTO "orders" DEFAULT VALUES;
            INSERT INTO "_jt_customers_orders" ("customer_id", "order_id") VALUES (1, 1), (2, 2);
            """);

        using var response = await host.Client.PutAsync(
            "/api/v1/admin/schema?confirm_destructive=true",
            new StringContent(OneToManySchema, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("applied").GetBoolean());
        Assert.Equal(
            1L,
            await host.QueryDataScalarAsync<long>("SELECT \"customer_id\" FROM \"orders\" WHERE \"id\" = 1;"));
        Assert.Equal(
            2L,
            await host.QueryDataScalarAsync<long>("SELECT \"customer_id\" FROM \"orders\" WHERE \"id\" = 2;"));
        Assert.Equal(
            0L,
            await host.QueryDataScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '_jt_customers_orders';"));
    }

    [Fact]
    public async Task PutSchema_RoutesReflectNewSchema_Immediately()
    {
        await using var host = await CreateHostAsync();

        // Apply schema via admin endpoint
        await host.Client.PutAsync("/api/v1/admin/schema",
            new StringContent(SimpleSchema, Encoding.UTF8, "application/json"));

        // Data route should now work
        using var createResponse = await host.Client.PostAsJsonAsync("/api/v1/data/widgets", new
        {
            name = "Test Widget"
        });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var listResponse = await host.Client.GetAsync("/api/v1/data/widgets");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var json = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("meta").GetProperty("total").GetInt64());
    }

    // ── GET /admin/migrations ──

    [Fact]
    public async Task GetMigrations_EmptyDatabase_ReturnsEmptyList()
    {
        await using var host = await CreateHostAsync();

        using var response = await host.Client.GetAsync("/api/v1/admin/migrations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task GetMigrations_AfterApply_ReturnsMigrationHistory()
    {
        await using var host = await CreateHostAsync();

        await host.ApplySchemaAsync(SimpleSchema);

        using var response = await host.Client.GetAsync("/api/v1/admin/migrations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.True(data.GetArrayLength() >= 1);

        var entry = data[0];
        Assert.True(entry.TryGetProperty("versionFrom", out _));
        Assert.True(entry.TryGetProperty("versionTo", out _));
        Assert.True(entry.TryGetProperty("appliedAt", out _));
    }

    // ── POST /admin/migrations/rollback ──

    [Fact]
    public async Task Rollback_NoMigrations_Returns400()
    {
        await using var host = await CreateHostAsync();

        using var response = await host.Client.PostAsync("/api/v1/admin/migrations/rollback", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NO_MIGRATIONS", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Rollback_AfterApply_RollsBackMigration()
    {
        await using var host = await CreateHostAsync();

        await host.ApplySchemaAsync(SimpleSchema);

        using var response = await host.Client.PostAsync("/api/v1/admin/migrations/rollback", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("rolledBack").GetBoolean());
    }

    // ── Auth enforcement ──

    [Fact]
    public async Task AdminEndpoints_InStandardMode_Require403ForNonAdmin()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });

        // No auth token → should get 401 or 403
        using var response = await host.Client.GetAsync("/api/v1/admin/schema");

        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden,
            $"Expected 401 or 403 but got {response.StatusCode}");
    }

    private static async Task<ApiIntegrationTestHost> CreateHostAsync()
    {
        return await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
    }

    private static async Task<int> GetCurrentSchemaVersionAsync(ApiIntegrationTestHost host)
    {
        using var response = await host.Client.GetAsync("/api/v1/admin/schema");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("version").GetInt32();
    }
}
