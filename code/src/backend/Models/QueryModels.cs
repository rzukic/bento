using System.Text.Json;

namespace Bento.Api.Models;

/// <summary>
/// An aggregation query (Part 6 R6.9): source table, filters, groups and
/// measures. Used by dashboard widgets and saved queries.
/// </summary>
public sealed record AggregationQueryRequest
{
    public string? Source { get; init; }
    /// <summary>
    /// <c>{ "column": value }</c> for equality or <c>{ "column": { "op": value } }</c>
    /// with the list endpoint's operators (eq, neq, gt, gte, lt, lte, like,
    /// in, null, notnull).
    /// </summary>
    public JsonElement? Filter { get; init; }
    public IReadOnlyList<QueryGroupBy>? GroupBy { get; init; }
    public IReadOnlyList<QueryMeasure>? Measures { get; init; }
    /// <summary>Output names (group columns or measures); a leading "-" sorts descending.</summary>
    public IReadOnlyList<string>? Sort { get; init; }
    public int? Limit { get; init; }
}

public sealed record QueryGroupBy
{
    public string? Column { get; init; }
    /// <summary>day, week, month, quarter or year — for date and date-time columns.</summary>
    public string? Bucket { get; init; }
    /// <summary>Output name; defaults to the column name.</summary>
    public string? As { get; init; }
}

public sealed record QueryMeasure
{
    /// <summary>Output name.</summary>
    public string? Name { get; init; }
    /// <summary>count, sum, avg, min, max or count_distinct.</summary>
    public string? Fn { get; init; }
    public string? Column { get; init; }
    /// <summary>Accumulate over the time bucket (sum and count only).</summary>
    public bool Running { get; init; }
    /// <summary>
    /// With <see cref="Running"/>: start from the total of the rows before
    /// the first bucket (rows the lower date bound of the filter left out),
    /// so a balance starts at its real level instead of zero.
    /// </summary>
    public bool CarryIn { get; init; }
}

public sealed record QueryResultColumn(string Name, string Type, string Role, string? LabelOf = null);

public sealed record AggregationQueryResponse(
    IReadOnlyList<IDictionary<string, object?>> Data,
    QueryResultMeta Meta);

public sealed record QueryResultMeta(IReadOnlyList<QueryResultColumn> Columns, bool Truncated);

// ── Saved queries ──

public sealed record SavedQueryRequest
{
    public string? Name { get; init; }
    public bool? IsShared { get; init; }
    public AggregationQueryRequest? Query { get; init; }
}

public sealed record SavedQueryRunRequest
{
    /// <summary>Extra filters added to the saved ones (e.g. a dashboard's date range).</summary>
    public JsonElement? Filter { get; init; }
}

public sealed record SavedQueryResponse(
    long Id,
    string Name,
    string Source,
    bool IsShared,
    bool IsOwner,
    JsonElement Query,
    string CreatedAt,
    string UpdatedAt);
