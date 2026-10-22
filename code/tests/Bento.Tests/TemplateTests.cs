using System.Net;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Templates are recorded per workspace; what the AI and MCP clients learn
/// about a use case (the finance conventions, a template's tables) comes only
/// from a workspace that has it installed.
/// </summary>
public sealed class TemplateTests
{
    private const string Schema = """
        { "tables": [
            { "name": "accounts", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }, { "name": "name", "type": "text" } ] },
            { "name": "transactions", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }, { "name": "amount", "type": "currency" } ] } ] }
        """;

    [Fact]
    public async Task Templates_AreRecordedPerWorkspace() => await TemplatesAsync(await ShareTests.HostAsync(Schema));

    internal static async Task TemplatesAsync(ApiIntegrationTestHost host)
    {
        await using var disposable = host;
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var readKey = await ApiKeyTests.CreateKeyAsync(host, admin, "read");

        // Nothing installed: the instructions name no use case.
        var instructions = await InstructionsAsync(host, readKey);
        Assert.DoesNotContain("finance", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Templates in this workspace", instructions);
        Assert.Equal(0, (await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/templates", readKey))).GetArrayLength());

        // Recording needs an admin, a sane id and tables that exist.
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/templates/finance", readKey, new { tables = new { } }), HttpStatusCode.Forbidden);
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/templates/Not%20OK", admin, new { }), HttpStatusCode.BadRequest, "template id");
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/templates/finance", admin, new { tables = new Dictionary<string, string> { ["trades"] = "trades" } }),
            HttpStatusCode.BadRequest, "no table 'trades'");

        var recorded = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/templates/finance", admin, new
        {
            title = "Finance",
            tables = new Dictionary<string, string> { ["accounts"] = "accounts", ["transactions"] = "transactions" }
        }));
        Assert.Equal("finance", recorded.GetProperty("id").GetString());
        var list = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/templates", readKey));
        Assert.Equal("transactions", Assert.Single(list.EnumerateArray()).GetProperty("tables").GetProperty("transactions").GetString());

        // Now the instructions name its tables and point at its conventions.
        instructions = await InstructionsAsync(host, readKey);
        Assert.Contains("The Finance template is installed: `accounts`, `transactions`", instructions);
        Assert.Contains("bento_guide topic finance", instructions);

        // Installed again under other names: both are known; again with the same names: still two.
        await host.ApplySchemaAsync("""
            { "tables": [
                { "name": "accounts", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }, { "name": "name", "type": "text" } ] },
                { "name": "transactions", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }, { "name": "amount", "type": "currency" } ] },
                { "name": "my_accounts", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }, { "name": "name", "type": "text" } ] } ] }
            """);
        for (var i = 0; i < 2; i++)
            await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/templates/finance", admin, new { title = "Finance", tables = new Dictionary<string, string> { ["accounts"] = "my_accounts" } }));
        Assert.Equal(2, (await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/templates", readKey))).GetArrayLength());
        Assert.Contains("`my_accounts` (its accounts)", await InstructionsAsync(host, readKey));

        // Another workspace knows nothing of it.
        await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/workspaces", admin, new { name = "Side" }));
        Assert.Equal(0, (await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/w/side/templates", admin))).GetArrayLength());

        // Removed: gone from the instructions again.
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Delete, "/api/v1/templates/finance", admin), HttpStatusCode.NoContent);
        Assert.DoesNotContain("Templates in this workspace", await InstructionsAsync(host, readKey));
    }

    [Fact]
    public async Task FinanceSetup_FromBeforeTemplates_IsFound()
    {
        static string Table(string name, params string[] columns) =>
            $$"""{ "name": "{{name}}", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }{{string.Concat(columns.Select(c => $$""", { "name": "{{c}}", "type": "text" }"""))}} ] }""";
        var schema = $$"""
            { "tables": [ {{Table("my_accounts", "name")}}, {{Table("my_categories", "name")}}, {{Table("my_transactions", "amount", "account_id", "category_id")}},
              {{Table("my_trades", "isin", "shares")}}, {{Table("my_prices", "isin", "price")}}, {{Table("my_dividends", "amount")}} ] }
            """;
        await using var host = await ShareTests.HostAsync(schema);
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var list = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/templates", admin));
        var finance = Assert.Single(list.EnumerateArray());
        Assert.Equal("finance", finance.GetProperty("id").GetString());
        Assert.Equal("my_trades", finance.GetProperty("tables").GetProperty("trades").GetString());
        Assert.Contains("`my_transactions` (its transactions)", await InstructionsAsync(host, admin));

        // Removed on purpose: not found again.
        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Delete, "/api/v1/templates/finance", admin), HttpStatusCode.NoContent);
        Assert.Equal(0, (await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/templates", admin))).GetArrayLength());
    }

    private static async Task<string> InstructionsAsync(ApiIntegrationTestHost host, string token) =>
        (await McpTests.McpAsync(host, token, "initialize", new { protocolVersion = "2025-06-18" })).GetProperty("result").GetProperty("instructions").GetString()!;

    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.RequestUri} {(int)response.StatusCode}: {text}");
            return JsonDocument.Parse(text).RootElement.GetProperty("data").Clone();
        }
    }

    private static async Task Status(HttpResponseMessage response, HttpStatusCode expected, string? contains = null)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.RequestUri}: expected {(int)expected}, got {(int)response.StatusCode}: {text}");
            if (contains is not null)
                Assert.Contains(contains, text);
        }
    }
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresTemplateTests
{
    [PostgresFact]
    public async Task Templates_AreRecordedPerWorkspace_OnPostgres()
    {
        var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync("""
            { "tables": [
                { "name": "accounts", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }, { "name": "name", "type": "text" } ] },
                { "name": "transactions", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }, { "name": "amount", "type": "currency" } ] } ] }
            """);
        await TemplateTests.TemplatesAsync(host);
    }
}
