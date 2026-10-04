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

    /// <summary>
    /// Values at the end of each period (Step 3.6): one row per period of the
    /// calendar, the measures computed over all rows with BUCKET_START() and
    /// BUCKET_END() — e.g. a balance as SUM(amount WHERE booked_on &lt;= BUCKET_END()).
    /// </summary>
    public QueryAsOf? AsOf { get; init; }

    /// <summary>Dashboard variables (name → number, text or yes/no) usable in measure formulas.</summary>
    public Dictionary<string, JsonElement>? Variables { get; init; }
}

public sealed record QueryAsOf
{
    /// <summary>day, week, month, quarter or year.</summary>
    public string? Bucket { get; init; }
    /// <summary>First and last day of the calendar (yyyy-MM-dd).</summary>
    public string? From { get; init; }
    public string? To { get; init; }
    /// <summary>Output name of the period; defaults to "period".</summary>
    public string? As { get; init; }
}

public sealed record QueryGroupBy
{
    public string? Column { get; init; }
    /// <summary>day, week, month, quarter or year — for date and date-time columns.</summary>
    public string? Bucket { get; init; }
    /// <summary>Output name; defaults to the column name.</summary>
    public string? As { get; init; }
    /// <summary>
    /// Only the N values ranked first by the first measure (Step 3.6b, "top
    /// values"); empty values are kept as their own group.
    /// </summary>
    public int? Top { get; init; }
    /// <summary>With <see cref="Top"/>: the remaining values as one group, flagged in <c>&lt;as&gt;_other</c>.</summary>
    public bool Other { get; init; }
    /// <summary>With <see cref="Top"/>: "desc" (largest first, default) or "asc" (smallest first, e.g. spending).</summary>
    public string? Rank { get; init; }
}

public sealed record QueryMeasure
{
    /// <summary>Output name.</summary>
    public string? Name { get; init; }
    /// <summary>
    /// A formula over the group's rows instead of fn/column (Step 3.6), e.g.
    /// <c>SUM(amount WHERE amount &gt; 0) / COUNT()</c>; <c>RUNNING_SUM(…)</c> makes it a running total.
    /// </summary>
    public string? Formula { get; init; }
    /// <summary>count, sum, avg, min, max, count_distinct, median or percentile.</summary>
    public string? Fn { get; init; }
    public string? Column { get; init; }
    /// <summary>With fn percentile: which one, 0–100 (90 = the value 90 % of the rows stay below).</summary>
    public double? Percentile { get; init; }
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

// ── Dashboard variables ──

public sealed record QueryVariable
{
    public string? Name { get; init; }
    /// <summary>The table a measure formula runs over; none = a formula over other variables.</summary>
    public string? Source { get; init; }
    public string? Formula { get; init; }
    /// <summary>Filters for the source rows (e.g. the dashboard's date range).</summary>
    public JsonElement? Filter { get; init; }
}

public sealed record QueryVariablesRequest
{
    public IReadOnlyList<QueryVariable>? Variables { get; init; }
}

public sealed record QueryVariableResult(string Name, string? Type, object? Value, string? Error);
