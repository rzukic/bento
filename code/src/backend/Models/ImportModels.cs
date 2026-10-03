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
}

public sealed record ImportRunRequest
{
    public string? Content { get; init; }
    public string? FileName { get; init; }
    public ImportFileSettings? Settings { get; init; }
    public IReadOnlyList<ImportColumnMapping>? Mapping { get; init; }
    /// <summary>Target columns that identify a row; rows already in the table are skipped.</summary>
    public IReadOnlyList<string>? KeyColumns { get; init; }
}

public sealed record ImportAnalyzedColumn(
    int Index,
    string Name,
    IReadOnlyList<string> Samples,
    string InferredType);

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
