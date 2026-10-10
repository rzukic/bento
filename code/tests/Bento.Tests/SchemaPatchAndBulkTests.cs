using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Beta feedback: every schema change needed the whole model (~30 KB, a ~57 KB
/// preview), and rows went in one POST at a time. Schema operations and
/// POST /data/{table}/bulk with upsertOn.
/// </summary>
public sealed class SchemaPatchAndBulkTests
{
    internal const string Schema = """
        {
            "tables": [
                { "name": "categories", "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true },
                    { "name": "name", "type": "text", "nullable": false }
                ] },
                { "name": "bookings", "columns": [
                    { "name": "id", "type": "integer", "primaryKey": true },
                    { "name": "external_id", "type": "text", "nullable": false, "unique": true },
                    { "name": "date", "type": "date", "nullable": false },
                    { "name": "amount", "type": "currency", "nullable": false },
                    { "name": "payee", "type": "text" },
                    { "name": "category_id", "type": "reference", "references": "categories" }
                ] }
            ],
            "relations": [ { "name": "category_bookings", "type": "1:N", "from": "categories", "to": "bookings", "column": "category_id" } ]
        }
        """;

    private static Task<ApiIntegrationTestHost> StartAsync() => ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "sqlite-test-secret"
    });

    [Fact]
    public Task SchemaOperations_OnSqlite() => SchemaOperationsAsync(StartAsync());

    [Fact]
    public Task Bulk_OnSqlite() => BulkAsync(StartAsync());

    internal static async Task SchemaOperationsAsync(Task<ApiIntegrationTestHost> start)
    {
        await using var host = await start;
        await host.ApplySchemaAsync(Schema);
        await PostAsync(host, "/api/v1/data/categories", new { name = "Food" });
        await PostAsync(host, "/api/v1/data/bookings", new { external_id = "A1", date = "2026-10-01", amount = -12.5, payee = "Billa", category_id = 1 });

        // Preview: compact, nothing changes.
        var operations = new object[]
        {
            new { op = "addColumn", table = "bookings", column = new { name = "note", type = "text" }, after = "payee" },
            new { op = "changeColumn", table = "bookings", column = "payee", set = new Dictionary<string, object?> { ["displayName"] = "Payee", ["description"] = null } },
            new { op = "renameColumn", table = "bookings", column = "payee", to = "counterparty" },
            new { op = "addTable", table = new { name = "goals", columns = new object[] { new { name = "id", type = "integer", primaryKey = true }, new { name = "target", type = "currency" } } } }
        };
        var preview = await SendAsync(host, HttpMethod.Post, "/api/v1/admin/schema/patch/preview", new { operations });
        Assert.True(preview.GetProperty("isValid").GetBoolean(), preview.ToString());
        Assert.False(preview.TryGetProperty("normalizedModel", out _));
        Assert.False(preview.TryGetProperty("plan", out _));
        var changes = preview.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("changeType").GetString()).ToList();
        Assert.Contains("AddColumn", changes);
        Assert.Contains("RenameColumn", changes);
        Assert.Contains("AddTable", changes);
        Assert.DoesNotContain("note", ColumnNames(await ModelAsync(host), "bookings"));

        // Apply: the column sits after payee, the renamed column keeps its data.
        var applied = await SendAsync(host, HttpMethod.Patch, "/api/v1/admin/schema", new { operations });
        Assert.True(applied.GetProperty("applied").GetBoolean(), applied.ToString());
        var model = await ModelAsync(host);
        Assert.Equal(["id", "external_id", "date", "amount", "counterparty", "note", "category_id"], ColumnNames(model, "bookings"));
        Assert.Contains("goals", model.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        var row = (await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/bookings")).GetProperty("data")[0];
        Assert.Equal("Billa", row.GetProperty("counterparty").GetString());

        // Removing a column deletes data: it needs the confirmation, like a whole model.
        var remove = new { operations = new object[] { new { op = "removeColumn", table = "bookings", column = "note" } } };
        using (var refused = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "/api/v1/admin/schema") { Content = JsonContent.Create(remove) }))
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.True((await SendAsync(host, HttpMethod.Patch, "/api/v1/admin/schema?confirm_destructive=true", remove)).GetProperty("applied").GetBoolean());
        Assert.DoesNotContain("note", ColumnNames(await ModelAsync(host), "bookings"));

        // Mistakes name the operation.
        await ExpectErrorAsync(host, new { operations = new object[] { new { op = "addColumn", table = "nope", column = new { name = "x", type = "text" } } } }, "operations[0].table", "There is no table 'nope'.");
        await ExpectErrorAsync(host, new { operations = new object[] { new { op = "addColumn", table = "bookings", column = new { name = "x", type = "text" } }, new { op = "explode" } } }, "operations[1].op", null);
        await ExpectErrorAsync(host, new { operations = new object[] { new { op = "changeColumn", table = "bookings", column = "amount", set = new { name = "value" } } } }, "operations[0].set.name", null);
        await ExpectErrorAsync(host, new { operations = Array.Empty<object>() }, "operations", null);

        // The whole-model preview can be compact too; by default it still has the model.
        var full = await SendAsync(host, HttpMethod.Post, "/api/v1/admin/schema/preview", model);
        Assert.True(full.TryGetProperty("normalizedModel", out _));
        var small = await SendAsync(host, HttpMethod.Post, "/api/v1/admin/schema/preview?compact=true", model);
        Assert.False(small.TryGetProperty("normalizedModel", out _));
    }

    internal static async Task BulkAsync(Task<ApiIntegrationTestHost> start)
    {
        await using var host = await start;
        await host.ApplySchemaAsync(Schema);
        await PostAsync(host, "/api/v1/data/categories", new { name = "Food" });
        await PostAsync(host, "/api/v1/data/categories", new { name = "Rent" });

        // Insert many at once.
        var first = await SendAsync(host, HttpMethod.Post, "/api/v1/data/bookings/bulk", new
        {
            rows = new object[]
            {
                new { external_id = "A1", date = "2026-10-01", amount = -12.5, payee = "Billa" },
                new { external_id = "A2", date = "2026-10-02", amount = -900, payee = "Landlord" },
                new { external_id = "A3", date = "2026-10-03", amount = 2500, payee = "Employer" }
            }
        });
        Assert.Equal(3, first.GetProperty("data").GetProperty("inserted").GetInt32());
        Assert.All(first.GetProperty("data").GetProperty("rows").EnumerateArray(), r => Assert.True(r.GetProperty("id").GetInt64() > 0));

        // Links in bulk: matched by external_id, only the sent columns change; a new key is added.
        // A key twice in one call: added, then changed.
        var second = await SendAsync(host, HttpMethod.Post, "/api/v1/data/bookings/bulk", new
        {
            rows = new object[]
            {
                new { external_id = "A1", category_id = 1 },
                new { external_id = "A2", category_id = 2 },
                new { external_id = "A4", date = "2026-10-04", amount = -3.2, payee = "Bakery" },
                new { external_id = "A4", category_id = 1 }
            },
            upsertOn = new[] { "external_id" }
        });
        var data = second.GetProperty("data");
        Assert.Equal(1, data.GetProperty("inserted").GetInt32());
        Assert.Equal(3, data.GetProperty("updated").GetInt32());
        Assert.Equal(["updated", "updated", "inserted", "updated"], data.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("action").GetString()));

        var rows = (await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/bookings?sort=external_id")).GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal("Billa", rows[0].GetProperty("payee").GetString());
        Assert.Equal(1, rows[0].GetProperty("category_id").GetInt64());
        Assert.Equal(2, rows[1].GetProperty("category_id").GetInt64());
        Assert.Equal(1, rows[3].GetProperty("category_id").GetInt64());

        // A bad row names itself, and nothing of the call is written.
        using var bad = await host.Client.PostAsJsonAsync("/api/v1/data/bookings/bulk", new
        {
            rows = new object[]
            {
                new { external_id = "B1", date = "2026-10-05", amount = -1 },
                new { external_id = "B2", date = "not a date", amount = -1 }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var error = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.StartsWith("Row 2: ", error.GetProperty("message").GetString());
        Assert.Equal(1, error.GetProperty("details").GetProperty("row").GetInt32());
        Assert.Equal(4, (await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/bookings")).GetProperty("data").GetArrayLength());

        // The key columns must be in every row, and be columns.
        using var missingKey = await host.Client.PostAsJsonAsync("/api/v1/data/bookings/bulk", new { rows = new object[] { new { amount = 1 } }, upsertOn = new[] { "external_id" } });
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        using var unknownKey = await host.Client.PostAsJsonAsync("/api/v1/data/bookings/bulk", new { rows = new object[] { new { amount = 1 } }, upsertOn = new[] { "nope" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknownKey.StatusCode);
        using var tooMany = await host.Client.PostAsJsonAsync("/api/v1/data/bookings/bulk", new { rows = Enumerable.Range(0, 1001).Select(i => new { external_id = $"X{i}" }) });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    private static List<string?> ColumnNames(JsonElement model, string table) =>
        model.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("name").GetString() == table)
            .GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();

    private static Task<JsonElement> ModelAsync(ApiIntegrationTestHost host) =>
        host.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/schema/model");

    private static async Task PostAsync(ApiIntegrationTestHost host, string path, object body)
    {
        using var response = await host.Client.PostAsJsonAsync(path, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> SendAsync(ApiIntegrationTestHost host, HttpMethod method, string path, object body)
    {
        using var response = await host.Client.SendAsync(new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) });
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, json.ToString());
        return json;
    }

    private static async Task ExpectErrorAsync(ApiIntegrationTestHost host, object body, string field, string? message)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/admin/schema/patch/preview", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.Equal(field, error.GetProperty("details").GetProperty("field").GetString());
        if (message is not null)
            Assert.Equal(message, error.GetProperty("message").GetString());
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresSchemaPatchAndBulkTests
{
    private static Task<ApiIntegrationTestHost> StartAsync() => ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "postgres-test-secret",
        ["Schema:AutoMigrateOnStartup"] = "false"
    });

    [PostgresFact]
    public Task SchemaOperations_OnPostgres() => SchemaPatchAndBulkTests.SchemaOperationsAsync(StartAsync());

    [PostgresFact]
    public Task Bulk_OnPostgres() => SchemaPatchAndBulkTests.BulkAsync(StartAsync());
}
