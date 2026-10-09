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
    string DateFormat);

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
/// or <c>invalid</c>; <c>Display</c> shows each mapped value as it will be
/// stored (references by their record label).
/// </summary>
public sealed record ImportRowResult(
    int Line,
    string Status,
    IReadOnlyDictionary<string, string?> Display,
    IReadOnlyList<ImportRowError> Errors);

public sealed record ImportRunResponse(
    bool DryRun,
    int Total,
    int New,
    int Duplicates,
    int Invalid,
    int Inserted,
    IReadOnlyList<ImportRowResult> Rows,
    bool RowsTruncated);

public sealed record ImportProfileConfig
{
    public ImportFileSettings? Settings { get; init; }
    public IReadOnlyList<ImportColumnMapping>? Mapping { get; init; }
    public IReadOnlyList<string>? KeyColumns { get; init; }
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
