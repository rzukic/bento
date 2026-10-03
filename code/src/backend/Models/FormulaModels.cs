using System.Text.Json;

namespace Bento.Api.Models;

public sealed record FormulaRequest
{
    /// <summary>The table whose columns the formula uses.</summary>
    public string? Table { get; init; }
    public string? Formula { get; init; }
    /// <summary>Optional declared result type (text, integer, float, currency, boolean, date, datetime).</summary>
    public string? ResultType { get; init; }
    /// <summary>
    /// Validate only: the table's columns as drafted in the schema designer,
    /// checked instead of the applied ones (the table need not exist yet).
    /// </summary>
    public JsonElement? Columns { get; init; }
    /// <summary>
    /// Validate only: the whole drafted Model JSON, so links and tables that
    /// exist only in the draft are checked too. Takes precedence over <see cref="Columns"/>.
    /// </summary>
    public JsonElement? Model { get; init; }
    /// <summary>The computed column the formula belongs to: using itself is reported as a cycle.</summary>
    public string? Column { get; init; }
    /// <summary>Preview only: number of sample rows (1–100, default 20).</summary>
    public int? Limit { get; init; }
}

public sealed record FormulaErrorResponse(string Message, int Start, int End);

public sealed record FormulaValidateResponse(
    bool Valid,
    string? Type,
    IReadOnlyList<FormulaErrorResponse> Errors,
    IReadOnlyList<string> Columns);

public sealed record FormulaPreviewRow(object? Id, object? Value, IDictionary<string, object?> Inputs);

public sealed record FormulaPreviewResponse(string Type, IReadOnlyList<string> Columns, IReadOnlyList<FormulaPreviewRow> Rows);
