using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Step 3.5 acceptance: computed columns are live in every read — list, one
/// row, write responses, related rows — and can be filtered, sorted, selected
/// and aggregated. Row formulas, lookups through references, sums over 1:N
/// and N:M relations, computed columns using computed columns. The same
/// scenario runs on Postgres (<see cref="PostgresComputedColumnTests"/>).
/// </summary>
public sealed class ComputedColumnTests
{
    internal const string Schema = """
        {
            "tables": [
                {
                    "name": "accounts",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text" },
                        { "name": "opening_balance", "type": "currency" },
                        { "name": "balance", "type": "computed", "formula": "= opening_balance + SUM(transactions.amount)" },
                        { "name": "spent", "type": "computed", "formula": "SUMIF(transactions.amount, transactions.amount < 0)" },
                        { "name": "bookings", "type": "computed", "formula": "COUNT(transactions)" },
                        { "name": "last_booking", "type": "computed", "formula": "MAX(transactions.booked_on)" },
                        { "name": "label", "type": "computed", "formula": "name & ' (' & bookings & ')'" }
                    ]
                },
                {
                    "name": "categories",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text" }
                    ]
                },
                {
                    "name": "transactions",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "account_id", "type": "reference", "references": "accounts" },
                        { "name": "category_id", "type": "reference", "references": "categories" },
                        { "name": "booked_on", "type": "date" },
                        { "name": "amount", "type": "currency" },
                        { "name": "note", "type": "text" },
                        { "name": "direction", "type": "computed", "formula": "IF(amount < 0, \"out\", \"in\")" },
                        { "name": "account_name", "type": "computed", "formula": "account_id.name" },
                        { "name": "category_name", "type": "computed", "formula": "COALESCE(category.name, \"none\")" },
                        { "name": "question", "type": "computed", "formula": "\"why? \" & note" }
                    ]
                },
                {
                    "name": "tags",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text" },
                        { "name": "uses", "type": "computed", "formula": "COUNT(transactions)" },
                        { "name": "total", "type": "computed", "formula": "SUM(transactions.amount)" }
                    ]
                },
                {
                    "name": "holdings",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "ticker", "type": "text" },
                        { "name": "shares", "type": "float" },
                        { "name": "buy_price", "type": "currency" },
                        { "name": "cost_basis", "type": "computed", "formula": "shares * buy_price" }
                    ]
                }
            ],
            "relations": [
                { "name": "account_transactions", "type": "1:N", "from": "accounts", "to": "transactions", "column": "account_id" },
                { "name": "category_transactions", "type": "1:N", "from": "categories", "to": "transactions", "column": "category_id" },
                { "name": "tag_transactions", "type": "N:M", "from": "tags", "to": "transactions" }
            ]
        }
        """;

    internal static async Task SeedAsync(ApiIntegrationTestHost host)
    {
        await PostAsync(host, "accounts", new { name = "Girokonto", opening_balance = 1000.00m });
        await PostAsync(host, "accounts", new { name = "Tagesgeld", opening_balance = 5000.00m });
        await PostAsync(host, "accounts", new { name = "Leer", opening_balance = 0m });
        await PostAsync(host, "categories", new { name = "Miete" });
        await PostAsync(host, "categories", new { name = "Lebensmittel" });

        await PostAsync(host, "transactions", new { account_id = 1, category_id = 1, booked_on = "2026-09-01", amount = -900.00m, note = "September" });
        await PostAsync(host, "transactions", new { account_id = 1, category_id = 2, booked_on = "2026-09-03", amount = -45.90m, note = "REWE" });
        await PostAsync(host, "transactions", new { account_id = 1, booked_on = "2026-09-28", amount = 3100.00m, note = "Gehalt" });
        await PostAsync(host, "transactions", new { account_id = 2, booked_on = "2026-09-30", amount = 12.34m, note = "Zinsen" });

        await PostAsync(host, "tags", new { name = "fix" });
        await PostAsync(host, "tags", new { name = "unused" });
        await PostAsync(host, "_jt_tags_transactions", new { tag_id = 1, transaction_id = 1 });
        await PostAsync(host, "_jt_tags_transactions", new { tag_id = 1, transaction_id = 4 });

        await PostAsync(host, "holdings", new { ticker = "VWCE", shares = 12.5, buy_price = 98.76m });
    }

