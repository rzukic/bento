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

        // Schema with missing table name
        var invalidSchema = """
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

        using var response = await host.Client.PutAsync("/api/v1/admin/schema",
            new StringContent(invalidSchema, Encoding.UTF8, "application/json"));

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
}
