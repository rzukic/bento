using System.Globalization;
using System.Text.Json;
using Dapper;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Models;

namespace Bento.Api.Services.Import;

/// <summary>
/// Imports CSV files into a table: reads the file, converts every mapped cell
/// with the file's locale, skips rows that are already in the table, validates
/// the rest with the CRUD rules and inserts them in one transaction.
/// </summary>
public sealed class ImportService
{
    public const int MaxReturnedRows = 1000;
    private const int AnalyzeSampleRows = 200;
    private const int PreviewRows = 20;

    private static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web);

    private readonly SchemaCache _schemaCache;
    private readonly RequestBodyValidator _validator;
    private readonly CrudService _crudService;
    private readonly QueryService _queryService;
    private readonly DatabaseConfig _dbConfig;

    public ImportService(
        SchemaCache schemaCache,
        RequestBodyValidator validator,
        CrudService crudService,
        QueryService queryService,
        DatabaseConfig dbConfig)
    {
        _schemaCache = schemaCache;
        _validator = validator;
        _crudService = crudService;
        _queryService = queryService;
        _dbConfig = dbConfig;
    }

    public ImportAnalyzeResponse Analyze(ImportAnalyzeRequest request)
    {
        var document = ReadDocument(request.Content, request.Settings);
        var locale = ResolveLocale(document, request.Settings);
        var headers = document.Headers;
        var sampleRecords = document.DataRecords.Take(AnalyzeSampleRows).Select(record => record.Cells).ToList();

        var columns = headers.Select((name, index) =>
        {
            var values = sampleRecords.Select(cells => index < cells.Length ? cells[index] : "").ToList();
            return new ImportAnalyzedColumn(
                index,
                name,
                values.Where(v => !ImportValueParser.IsBlank(v)).Select(v => v.Trim()).Distinct().Take(5).ToList(),
                ImportValueParser.InferType(values, locale).ToString().ToLowerInvariant());
        }).ToList();

        return new ImportAnalyzeResponse(
            ToResolvedSettings(document, locale),
            columns,
            document.Preamble.Select(cells => (IReadOnlyList<string>)cells).ToList(),
            document.DataRecords.Take(PreviewRows).Select(record => (IReadOnlyList<string>)record.Cells).ToList(),
            document.DataRecords.Count(),
            new ImportSupportedOptions(
                CsvReader.SupportedEncodings,
                CsvReader.SupportedDelimiters.Select(DelimiterText).ToList(),
                [",", "."],
                ImportLocale.DateFormats));
    }

    public async Task<ImportRunResponse> RunAsync(string tableName, ImportRunRequest request, bool dryRun)
    {
        var table = _schemaCache.GetTable(tableName)
            ?? throw new ApiNotFoundException($"Table '{tableName}' does not exist.",
                new Dictionary<string, object?> { ["table"] = tableName });
        var document = ReadDocument(request.Content, request.Settings);
        var locale = ResolveLocale(document, request.Settings);
        var mapping = ResolveMapping(table, document, request.Mapping);
        var keyColumns = ResolveKeyColumns(mapping, request.KeyColumns);
        var references = await LoadReferenceLookupsAsync(mapping.Select(m => m.Column));

        var rows = new List<PendingRow>();
        foreach (var (line, cells) in document.DataRecords)
            rows.Add(ConvertRow(line, cells, mapping, locale, references));

        await MarkDuplicatesAsync(table, rows, keyColumns);
        MarkUniqueConflictsInFile(table, rows);
        foreach (var row in rows.Where(r => r.Status == ImportRowStatus.New))
            await ValidateAsync(table, row);

        var newRows = rows.Where(r => r.Status == ImportRowStatus.New).ToList();
        var inserted = 0;
        if (!dryRun && newRows.Count > 0)
        {
            try
            {
                inserted = await _crudService.InsertManyAsync(table, newRows.Select(r => r.Body!.Value).ToList());
            }
            catch (Exception exception) when (exception is not ApiException)
            {
                throw new ApiConflictException(
                    "The import was rolled back; no rows were written. " + exception.Message,
                    new Dictionary<string, object?> { ["table"] = table.Name });
            }
        }

        return new ImportRunResponse(
            dryRun,
            rows.Count,
            newRows.Count,
            rows.Count(r => r.Status == ImportRowStatus.Duplicate),
            rows.Count(r => r.Status == ImportRowStatus.Invalid),
            inserted,
            rows.Take(MaxReturnedRows).Select(r => new ImportRowResult(
                r.Line,
                r.Status switch
                {
                    ImportRowStatus.Duplicate => "duplicate",
                    ImportRowStatus.Invalid => "invalid",
                    _ => "new"
                },
                r.Display,
                r.Errors)).ToList(),
            rows.Count > MaxReturnedRows);
    }

    // ── File and settings ─────────────────────────────────────────────────

    private static CsvDocument ReadDocument(string? content, ImportFileSettings? settings)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw Invalid("content", "Choose a file to import.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(content);
        }
        catch (FormatException)
        {
            throw Invalid("content", "content must be the file encoded as base64.");
        }

        if (bytes.Length == 0)
            throw Invalid("content", "The file is empty.");

        string? encoding = null;
        if (!string.IsNullOrWhiteSpace(settings?.Encoding))
        {
            try
            {
                encoding = System.Text.Encoding.GetEncoding(settings.Encoding.Trim()).WebName;
            }
            catch (ArgumentException)
            {
                throw Invalid("settings.encoding", $"Unknown encoding '{settings.Encoding}'.");
            }
        }

        char? delimiter = settings?.Delimiter switch
        {
            null or "" => null,
            "\\t" or "tab" => '\t',
            { Length: 1 } single => single[0],
            _ => throw Invalid("settings.delimiter", "The delimiter must be a single character.")
        };

        var document = CsvReader.Read(bytes, encoding, delimiter, settings?.HeaderLine);
        if (document.Records.Count == 0)
            throw Invalid("content", "The file has no rows.");
        return document;
    }

    private static ImportLocale ResolveLocale(CsvDocument document, ImportFileSettings? settings)
    {
        var detected = ImportValueParser.DetectLocale(
            document.DataRecords.Take(AnalyzeSampleRows).SelectMany(record => record.Cells));
        var decimalSeparator = settings?.DecimalSeparator switch
        {
            null or "" => detected.DecimalSeparator,
            "," or "." => settings.DecimalSeparator,
            _ => throw Invalid("settings.decimalSeparator", "The decimal separator must be ',' or '.'.")
        };
        var dateFormat = string.IsNullOrWhiteSpace(settings?.DateFormat) ? detected.DateFormat : settings.DateFormat.Trim();
        if (!ImportLocale.DateFormats.Contains(dateFormat))
            throw Invalid("settings.dateFormat", $"Unsupported date format '{dateFormat}'.");
        return new ImportLocale(decimalSeparator, dateFormat);
    }

    private static ImportResolvedSettings ToResolvedSettings(CsvDocument document, ImportLocale locale) =>
        new(document.Encoding, DelimiterText(document.Delimiter), document.HeaderLine, locale.DecimalSeparator, locale.DateFormat);

    private static string DelimiterText(char delimiter) => delimiter == '\t' ? "\\t" : delimiter.ToString();

    // ── Mapping ───────────────────────────────────────────────────────────

    private sealed record MappedColumn(int SourceIndex, string Source, ColumnDef Column);

    private static List<MappedColumn> ResolveMapping(TableDef table, CsvDocument document, IReadOnlyList<ImportColumnMapping>? mapping)
    {
        var entries = (mapping ?? []).Where(m => !string.IsNullOrWhiteSpace(m.Target)).ToList();
        if (entries.Count == 0)
            throw Invalid("mapping", "Map at least one file column to a table column.");

        var headers = document.Headers;
        var result = new List<MappedColumn>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var sourceIndex = headers.ToList().FindIndex(h => string.Equals(h, entry.Source?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (sourceIndex < 0)
                throw Invalid("mapping", $"The file has no column '{entry.Source}'.");

            var column = table.Columns.FirstOrDefault(c => string.Equals(c.Name, entry.Target, StringComparison.OrdinalIgnoreCase))
                ?? throw Invalid("mapping", $"Table '{table.Name}' has no column '{entry.Target}'.");
            if (!CrudService.IsWritableColumn(column) || !ColumnTypes.HasPhysicalColumn(column.Type))
                throw Invalid("mapping", $"Column '{column.Name}' is filled automatically and cannot be imported.");
            if (!seenTargets.Add(column.Name))
                throw Invalid("mapping", $"Column '{column.Name}' is mapped twice.");

            result.Add(new MappedColumn(sourceIndex, headers[sourceIndex], column));
        }

        return result;
    }

    private static List<ColumnDef> ResolveKeyColumns(List<MappedColumn> mapping, IReadOnlyList<string>? keyColumns)
    {
        var result = new List<ColumnDef>();
        foreach (var name in keyColumns ?? [])
        {
            var mapped = mapping.FirstOrDefault(m => string.Equals(m.Column.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw Invalid("keyColumns", $"Key column '{name}' must be one of the mapped columns.");
            if (!result.Contains(mapped.Column))
                result.Add(mapped.Column);
        }
        return result;
    }

    // ── References by record label ───────────────────────────────────────

    private sealed record ReferenceLookup(
        string TableName,
        string? LabelColumn,
        Dictionary<string, List<object>> ByLabel,
        Dictionary<string, (object Id, string Label)> ById);

    private async Task<Dictionary<string, ReferenceLookup>> LoadReferenceLookupsAsync(IEnumerable<ColumnDef> columns)
    {
        var lookups = new Dictionary<string, ReferenceLookup>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns.Where(c => c.Type == ColumnType.Reference && !string.IsNullOrWhiteSpace(c.References)))
        {
            if (lookups.ContainsKey(column.References!))
                continue;

            var target = _schemaCache.GetTable(column.References!);
            var primaryKey = target?.Columns.FirstOrDefault(c => c.PrimaryKey);
            if (target is null || primaryKey is null)
                continue;

            var labelColumn = CrudService.GetRecordLabelColumns(target).FirstOrDefault();
            var select = labelColumn is null
                ? $"SELECT {Quote(primaryKey.Name)} AS id FROM {Quote(target.Name)}"
                : $"SELECT {Quote(primaryKey.Name)} AS id, {Quote(labelColumn)} AS label FROM {Quote(target.Name)}";

            await using var scope = await _queryService.UseDataAsync();
            var records = await scope.Db.Connection.QueryAsync(select);

            var byLabel = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
            var byId = new Dictionary<string, (object Id, string Label)>(StringComparer.OrdinalIgnoreCase);
            foreach (IDictionary<string, object?> record in records)
            {
                var id = record["id"];
                if (id is null)
                    continue;
                var label = labelColumn is null ? null : Convert.ToString(record["label"], CultureInfo.InvariantCulture)?.Trim();
                byId[Convert.ToString(id, CultureInfo.InvariantCulture)!] = (id, label ?? Convert.ToString(id, CultureInfo.InvariantCulture)!);
                if (!string.IsNullOrEmpty(label))
                {
                    if (!byLabel.TryGetValue(label, out var ids))
                        byLabel[label] = ids = [];
                    ids.Add(id);
                }
            }

            lookups[column.References!] = new ReferenceLookup(target.Name, labelColumn, byLabel, byId);
        }
        return lookups;
    }

    // ── Rows ──────────────────────────────────────────────────────────────

    private enum ImportRowStatus { New, Duplicate, Invalid }

    private sealed class PendingRow
    {
        public required int Line { get; init; }
        public Dictionary<string, object?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string?> Display { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ImportRowError> Errors { get; } = [];
        public ImportRowStatus Status { get; set; } = ImportRowStatus.New;
        public JsonElement? Body { get; set; }
    }

    private PendingRow ConvertRow(
        int line,
        string[] cells,
        List<MappedColumn> mapping,
        ImportLocale locale,
        Dictionary<string, ReferenceLookup> references)
    {
        var row = new PendingRow { Line = line };
        foreach (var mapped in mapping)
        {
            var raw = mapped.SourceIndex < cells.Length ? cells[mapped.SourceIndex] : "";
            var column = mapped.Column;
            if (ImportValueParser.IsBlank(raw))
            {
                // Blank leaves the column out: its default applies, or the
                // validator reports a required column.
                row.Display[column.Name] = null;
                continue;
            }

            if (TryConvert(raw.Trim(), column, locale, references, out var value, out var display, out var error))
            {
                row.Values[column.Name] = value;
                row.Display[column.Name] = display;
            }
            else
            {
                row.Display[column.Name] = raw.Trim();
                row.Errors.Add(new ImportRowError(column.Name, error!));
            }
        }

        if (row.Errors.Count > 0)
            row.Status = ImportRowStatus.Invalid;
        return row;
    }

    private bool TryConvert(
        string raw,
        ColumnDef column,
        ImportLocale locale,
        Dictionary<string, ReferenceLookup> references,
        out object? value,
        out string? display,
        out string? error)
    {
        value = null;
        display = null;
        error = null;

        switch (column.Type)
        {
            case ColumnType.Integer:
                if (ImportValueParser.TryParseNumber(raw, locale, out var integer) && integer == decimal.Truncate(integer)
                    && integer is >= long.MinValue and <= long.MaxValue)
                {
                    value = (long)integer;
                    display = value.ToString();
                    return true;
                }
                error = $"Not a whole number: '{raw}'.";
                return false;

            case ColumnType.Float or ColumnType.Currency:
                if (ImportValueParser.TryParseNumber(raw, locale, out var number))
                {
                    value = number;
                    display = number.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                error = $"Not a number with '{locale.DecimalSeparator}' as decimal separator: '{raw}'.";
                return false;

            case ColumnType.Date:
                if (ImportValueParser.TryParseDate(raw, locale, out var date))
                {
                    value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    display = (string)value;
                    return true;
                }
                error = $"Not a date in {locale.DateFormat}: '{raw}'.";
                return false;

            case ColumnType.DateTime:
                if (ImportValueParser.TryParseDateTime(raw, locale, out var stamp))
                {
                    value = stamp;
                    display = stamp;
                    return true;
                }
                error = $"Not a date and time in {locale.DateFormat} HH:mm: '{raw}'.";
                return false;

            case ColumnType.Boolean:
                if (ImportValueParser.TryParseBoolean(raw, out var flag))
                {
                    value = flag;
                    display = flag ? "true" : "false";
                    return true;
                }
                error = $"Not yes/no: '{raw}'.";
                return false;

            case ColumnType.Enum:
                var option = column.Values.FirstOrDefault(v => string.Equals(v, raw, StringComparison.OrdinalIgnoreCase));
                if (option is not null || column.Values.Count == 0)
                {
                    value = option ?? raw;
                    display = (string)value;
                    return true;
                }
                error = $"'{raw}' is not one of: {string.Join(", ", column.Values)}.";
                return false;

            case ColumnType.Uuid:
                if (Guid.TryParse(raw, out var guid))
                {
                    value = guid.ToString();
                    display = (string)value;
                    return true;
                }
                error = $"Not a UUID: '{raw}'.";
                return false;

            case ColumnType.Json:
                try
                {
                    value = JsonDocument.Parse(raw).RootElement.Clone();
                }
                catch (JsonException)
                {
                    value = raw;
                }
                display = raw;
                return true;

            case ColumnType.Reference:
                if (column.References is not null && references.TryGetValue(column.References, out var lookup))
                {
                    if (lookup.ByLabel.TryGetValue(raw, out var ids))
                    {
                        if (ids.Count > 1)
                        {
                            error = $"More than one {lookup.TableName} record is named '{raw}'.";
                            return false;
                        }
                        value = ids[0];
                        display = raw;
                        return true;
                    }
                    if (lookup.ById.TryGetValue(raw, out var byId))
                    {
                        value = byId.Id;
                        display = byId.Label;
                        return true;
                    }
                    error = $"No {lookup.TableName} record named '{raw}'.";
                    return false;
                }
                value = raw;
                display = raw;
                return true;

            default:
                value = raw;
                display = raw;
                return true;
        }
    }

    // ── Duplicates ────────────────────────────────────────────────────────

    /// <summary>
    /// A row is a duplicate when the table already holds a row with the same
    /// key values. Counts are kept per key: a file with two identical coffees
    /// on the same day imports both, unless the table already has them —
    /// re-importing an overlapping statement adds only what is missing.
    /// </summary>
    private async Task MarkDuplicatesAsync(TableDef table, List<PendingRow> rows, List<ColumnDef> keyColumns)
    {
        if (keyColumns.Count == 0)
            return;

        var existing = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var scope = await _queryService.UseDataAsync())
        {
            var select = $"SELECT {string.Join(", ", keyColumns.Select(c => Quote(c.Name)))} FROM {Quote(table.Name)}";
            foreach (IDictionary<string, object?> record in await scope.Db.Connection.QueryAsync(select))
            {
                var key = KeyOf(keyColumns, column => record.TryGetValue(column.Name, out var v) ? v : null);
                existing[key] = existing.GetValueOrDefault(key) + 1;
            }
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.Status == ImportRowStatus.New))
        {
            var key = KeyOf(keyColumns, column => row.Values.GetValueOrDefault(column.Name));
            var count = seen.GetValueOrDefault(key);
            seen[key] = count + 1;
            if (count < existing.GetValueOrDefault(key))
                row.Status = ImportRowStatus.Duplicate;
        }
    }

    private static string KeyOf(List<ColumnDef> keyColumns, Func<ColumnDef, object?> valueOf) =>
        string.Join("\u001F", keyColumns.Select(column => Canonical(valueOf(column), column.Type)));

    /// <summary>One text form per value, whatever the provider returned (double, decimal, DateTime, 0/1 …).</summary>
    private static string Canonical(object? value, ColumnType type)
    {
        if (value is null || value is DBNull)
            return "\u0000";
        if (value is JsonElement element)
            return element.GetRawText();

        switch (type)
        {
            case ColumnType.Integer or ColumnType.Float or ColumnType.Currency:
                return decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? number.ToString("0.############################", CultureInfo.InvariantCulture)
                    : Convert.ToString(value, CultureInfo.InvariantCulture)!;
            case ColumnType.Boolean:
                return value switch
                {
                    bool flag => flag ? "true" : "false",
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture) is "1" or "true" or "True" ? "true" : "false"
                };
            case ColumnType.Date:
                return value switch
                {
                    DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DateTime dateTime => dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    _ => Truncate(Convert.ToString(value, CultureInfo.InvariantCulture)!, 10)
                };
            case ColumnType.DateTime:
                return value switch
                {
                    DateTime dateTime => dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    DateTimeOffset offset => offset.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    _ => Truncate(Convert.ToString(value, CultureInfo.InvariantCulture)!.Replace(' ', 'T'), 19)
                };
            case ColumnType.Uuid:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!.ToLowerInvariant();
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!.Trim();
        }
    }

    private static string Truncate(string text, int length) => text.Length > length ? text[..length] : text;

    /// <summary>Two new rows with the same value in a unique column cannot both be inserted.</summary>
    private static void MarkUniqueConflictsInFile(TableDef table, List<PendingRow> rows)
    {
        foreach (var column in table.Columns.Where(c => c.Unique && !c.PrimaryKey))
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in rows.Where(r => r.Status == ImportRowStatus.New && r.Values.ContainsKey(column.Name)))
            {
                var key = Canonical(row.Values[column.Name], column.Type);
                if (seen.TryGetValue(key, out var firstLine))
                {
                    row.Status = ImportRowStatus.Invalid;
                    row.Errors.Add(new ImportRowError(column.Name, $"The same value appears on line {firstLine}; '{column.Name}' must be unique."));
                }
                else
                {
                    seen[key] = row.Line;
                }
            }
        }
    }

    // ── Validation ───────────────────────────────────────────────────────

    private async Task ValidateAsync(TableDef table, PendingRow row)
    {
        var body = JsonSerializer.SerializeToElement(row.Values, BodyJson);
        try
        {
            await _validator.ValidateCreateAsync(table, body);
            row.Body = body;
        }
        catch (ApiValidationException exception)
        {
            row.Status = ImportRowStatus.Invalid;
            if (exception.Details?.GetValueOrDefault("errors") is IEnumerable<Dictionary<string, object?>> errors)
            {
                foreach (var error in errors)
                {
                    row.Errors.Add(new ImportRowError(
                        Convert.ToString(error.GetValueOrDefault("field"), CultureInfo.InvariantCulture) ?? "",
                        Convert.ToString(error.GetValueOrDefault("message"), CultureInfo.InvariantCulture) ?? exception.Message));
                }
            }
            else
            {
                var field = Convert.ToString(exception.Details?.GetValueOrDefault("column") ?? exception.Details?.GetValueOrDefault("field"), CultureInfo.InvariantCulture);
                row.Errors.Add(new ImportRowError(field ?? "", exception.Message));
            }
        }
        catch (ApiException exception)
        {
            // Unique values already in the table (409) and missing referenced rows (422).
            row.Status = ImportRowStatus.Invalid;
            row.Errors.Add(new ImportRowError(
                Convert.ToString(exception.Details?.GetValueOrDefault("column"), CultureInfo.InvariantCulture) ?? "",
                exception.Message));
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}
