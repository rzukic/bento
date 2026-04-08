using System.Text.RegularExpressions;
using Repere.Api.Db.Schema;
using Repere.Api.Exceptions;

namespace Repere.Api.Services;

public sealed record ListQueryParameters
{
    public int Page { get; init; } = 1;
    public int PerPage { get; init; } = 25;
    public IReadOnlyList<FilterClause> Filters { get; init; } = [];
    public IReadOnlyList<SortClause> Sorts { get; init; } = [];
    public IReadOnlyList<string>? Fields { get; init; }
    public IReadOnlyList<string> Includes { get; init; } = [];
    public string? Search { get; init; }
}

public sealed record FilterClause(string Column, string Operator, string? Value);

public sealed record SortClause(string Column, bool Descending);

public static class QueryParameterParser
{
    private const int MaxPerPage = 1000;
    private const int DefaultPerPage = 25;

    private static readonly Regex FilterExactRegex = new(@"^filter\[(\w+)\]$", RegexOptions.Compiled);
    private static readonly Regex FilterOpRegex = new(@"^filter\[(\w+)\]\[(\w+)\]$", RegexOptions.Compiled);

    private static readonly HashSet<string> ValidOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "neq", "gt", "gte", "lt", "lte", "like", "in", "null", "notnull"
    };

    /// <summary>
    /// Parse only pagination parameters (no filter/sort column validation).
    /// Used for sub-resource endpoints where the target table isn't known upfront.
    /// </summary>
    public static ListQueryParameters ParseBasic(IQueryCollection query)
    {
        var page = ParseInt(query, "page", 1);
        var perPage = ParseInt(query, "per_page", DefaultPerPage);

        if (page < 1) page = 1;
        if (perPage < 1) perPage = 1;
        if (perPage > MaxPerPage) perPage = MaxPerPage;

        return new ListQueryParameters
        {
            Page = page,
            PerPage = perPage
        };
    }

    public static ListQueryParameters Parse(IQueryCollection query, TableDef table)
    {
        var page = ParseInt(query, "page", 1);
        var perPage = ParseInt(query, "per_page", DefaultPerPage);

        if (page < 1) page = 1;
        if (perPage < 1) perPage = 1;
        if (perPage > MaxPerPage) perPage = MaxPerPage;

        var filters = ParseFilters(query, table);
        var sorts = ParseSorts(query, table);
        var fields = ParseFields(query, table);
        var includes = ParseIncludes(query);
        var search = ParseSearch(query);

        return new ListQueryParameters
        {
            Page = page,
            PerPage = perPage,
            Filters = filters,
            Sorts = sorts,
            Fields = fields,
            Includes = includes,
            Search = search
        };
    }

    private static string? ParseSearch(IQueryCollection query)
    {
        if (!query.TryGetValue("search", out var value)) return null;
        var s = value.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static List<FilterClause> ParseFilters(IQueryCollection query, TableDef table)
    {
        var filters = new List<FilterClause>();
        var columnNames = GetPhysicalColumnNames(table);

        foreach (var key in query.Keys)
        {
            var exactMatch = FilterExactRegex.Match(key);
            if (exactMatch.Success)
            {
                var col = exactMatch.Groups[1].Value;
                ValidateColumnName(col, columnNames);
                filters.Add(new FilterClause(col, "eq", query[key].ToString()));
                continue;
            }

            var opMatch = FilterOpRegex.Match(key);
            if (opMatch.Success)
            {
                var col = opMatch.Groups[1].Value;
                var op = opMatch.Groups[2].Value.ToLowerInvariant();
                ValidateColumnName(col, columnNames);
                ValidateOperator(op);
                filters.Add(new FilterClause(col, op, query[key].ToString()));
            }
        }

        return filters;
    }

    private static List<SortClause> ParseSorts(IQueryCollection query, TableDef table)
    {
        if (!query.TryGetValue("sort", out var sortValue) || string.IsNullOrWhiteSpace(sortValue))
            return [];

        var columnNames = GetPhysicalColumnNames(table);
        var sorts = new List<SortClause>();

        foreach (var part in sortValue.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var descending = part.StartsWith('-');
            var col = descending ? part[1..] : part;
            ValidateColumnName(col, columnNames);
            sorts.Add(new SortClause(col, descending));
        }

        return sorts;
    }

    private static HashSet<string> GetPhysicalColumnNames(TableDef table)
    {
        return table.Columns
            .Where(c => Db.ColumnTypes.HasPhysicalColumn(c.Type))
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateColumnName(string column, HashSet<string> validColumns)
    {
        if (!validColumns.Contains(column))
        {
            throw new ApiValidationException(
                $"Unknown column '{column}'.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["column"] = column,
                    ["availableColumns"] = validColumns.ToList()
                });
        }
    }

    private static void ValidateOperator(string op)
    {
        if (!ValidOperators.Contains(op))
        {
            throw new ApiValidationException(
                $"Unknown filter operator '{op}'.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["operator"] = op,
                    ["validOperators"] = ValidOperators.ToList()
                });
        }
    }

    private static List<string>? ParseFields(IQueryCollection query, TableDef table)
    {
        if (!query.TryGetValue("fields", out var fieldsValue) || string.IsNullOrWhiteSpace(fieldsValue))
            return null;

        var columnNames = GetPhysicalColumnNames(table);
        var fields = new List<string>();

        foreach (var part in fieldsValue.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ValidateColumnName(part, columnNames);
            fields.Add(part);
        }

        return fields;
    }

    private static List<string> ParseIncludes(IQueryCollection query)
    {
        if (!query.TryGetValue("include", out var includeValue) || string.IsNullOrWhiteSpace(includeValue))
            return [];

        return includeValue.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static int ParseInt(IQueryCollection query, string key, int defaultValue)
    {
        if (!query.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return int.TryParse(value, out var result) ? result : defaultValue;
    }
}
