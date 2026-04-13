using System.Text.Json;
using Dapper;
using Repere.Api.Db;
using Repere.Api.Db.Schema;
using Repere.Api.Exceptions;
using SqlKata;
using SqlKata.Execution;

namespace Repere.Api.Services;

public sealed record ListResult
{
    public required IReadOnlyList<IDictionary<string, object?>> Data { get; init; }
    public required int Page { get; init; }
    public required int PerPage { get; init; }
    public required long Total { get; init; }
    public required int TotalPages { get; init; }
}

/// <summary>
/// Performs CRUD operations against user tables via the data pool.
/// </summary>
public sealed class CrudService
{
    private readonly QueryService _queryService;
    private readonly DatabaseConfig _dbConfig;
    private readonly RequestBodyValidator _validator;

    public CrudService(QueryService queryService, DatabaseConfig dbConfig, RequestBodyValidator validator)
    {
        _queryService = queryService;
        _dbConfig = dbConfig;
        _validator = validator;
    }

    public async Task<ListResult> ListAsync(
        TableDef table,
        ListQueryParameters parameters,
        IReadOnlyCollection<string>? additionalSelectColumns = null)
    {
        await using var scope = await _queryService.UseDataAsync();

        // Build the base query with filters
        var baseQuery = scope.Db.Query(table.Name);
        ApplyFiltersStatic(baseQuery, parameters.Filters);
        ApplySearchStatic(baseQuery, parameters.Search, table, _dbConfig.Provider);

        // Count total matching rows
        var total = await baseQuery.Clone().CountAsync<long>();

        // Apply sorting
        baseQuery = ApplySortingStatic(baseQuery, parameters.Sorts, table, _dbConfig.Provider);

        // Apply field selection
        if (parameters.Fields is { Count: > 0 })
        {
            var selectedColumns = BuildSelectedColumns(parameters.Fields, additionalSelectColumns);
            baseQuery = baseQuery.Select(selectedColumns.ToArray());
        }

        // Apply pagination
        baseQuery = baseQuery.ForPage(parameters.Page, parameters.PerPage);

        var rows = (await baseQuery.GetAsync()).Select(ToDictionary).ToList();
        var totalPages = parameters.PerPage > 0
            ? (int)Math.Ceiling((double)total / parameters.PerPage)
            : 0;

        return new ListResult
        {
            Data = rows,
            Page = parameters.Page,
            PerPage = parameters.PerPage,
            Total = total,
            TotalPages = totalPages
        };
    }

    private static IReadOnlyList<string> BuildSelectedColumns(
        IReadOnlyList<string> requestedFields,
        IReadOnlyCollection<string>? additionalSelectColumns)
    {
        if (additionalSelectColumns is null || additionalSelectColumns.Count == 0)
            return requestedFields;

        var selected = new List<string>(requestedFields.Count + additionalSelectColumns.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in requestedFields)
        {
            if (seen.Add(field))
                selected.Add(field);
        }

        foreach (var field in additionalSelectColumns)
        {
            if (seen.Add(field))
                selected.Add(field);
        }

        return selected;
    }

    public async Task<IDictionary<string, object?>> GetByIdAsync(TableDef table, long id)
    {
        await using var scope = await _queryService.UseDataAsync();
        var row = await scope.Db.Query(table.Name)
            .Where("id", id)
            .FirstOrDefaultAsync();

        if (row is null)
        {
            throw new ApiNotFoundException(
                $"Row with id {id} not found in '{table.Name}'.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["table"] = table.Name,
                    ["id"] = id
                });
        }

