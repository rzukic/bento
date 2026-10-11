using System.Text;
using System.Text.Json;

namespace Bento.Api.Services.Import;

/// <summary>
/// Reads a JSON document as a table: an array of objects, at the top or
/// under a path (<c>data.transactions</c>), one record per object. Columns are
/// the keys in the order they first appear; nested objects become
/// <c>parent.child</c> columns, arrays stay JSON text. Numbers keep their
/// invariant form, yes/no become <c>true</c>/<c>false</c>, null is empty.
/// </summary>
public static class JsonTableReader
{
    private const int MaxDepth = 4;

    /// <summary>Whether the text starts like JSON (after a BOM and white space).</summary>
    public static bool LooksLikeJson(string text)
    {
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c == '﻿')
                continue;
            return c is '[' or '{';
        }
        return false;
    }

    /// <summary>
    /// The records (a header record, then one per object) and the path they
    /// were found at. Without a path the first array of objects is taken,
    /// searching the top level first. Throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static (List<CsvRecord> Records, string Path) Read(string text, string? path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 });
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"The file is not valid JSON: {e.Message}", e);
        }

        using (document)
        {
            var (items, found) = string.IsNullOrWhiteSpace(path) ? Find(document.RootElement) : At(document.RootElement, path.Trim());
            var columns = new List<string>();
            var known = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<Dictionary<string, string>>();
            foreach (var item in items.EnumerateArray())
            {
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                if (item.ValueKind == JsonValueKind.Object)
                    Flatten(item, "", row, 0);
                else
                    row["value"] = Text(item);
                foreach (var key in row.Keys)
                    if (known.Add(key))
                        columns.Add(key);
                rows.Add(row);
            }

            // Line 1 is the header; object n is on line n + 1, as in a CSV file.
            var records = new List<CsvRecord>(rows.Count + 1) { new(1, columns.ToArray()) };
            for (var i = 0; i < rows.Count; i++)
                records.Add(new CsvRecord(i + 2, columns.Select(c => rows[i].GetValueOrDefault(c, "")).ToArray()));
            return (records, found);
        }
    }

    private static (JsonElement Items, string Path) At(JsonElement root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(part, out var next))
                current = next;
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(part, out var index) && index >= 0 && index < current.GetArrayLength())
                current = current[index];
            else
                throw new InvalidDataException($"The JSON has nothing at '{path}'.");
        }
        if (current.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"'{path}' in the JSON is not a list.");
        return (current, path);
    }

    /// <summary>The root if it is a list, else the first list of objects, nearest to the top first.</summary>
    private static (JsonElement Items, string Path) Find(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return (root, "");
        var queue = new Queue<(JsonElement Element, string Path, int Depth)>();
        queue.Enqueue((root, "", 0));
        while (queue.Count > 0)
        {
            var (element, path, depth) = queue.Dequeue();
            if (element.ValueKind != JsonValueKind.Object || depth > MaxDepth)
                continue;
            foreach (var property in element.EnumerateObject())
            {
                var childPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                if (property.Value.ValueKind == JsonValueKind.Array && property.Value.EnumerateArray().Any(i => i.ValueKind == JsonValueKind.Object))
                    return (property.Value, childPath);
                queue.Enqueue((property.Value, childPath, depth + 1));
            }
        }
        throw new InvalidDataException("The JSON has no list of records; it should be [ {…}, {…} ] or contain one.");
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> row, int depth)
    {
        foreach (var property in element.EnumerateObject())
        {
            var name = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object && depth < MaxDepth)
                Flatten(property.Value, name, row, depth + 1);
            else
                row[name] = Text(property.Value);
        }
    }

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText()
    };

    /// <summary>The bytes as text: UTF-8 (with or without BOM) or UTF-16 with a BOM, as JSON allows.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF))
            return (bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(bytes, 2, bytes.Length - 2);
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }
}
