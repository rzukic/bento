using System.Text.Json;

namespace Bento.Api.Models;

/// <summary>How a file is read. Every field is optional; missing ones are detected.</summary>
public sealed record ImportFileSettings
{
    public string? Encoding { get; init; }
    public string? Delimiter { get; init; }
    /// <summary>0-based index of the header record.</summary>
    public int? HeaderLine { get; init; }
    public string? DecimalSeparator { get; init; }
    public string? DateFormat { get; init; }
    /// <summary><c>csv</c>, <c>xlsx</c> or <c>json</c>; detected from the file when missing.</summary>
    public string? Format { get; init; }
    /// <summary>Excel: the sheet to read (the first when missing).</summary>
    public string? Sheet { get; init; }
    /// <summary>JSON: where the list of records is, e.g. <c>data.transactions</c> (found when missing).</summary>
    public string? JsonPath { get; init; }
}

public sealed record ImportAnalyzeRequest
{
    /// <summary>The file, base64-encoded (bytes are needed to detect the encoding).</summary>
    public string? Content { get; init; }
    public string? FileName { get; init; }
    public ImportFileSettings? Settings { get; init; }
}

public sealed record ImportColumnMapping
{
    /// <summary>Header text of the file column.</summary>
    public string? Source { get; init; }
    /// <summary>Target column in the table.</summary>
    public string? Target { get; init; }
    /// <summary>
    /// Instead of a file column: the same value for every row, e.g. the
    /// account a bank export belongs to (a record's name or id for a link).
    /// </summary>
    public string? Value { get; init; }
}

public sealed record ImportRunRequest
{
    public string? Content { get; init; }
    public string? FileName { get; init; }
    public ImportFileSettings? Settings { get; init; }
    public IReadOnlyList<ImportColumnMapping>? Mapping { get; init; }
    /// <summary>Target columns that identify a row; rows already in the table are skipped.</summary>
    public IReadOnlyList<string>? KeyColumns { get; init; }
    /// <summary>
    /// With key columns: a row already in the table whose other mapped values
    /// differ is updated (status <c>changed</c>) instead of skipped — e.g. a
    /// booking whose amount changed at the bank. Empty cells change nothing.
    /// </summary>
    public bool UpdateChanged { get; init; }
    /// <summary>
    /// Also skip rows whose key repeats an earlier row of the same file — for
    /// filling a lookup table (one category per name) from a column.
    /// </summary>
    public bool DistinctKeys { get; init; }
    /// <summary>
    /// Values chosen per row for one column, by file line — e.g. the categories
    /// picked in the review (Step 4.8). They win over the file's value.
    /// </summary>
    public ImportAssign? Assign { get; init; }
}

public sealed record ImportAssign
{
    public string? Column { get; init; }
    /// <summary>File line → value (a record's id or name for a link).</summary>
    public IReadOnlyDictionary<string, string?>? Values { get; init; }
}

/// <summary>
/// A file column. <c>Blank</c> counts empty cells; <c>Distinct</c> lists up
/// to 100 distinct values (trimmed, case-insensitive) and
/// <c>DistinctCount</c> how many there are in total — enough to propose a
/// required flag and lookup tables when a table is created from the file.
/// </summary>
public sealed record ImportAnalyzedColumn(
    int Index,
    string Name,
    IReadOnlyList<string> Samples,
    string InferredType,
    int Blank,
    int DistinctCount,
    IReadOnlyList<string> Distinct);

public sealed record ImportResolvedSettings(
    string Encoding,
    string Delimiter,
    int HeaderLine,
    string DecimalSeparator,
    string DateFormat,
    string Format = "csv",
    string? Sheet = null,
    IReadOnlyList<string>? Sheets = null,
    string? JsonPath = null);

public sealed record ImportAnalyzeResponse(
    ImportResolvedSettings Settings,
    IReadOnlyList<ImportAnalyzedColumn> Columns,
    IReadOnlyList<IReadOnlyList<string>> Preamble,
    IReadOnlyList<IReadOnlyList<string>> PreviewRows,
    int RowCount,
    ImportSupportedOptions Options);

public sealed record ImportSupportedOptions(
    IReadOnlyList<string> Encodings,
    IReadOnlyList<string> Delimiters,
    IReadOnlyList<string> DecimalSeparators,
    IReadOnlyList<string> DateFormats);

public sealed record ImportRowError(string Field, string Message);