    internal static async Task ListAndGet(ApiIntegrationTestHost host)
    {
        var accounts = await ListAsync(host, "accounts?sort=id");
        Assert.Equal(
            ["Girokonto 3154.1 -945.9 3 2026-09-28 Girokonto (3)", "Tagesgeld 5012.34 0 1 2026-09-30 Tagesgeld (1)", "Leer 0 0 0 null Leer (0)"],
            accounts.Select(a => Row(a, "name", "balance", "spent", "bookings", "last_booking", "label")));

        var one = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/accounts/1");
        Assert.Equal(3154.1m, one.GetProperty("data").GetProperty("balance").GetDecimal());

        var transactions = await ListAsync(host, "transactions?sort=id");
        Assert.Equal(
            ["out Girokonto Miete why? September", "out Girokonto Lebensmittel why? REWE", "in Girokonto none why? Gehalt", "in Tagesgeld none why? Zinsen"],
            transactions.Select(t => Row(t, "direction", "account_name", "category_name", "question")));

        var tags = await ListAsync(host, "tags?sort=id");
        Assert.Equal(["fix 2 -887.66", "unused 0 0"], tags.Select(t => Row(t, "name", "uses", "total")));

        var holding = Assert.Single(await ListAsync(host, "holdings"));
        Assert.Equal(1234.5m, holding.GetProperty("cost_basis").GetDecimal());
    }

