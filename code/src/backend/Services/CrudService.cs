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

    public CrudService(QueryService queryService, DatabaseConfig dbConfig)
    {
        _queryService = queryService;
        _dbConfig = dbConfig;
    }

    public async Task<ListResult> ListAsync(TableDef table, ListQueryParameters parameters)
    {
        await using var scope = await _queryService.UseDataAsync();

        // Build the base query with filters
        var baseQuery = scope.Db.Query(table.Name);
        ApplyFiltersStatic(baseQuery, parameters.Filters);

        // Count total matching rows
        var total = await baseQuery.Clone().CountAsync<long>();

        // Apply sorting
        foreach (var sort in parameters.Sorts)
        {
            if (sort.Descending)
                baseQuery = baseQuery.OrderByDesc(sort.Column);
            else
                baseQuery = baseQuery.OrderBy(sort.Column);
        }

        // Apply field selection
        if (parameters.Fields is { Count: > 0 })
        {
            baseQuery = baseQuery.Select(parameters.Fields.ToArray());
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

            data[col.Name] = CoerceValue(prop.Value, col.Type);
        }

        return data;
    }

    private static object? CoerceValue(JsonElement value, ColumnType type)
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
