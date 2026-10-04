using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Computed columns have no database column: adding one to an existing
/// table, changing it and dropping it must not generate DDL. Adding one
/// failed with "Computed columns have no physical DB type".
/// </summary>
public sealed class ComputedColumnMigrationTests
{
    private const string Plain = """
        { "tables": [ { "name": "bookings", "columns": [
            { "name": "id", "type": "integer", "primaryKey": true },
            { "name": "amount", "type": "currency" } ] } ] }
        """;

    private static string WithComputed(string formula, bool required = false) => $$"""
        { "tables": [ { "name": "bookings", "columns": [
            { "name": "id", "type": "integer", "primaryKey": true },
            { "name": "amount", "type": "currency" },
            { "name": "spent", "type": "computed", "formula": "{{formula}}", "nullable": {{(required ? "false" : "true")}} } ] } ] }
        """;

    internal static async Task AddChangeAndDropAComputedColumn(ApiIntegrationTestHost host)
    {
        await host.ApplySchemaAsync(Plain);
        (await host.Client.PostAsJsonAsync("/api/v1/data/bookings", new { amount = -12.5m })).EnsureSuccessStatusCode();

        await host.ApplySchemaAsync(WithComputed("amount * -1"));
        await host.ApplySchemaAsync(WithComputed("ABS(amount)", required: true));
        await host.ApplySchemaAsync(Plain);

        var rows = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/bookings");
        var row = Assert.Single(rows.GetProperty("data").EnumerateArray());
        Assert.Equal(-12.5m, row.GetProperty("amount").GetDecimal());
        Assert.False(row.TryGetProperty("spent", out _));
    }

    [Fact]
    public async Task ComputedColumns_NeedNoDdl_OnSqlite()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?> { ["Backend:AuthMode"] = "open", ["Schema:AutoMigrateOnStartup"] = "false" });
        await AddChangeAndDropAComputedColumn(host);
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresComputedColumnMigrationTests
{
    [PostgresFact]
    public async Task ComputedColumns_NeedNoDdl_OnPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await ComputedColumnMigrationTests.AddChangeAndDropAComputedColumn(host);
    }
}