    internal static async Task FilterSortAndSelect(ApiIntegrationTestHost host)
    {
        Assert.Equal(["Tagesgeld", "Girokonto", "Leer"], (await ListAsync(host, "accounts?sort=-balance")).Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(["Tagesgeld"], (await ListAsync(host, "accounts?filter[balance][gt]=4000")).Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(["Girokonto"], (await ListAsync(host, "accounts?filter[spent][lt]=0")).Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(["Leer"], (await ListAsync(host, "accounts?filter[last_booking][null]=true")).Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(["Girokonto"], (await ListAsync(host, "accounts?filter[last_booking][lte]=2026-09-28")).Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(2, (await ListAsync(host, "transactions?filter[direction]=out")).Count);
        Assert.Equal(2, (await ListAsync(host, "transactions?filter[category_name][like]=%te%")).Count);
        Assert.Equal(["Lebensmittel", "Miete"], (await ListAsync(host, "transactions?filter[category_name][in]=Miete,Lebensmittel&sort=category_name")).Select(t => t.GetProperty("category_name").GetString()));

        var selected = Assert.Single(await ListAsync(host, "accounts?fields=name,balance&filter[name]=Leer"));
        Assert.Equal(["name", "balance"], selected.EnumerateObject().Select(p => p.Name));

        // Unknown columns are still rejected.
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/v1/data/accounts?sort=nope")).StatusCode);
    }

    internal static async Task WritesReturnLiveValues(ApiIntegrationTestHost host)
    {
        using var created = await host.Client.PostAsJsonAsync("/api/v1/data/transactions", new { account_id = 3, booked_on = "2026-10-01", amount = 50m, note = "Start" });
        var transaction = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("in", transaction.GetProperty("direction").GetString());
        Assert.Equal("Leer", transaction.GetProperty("account_name").GetString());

        var account = (await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/accounts/3")).GetProperty("data");
        Assert.Equal("Leer 50 1 2026-10-01", Row(account, "name", "balance", "bookings", "last_booking"));

        using var patched = await host.Client.PatchAsJsonAsync("/api/v1/data/accounts/3", new { opening_balance = 10m });
        Assert.Equal(60m, (await patched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("balance").GetDecimal());

        using var write = await host.Client.PatchAsJsonAsync("/api/v1/data/accounts/3", new { balance = 1m });
        Assert.Equal(HttpStatusCode.BadRequest, write.StatusCode);
    }

    internal static async Task RelatedRowsAndAggregation(ApiIntegrationTestHost host)
    {
        var related = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/accounts/1/account_transactions?sort=id");
        Assert.Equal(["out", "out", "in"], related.GetProperty("data").EnumerateArray().Select(t => t.GetProperty("direction").GetString()));

        var included = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/transactions?include=account&sort=id&per_page=1");
        Assert.Equal(3154.1m, included.GetProperty("data")[0].GetProperty("account").GetProperty("balance").GetDecimal());

        using var query = await host.Client.PostAsJsonAsync("/api/v1/query", new
        {
            source = "transactions",
            filter = new { direction = "out" },
            groupBy = new[] { new { column = "category_name" } },
            measures = new[] { new { name = "total", fn = "sum", column = "amount" } }
        });
        var result = await query.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(query.IsSuccessStatusCode, result.ToString());
        Assert.Equal(["Lebensmittel -45.9", "Miete -900"], result.GetProperty("data").EnumerateArray().Select(r => Row(r, "category_name", "total")));

        using var byBalance = await host.Client.PostAsJsonAsync("/api/v1/query", new
        {
            source = "accounts",
            measures = new[] { new { name = "net_worth", fn = "sum", column = "balance" } }
        });
        var worth = await byBalance.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(byBalance.IsSuccessStatusCode, worth.ToString());
        Assert.Equal(8166.44m, worth.GetProperty("data")[0].GetProperty("net_worth").GetDecimal());
    }

    internal static async Task RunAsync(ApiIntegrationTestHost host)
    {
        await host.ApplySchemaAsync(Schema);
        await SeedAsync(host);
        await ListAndGet(host);
        await FilterSortAndSelect(host);
        await RelatedRowsAndAggregation(host);
        await WritesReturnLiveValues(host);
    }

    [Fact]
    public async Task ComputedColumns_AreLive_OnSqlite()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?> { ["Backend:AuthMode"] = "open", ["Schema:AutoMigrateOnStartup"] = "false" });
        await RunAsync(host);
    }

    [Fact]
    public async Task SchemaWithACycleOrABrokenFormula_IsRejected_AndResultTypesAreStored()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?> { ["Backend:AuthMode"] = "open", ["Schema:AutoMigrateOnStartup"] = "false" });
        await host.ApplySchemaAsync(Schema);

        var model = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/schema/model");
        var json = model.ToString();
        Assert.Contains("\"resultType\":\"currency\"", json.Replace(" ", ""));

        var cyclic = Schema.Replace("\"formula\": \"COUNT(transactions)\" }", "\"formula\": \"COUNT(transactions) + label\" }");
        using var preview = await host.Client.PostAsync("/api/v1/admin/schema/preview", new StringContent(cyclic, System.Text.Encoding.UTF8, "application/json"));
        var errors = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("message").GetString()).ToList();
        Assert.Contains(errors, e => e!.Contains("Circular reference: accounts.bookings → accounts.label → accounts.bookings.", StringComparison.Ordinal));
    }

    // ── Helpers ──

    private static async Task PostAsync(ApiIntegrationTestHost host, string table, object body)
    {
        using var response = await host.Client.PostAsJsonAsync($"/api/v1/data/{table}", body);
        Assert.True(response.IsSuccessStatusCode, $"{table}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<List<JsonElement>> ListAsync(ApiIntegrationTestHost host, string path)
    {
        using var response = await host.Client.GetAsync($"/api/v1/data/{path}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {body}");
        return body.GetProperty("data").EnumerateArray().ToList();
    }

    private static string Row(JsonElement row, params string[] columns) =>
        string.Join(" ", columns.Select(c => FormulaConformanceTests.Text(row.GetProperty(c))));
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresComputedColumnTests
{
    [PostgresFact]
    public async Task ComputedColumns_AreLive_OnPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await ComputedColumnTests.RunAsync(host);
    }
}