/// <summary>
/// One data record of the file. <c>Status</c> is <c>new</c>, <c>duplicate</c>
/// (already in the table, unchanged), <c>changed</c> (in the table with other
/// values; updated with <c>updateChanged</c>) or <c>invalid</c>; <c>Display</c>
/// shows each mapped value as it will be stored (references by their record
/// label), <c>Before</c> the values a changed row had.
/// </summary>
public sealed record ImportRowResult(
    int Line,
    string Status,
    IReadOnlyDictionary<string, string?> Display,
    IReadOnlyList<ImportRowError> Errors,
    IReadOnlyDictionary<string, string?>? Before = null);

public sealed record ImportRunResponse(
    bool DryRun,
    int Total,
    int New,
    int Duplicates,
    int Invalid,
    int Inserted,
    IReadOnlyList<ImportRowResult> Rows,
    bool RowsTruncated,
    int Changed = 0,
    int Updated = 0);

public sealed record ImportProfileConfig
{
    public ImportFileSettings? Settings { get; init; }
    public IReadOnlyList<ImportColumnMapping>? Mapping { get; init; }
    public IReadOnlyList<string>? KeyColumns { get; init; }
    public bool UpdateChanged { get; init; }
}

public sealed record ImportProfileRequest
{
    public string? Name { get; init; }
    public ImportProfileConfig? Config { get; init; }
}

public sealed record ImportProfileResponse(
    long Id,
    string Name,
    string TableName,
    JsonElement Config,
    string CreatedAt,
    string UpdatedAt);

// ── Import from a URL and scheduled imports (Step 5.1) ──────────────────

/// <summary>Fetch a file for the import page: <c>POST /import/fetch</c>.</summary>
public sealed record ImportFetchRequest
{
    public string? Url { get; init; }
    /// <summary><c>{{name}}</c> in a value (or the URL) is a credential from the vault.</summary>
    public IReadOnlyList<Bento.Api.Services.Import.ImportHeader>? Headers { get; init; }
}

/// <summary>Runs a function for the import page, as if its result were a fetched file.</summary>
public sealed record ImportFunctionRequest
{
    public string? Function { get; init; }
}

public sealed record ImportFetchResponse(string Content, string FileName, string? ContentType, long Bytes, string FinalUrl);

/// <summary>When a saved import runs on its own: <c>manual</c>, <c>daily</c> at a time, or every few <c>hours</c>.</summary>
public sealed record ImportSchedule
{
    public string? Kind { get; init; }
    /// <summary>daily: HH:mm in <see cref="TimeZone"/>.</summary>
    public string? At { get; init; }
    /// <summary>hours: 1–168.</summary>
    public int? Every { get; init; }
    /// <summary>An IANA time zone; none: the workspace's (Settings → Workspace).</summary>
    public string? TimeZone { get; init; }
}

/// <summary>A saved import: where the file comes from, how it is read and mapped, and when it runs.</summary>
public sealed record ImportSourceConfig
{
    /// <summary>Where the file comes from: a URL, or <see cref="Function"/>.</summary>
    public string? Url { get; init; }
    /// <summary>Instead of a URL (Step 5.11): a function whose result is the file — rows as JSON, or CSV text.</summary>
    public string? Function { get; init; }
    public IReadOnlyList<Bento.Api.Services.Import.ImportHeader>? Headers { get; init; }
    public ImportFileSettings? Settings { get; init; }
    public IReadOnlyList<ImportColumnMapping>? Mapping { get; init; }
    public IReadOnlyList<string>? KeyColumns { get; init; }
    public bool UpdateChanged { get; init; }
    /// <summary>A column new rows get from the categorisation rules and earlier rows (Step 4.8), e.g. category_id.</summary>
    public string? CategoryColumn { get; init; }
    public ImportSchedule? Schedule { get; init; }
}

public sealed record ImportSourceRequest
{
    public string? Name { get; init; }
    public ImportSourceConfig? Config { get; init; }
    public bool? Enabled { get; init; }
}

/// <param name="Trigger"><c>schedule</c> or <c>manual</c>.</param>
/// <param name="Status"><c>running</c>, <c>ok</c> or <c>failed</c>.</param>
public sealed record ImportSourceRun(
    long Id,
    string Trigger,
    string StartedAt,
    string? FinishedAt,
    string Status,
    int Total,
    int Inserted,
    int Updated,
    int Duplicates,
    int Invalid,
    int Categorised,
    long Bytes,
    string? Error);

public sealed record ImportSourceResponse(
    long Id,
    string Name,
    string TableName,
    ImportSourceConfig Config,
    bool Enabled,
    ImportSourceRun? LastRun,
    string? NextRunAt,
    string CreatedAt,
    string UpdatedAt);
