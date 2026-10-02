using System.Globalization;
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
    private readonly SchemaCache _schemaCache;

    public CrudService(
        QueryService queryService,
        DatabaseConfig dbConfig,
        RequestBodyValidator validator,
        SchemaCache schemaCache)
    {
        _queryService = queryService;
        _dbConfig = dbConfig;
        _validator = validator;
        _schemaCache = schemaCache;
    }

    public async Task<ListResult> ListAsync(
        TableDef table,
        ListQueryParameters parameters,
        IReadOnlyCollection<string>? additionalSelectColumns = null)
    {
        await using var scope = await _queryService.UseDataAsync();

        // Build the base query with filters
        var baseQuery = scope.Db.Query(table.Name);
        ApplyFiltersStatic(baseQuery, parameters.Filters, table, _dbConfig.Provider);
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

    public async Task<IDictionary<string, object?>> GetByIdAsync(TableDef table, string id)
    {
        var keyValues = GetKeyValues(table, id);
        await using var scope = await _queryService.UseDataAsync();
        var row = await ApplyKeyFilter(scope.Db.Query(table.Name), keyValues)
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
        var primaryKeyColumns = GetPrimaryKeyColumns(table);

        await using var scope = await _queryService.UseDataAsync();
        object? row;

        if (primaryKeyColumns.Count == 1)
        {
            var primaryKey = primaryKeyColumns[0];
            var id = await InsertAndGetIdAsync(scope, table.Name, primaryKey, data);

            row = await scope.Db.Query(table.Name)
                .Where(primaryKey.Name, id)
                .FirstOrDefaultAsync();
        }
        else
        {
            await InsertAsync(scope, table.Name, data);

            var query = scope.Db.Query(table.Name);
            foreach (var primaryKey in primaryKeyColumns)
            {
                if (!data.TryGetValue(primaryKey.Name, out var value))
                {
                    throw new InvalidOperationException(
                        $"Insert for table '{table.Name}' did not include composite primary key column '{primaryKey.Name}'.");
                }

                query = query.Where(primaryKey.Name, value);
            }

            row = await query.FirstOrDefaultAsync();
        }

        return ToDictionary(row!);
    }

    public async Task<IDictionary<string, object?>> UpdateAsync(TableDef table, string id, JsonElement body)
    {
        var keyValues = GetKeyValues(table, id);
        var validationId = GetSingleKeyValueOrCompositeToken(keyValues, id);
        await EnsureRowExistsAsync(table, keyValues);
        await _validator.ValidateUpdateAsync(table, validationId, body);

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
        await ApplyKeyFilter(scope.Db.Query(table.Name), keyValues).UpdateAsync(data);

        var row = await ApplyKeyFilter(scope.Db.Query(table.Name), keyValues)
            .FirstOrDefaultAsync();

        return ToDictionary(row!);
    }

    public async Task<IDictionary<string, object?>> PatchAsync(TableDef table, string id, JsonElement body)
    {
        var keyValues = GetKeyValues(table, id);
        var validationId = GetSingleKeyValueOrCompositeToken(keyValues, id);
        await EnsureRowExistsAsync(table, keyValues);
        await _validator.ValidatePatchAsync(table, validationId, body);

        var columns = GetWritableColumns(table);
        var data = ExtractColumnValues(body, columns);

        if (data.Count > 0)
        {
            await using var scope = await _queryService.UseDataAsync();
            await ApplyKeyFilter(scope.Db.Query(table.Name), keyValues).UpdateAsync(data);
        }

        await using var readScope = await _queryService.UseDataAsync();
        var row = await ApplyKeyFilter(readScope.Db.Query(table.Name), keyValues)
            .FirstOrDefaultAsync();

        return ToDictionary(row!);
    }

    public async Task DeleteAsync(TableDef table, string id)
    {
        var keyValues = GetKeyValues(table, id);
        await using var scope = await _queryService.UseDataAsync();
        var affected = await ApplyKeyFilter(scope.Db.Query(table.Name), keyValues).DeleteAsync();

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

    private async Task EnsureRowExistsAsync(
        TableDef table,
        IReadOnlyDictionary<string, object> keyValues)
    {
        await using var scope = await _queryService.UseDataAsync();
        var exists = await ApplyKeyFilter(scope.Db.Query(table.Name), keyValues)
            .SelectRaw("1")
            .FirstOrDefaultAsync();

        if (exists is null)
        {
            var rowKey = string.Join("|", keyValues.Select(pair => $"{pair.Key}:{pair.Value}"));
            throw new ApiNotFoundException(
                $"Row with id {rowKey} not found in '{table.Name}'.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["table"] = table.Name,
                    ["id"] = rowKey
                });
        }
    }

    private static SqlKata.Query ApplyKeyFilter(
        SqlKata.Query query,
        IReadOnlyDictionary<string, object> keyValues)
    {
        foreach (var pair in keyValues)
        {
            query = query.Where(pair.Key, pair.Value);
        }

        return query;
    }

    private static List<ColumnDef> GetWritableColumns(TableDef table)
    {
        return table.Columns
            .Where(IsWritableColumn)
            .ToList();
    }

    internal static bool IsWritableColumn(ColumnDef column)
    {
        if (column.Type == ColumnType.Computed)
            return false;

        if (column.Type == ColumnType.Reference && column.PrimaryKey)
            return true;

        return !column.PrimaryKey;
    }

    private Dictionary<string, object?> ExtractColumnValues(JsonElement body, List<ColumnDef> columns)
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

            data[col.Name] = CoerceValue(prop.Value, col);
        }

        return data;
    }

    private object? CoerceValue(JsonElement value, ColumnDef column)
    {
        // Postgres uuid columns need a Guid parameter; a string parameter is sent
        // as text and the insert/update fails with "column is of type uuid but
        // expression is of type text". SQLite stores uuids as text.
        if (column.Type == ColumnType.Uuid && _dbConfig.Provider == DatabaseProvider.Postgres)
        {
            if (value.ValueKind == JsonValueKind.Null)
                return null;

            var raw = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.GetRawText();
            if (raw is not null && Guid.TryParse(raw, out var guid))
                return guid;
        }

        if (column.Type == ColumnType.Reference && IsUuidReference(column))
        {
            if (value.ValueKind == JsonValueKind.Null)
                return null;

            var raw = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.GetRawText();
            if (raw is not null && Guid.TryParse(raw, out var guid))
                return _dbConfig.Provider == DatabaseProvider.Postgres ? guid : guid.ToString();
        }

        // Postgres DATE / TIMESTAMPTZ columns reject text parameters; SQLite stores
        // ISO 8601 strings. The validator has already checked the string shape.
        if (column.Type is ColumnType.Date or ColumnType.DateTime && value.ValueKind == JsonValueKind.String)
            return CoerceTemporalForProvider(value.GetString(), column.Type, _dbConfig.Provider);

        return CoerceValueStatic(value, column.Type);
    }

    /// <summary>
    /// Converts an ISO 8601 string into the parameter type the provider expects for
    /// a <see cref="ColumnType.Date"/> or <see cref="ColumnType.DateTime"/> column.
    /// Postgres gets <see cref="DateOnly"/> / UTC <see cref="DateTime"/>, SQLite
    /// keeps the string. Unparseable input is returned unchanged so the database
    /// (or the validator upstream) reports it.
    /// </summary>
    internal static object? CoerceTemporalForProvider(string? raw, ColumnType type, DatabaseProvider provider)
    {
        if (raw is null || provider != DatabaseProvider.Postgres)
            return raw;

        if (type == ColumnType.Date)
        {
            if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return date;
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateFromStamp))
                return DateOnly.FromDateTime(dateFromStamp.UtcDateTime);
            return raw;
        }

        if (type == ColumnType.DateTime)
        {
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp)
                ? stamp.UtcDateTime
                : raw;
        }

        return raw;
    }

    private bool IsUuidReference(ColumnDef column)
    {
        return column.Type == ColumnType.Reference &&
            !string.IsNullOrWhiteSpace(column.References) &&
            _schemaCache.GetTable(column.References)?.Columns.FirstOrDefault(c => c.PrimaryKey)?.Type == ColumnType.Uuid;
    }

    internal static object? CoerceValueStatic(JsonElement value, ColumnType type)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;

        return type switch
        {
            ColumnType.Text or ColumnType.Enum or ColumnType.File
                => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
            ColumnType.Integer
                => value.ValueKind switch
                {
                    JsonValueKind.Number => value.GetInt64(),
                    JsonValueKind.String when long.TryParse(value.GetString(), out var n) => n,
                    _ => value.GetRawText()
                },
            ColumnType.Reference
                => value.ValueKind switch
                {
                    JsonValueKind.Number => value.GetInt64(),
                    JsonValueKind.String when long.TryParse(value.GetString(), out var n) => n,
                    JsonValueKind.String => value.GetString(),
                    _ => value.GetRawText()
                },
            ColumnType.Uuid
                => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
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

    private async Task<object> InsertAndGetIdAsync(
        QueryScope scope,
        string tableName,
        ColumnDef primaryKey,
        Dictionary<string, object?> data)
    {
        if (primaryKey.Type == ColumnType.Uuid)
        {
            var generatedId = Guid.NewGuid().ToString();
            data[primaryKey.Name] = _dbConfig.Provider == DatabaseProvider.Postgres
                ? Guid.Parse(generatedId)
                : generatedId;
        }

        var columns = data.Keys.ToList();

        if (columns.Count == 0)
        {
            if (_dbConfig.Provider == DatabaseProvider.Postgres)
            {
                var sql = $"INSERT INTO \"{tableName}\" DEFAULT VALUES RETURNING \"{primaryKey.Name}\";";
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
            var sql = $"INSERT INTO \"{tableName}\" ({quotedColumns}) VALUES ({quotedParams}) RETURNING \"{primaryKey.Name}\";";
            var id = await scope.Db.Connection.ExecuteScalarAsync(sql, parameters);
            return NormalizeReturnedId(id);
        }

        var insertSql = $"INSERT INTO \"{tableName}\" ({quotedColumns}) VALUES ({quotedParams});";
        await scope.Db.Connection.ExecuteAsync(insertSql, parameters);
        if (primaryKey.Type == ColumnType.Uuid)
            return data[primaryKey.Name]!;

        return await scope.Db.Connection.ExecuteScalarAsync<long>("SELECT last_insert_rowid();");
    }

    private async Task InsertAsync(
        QueryScope scope,
        string tableName,
        Dictionary<string, object?> data)
    {
        if (data.Count == 0)
        {
            var defaultValuesSql = $"INSERT INTO \"{tableName}\" DEFAULT VALUES;";
            await scope.Db.Connection.ExecuteAsync(defaultValuesSql);
            return;
        }

        var columns = data.Keys.ToList();
        var parameters = new DynamicParameters();
        for (int i = 0; i < columns.Count; i++)
        {
            parameters.Add($"p{i}", data[columns[i]]);
        }

        var quotedColumns = string.Join(", ", columns.Select(c => $"\"{c}\""));
        var quotedParams = string.Join(", ", columns.Select((_, i) => $"@p{i}"));
        var sql = $"INSERT INTO \"{tableName}\" ({quotedColumns}) VALUES ({quotedParams});";
        await scope.Db.Connection.ExecuteAsync(sql, parameters);
    }

    private static List<ColumnDef> GetPrimaryKeyColumns(TableDef table)
    {
        var primaryKeyColumns = table.Columns.Where(c => c.PrimaryKey).ToList();
        if (primaryKeyColumns.Count == 0)
        {
            throw new InvalidOperationException($"Table '{table.Name}' does not define a primary key column.");
        }

        return primaryKeyColumns;
    }

    private IReadOnlyDictionary<string, object> GetKeyValues(TableDef table, string id)
    {
        var primaryKeyColumns = GetPrimaryKeyColumns(table);
        if (primaryKeyColumns.Count == 1)
        {
            var primaryKey = primaryKeyColumns[0];
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                [primaryKey.Name] = CoerceId(id, primaryKey)
            };
        }

        var parsedSegments = id
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(segment => segment.Split(':', 2, StringSplitOptions.TrimEntries))
            .ToDictionary(
                parts => parts[0],
                parts => parts.Length > 1 ? parts[1] : string.Empty,
                StringComparer.OrdinalIgnoreCase);

        var keyValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var primaryKey in primaryKeyColumns)
        {
            if (!parsedSegments.TryGetValue(primaryKey.Name, out var rawValue) ||
                string.IsNullOrWhiteSpace(rawValue))
            {
                throw new ApiValidationException(
                    $"Composite primary key for table '{table.Name}' is missing '{primaryKey.Name}'.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["table"] = table.Name,
                        ["id"] = id,
                        ["field"] = primaryKey.Name
                    });
            }

            keyValues[primaryKey.Name] = CoerceId(rawValue, primaryKey);
        }

        return keyValues;
    }

    private static object GetSingleKeyValueOrCompositeToken(
        IReadOnlyDictionary<string, object> keyValues,
        string rawId)
    {
        return keyValues.Count == 1 ? keyValues.Values.First() : rawId;
    }

    private static ColumnDef GetPrimaryKeyColumn(TableDef table)
    {
        return GetPrimaryKeyColumns(table)[0];
    }

    private object CoerceId(string id, ColumnDef primaryKey)
    {
        if (primaryKey.Type == ColumnType.Uuid || IsUuidReference(primaryKey))
        {
            if (!Guid.TryParse(id, out var guid))
                throw new ApiValidationException(
                    $"Primary key '{primaryKey.Name}' expects a UUID value.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["field"] = primaryKey.Name,
                        ["value"] = id
                    });

            return _dbConfig.Provider == DatabaseProvider.Postgres ? guid : guid.ToString();
        }

        if (!long.TryParse(id, out var numericId))
            throw new ApiValidationException(
                $"Primary key '{primaryKey.Name}' expects an integer value.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["field"] = primaryKey.Name,
                    ["value"] = id
                });

        return numericId;
    }

    private static object NormalizeReturnedId(object? id)
    {
        return id switch
        {
            Guid guid => guid,
            null => throw new InvalidOperationException("Insert did not return a primary key value."),
            _ => id
        };
    }

    internal static void ApplySearchStatic(
        SqlKata.Query query,
        string? search,
        TableDef table,
        DatabaseProvider provider)
    {
        if (string.IsNullOrWhiteSpace(search)) return;

        var hasTextLikeColumns = table.Columns.Any(c =>
            c.Type is ColumnType.Text or ColumnType.Enum or ColumnType.File or ColumnType.Date or ColumnType.DateTime or ColumnType.Uuid);

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

    internal static void ApplyFiltersStatic(
        SqlKata.Query query,
        IReadOnlyList<FilterClause> filters,
        TableDef? table = null,
        DatabaseProvider? provider = null)
    {
        var columnsByName = table?.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var filter in filters)
        {
            ColumnDef? column = null;
            columnsByName?.TryGetValue(filter.Column, out column);
            var value = CoerceFilterValue(filter.Value, column, provider);
            switch (filter.Operator)
            {
                case "eq":
                    query.Where(filter.Column, value);
                    break;
                case "neq":
                    query.WhereNot(filter.Column, value);
                    break;
                case "gt":
                    query.Where(filter.Column, ">", value);
                    break;
                case "gte":
                    query.Where(filter.Column, ">=", value);
                    break;
                case "lt":
                    query.Where(filter.Column, "<", value);
                    break;
                case "lte":
                    query.Where(filter.Column, "<=", value);
                    break;
                case "like":
                    query.WhereLike(filter.Column, filter.Value ?? "");
                    break;
                case "in":
                    var values = (filter.Value ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(v => CoerceFilterValue(v, column, provider))
                        .ToArray();
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

    private static object? CoerceFilterValue(string? value, ColumnDef? column, DatabaseProvider? provider)
    {
        if (value is null || column is null)
            return value;

        return column.Type switch
        {
            ColumnType.Uuid when provider == DatabaseProvider.Postgres && Guid.TryParse(value, out var guid) => guid,
            ColumnType.Reference when provider == DatabaseProvider.Postgres && Guid.TryParse(value, out var refGuid) => refGuid,
            ColumnType.Integer or ColumnType.Reference when long.TryParse(value, out var n) => n,
            ColumnType.Date or ColumnType.DateTime when provider is not null
                => CoerceTemporalForProvider(value, column.Type, provider.Value),
            _ => value
        };
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
