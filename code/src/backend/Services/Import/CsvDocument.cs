using System.Text;

namespace Bento.Api.Services.Import;

/// <summary>
/// A decoded and split file: every physical record as raw cells, plus the
/// settings it was read with (detected unless the caller fixed them). Excel
/// and JSON files are read into the same records (<see cref="Format"/>).
/// </summary>
public sealed record CsvDocument(
    string Encoding,
    char Delimiter,
    int HeaderLine,
    IReadOnlyList<CsvRecord> Records)
{
    /// <summary><c>csv</c>, <c>xlsx</c> or <c>json</c>.</summary>
    public string Format { get; init; } = "csv";
    /// <summary>Excel: the workbook's sheets and the one read.</summary>
    public IReadOnlyList<string>? Sheets { get; init; }
    public string? Sheet { get; init; }
    /// <summary>JSON: where the list of records was found (empty: the top).</summary>
    public string? JsonPath { get; init; }

    /// <summary>The header cells (trimmed; empty headers become "Column N").</summary>
    public IReadOnlyList<string> Headers =>
        HeaderLine < Records.Count
            ? Records[HeaderLine].Cells.Select((cell, index) =>
                string.IsNullOrWhiteSpace(cell) ? $"Column {index + 1}" : cell.Trim()).ToList()
            : [];

    /// <summary>Records above the header (bank exports put account details there).</summary>
    public IEnumerable<string[]> Preamble => Records.Take(HeaderLine).Select(record => record.Cells);

    /// <summary>Data records below the header with the line they start on in the file.</summary>
    public IEnumerable<(int Line, string[] Cells)> DataRecords =>
        Records
            .Skip(HeaderLine + 1)
            .Where(record => record.Cells.Any(cell => !string.IsNullOrWhiteSpace(cell)))
            .Select(record => (record.Line, record.Cells));
}

/// <summary>One CSV record and the 1-based line of the file it starts on.</summary>
public sealed record CsvRecord(int Line, string[] Cells)
{
    public int Length => Cells.Length;
}

/// <summary>
/// Reads CSV files the way bank and broker exports come: UTF-8 (with or
/// without BOM) or Windows-1252, separated by <c>;</c>, <c>,</c>, tab or
/// <c>|</c>, quoted per RFC 4180, sometimes with a few lines of account
/// details above the header.
/// </summary>
public static class CsvReader
{
    public const string Utf8 = "utf-8";
    public const string Windows1252 = "windows-1252";

    private static readonly char[] CandidateDelimiters = [';', ',', '\t', '|'];

    static CsvReader()
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static IReadOnlyList<string> SupportedEncodings { get; } = [Utf8, Windows1252, "iso-8859-1", "utf-16"];

    public static IReadOnlyList<char> SupportedDelimiters => CandidateDelimiters;

    /// <summary>
    /// Decodes and splits <paramref name="bytes"/>. Encoding, delimiter and
    /// header line are detected unless given.
    /// </summary>
    public static CsvDocument Read(byte[] bytes, string? encoding = null, char? delimiter = null, int? headerLine = null)
    {
        var (text, encodingName) = Decode(bytes, encoding);
        var chosenDelimiter = delimiter ?? DetectDelimiter(text);
        var records = Split(text, chosenDelimiter);
        var chosenHeader = headerLine is { } fixedHeader && fixedHeader >= 0 && fixedHeader < Math.Max(records.Count, 1)
            ? fixedHeader
            : DetectHeaderLine(records);
        return new CsvDocument(encodingName, chosenDelimiter, chosenHeader, records);
    }

    public static (string Text, string Encoding) Decode(byte[] bytes, string? encoding = null)
    {
        if (!string.IsNullOrWhiteSpace(encoding))
        {
            var chosen = System.Text.Encoding.GetEncoding(encoding.Trim());
            var preamble = chosen.GetPreamble();
            var offset = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
            return (chosen.GetString(bytes, offset, bytes.Length - offset), chosen.WebName);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return (System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), Utf8);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            return (System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "utf-16");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return (System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "utf-16BE");

        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (strictUtf8.GetString(bytes), Utf8);
        }
        catch (DecoderFallbackException)
        {
            // Not valid UTF-8: German bank exports are almost always Windows-1252.
            return (System.Text.Encoding.GetEncoding(1252).GetString(bytes), Windows1252);
        }
    }

    /// <summary>
    /// The delimiter that splits the most lines into the same number of cells
    /// (more than one). Lines above a header often have a different count, so
    /// the most common count wins rather than the first line's.
    /// </summary>
    public static char DetectDelimiter(string text)
    {
        var best = ';';
        var bestScore = -1;
        foreach (var candidate in CandidateDelimiters)
        {
            var counts = Split(text, candidate, maxRecords: 50)
                .Select(record => record.Length)
                .Where(count => count > 1)
                .GroupBy(count => count)
                .Select(group => (Count: group.Key, Lines: group.Count()))
                .OrderByDescending(group => group.Lines)
                .ThenByDescending(group => group.Count)
                .FirstOrDefault();
            // Lines first, then cells per line: a ";" file whose text happens to
            // contain commas still scores higher on ";".
            var score = counts.Lines * 1000 + counts.Count;
            if (counts.Lines > 0 && score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>
    /// The first line that has the file's usual number of cells and is
    /// followed by a line with the same number — skipping preamble lines such
    /// as "Kontonummer:;DE12…" above the real header.
    /// </summary>
    public static int DetectHeaderLine(IReadOnlyList<CsvRecord> records)
    {
        if (records.Count == 0)
            return 0;

        var usual = records
            .Select(record => record.Length)
            .GroupBy(count => count)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key)
            .First().Key;

        for (var index = 0; index < records.Count; index++)
        {
            if (records[index].Length != usual || records[index].Cells.All(string.IsNullOrWhiteSpace))
                continue;
            var next = index + 1 < records.Count ? records[index + 1] : null;
            if (next is null || next.Length == usual)
                return index;
        }

        return 0;
    }

    /// <summary>RFC 4180 split: quoted cells may contain the delimiter, line breaks and doubled quotes.</summary>
    public static List<CsvRecord> Split(string text, char delimiter, int maxRecords = int.MaxValue)
    {
        var records = new List<CsvRecord>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var cellStarted = false;
        var recordHasContent = false;
        var line = 1;
        var recordLine = 1;

        void EndCell()
        {
            cells.Add(cell.ToString());
            cell.Clear();
            cellStarted = false;
        }

        void EndRecord()
        {
            EndCell();
            if (recordHasContent || cells.Count > 1)
                records.Add(new CsvRecord(recordLine, cells.ToArray()));
            cells.Clear();
            recordHasContent = false;
            recordLine = line;
        }

        for (var index = 0; index < text.Length && records.Count < maxRecords; index++)
        {
            var current = text[index];
            if (inQuotes)
            {
                if (current == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        cell.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (current == '\n')
                        line++;
                    cell.Append(current);
                }
                continue;
            }

            if (current == '"' && !cellStarted)
            {
                inQuotes = true;
                cellStarted = true;
                recordHasContent = true;
            }
            else if (current == delimiter)
            {
                EndCell();
                recordHasContent = true;
            }
            else if (current == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                    index++;
                line++;
                EndRecord();
            }
            else if (current == '\n')
            {
                line++;
                EndRecord();
            }
            else
            {
                cell.Append(current);
                cellStarted = true;
                if (!char.IsWhiteSpace(current))
                    recordHasContent = true;
            }
        }

        if (records.Count < maxRecords && (cell.Length > 0 || cells.Count > 0 || recordHasContent))
            EndRecord();

        return records;
    }
}
