using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;

namespace Bento.Api.Services.Import;

/// <summary>
/// Reads one sheet of an Excel workbook (<c>.xlsx</c>, Office Open XML) into
/// the same records a CSV file gives: every cell as text, numbers in invariant
/// form (<c>1234.5</c>), dates as <c>yyyy-MM-dd</c> (with the time when it has
/// one), yes/no as <c>true</c>/<c>false</c>. Formulas give their last computed
/// value. A small reader on the platform's zip and XML classes; no dependency.
/// </summary>
public static partial class XlsxReader
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const long MaxUnpackedBytes = 300L * 1024 * 1024;

    /// <summary>Whether the bytes are a zip file (an <c>.xlsx</c> is one).</summary>
    public static bool LooksLikeXlsx(byte[] bytes) =>
        bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4;

    /// <summary>
    /// The sheet named <paramref name="sheet"/> as records, with the workbook's
    /// sheet names. Without a name, the sheet with the most rows of two or more
    /// values (a cover sheet with a title comes first in many exports). Throws
    /// <see cref="InvalidDataException"/> for a file that is not a readable workbook.
    /// </summary>
    public static (List<CsvRecord> Records, IReadOnlyList<string> Sheets, string Sheet) Read(byte[] bytes, string? sheet, long maxCells = 2_000_000)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var sheets = ReadSheets(zip);
            if (sheets.Count == 0)
                throw new InvalidDataException("The workbook has no sheets.");
            // A few MB of zip can unpack to gigabytes: refuse before reading.
            if (zip.Entries.Sum(e => e.Length) > MaxUnpackedBytes)
                throw new InvalidDataException($"The workbook unpacks to more than {MaxUnpackedBytes / 1024 / 1024} MB; export the sheet as CSV.");
            var strings = ReadSharedStrings(zip);
            var dateStyles = ReadDateStyles(zip);
            List<CsvRecord> Records(SheetInfo info) => ReadSheet(
                zip.GetEntry(info.Path) ?? throw new InvalidDataException($"The sheet '{info.Name}' is missing from the file."),
                strings, dateStyles, maxCells);
            var names = sheets.Select(s => s.Name).ToList();

            if (!string.IsNullOrWhiteSpace(sheet))
            {
                var named = sheets.FirstOrDefault(s => string.Equals(s.Name, sheet.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"The workbook has no sheet '{sheet}'.");
                return (Records(named), names, named.Name);
            }
            var best = sheets.Select(s => (Sheet: s, Records: Records(s)))
                .Select((read, index) => (read.Sheet, read.Records, Rows: read.Records.Count(r => r.Cells.Count(c => c.Length > 0) >= 2), Index: index))
                .OrderByDescending(read => read.Rows).ThenBy(read => read.Index)
                .First();
            return (best.Records, names, best.Sheet.Name);
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or IOException)
        {
            throw new InvalidDataException(e is InvalidDataException && e.Message.StartsWith("The ", StringComparison.Ordinal)
                ? e.Message
                : "The file is not an Excel workbook (.xlsx) that can be read.", e);
        }
    }

    private sealed record SheetInfo(string Name, string Path);

    private static List<SheetInfo> ReadSheets(ZipArchive zip)
    {
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (zip.GetEntry("xl/_rels/workbook.xml.rels") is { } rels)
        {
            foreach (var element in Elements(rels, "Relationship"))
            {
                var id = element.GetAttribute("Id");
                var target = element.GetAttribute("Target");
                if (id.Length > 0 && target.Length > 0)
                    targets[id] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            }
        }
        var workbook = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("The file is not an Excel workbook (.xlsx) that can be read.");
        var sheets = new List<SheetInfo>();
        foreach (var element in Elements(workbook, "sheet"))
        {
            var id = element.GetAttribute("id", Relationships);
            if (targets.TryGetValue(id, out var path))
                sheets.Add(new SheetInfo(element.GetAttribute("name"), path));
        }
        return sheets;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var result = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is not { } entry)
            return result;
        // <si> holds a <t>, or rich-text runs <r><t>…</t></r>; phonetic hints (<rPh>) are not text.
        foreach (var item in Elements(entry, "si"))
            result.Add(Text(item));
        return result;
    }

    /// <summary>The cell styles (index into cellXfs) whose number format is a date or time.</summary>
    private static HashSet<int> ReadDateStyles(ZipArchive zip)
    {
        var result = new HashSet<int>();
        if (zip.GetEntry("xl/styles.xml") is not { } entry)
            return result;
        var document = Load(entry);
        var custom = new Dictionary<int, string>();
        foreach (XmlElement format in document.GetElementsByTagName("numFmt", Main))
            if (int.TryParse(format.GetAttribute("numFmtId"), out var id))
                custom[id] = format.GetAttribute("formatCode");
        var cellXfs = document.GetElementsByTagName("cellXfs", Main).OfType<XmlElement>().FirstOrDefault();
        if (cellXfs is null)
            return result;
        var index = 0;
        foreach (var xf in cellXfs.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "xf"))
        {
            if (int.TryParse(xf.GetAttribute("numFmtId"), out var id) &&
                (id is >= 14 and <= 22 or >= 45 and <= 47 || custom.TryGetValue(id, out var code) && IsDateFormat(code)))
                result.Add(index);
            index++;
        }
        return result;
    }

    /// <summary>A format code with day, month or year parts outside quotes and colour or locale brackets.</summary>
    public static bool IsDateFormat(string code)
    {
        var plain = FormatNoise().Replace(code, "");
        return plain.IndexOfAny(['d', 'D', 'y', 'Y', 'm', 'M', 'h', 'H']) >= 0 && plain.IndexOfAny(['0', '#', '?']) < 0;
    }

    [GeneratedRegex("\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.|_.|\\*.")]
    private static partial Regex FormatNoise();

    private static List<CsvRecord> ReadSheet(ZipArchiveEntry entry, List<string> strings, HashSet<int> dateStyles, long maxCells)
    {
        var rows = new SortedDictionary<int, SortedDictionary<int, string>>();
        long cells = 0;
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = false });
        var rowNumber = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != Main)
                continue;
            if (reader.LocalName == "row")
            {
                rowNumber = int.TryParse(reader.GetAttribute("r"), out var r) ? r : rowNumber + 1;
                continue;
            }
            if (reader.LocalName != "c")
                continue;

            var reference = reader.GetAttribute("r");
            var type = reader.GetAttribute("t");
            var style = int.TryParse(reader.GetAttribute("s"), out var s) ? s : -1;
            var column = reference is null ? (rows.TryGetValue(rowNumber, out var current) && current.Count > 0 ? current.Keys.Max() + 1 : 0) : ColumnIndex(reference);
            if (reference is not null && RowOf(reference) is { } fromReference)
                rowNumber = fromReference;
            string? value = null;
            string? inline = null;
            if (!reader.IsEmptyElement)
            {
                using var cell = reader.ReadSubtree();
                while (cell.Read())
                {
                    if (cell.NodeType != XmlNodeType.Element)
                        continue;
                    if (cell.LocalName == "v")
                        value = cell.ReadElementContentAsString();
                    else if (cell.LocalName == "is")
                        inline = Text(cell);
                }
            }

            var text = type switch
            {
                "s" => int.TryParse(value, out var i) && i >= 0 && i < strings.Count ? strings[i] : "",
                "inlineStr" => inline ?? "",
                "b" => value == "1" ? "true" : "false",
                "e" => "",
                "str" => value ?? "",
                _ => value is null ? "" : dateStyles.Contains(style) ? DateText(value) : value
            };
            if (text.Length == 0)
                continue;
            if (++cells > maxCells)
                throw new InvalidDataException($"The sheet has more than {maxCells:N0} cells; split it or export a CSV.");
            if (!rows.TryGetValue(rowNumber, out var row))
                rows[rowNumber] = row = new SortedDictionary<int, string>();
            row[column] = text;
        }

        // Columns without a value stay as empty cells, so positions match the sheet; each record keeps its row
        // number and ends at its last value, like a CSV line — a title row above the header is then shorter
        // than the table, and the header is found below it.
        return rows.Select(pair =>
        {
            var values = new string[pair.Value.Keys.Max() + 1];
            Array.Fill(values, "");
            foreach (var (index, text) in pair.Value)
                values[index] = text;
            return new CsvRecord(pair.Key, values);
        }).ToList();
    }

    /// <summary>An Excel serial date (days since 1899-12-30) as yyyy-MM-dd, with the time if it has one.</summary>
    private static string DateText(string serial)
    {
        if (!double.TryParse(serial, NumberStyles.Float, CultureInfo.InvariantCulture, out var days) || days < 0 || days > 2958465)
            return serial;
        var moment = new DateTime(1899, 12, 30).AddSeconds(Math.Round(days * 86400));
        // Only a time of day (e.g. 0.5 for 12:00).
        if (days < 1)
            return moment.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return moment.TimeOfDay == TimeSpan.Zero
            ? moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : moment.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>"BC12" → 54 (0-based column).</summary>
    public static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var c in reference)
        {
            if (c is >= 'A' and <= 'Z')
                index = index * 26 + (c - 'A' + 1);
            else if (c is >= 'a' and <= 'z')
                index = index * 26 + (c - 'a' + 1);
            else
                break;
        }
        return Math.Max(index - 1, 0);
    }

    private static int? RowOf(string reference)
    {
        var digits = reference.SkipWhile(char.IsLetter).ToArray();
        return int.TryParse(digits, out var row) ? row : null;
    }

    private static string Text(XmlReader reader)
    {
        var text = new System.Text.StringBuilder();
        using var inner = reader.ReadSubtree();
        var skip = 0;
        while (inner.Read())
        {
            if (inner.NodeType == XmlNodeType.Element && inner.LocalName == "rPh" && !inner.IsEmptyElement)
                skip++;
            else if (inner.NodeType == XmlNodeType.EndElement && inner.LocalName == "rPh")
                skip--;
            else if (skip == 0 && inner.NodeType == XmlNodeType.Element && inner.LocalName == "t")
                text.Append(inner.ReadElementContentAsString());
        }
        return text.ToString();
    }

    private static string Text(XmlElement element) =>
        string.Concat(element.GetElementsByTagName("t", Main).OfType<XmlElement>()
            .Where(t => t.ParentNode?.LocalName != "rPh" && t.ParentNode?.ParentNode?.LocalName != "rPh")
            .Select(t => t.InnerText));

    private static IEnumerable<XmlElement> Elements(ZipArchiveEntry entry, string name)
    {
        var document = Load(entry);
        foreach (XmlElement element in document.GetElementsByTagName("*"))
            if (element.LocalName == name)
                yield return element;
    }

    private static XmlDocument Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var document = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        document.Load(reader);
        return document;
    }
}
