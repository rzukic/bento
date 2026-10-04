using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Bento.Tests;

public sealed class DropTableWithRelationTests
{
    private const string Before = """
        { "tables": [
            { "name": "kategorie", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }, { "name": "name", "type": "text" } ] },
            { "name": "girokonto", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }, { "name": "betrag", "type": "currency" }, { "name": "kategorie_id", "type": "reference", "references": "kategorie" } ] },
            { "name": "girokonto_2", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }, { "name": "betrag", "type": "currency" }, { "name": "kategorie_id", "type": "reference", "references": "kategorie" } ] }
          ],
          "relations": [
            { "name": "kategorie_girokonto", "type": "1:N", "from": "kategorie", "to": "girokonto", "column": "kategorie_id" },
            { "name": "kategorie_girokonto_2", "type": "1:N", "from": "kategorie", "to": "girokonto_2", "column": "kategorie_id" }
          ] }
        """;

    private const string After = """
        { "tables": [
            { "name": "kategorie", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }, { "name": "name", "type": "text" } ] },
            { "name": "girokonto", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }, { "name": "betrag", "type": "currency" }, { "name": "kategorie_id", "type": "reference", "references": "kategorie" } ] }
          ],
          "relations": [
            { "name": "kategorie_girokonto", "type": "1:N", "from": "kategorie", "to": "girokonto", "column": "kategorie_id" }
          ] }
        """;

    /// <summary>
    /// Deleting a second imported table that links to the same lookup table
    /// ("Girokonto 2" → kategorie) failed with "Sequence contains no matching
    /// element": the dropped 1:N relation queued a SQLite rebuild of the table
    /// being dropped.
    /// </summary>
    internal static async Task DropsTheTableAndKeepsTheRest(ApiIntegrationTestHost host)
    {
        await host.ApplySchemaAsync(Before);
        (await host.Client.PostAsJsonAsync("/api/v1/data/kategorie", new { name = "Miete" })).EnsureSuccessStatusCode();
        (await host.Client.PostAsJsonAsync("/api/v1/data/girokonto", new { betrag = -900m, kategorie_id = 1 })).EnsureSuccessStatusCode();
        (await host.Client.PostAsJsonAsync("/api/v1/data/girokonto_2", new { betrag = -900m, kategorie_id = 1 })).EnsureSuccessStatusCode();

        using var preview = await host.Client.PostAsync("/api/v1/admin/schema/preview", new StringContent(After, Encoding.UTF8, "application/json"));
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("errors").EnumerateArray());

        await host.ApplySchemaAsync(After);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/v1/data/girokonto_2")).StatusCode);
        var rows = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/girokonto");
        var row = Assert.Single(rows.GetProperty("data").EnumerateArray());
        Assert.Equal(1, row.GetProperty("kategorie_id").GetInt64());
        Assert.Single((await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/kategorie")).GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task DropTableWithOneToManyRelation_OnSqlite()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?> { ["Backend:AuthMode"] = "open", ["Schema:AutoMigrateOnStartup"] = "false" });
        await DropsTheTableAndKeepsTheRest(host);
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresDropTableWithRelationTests
{
    [PostgresFact]
    public async Task DropTableWithOneToManyRelation_OnPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await DropTableWithRelationTests.DropsTheTableAndKeepsTheRest(host);
    }
}
