using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Step 3.1 acceptance: a German bank export (Windows-1252, semicolons,
/// decimal comma, account lines above the header) imports into a
/// transactions table, re-importing adds nothing twice, invalid rows are
/// reported per line, and mapping profiles are stored per table.
/// </summary>
public sealed class ImportApiTests
{
    static ImportApiTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    internal const string FinanceSchema = """
        {
            "tables": [
                {
                    "name": "categories",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "name", "type": "text", "nullable": false, "unique": true }
                    ]
                },
                {
                    "name": "transactions",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "booked_on", "type": "date", "nullable": false },
                        { "name": "counterparty", "type": "text" },
                        { "name": "purpose", "type": "text" },
                        { "name": "amount", "type": "currency", "nullable": false },
                        { "name": "category_id", "type": "reference", "references": "categories" }
                    ]
                }
            ],
            "relations": [
                { "name": "category_transactions", "type": "1:N", "from": "categories", "to": "transactions", "column": "category_id" }
            ]
        }
        """;

    internal const string StatementSeptember = """
        "Kontonummer:";"DE12 1203 0000 0012 3456 78";
        "Kontostand vom 30.09.2026:";"2.345,67 EUR";

        "Buchungstag";"Auftraggeber / Begünstigter";"Verwendungszweck";"Betrag (EUR)";"Kategorie";
        "01.09.2026";"REWE Markt";"Einkauf";"-45,90";"Lebensmittel";
        "02.09.2026";"Arbeitgeber GmbH";"Gehalt September";"3.250,00";"Gehalt";
        "03.09.2026";"Bäckerei Müller";"Kaffee";"-3,20";"Lebensmittel";
        "03.09.2026";"Bäckerei Müller";"Kaffee";"-3,20";"Lebensmittel";
        """;

    internal static readonly object[] Mapping =
    [
        new { source = "Buchungstag", target = "booked_on" },
        new { source = "Auftraggeber / Begünstigter", target = "counterparty" },
        new { source = "Verwendungszweck", target = "purpose" },
        new { source = "Betrag (EUR)", target = "amount" },
        new { source = "Kategorie", target = "category_id" }
    ];

    internal static readonly string[] KeyColumns = ["booked_on", "counterparty", "purpose", "amount"];

    [Fact]
    public async Task Analyze_DetectsGermanBankExport()
    {
        await using var host = await CreateHostAsync();

        var result = await PostDataAsync(host, "/api/v1/import/analyze", new { content = Encode(StatementSeptember) });

        var settings = result.GetProperty("settings");
        Assert.Equal("windows-1252", settings.GetProperty("encoding").GetString());
        Assert.Equal(";", settings.GetProperty("delimiter").GetString());
        Assert.Equal(2, settings.GetProperty("headerLine").GetInt32());
        Assert.Equal(",", settings.GetProperty("decimalSeparator").GetString());
        Assert.Equal("dd.MM.yyyy", settings.GetProperty("dateFormat").GetString());
        Assert.Equal(4, result.GetProperty("rowCount").GetInt32());
        Assert.Equal(2, result.GetProperty("preamble").GetArrayLength());

        var columns = result.GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal("Auftraggeber / Begünstigter", columns[1].GetProperty("name").GetString());
        Assert.Equal("date", columns[0].GetProperty("inferredType").GetString());
        Assert.Equal("float", columns[3].GetProperty("inferredType").GetString());
    }

    [Fact]
    public async Task Analyze_ReportsBlankCellsAndDistinctValuesPerColumn()
    {
        await using var host = await CreateHostAsync();
        var file = StatementSeptember + "\n\"05.09.2026\";\"Kiosk\";\"\";\"-2,00\";\"lebensmittel\";\n";

        var result = await PostDataAsync(host, "/api/v1/import/analyze", new { content = Encode(file) });

        var columns = result.GetProperty("columns").EnumerateArray().ToList();
        var purpose = columns.Single(c => c.GetProperty("name").GetString() == "Verwendungszweck");
        Assert.Equal(1, purpose.GetProperty("blank").GetInt32());
        var category = columns.Single(c => c.GetProperty("name").GetString() == "Kategorie");
        Assert.Equal(0, category.GetProperty("blank").GetInt32());
        // "lebensmittel" and "Lebensmittel" are one value.
        Assert.Equal(2, category.GetProperty("distinctCount").GetInt32());
        Assert.Equal(["Lebensmittel", "Gehalt"], category.GetProperty("distinct").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task Import_WithDistinctKeys_FillsALookupTableOncePerValue()
    {
        await using var host = await CreateHostAsync();
        var file = StatementSeptember + "\n\"05.09.2026\";\"Kiosk\";\"Zeitung\";\"-2,00\";\"lebensmittel\";\n";
        object Body() => new
        {
            content = Encode(file),
            mapping = new[] { new { source = "Kategorie", target = "name" } },
            keyColumns = new[] { "name" },
            distinctKeys = true
        };

        var first = await PostDataAsync(host, "/api/v1/import/categories", Body());
        Assert.Equal(2, first.GetProperty("inserted").GetInt32());
        Assert.Equal(3, first.GetProperty("duplicates").GetInt32());
        Assert.Equal(0, first.GetProperty("invalid").GetInt32());

        var again = await PostDataAsync(host, "/api/v1/import/categories", Body());
        Assert.Equal(0, again.GetProperty("inserted").GetInt32());
        Assert.Equal(5, again.GetProperty("duplicates").GetInt32());
    }

    [Fact]
    public async Task Import_ThenReimport_AddsRowsOnceAndKeepsRealRepeats()
    {
        await using var host = await CreateHostAsync();
        await SeedCategoriesAsync(host);

        var preview = await PostDataAsync(host, "/api/v1/import/transactions/preview", RunBody(StatementSeptember));
        Assert.True(preview.GetProperty("dryRun").GetBoolean());
        Assert.Equal(4, preview.GetProperty("new").GetInt32());
        Assert.Equal(0, preview.GetProperty("inserted").GetInt32());
        Assert.Equal(0, await CountAsync(host));

        var first = await PostDataAsync(host, "/api/v1/import/transactions", RunBody(StatementSeptember));
        Assert.Equal(4, first.GetProperty("inserted").GetInt32());
        Assert.Equal(4, await CountAsync(host));

        var rows = await ListAsync(host);
        var groceries = rows.First(r => r.GetProperty("counterparty").GetString() == "REWE Markt");
        Assert.Equal("2026-09-01", groceries.GetProperty("booked_on").GetString()![..10]);
        Assert.Equal(-45.90m, groceries.GetProperty("amount").GetDecimal());
        Assert.Equal(1, groceries.GetProperty("category_id").GetInt64());

        // The same statement again: everything is already there.
        var second = await PostDataAsync(host, "/api/v1/import/transactions", RunBody(StatementSeptember));
        Assert.Equal(0, second.GetProperty("inserted").GetInt32());
        Assert.Equal(4, second.GetProperty("duplicates").GetInt32());

        // An overlapping export with a third coffee: only the missing one is new.
        var overlapping = StatementSeptember + "\n\"03.09.2026\";\"Bäckerei Müller\";\"Kaffee\";\"-3,20\";\"Lebensmittel\";\n";
        var third = await PostDataAsync(host, "/api/v1/import/transactions", RunBody(overlapping));
        Assert.Equal(1, third.GetProperty("inserted").GetInt32());
        Assert.Equal(4, third.GetProperty("duplicates").GetInt32());
        Assert.Equal(5, await CountAsync(host));
    }

    [Fact]
    public async Task Import_FillsAColumnWithAFixedValue_ForEveryRow()
    {
        await using var host = await CreateHostAsync();
        await SeedCategoriesAsync(host);

        // A bank export belongs to one account: here every row gets the category "Gehalt" by name.
        var body = new
        {
            content = Encode(StatementSeptember),
            mapping = new object[]
            {
                new { source = "Buchungstag", target = "booked_on" },
                new { source = "Auftraggeber / Begünstigter", target = "counterparty" },
                new { source = "Betrag (EUR)", target = "amount" },
                new { target = "purpose", value = "Kontoauszug September" },
                new { target = "category_id", value = "Gehalt" }
            },
            keyColumns = new[] { "booked_on", "counterparty", "amount" }
        };
        var imported = await PostDataAsync(host, "/api/v1/import/transactions", body);
        Assert.Equal(4, imported.GetProperty("inserted").GetInt32());
        var rows = await ListAsync(host);
        var gehaltId = (await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/categories?filter[name]=Gehalt")).GetProperty("data")[0].GetProperty("id").GetInt64();
        Assert.All(rows, r =>
        {
            Assert.Equal("Kontoauszug September", r.GetProperty("purpose").GetString());
            Assert.Equal(gehaltId, r.GetProperty("category_id").GetInt64());
        });

        // A column takes a file column or a fixed value, not both; a name that matches no record is reported.
        using var both = await host.Client.PostAsJsonAsync("/api/v1/import/transactions", new
        {
            content = Encode(StatementSeptember),
            mapping = new object[] { new { source = "Buchungstag", target = "booked_on", value = "2026-09-01" }, new { source = "Betrag (EUR)", target = "amount" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        var unknown = await PostDataAsync(host, "/api/v1/import/transactions/preview", new
        {
            content = Encode(StatementSeptember),
            mapping = new object[] { new { source = "Buchungstag", target = "booked_on" }, new { source = "Betrag (EUR)", target = "amount" }, new { target = "category_id", value = "Urlaub" } }
        });
        Assert.True(unknown.GetProperty("invalid").GetInt32() > 0);
    }

    [Fact]
    public async Task Preview_ReportsInvalidRowsWithTheirFileLine()
    {
        await using var host = await CreateHostAsync();
        await SeedCategoriesAsync(host);
        var file = StatementSeptember +
            "\n\"31.02.2026\";\"Nobody\";\"Bad date\";\"-1,00\";\"Lebensmittel\";" +
            "\n\"04.09.2026\";\"Kino\";\"Film\";\"-12,00\";\"Freizeit\";\n";

        var preview = await PostDataAsync(host, "/api/v1/import/transactions/preview", RunBody(file));

        Assert.Equal(4, preview.GetProperty("new").GetInt32());
        Assert.Equal(2, preview.GetProperty("invalid").GetInt32());
        var invalid = preview.GetProperty("rows").EnumerateArray()
            .Where(r => r.GetProperty("status").GetString() == "invalid")
            .ToList();
        Assert.Equal(9, invalid[0].GetProperty("line").GetInt32());
        Assert.Equal("booked_on", invalid[0].GetProperty("errors")[0].GetProperty("field").GetString());
        Assert.Contains("Not a date", invalid[0].GetProperty("errors")[0].GetProperty("message").GetString());
        Assert.Equal("category_id", invalid[1].GetProperty("errors")[0].GetProperty("field").GetString());
        Assert.Contains("Freizeit", invalid[1].GetProperty("errors")[0].GetProperty("message").GetString());

        // Display values show what will be stored, references by name.
        var firstNew = preview.GetProperty("rows")[0].GetProperty("display");
        Assert.Equal("-45.90", firstNew.GetProperty("amount").GetString());
        Assert.Equal("2026-09-01", firstNew.GetProperty("booked_on").GetString());
        Assert.Equal("Lebensmittel", firstNew.GetProperty("category_id").GetString());

        // Executing skips the invalid rows and writes the rest.
        var run = await PostDataAsync(host, "/api/v1/import/transactions", RunBody(file));
        Assert.Equal(4, run.GetProperty("inserted").GetInt32());
        Assert.Equal(2, run.GetProperty("invalid").GetInt32());
    }

    [Fact]
    public async Task Run_RejectsMappingToUnknownColumnsAndKeysOutsideTheMapping()
    {
        await using var host = await CreateHostAsync();

        using var unknownColumn = await host.Client.PostAsJsonAsync("/api/v1/import/transactions/preview", new
        {
            content = Encode(StatementSeptember),
            mapping = new[] { new { source = "Buchungstag", target = "nope" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknownColumn.StatusCode);

        using var keyOutside = await host.Client.PostAsJsonAsync("/api/v1/import/transactions/preview", new
        {
            content = Encode(StatementSeptember),
            mapping = new[] { new { source = "Buchungstag", target = "booked_on" } },
            keyColumns = new[] { "amount" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, keyOutside.StatusCode);
    }

    [Fact]
    public async Task Profiles_CreateListUpdateDelete()
    {
        await using var host = await CreateHostAsync();
        var config = new
        {
            settings = new { encoding = "windows-1252", delimiter = ";", decimalSeparator = ",", dateFormat = "dd.MM.yyyy" },
            mapping = Mapping,
            keyColumns = KeyColumns
        };

        using var created = await host.Client.PostAsJsonAsync("/api/v1/import/transactions/profiles", new { name = "DKB Girokonto", config });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var profile = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var id = profile.GetProperty("id").GetInt64();
        Assert.Equal("transactions", profile.GetProperty("tableName").GetString());
        Assert.Equal(5, profile.GetProperty("config").GetProperty("mapping").GetArrayLength());

        using var duplicateName = await host.Client.PostAsJsonAsync("/api/v1/import/transactions/profiles", new { name = "dkb girokonto", config });
        Assert.Equal(HttpStatusCode.Conflict, duplicateName.StatusCode);

        using var renamed = await host.Client.PutAsJsonAsync($"/api/v1/import/transactions/profiles/{id}", new { name = "DKB", config });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var list = await GetDataAsync(host, "/api/v1/import/transactions/profiles");
        Assert.Equal("DKB", Assert.Single(list.EnumerateArray()).GetProperty("name").GetString());

        using var deleted = await host.Client.DeleteAsync($"/api/v1/import/transactions/profiles/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, (await GetDataAsync(host, "/api/v1/import/transactions/profiles")).GetArrayLength());
    }

    [Fact]
    public async Task Import_RequiresEditorRole()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(FinanceSchema);

        using var register = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new { username = "bootstrap-admin", password = "supersecure" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var adminToken = await LoginAsync(host, "bootstrap-admin", "supersecure");

        using var createViewer = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = JsonContent.Create(new { username = "viewer", password = "anotherpass", role = "viewer" })
        };
        createViewer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var viewerCreated = await host.Client.SendAsync(createViewer);
        Assert.Equal(HttpStatusCode.Created, viewerCreated.StatusCode);
        var viewerToken = await LoginAsync(host, "viewer", "anotherpass");

        using var analyze = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import/analyze")
        {
            Content = JsonContent.Create(new { content = Encode(StatementSeptember) })
        };
        analyze.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var forbidden = await host.Client.SendAsync(analyze);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    // ── Helpers ──

    internal static object RunBody(string file) => new
    {
        content = Encode(file),
        mapping = Mapping,
        keyColumns = KeyColumns
    };

    internal static string Encode(string text) => Convert.ToBase64String(Encoding.GetEncoding(1252).GetBytes(text));

    internal static async Task SeedCategoriesAsync(ApiIntegrationTestHost host)
    {
        foreach (var name in new[] { "Lebensmittel", "Gehalt" })
        {
            using var response = await host.Client.PostAsJsonAsync("/api/v1/data/categories", new { name });
            response.EnsureSuccessStatusCode();
        }
    }

    internal static async Task<JsonElement> PostDataAsync(ApiIntegrationTestHost host, string url, object body)
    {
        using var response = await host.Client.PostAsJsonAsync(url, body);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {payload}");
        return payload.GetProperty("data");
    }

    private static async Task<JsonElement> GetDataAsync(ApiIntegrationTestHost host, string url)
    {
        var payload = await host.Client.GetFromJsonAsync<JsonElement>(url);
        return payload.GetProperty("data");
    }

    internal static async Task<List<JsonElement>> ListAsync(ApiIntegrationTestHost host)
    {
        var payload = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/transactions?per_page=100&sort=id");
        return payload.GetProperty("data").EnumerateArray().ToList();
    }

    internal static async Task<long> CountAsync(ApiIntegrationTestHost host)
    {
        var payload = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/transactions?per_page=1");
        return payload.GetProperty("meta").GetProperty("total").GetInt64();
    }

    private static async Task<string> LoginAsync(ApiIntegrationTestHost host, string username, string password)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<Bento.Api.Models.AuthTokenResponse>();
        return payload!.AccessToken;
    }

    private static async Task<ApiIntegrationTestHost> CreateHostAsync()
    {
        var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(FinanceSchema);
        return host;
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresImportApiTests
{
    [PostgresFact]
    public async Task Import_ThenReimport_WorksOnPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(ImportApiTests.FinanceSchema);
        await ImportApiTests.SeedCategoriesAsync(host);

        // A date column written through the plain CRUD route binds a DateOnly too.
        using var created = await host.Client.PostAsJsonAsync("/api/v1/data/transactions", new { booked_on = "2026-08-31", amount = 1.5 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var cleanup = await host.Client.DeleteAsync($"/api/v1/data/transactions/{(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetInt64()}");

        var first = await ImportApiTests.PostDataAsync(host, "/api/v1/import/transactions", ImportApiTests.RunBody(ImportApiTests.StatementSeptember));
        Assert.Equal(4, first.GetProperty("inserted").GetInt32());

        // Dates come back as DateOnly/DateTime and amounts as numeric on
        // Postgres; the duplicate check must still recognise every row.
        var second = await ImportApiTests.PostDataAsync(host, "/api/v1/import/transactions", ImportApiTests.RunBody(ImportApiTests.StatementSeptember));
        Assert.Equal(0, second.GetProperty("inserted").GetInt32());
        Assert.Equal(4, second.GetProperty("duplicates").GetInt32());

        var rows = await ImportApiTests.ListAsync(host);
        var groceries = rows.First(r => r.GetProperty("counterparty").GetString() == "REWE Markt");
        Assert.Equal("2026-09-01", groceries.GetProperty("booked_on").GetString()![..10]);
        Assert.Equal(-45.90m, groceries.GetProperty("amount").GetDecimal());

        using var profile = await host.Client.PostAsJsonAsync("/api/v1/import/transactions/profiles", new
        {
            name = "DKB",
            config = new { mapping = ImportApiTests.Mapping, keyColumns = ImportApiTests.KeyColumns }
        });
        Assert.Equal(HttpStatusCode.Created, profile.StatusCode);
    }
}
