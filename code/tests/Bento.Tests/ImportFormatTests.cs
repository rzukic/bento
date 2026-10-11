using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bento.Api.Services.Import;

namespace Bento.Tests;

/// <summary>
/// Step 5.1: Excel and JSON files import like CSV files (analysis, mapping,
/// preview), and a re-import with key columns updates rows that changed at
/// the source instead of skipping them.
/// </summary>
public sealed class ImportFormatTests
{
    [Fact]
    public async Task Excel_ReadsTheChosenSheet_WithDatesNumbersAndFormulas()
    {
        await using var host = await CreateHostAsync();
        var workbook = Workbook(
            ("Notes", """<row r="1"><c r="A1" t="inlineStr"><is><t>Nothing here</t></is></c></row>"""),
            ("Konto", """
                <row r="1"><c r="A1" t="inlineStr"><is><t>Kontoauszug September</t></is></c></row>
                <row r="2"><c r="A2" t="s"><v>0</v></c><c r="B2" t="s"><v>1</v></c><c r="C2" t="s"><v>2</v></c></row>
                <row r="3"><c r="A3" s="1"><v>46266</v></c><c r="B3" t="s"><v>3</v></c><c r="C3"><v>-45.9</v></c></row>
                <row r="5"><c r="A5" s="1"><v>46267</v></c><c r="B5" t="inlineStr"><is><t>Arbeitgeber GmbH</t></is></c><c r="C5"><f>3000+250</f><v>3250</v></c></row>
                """));

        // Without a sheet: the one with the rows, not the cover sheet before it.
        var analyzed = await ImportApiTests.PostDataAsync(host, "/api/v1/import/analyze", new { content = workbook, fileName = "konto.xlsx" });
        var settings = analyzed.GetProperty("settings");
        Assert.Equal("xlsx", settings.GetProperty("format").GetString());
        Assert.Equal("Konto", settings.GetProperty("sheet").GetString());
        Assert.Equal(["Notes", "Konto"], settings.GetProperty("sheets").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(["Buchungstag", "Empfänger", "Betrag"], analyzed.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Equal("date", analyzed.GetProperty("columns")[0].GetProperty("inferredType").GetString());
        Assert.Equal(2, analyzed.GetProperty("rowCount").GetInt32());
        // The title row above the header is skipped.
        Assert.Equal(1, settings.GetProperty("headerLine").GetInt32());
        Assert.Equal("Kontoauszug September", analyzed.GetProperty("preamble")[0][0].GetString());

        var result = await ImportApiTests.PostDataAsync(host, "/api/v1/import/transactions", new
        {
            content = workbook,
            fileName = "konto.xlsx",
            settings = new { sheet = "Konto" },
            mapping = new object[]
            {
                new { source = "Buchungstag", target = "booked_on" },
                new { source = "Empfänger", target = "counterparty" },
                new { source = "Betrag", target = "amount" }
            }
        });
        Assert.Equal(2, result.GetProperty("inserted").GetInt32());
        // The file's row numbers, as Excel shows them.
        Assert.Equal([3, 5], result.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("line").GetInt32()));
        var rows = await ImportApiTests.ListAsync(host);
        Assert.Equal(["2026-09-01|REWE Markt|-45.9", "2026-09-02|Arbeitgeber GmbH|3250"],
            rows.Select(r => $"{r.GetProperty("booked_on").GetString()![..10]}|{r.GetProperty("counterparty").GetString()}|{r.GetProperty("amount").GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
    }

    [Fact]
    public async Task Excel_ThatIsNotOne_SaysSo()
    {
        await using var host = await CreateHostAsync();
        using var response = await host.Client.PostAsJsonAsync("/api/v1/import/analyze", new { content = Convert.ToBase64String([(byte)'P', (byte)'K', 3, 4, 0, 0, 1, 2]), fileName = "broken.xlsx" });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Contains("not an Excel workbook", body);
    }

    [Fact]
    public async Task Json_FindsTheListOfRecords_AndFlattensNestedValues()
    {
        await using var host = await CreateHostAsync();
        var json = Convert.ToBase64String(Encoding.UTF8.GetBytes("""
            { "account": "DE12", "data": { "transactions": [
                { "date": "2026-09-01", "payee": { "name": "REWE Markt" }, "amount": -45.9, "tags": ["food"] },
                { "date": "2026-09-02", "payee": { "name": "Arbeitgeber GmbH" }, "amount": 3250, "booked": true }
            ] } }
            """));

        var analyzed = await ImportApiTests.PostDataAsync(host, "/api/v1/import/analyze", new { content = json, fileName = "api.json" });
        Assert.Equal("json", analyzed.GetProperty("settings").GetProperty("format").GetString());
        Assert.Equal("data.transactions", analyzed.GetProperty("settings").GetProperty("jsonPath").GetString());
        Assert.Equal(["date", "payee.name", "amount", "tags", "booked"], analyzed.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Equal("[\"food\"]", analyzed.GetProperty("previewRows")[0][3].GetString());

        var result = await ImportApiTests.PostDataAsync(host, "/api/v1/import/transactions", new
        {
            content = json,
            mapping = new object[]
            {
                new { source = "date", target = "booked_on" },
                new { source = "payee.name", target = "counterparty" },
                new { source = "amount", target = "amount" }
            }
        });
        Assert.Equal(2, result.GetProperty("inserted").GetInt32());
        Assert.Equal(-45.9m, (await ImportApiTests.ListAsync(host))[0].GetProperty("amount").GetDecimal());

        // A path that is not there is named.
        using var missing = await host.Client.PostAsJsonAsync("/api/v1/import/analyze", new { content = json, settings = new { jsonPath = "data.items" } });
        Assert.Contains("nothing at 'data.items'", await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reimport_UpdatesChangedRows_AndLeavesTheRestAlone() => await ReimportAsync(await CreateHostAsync());

    internal static async Task ReimportAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        await ImportApiTests.SeedCategoriesAsync(host);
        object Body(string file, bool updateChanged) => new
        {
            content = ImportApiTests.Encode(file),
            mapping = new object[]
            {
                new { source = "Buchungstag", target = "booked_on" },
                new { source = "Auftraggeber / Begünstigter", target = "counterparty" },
                new { source = "Verwendungszweck", target = "purpose" },
                new { source = "Betrag (EUR)", target = "amount" }
            },
            // The amount is not part of the key: it is what may change.
            keyColumns = new[] { "booked_on", "counterparty", "purpose" },
            updateChanged
        };
        var first = await ImportApiTests.PostDataAsync(host, "/api/v1/import/transactions", Body(ImportApiTests.StatementSeptember, false));
        Assert.Equal(4, first.GetProperty("inserted").GetInt32());

        // Categorised by hand after the import: a re-import must keep it.
        var rewe = (await ImportApiTests.ListAsync(host))[0];
        using (var patched = await host.Client.PatchAsJsonAsync($"/api/v1/data/transactions/{rewe.GetProperty("id")}", new { category_id = 1 }))
            patched.EnsureSuccessStatusCode();

        // The bank corrected REWE's amount, the salary has no amount in this export, and a new booking came.
        var corrected = ImportApiTests.StatementSeptember
            .Replace("\"-45,90\"", "\"-46,10\"")
            .Replace("\"3.250,00\"", "\"\"")
            + "\n\"04.09.2026\";\"Stadtwerke\";\"Strom\";\"-60,00\";\"\";";

        // Without updating, the changed row is a duplicate as before.
        var skipped = await ImportApiTests.PostDataAsync(host, "/api/v1/import/transactions/preview", Body(corrected, false));
        Assert.Equal(0, skipped.GetProperty("changed").GetInt32());
        Assert.Equal(4, skipped.GetProperty("duplicates").GetInt32());

        var preview = await ImportApiTests.PostDataAsync(host, "/api/v1/import/transactions/preview", Body(corrected, true));
        Assert.Equal(1, preview.GetProperty("changed").GetInt32());
        Assert.Equal(1, preview.GetProperty("new").GetInt32());
        Assert.Equal(3, preview.GetProperty("duplicates").GetInt32());
        var changed = preview.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("status").GetString() == "changed");
        Assert.Equal("-45.9", changed.GetProperty("before").GetProperty("amount").GetString());
        Assert.Equal(["amount"], changed.GetProperty("before").EnumerateObject().Select(p => p.Name));

        var run = await ImportApiTests.PostDataAsync(host, "/api/v1/import/transactions", Body(corrected, true));
        Assert.Equal(1, run.GetProperty("updated").GetInt32());
        Assert.Equal(1, run.GetProperty("inserted").GetInt32());

        var rows = await ImportApiTests.ListAsync(host);
        Assert.Equal(5, rows.Count);
        Assert.Equal(-46.1m, rows[0].GetProperty("amount").GetDecimal());
        Assert.Equal(1, rows[0].GetProperty("category_id").GetInt32());
        Assert.Equal(3250m, rows[1].GetProperty("amount").GetDecimal());

        // Nothing changed since: a third run writes nothing.
        var again = await ImportApiTests.PostDataAsync(host, "/api/v1/import/transactions/preview", Body(corrected, true));
        Assert.Equal(0, again.GetProperty("changed").GetInt32());
        Assert.Equal(0, again.GetProperty("new").GetInt32());
    }

    [Fact]
    public void ExcelFormatCodes_AreDatesOnlyWithDateParts()
    {
        Assert.True(XlsxReader.IsDateFormat("dd.mm.yyyy"));
        Assert.True(XlsxReader.IsDateFormat("[$-407]d. mmmm yyyy;@"));
        Assert.False(XlsxReader.IsDateFormat("#,##0.00 \"EUR\""));
        Assert.False(XlsxReader.IsDateFormat("[Red]0.00"));
        Assert.Equal(27, XlsxReader.ColumnIndex("AB12"));
    }

    // ── Helpers ──

    private static Task<ApiIntegrationTestHost> CreateHostAsync() => CreateHostAsync(ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Schema:AutoMigrateOnStartup"] = "false"
    }));

    internal static async Task<ApiIntegrationTestHost> CreateHostAsync(Task<ApiIntegrationTestHost> starting)
    {
        var host = await starting;
        await host.ApplySchemaAsync(ImportApiTests.FinanceSchema);
        return host;
    }

    /// <summary>A minimal .xlsx with the given sheets, base64; style 1 is a date (format 14).</summary>
    private static string Workbook(params (string Name, string Rows)[] sheets)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
            const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            Add("xl/workbook.xml", $"""<workbook xmlns="{main}" xmlns:r="{rel}"><sheets>{string.Concat(sheets.Select((s, i) => $"""<sheet name="{s.Name}" sheetId="{i + 1}" r:id="rId{i + 1}"/>"""))}</sheets></workbook>""");
            Add("xl/_rels/workbook.xml.rels", $"""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{string.Concat(sheets.Select((_, i) => $"""<Relationship Id="rId{i + 1}" Type="{rel}/worksheet" Target="worksheets/sheet{i + 1}.xml"/>"""))}</Relationships>""");
            Add("xl/sharedStrings.xml", $"""<sst xmlns="{main}"><si><t>Buchungstag</t></si><si><r><t>Empf</t></r><r><t>änger</t></r></si><si><t>Betrag</t></si><si><t>REWE Markt</t><rPh><t>ignored</t></rPh></si></sst>""");
            Add("xl/styles.xml", $"""<styleSheet xmlns="{main}"><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14"/></cellXfs></styleSheet>""");
            for (var i = 0; i < sheets.Length; i++)
                Add($"xl/worksheets/sheet{i + 1}.xml", $"""<worksheet xmlns="{main}"><sheetData>{sheets[i].Rows}</sheetData></worksheet>""");
        }
        return Convert.ToBase64String(stream.ToArray());
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresImportFormatTests
{
    [PostgresFact]
    public async Task Reimport_UpdatesChangedRows_OnPostgres() =>
        await ImportFormatTests.ReimportAsync(await ImportFormatTests.CreateHostAsync(ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        })));
}
