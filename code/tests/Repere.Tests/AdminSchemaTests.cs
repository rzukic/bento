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
