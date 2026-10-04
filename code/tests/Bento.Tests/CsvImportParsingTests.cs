using System.Text;
using Bento.Api.Db;
using Bento.Api.Services.Import;

namespace Bento.Tests;

public sealed class CsvImportParsingTests
{
    static CsvImportParsingTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly ImportLocale German = ImportLocale.German;
    private static readonly ImportLocale English = new(".", "dd/MM/yyyy");

    // ── Reading ──

    [Fact]
    public void Read_GermanBankExport_DetectsWindows1252SemicolonAndHeaderBelowPreamble()
    {
        const string text = """
            "Kontonummer:";"DE12 1203 0000 0012 3456 78";
            "Kontostand vom 30.09.2026:";"2.345,67 EUR";

            "Buchungstag";"Empfänger";"Verwendungszweck";"Betrag (EUR)";
            "01.09.2026";"Bäckerei Müller";"Brötchen; Kaffee";"-3,20";
            "02.09.2026";"Arbeitgeber GmbH";"Gehalt September";"3.250,00";
            """;
        var bytes = Encoding.GetEncoding(1252).GetBytes(text);

        var document = CsvReader.Read(bytes);

        Assert.Equal(CsvReader.Windows1252, document.Encoding);
        Assert.Equal(';', document.Delimiter);
        Assert.Equal(2, document.HeaderLine);
        Assert.Equal(["Buchungstag", "Empfänger", "Verwendungszweck", "Betrag (EUR)", "Column 5"], document.Headers);
        var rows = document.DataRecords.ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Bäckerei Müller", rows[0].Cells[1]);
        Assert.Equal("Brötchen; Kaffee", rows[0].Cells[2]);
        Assert.Equal(5, rows[0].Line);
    }

    [Fact]
    public void Read_Utf8WithBom_AndCommaDelimiter_KeepsQuotedCommasNewlinesAndQuotes()
    {
        const string text = "date,payee,memo,amount\r\n2026-09-01,\"Shop, Inc.\",\"line one\r\nline two\",-12.50\r\n2026-09-02,Cafe,\"say \"\"hi\"\"\",3.00\r\n";
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();

        var document = CsvReader.Read(bytes);

        Assert.Equal(CsvReader.Utf8, document.Encoding);
        Assert.Equal(',', document.Delimiter);
        Assert.Equal(0, document.HeaderLine);
        var rows = document.DataRecords.ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Shop, Inc.", rows[0].Cells[1]);
        Assert.Equal("line one\r\nline two", rows[0].Cells[2]);
        Assert.Equal("say \"hi\"", rows[1].Cells[2]);
    }

    [Fact]
    public void Read_SemicolonFileWithCommasInText_PrefersSemicolon()
    {
        var bytes = Encoding.UTF8.GetBytes("a;b;c\n1,5;x, y;z\n2,5;q, r;s\n");

        Assert.Equal(';', CsvReader.Read(bytes).Delimiter);
    }

    [Fact]
    public void Read_FixedSettings_OverrideDetection()
    {
        var bytes = Encoding.UTF8.GetBytes("title line\nh1|h2\nv1|v2\n");

        var document = CsvReader.Read(bytes, encoding: "utf-8", delimiter: '|', headerLine: 1);

        Assert.Equal(["h1", "h2"], document.Headers);
        Assert.Single(document.DataRecords);
    }

    // ── Numbers ──

    [Theory]
    [InlineData("-1.234,56", -1234.56)]
    [InlineData("1.234,56-", -1234.56)]
    [InlineData("12,50 €", 12.50)]
    [InlineData("EUR 3,00", 3.00)]
    [InlineData("(3,00)", -3.00)]
    [InlineData("+7", 7)]
    [InlineData("1 234,5", 1234.5)]
    [InlineData("0,99", 0.99)]
    public void TryParseNumber_German(string raw, double expected)
    {
        Assert.True(ImportValueParser.TryParseNumber(raw, German, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("-1,234.56", -1234.56)]
    [InlineData("$12.50", 12.50)]
    [InlineData("1234", 1234)]
    public void TryParseNumber_English(string raw, double expected)
    {
        Assert.True(ImportValueParser.TryParseNumber(raw, English, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("1.23,4")]
    [InlineData("12,5x")]
    [InlineData("31.12.2026")]
    [InlineData("")]
    public void TryParseNumber_RejectsMalformedGermanNumbers(string raw)
    {
        Assert.False(ImportValueParser.TryParseNumber(raw, German, out _));
    }

    // ── Dates ──

    [Theory]
    [InlineData("31.12.2026", "2026-12-31")]
    [InlineData("1.2.2026", "2026-02-01")]
    [InlineData("2026-12-31", "2026-12-31")]
    public void TryParseDate_German(string raw, string expected)
    {
        Assert.True(ImportValueParser.TryParseDate(raw, German, out var date));
        Assert.Equal(expected, date.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void TryParseDate_RejectsImpossibleDates()
    {
        Assert.False(ImportValueParser.TryParseDate("31.02.2026", German, out _));
    }

    [Theory]
    [InlineData("31.12.2026 14:05", "2026-12-31T14:05:00")]
    [InlineData("31.12.2026", "2026-12-31T00:00:00")]
    [InlineData("2026-12-31T14:05:00+02:00", "2026-12-31T12:05:00Z")]
    public void TryParseDateTime_KeepsWallTimeAndConvertsZonedValuesToUtc(string raw, string expected)
    {
        Assert.True(ImportValueParser.TryParseDateTime(raw, German, out var value));
        Assert.Equal(expected, value);
    }

    // ── Detection ──

    [Fact]
    public void DetectLocale_GermanCells()
    {
        var locale = ImportValueParser.DetectLocale(["01.09.2026", "-45,90", "Einkauf", "3.250,00", "15.09.2026"]);

        Assert.Equal(",", locale.DecimalSeparator);
        Assert.Equal("dd.MM.yyyy", locale.DateFormat);
    }

    [Fact]
    public void DetectLocale_IsoCells()
    {
        var locale = ImportValueParser.DetectLocale(["2026-09-01", "-45.90", "groceries"]);

        Assert.Equal(".", locale.DecimalSeparator);
        Assert.Equal("yyyy-MM-dd", locale.DateFormat);
    }

    [Fact]
    public void InferType_RecognisesDatesAmountsIntegersBooleansAndText()
    {
        Assert.Equal(ColumnType.Date, ImportValueParser.InferType(["01.09.2026", "15.09.2026"], German));
        Assert.Equal(ColumnType.Float, ImportValueParser.InferType(["-45,90", "3.250,00"], German));
        Assert.Equal(ColumnType.Currency, ImportValueParser.InferType(["-45,90 €", "3,00 €"], German));
        Assert.Equal(ColumnType.Integer, ImportValueParser.InferType(["1", "42", ""], German));
        Assert.Equal(ColumnType.Boolean, ImportValueParser.InferType(["ja", "nein"], German));
        Assert.Equal(ColumnType.Text, ImportValueParser.InferType(["REWE", "Miete"], German));
    }
}