        return ToDictionary(row);
    }

    public async Task<IDictionary<string, object?>> CreateAsync(TableDef table, JsonElement body)
    {
        await _validator.ValidateCreateAsync(table, body);

        var columns = GetWritableColumns(table);
        var data = ExtractColumnValues(body, columns);

        await using var scope = await _queryService.UseDataAsync();
        var id = await InsertAndGetIdAsync(scope, table.Name, data);

        // Read back the full row
        var row = await scope.Db.Query(table.Name)
            .Where("id", id)
            .FirstOrDefaultAsync();

        return ToDictionary(row!);
    }

    public async Task<IDictionary<string, object?>> UpdateAsync(TableDef table, long id, JsonElement body)
    {
        await EnsureRowExistsAsync(table, id);
        await _validator.ValidateUpdateAsync(table, id, body);

        var columns = GetWritableColumns(table);
        var data = ExtractColumnValues(body, columns);

        // For PUT, set missing writable columns to null
        foreach (var col in columns)
        {
            if (!data.ContainsKey(col.Name))
            {
                data[col.Name] = null;
            }
        }

        await using var scope = await _queryService.UseDataAsync();
        await scope.Db.Query(table.Name).Where("id", id).UpdateAsync(data);

        var row = await scope.Db.Query(table.Name)
            .Where("id", id)
            .FirstOrDefaultAsync();

        return ToDictionary(row!);
    }

    public async Task<IDictionary<string, object?>> PatchAsync(TableDef table, long id, JsonElement body)
    {
        await EnsureRowExistsAsync(table, id);
        await _validator.ValidatePatchAsync(table, id, body);

        var columns = GetWritableColumns(table);
        var data = ExtractColumnValues(body, columns);

        if (data.Count > 0)
        {
            await using var scope = await _queryService.UseDataAsync();
            await scope.Db.Query(table.Name).Where("id", id).UpdateAsync(data);
        }

        await using var readScope = await _queryService.UseDataAsync();
        var row = await readScope.Db.Query(table.Name)
            .Where("id", id)
            .FirstOrDefaultAsync();

        return ToDictionary(row!);
    }

    public async Task DeleteAsync(TableDef table, long id)
    {
        await using var scope = await _queryService.UseDataAsync();
        var affected = await scope.Db.Query(table.Name).Where("id", id).DeleteAsync();

        if (affected == 0)
        {
            throw new ApiNotFoundException(
                $"Row with id {id} not found in '{table.Name}'.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["table"] = table.Name,
                    ["id"] = id
                });
        }
    }

    private async Task EnsureRowExistsAsync(TableDef table, long id)
    {
        await using var scope = await _queryService.UseDataAsync();
        var exists = await scope.Db.Query(table.Name)
            .Where("id", id)
            .SelectRaw("1")
            .FirstOrDefaultAsync();

        if (exists is null)
        {
            throw new ApiNotFoundException(
                $"Row with id {id} not found in '{table.Name}'.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["table"] = table.Name,
                    ["id"] = id
                });
        }
    }

    private static List<ColumnDef> GetWritableColumns(TableDef table)
    {
        return table.Columns
            .Where(c => !c.PrimaryKey && c.Type != ColumnType.Computed)
            .ToList();
    }

    private static Dictionary<string, object?> ExtractColumnValues(JsonElement body, List<ColumnDef> columns)
    {
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (body.ValueKind != JsonValueKind.Object)
            return data;

        foreach (var prop in body.EnumerateObject())
        {
            var col = columns.FirstOrDefault(c =>
                string.Equals(c.Name, prop.Name, StringComparison.OrdinalIgnoreCase));

            if (col is null)
                continue; // Unknown columns silently ignored; validation is TASK-039

            data[col.Name] = CoerceValueStatic(prop.Value, col.Type);
        }

        return data;
    }

    internal static object? CoerceValueStatic(JsonElement value, ColumnType type)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;

        return type switch
        {
            ColumnType.Text or ColumnType.Enum or ColumnType.File
                => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
            ColumnType.Integer or ColumnType.Reference
                => value.ValueKind switch
                {
                    JsonValueKind.Number => value.GetInt64(),
                    JsonValueKind.String when long.TryParse(value.GetString(), out var n) => n,
                    _ => value.GetRawText()
                },
            ColumnType.Float or ColumnType.Currency
                => value.ValueKind switch
                {
                    JsonValueKind.Number => value.GetDouble(),
                    JsonValueKind.String when double.TryParse(value.GetString(), out var d) => d,
                    _ => value.GetRawText()
                },
            ColumnType.Boolean
                => value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => value.GetInt64() != 0,
                    JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => b,
                    _ => value.GetRawText()
                },
            ColumnType.Date or ColumnType.DateTime
                => value.GetString(),
            ColumnType.Json
                => value.GetRawText(),
            _ => value.GetRawText()
        };
    }

    private async Task<long> InsertAndGetIdAsync(QueryScope scope, string tableName, Dictionary<string, object?> data)
    {
        var columns = data.Keys.ToList();

        if (columns.Count == 0)
        {
            if (_dbConfig.Provider == DatabaseProvider.Postgres)
            {
                var sql = $"INSERT INTO \"{tableName}\" DEFAULT VALUES RETURNING \"id\";";
                return await scope.Db.Connection.ExecuteScalarAsync<long>(sql);
            }

            var defaultValuesSql = $"INSERT INTO \"{tableName}\" DEFAULT VALUES;";
            await scope.Db.Connection.ExecuteAsync(defaultValuesSql);
            return await scope.Db.Connection.ExecuteScalarAsync<long>("SELECT last_insert_rowid();");
        }

        var parameters = new DynamicParameters();
        for (int i = 0; i < columns.Count; i++)
        {
            parameters.Add($"p{i}", data[columns[i]]);
        }

        var quotedColumns = string.Join(", ", columns.Select(c => $"\"{c}\""));
        var quotedParams = string.Join(", ", columns.Select((_, i) => $"@p{i}"));

        if (_dbConfig.Provider == DatabaseProvider.Postgres)
        {
            var sql = $"INSERT INTO \"{tableName}\" ({quotedColumns}) VALUES ({quotedParams}) RETURNING \"id\";";
            return await scope.Db.Connection.ExecuteScalarAsync<long>(sql, parameters);
        }

        var insertSql = $"INSERT INTO \"{tableName}\" ({quotedColumns}) VALUES ({quotedParams});";
        await scope.Db.Connection.ExecuteAsync(insertSql, parameters);
        return await scope.Db.Connection.ExecuteScalarAsync<long>("SELECT last_insert_rowid();");
    }

    internal static void ApplySearchStatic(
        SqlKata.Query query,
        string? search,
        TableDef table,
        DatabaseProvider provider)
    {
        if (string.IsNullOrWhiteSpace(search)) return;

        var hasTextLikeColumns = table.Columns.Any(c =>
            c.Type is ColumnType.Text or ColumnType.Enum or ColumnType.File or ColumnType.Date or ColumnType.DateTime);

        if (!hasTextLikeColumns) return;

        var searchableColumns = table.Columns
            .Where(c => ColumnTypes.HasPhysicalColumn(c.Type) && c.Type is not (ColumnType.Json or ColumnType.Boolean))
            .Select(c => c.Name)
            .ToList();

        if (searchableColumns.Count == 0) return;

        var terms = ExtractSearchTerms(search);

        if (terms.Count == 0) return;

        foreach (var term in terms)
        {
            var pattern = $"%{term}%";
            query.Where(q =>
            {
                q = q.WhereRaw(BuildSearchClause(searchableColumns[0], provider), pattern);
                for (int i = 1; i < searchableColumns.Count; i++)
                {
                    q = q.OrWhereRaw(BuildSearchClause(searchableColumns[i], provider), pattern);
                }
                return q;
            });
        }
    }

    private static string BuildSearchClause(string columnName, DatabaseProvider provider)
    {
        var quotedColumn = QuoteIdentifier(columnName);
        return provider == DatabaseProvider.Postgres
            ? $"CAST({quotedColumn} AS TEXT) ILIKE ?"
            : $"CAST({quotedColumn} AS TEXT) LIKE ?";
    }

    private static List<string> ExtractSearchTerms(string search)
    {
        var terms = new List<string>();
        var buffer = new List<char>(search.Length);

        foreach (var ch in search)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer.Add(ch);
                continue;
            }

            FlushToken(buffer, terms);
        }

        FlushToken(buffer, terms);
        return terms
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void FlushToken(List<char> buffer, List<string> terms)
    {
        if (buffer.Count == 0) return;
        terms.Add(new string(buffer.ToArray()));
        buffer.Clear();
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    internal static Query ApplySortingStatic(
        Query query,
        IReadOnlyList<SortClause> sorts,
        TableDef table,
        DatabaseProvider provider)
    {
        if (sorts.Count == 0)
            return query;

        var columnLookup = table.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var sort in sorts)
        {
            if (!columnLookup.TryGetValue(sort.Column, out var column))
            {
                query = sort.Descending ? query.OrderByDesc(sort.Column) : query.OrderBy(sort.Column);
                continue;
            }

            var rawExpression = BuildSortExpression(column, provider);
            if (rawExpression is null)
            {
                query = sort.Descending ? query.OrderByDesc(sort.Column) : query.OrderBy(sort.Column);
                continue;
            }

            query = query.OrderByRaw($"{rawExpression} {(sort.Descending ? "DESC" : "ASC")}");
        }

        return query;
    }

    private static string? BuildSortExpression(ColumnDef column, DatabaseProvider provider)
    {
        if (provider != DatabaseProvider.Sqlite)
            return null;

        if (column.Type is not ColumnType.Date and not ColumnType.DateTime)
            return null;

        return BuildSqliteTemporalSortExpression(QuoteIdentifier(column.Name));
    }

    private static string BuildSqliteTemporalSortExpression(string quotedColumn)
    {
        var remainderAfterYear = $"substr({quotedColumn}, 6)";
        var monthSeparatorIndex = $"instr({remainderAfterYear}, '-')";
        var monthText = $"substr({remainderAfterYear}, 1, {monthSeparatorIndex} - 1)";
        var dayAndTime = $"substr({remainderAfterYear}, {monthSeparatorIndex} + 1)";
        var dayTimeSeparatorIndex =
            $"CASE WHEN instr({dayAndTime}, 'T') > 0 THEN instr({dayAndTime}, 'T') " +
            $"WHEN instr({dayAndTime}, ' ') > 0 THEN instr({dayAndTime}, ' ') ELSE 0 END";
        var dayText =
            $"CASE WHEN {dayTimeSeparatorIndex} > 0 THEN substr({dayAndTime}, 1, {dayTimeSeparatorIndex} - 1) " +
            $"ELSE {dayAndTime} END";
        var timeText =
            $"CASE WHEN {dayTimeSeparatorIndex} > 0 THEN substr({dayAndTime}, {dayTimeSeparatorIndex}) " +
            "ELSE '' END";

        var normalized =
            $"printf('%04d-%02d-%02d%s', " +
            $"CAST(substr({quotedColumn}, 1, 4) AS INTEGER), " +
            $"CAST({monthText} AS INTEGER), " +
            $"CAST({dayText} AS INTEGER), " +
            $"{timeText})";

        return $"CASE WHEN {quotedColumn} IS NULL OR {quotedColumn} = '' THEN NULL ELSE {normalized} END";
    }

    internal static void ApplyFiltersStatic(SqlKata.Query query, IReadOnlyList<FilterClause> filters)
    {
        foreach (var filter in filters)
        {
            switch (filter.Operator)
            {
                case "eq":
                    query.Where(filter.Column, filter.Value);
                    break;
                case "neq":
                    query.WhereNot(filter.Column, filter.Value);
                    break;
                case "gt":
                    query.Where(filter.Column, ">", filter.Value);
                    break;
                case "gte":
                    query.Where(filter.Column, ">=", filter.Value);
                    break;
                case "lt":
                    query.Where(filter.Column, "<", filter.Value);
                    break;
                case "lte":
                    query.Where(filter.Column, "<=", filter.Value);
                    break;
                case "like":
                    query.WhereLike(filter.Column, filter.Value ?? "");
                    break;
                case "in":
                    var values = (filter.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    query.WhereIn(filter.Column, values);
                    break;
                case "null":
                    query.WhereNull(filter.Column);
                    break;
                case "notnull":
                    query.WhereNotNull(filter.Column);
                    break;
            }
        }
    }

    private static IDictionary<string, object?> ToDictionary(object row)
    {
        if (row is IDictionary<string, object?> dict)
            return dict;

        if (row is IDictionary<string, object> nonNullableDict)
        {
            var result = new Dictionary<string, object?>(nonNullableDict.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in nonNullableDict)
            {
                result[kvp.Key] = kvp.Value;
            }
            return result;
        }

        throw new InvalidOperationException($"Unexpected row type: {row.GetType().FullName}");
    }
}
