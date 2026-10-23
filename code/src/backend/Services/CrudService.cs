using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Formula;
using SqlKata;
using SqlKata.Execution;

namespace Bento.Api.Services;

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
    private readonly ComputedColumns _computed;
    private readonly DataVersion? _versions;
    private readonly RowEvents? _events;

    public CrudService(
        QueryService queryService,
        DatabaseConfig dbConfig,
        RequestBodyValidator validator,
        SchemaCache schemaCache,
        ComputedColumns computed,
        DataVersion? versions = null,
        RowEvents? events = null)
    {
        _events = events;
        _queryService = queryService;
        _dbConfig = dbConfig;
        _validator = validator;
        _schemaCache = schemaCache;
        _computed = computed;
        _versions = versions;
    }

    /// <summary>A table made from a saved query (Step 5.8): its rows come from the query.</summary>
    public static long? SourceQueryOf(TableDef table) =>
        table.AdditionalData is not null && table.AdditionalData.TryGetValue("fromQuery", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var value)
            ? value
            : null;

    /// <summary>Rows of a table made from a saved query change with the query, not by hand; every other write counts as a change.</summary>
    private void BeforeWrite(TableDef table)
    {
        if (SourceQueryOf(table) is not null)
            throw new ApiForbiddenException($"'{table.Name}' is made from a saved query; change the query, not its rows.",
                new Dictionary<string, object?> { ["table"] = table.Name });
        _versions?.Bump();
    }

    // ── Row events (Step 5.12) ───────────────────────────────────────────

    private async Task<bool> WantsAsync(TableDef table, string op) => _events is not null && await _events.WantsAsync(table, op);

    private void Publish(TableDef table, string op, IReadOnlyList<RowChange> rows) => _events?.Publish(table, op, rows);

    /// <summary>The row's key as the API writes it: the id, or <c>a:1|b:2</c> for a composite key.</summary>
    private static string KeyOf(TableDef table, IDictionary<string, object?> row)
    {
        var keys = table.Columns.Where(c => c.PrimaryKey).ToList();
        string Text(ColumnDef c) => Convert.ToString(row.TryGetValue(c.Name, out var v) ? v : null, CultureInfo.InvariantCulture) ?? "";
        return keys.Count == 1 ? Text(keys[0]) : string.Join("|", keys.Select(k => $"{k.Name}:{Text(k)}"));
    }

    /// <summary>A whole row as the API shows it, or null; inside the write's transaction when there is one.</summary>
    private async Task<IDictionary<string, object?>?> ReadRowAsync(QueryScope scope, TableDef table, IReadOnlyDictionary<string, object> keyValues, IDbTransaction? transaction = null)
    {
        var row = await ApplyKeyFilter(ReadQuery(scope, table), keyValues).FirstOrDefaultAsync(transaction);
        return row is null ? null : ToResponseRow(table, row);
    }

    /// <summary>The stored columns whose value differs between two readings of a row.</summary>
    private static List<string> ChangedColumns(TableDef table, IDictionary<string, object?> before, IDictionary<string, object?> after) =>
        table.Columns.Where(c => ColumnTypes.HasPhysicalColumn(c.Type) && !c.PrimaryKey)
            .Where(c => JsonSerializer.Serialize(before.TryGetValue(c.Name, out var b) ? b : null) != JsonSerializer.Serialize(after.TryGetValue(c.Name, out var a) ? a : null))
            .Select(c => c.Name).ToList();

    private static IReadOnlyDictionary<string, object> SingleKey(ColumnDef key, object id) =>
        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { [key.Name] = id };

    /// <summary>A query for whole rows: the stored columns and the computed ones.</summary>
    private Query ReadQuery(QueryScope scope, TableDef table) => _computed.Select(scope.Db.Query(table.Name), table);

    public async Task<ListResult> ListAsync(
        TableDef table,
        ListQueryParameters parameters,
        IReadOnlyCollection<string>? additionalSelectColumns = null)
    {
        await using var scope = await _queryService.UseDataAsync();

        // Build the base query with filters
        var baseQuery = scope.Db.Query(table.Name);
        var computed = _computed.For(table);
        ApplyFiltersStatic(baseQuery, parameters.Filters, table, _dbConfig.Provider, computed);
        ApplySearchStatic(baseQuery, parameters.Search, table, _dbConfig.Provider, _schemaCache.GetTable);

        // Count total matching rows
        var total = await baseQuery.Clone().CountAsync<long>();

        // Apply sorting
        baseQuery = ApplySortingStatic(baseQuery, parameters.Sorts, table, _dbConfig.Provider, computed);

        // Apply field selection (computed columns by their formula)
        baseQuery = parameters.Fields is { Count: > 0 }
            ? _computed.Select(baseQuery, table, BuildSelectedColumns(parameters.Fields, additionalSelectColumns).ToList())
            : _computed.Select(baseQuery, table);

        // Apply pagination
        baseQuery = baseQuery.ForPage(parameters.Page, parameters.PerPage);

        var rows = (await baseQuery.GetAsync()).Select(row => ToResponseRow(table, (object)row)).ToList();
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
        var row = await ApplyKeyFilter(ReadQuery(scope, table), keyValues)
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

        return ToResponseRow(table, row);
    }

    public async Task<IDictionary<string, object?>> CreateAsync(TableDef table, JsonElement body)
    {
        BeforeWrite(table);
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

            row = await ReadQuery(scope, table)
                .Where($"{table.Name}.{primaryKey.Name}", id)
                .FirstOrDefaultAsync();
        }
        else
        {
            // A composite key (an N:M link) that already exists is a conflict,
            // not a database error.
            var existing = scope.Db.Query(table.Name);
            var hasAllKeys = true;
            foreach (var primaryKey in primaryKeyColumns)
            {
                if (!data.TryGetValue(primaryKey.Name, out var keyValue) || keyValue is null)
                {
                    hasAllKeys = false;
                    break;
                }
                existing = existing.Where(primaryKey.Name, keyValue);
            }
            if (hasAllKeys && await existing.FirstOrDefaultAsync() is not null)
            {
                throw new ApiConflictException(
                    "This row already exists.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["table"] = table.Name,
                        ["columns"] = primaryKeyColumns.Select(c => c.Name).ToArray()
                    });
            }

            await InsertAsync(scope, table.Name, data);

            var query = ReadQuery(scope, table);
            foreach (var primaryKey in primaryKeyColumns)
            {
                if (!data.TryGetValue(primaryKey.Name, out var value))
                {
                    throw new InvalidOperationException(
                        $"Insert for table '{table.Name}' did not include composite primary key column '{primaryKey.Name}'.");
                }

                query = query.Where($"{table.Name}.{primaryKey.Name}", value);
            }

            row = await query.FirstOrDefaultAsync();
        }

        var created = ToResponseRow(table, row!);
        Publish(table, "created", [new RowChange(KeyOf(table, created), null, created)]);
        return created;
    }

    public async Task<IDictionary<string, object?>> UpdateAsync(TableDef table, string id, JsonElement body)
    {
        BeforeWrite(table);
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
        await EnsureKeyFreeAsync(scope, table, keyValues, data);
        var before = await WantsAsync(table, "updated") ? await ReadRowAsync(scope, table, keyValues) : null;
        await ApplyKeyFilter(scope.Db.Query(table.Name), keyValues).UpdateAsync(data);

        var row = await ApplyKeyFilter(ReadQuery(scope, table), KeyValuesAfterWrite(keyValues, data))
            .FirstOrDefaultAsync();

        var updated = ToResponseRow(table, row!);
        PublishUpdate(table, before, updated);
        return updated;
    }

    /// <summary>An updated row; with what it was before (when a rule listens), only when a value really changed.</summary>
    private void PublishUpdate(TableDef table, IDictionary<string, object?>? before, IDictionary<string, object?> after)
    {
        if (before is null)
        {
            Publish(table, "updated", [new RowChange(KeyOf(table, after), null, after)]);
            return;
        }
        var changed = ChangedColumns(table, before, after);
        if (changed.Count > 0)
            Publish(table, "updated", [new RowChange(KeyOf(table, after), before, after, changed)]);
    }

    public async Task<IDictionary<string, object?>> PatchAsync(TableDef table, string id, JsonElement body)
    {
        BeforeWrite(table);
        var keyValues = GetKeyValues(table, id);
        var validationId = GetSingleKeyValueOrCompositeToken(keyValues, id);
        await EnsureRowExistsAsync(table, keyValues);
        await _validator.ValidatePatchAsync(table, validationId, body);

        var columns = GetWritableColumns(table);
        var data = ExtractColumnValues(body, columns);

        IDictionary<string, object?>? before = null;
        if (data.Count > 0)
        {
            await using var scope = await _queryService.UseDataAsync();
            await EnsureKeyFreeAsync(scope, table, keyValues, data);
            if (await WantsAsync(table, "updated"))
                before = await ReadRowAsync(scope, table, keyValues);
            await ApplyKeyFilter(scope.Db.Query(table.Name), keyValues).UpdateAsync(data);
        }

        await using var readScope = await _queryService.UseDataAsync();
        var row = await ApplyKeyFilter(ReadQuery(readScope, table), KeyValuesAfterWrite(keyValues, data))
            .FirstOrDefaultAsync();

        var patched = ToResponseRow(table, row!);
        if (data.Count > 0)
            PublishUpdate(table, before, patched);
        return patched;
    }

    /// <summary>
    /// Moving a row to a key another row already has (a junction row pointed
    /// at a record it is already linked to) is a conflict, not a database error.
    /// </summary>
    private static async Task EnsureKeyFreeAsync(
        QueryScope scope,
        TableDef table,
        IReadOnlyDictionary<string, object> keyValues,
        IDictionary<string, object?> written)
    {
        var nextKey = KeyValuesAfterWrite(keyValues, written);
        var changed = nextKey.Any(pair => !Equals(pair.Value, keyValues[pair.Key]) &&
            !string.Equals(Convert.ToString(pair.Value, CultureInfo.InvariantCulture), Convert.ToString(keyValues[pair.Key], CultureInfo.InvariantCulture), StringComparison.Ordinal));
        if (!changed)
            return;

        if (await ApplyKeyFilter(scope.Db.Query(table.Name), nextKey).FirstOrDefaultAsync() is not null)
        {
            throw new ApiConflictException(
                "This row already exists.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["table"] = table.Name,
                    ["columns"] = nextKey.Keys.ToArray()
                });
        }
    }

    /// <summary>
    /// The row's key after an update: a written key column (a junction row
    /// pointed at another record) changes the key the row is read back by.
    /// </summary>
    private static IReadOnlyDictionary<string, object> KeyValuesAfterWrite(
        IReadOnlyDictionary<string, object> keyValues,
        IDictionary<string, object?> written)
    {
        var next = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in keyValues)
        {
            var writtenKey = written.Keys.FirstOrDefault(k => string.Equals(k, pair.Key, StringComparison.OrdinalIgnoreCase));
            next[pair.Key] = writtenKey is not null && written[writtenKey] is { } value ? value : pair.Value;
        }
        return next;
    }

    public async Task DeleteAsync(TableDef table, string id)
    {
        BeforeWrite(table);
        var keyValues = GetKeyValues(table, id);
        await using var scope = await _queryService.UseDataAsync();
        var before = await WantsAsync(table, "deleted") ? await ReadRowAsync(scope, table, keyValues) : null;
        var affected = await ApplyKeyFilter(scope.Db.Query(table.Name), keyValues).DeleteAsync();
        if (affected > 0)
            Publish(table, "deleted", [new RowChange(before is null ? id : KeyOf(table, before), before, null)]);

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
        Dictionary<string, object?> data,
        IDbTransaction? transaction = null)
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
                return await scope.Db.Connection.ExecuteScalarAsync<long>(sql, transaction: transaction);
            }

            var defaultValuesSql = $"INSERT INTO \"{tableName}\" DEFAULT VALUES;";
            await scope.Db.Connection.ExecuteAsync(defaultValuesSql, transaction: transaction);
            return await scope.Db.Connection.ExecuteScalarAsync<long>("SELECT last_insert_rowid();", transaction: transaction);
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
            var id = await scope.Db.Connection.ExecuteScalarAsync(sql, parameters, transaction);
            return NormalizeReturnedId(id);
        }

        var insertSql = $"INSERT INTO \"{tableName}\" ({quotedColumns}) VALUES ({quotedParams});";
        await scope.Db.Connection.ExecuteAsync(insertSql, parameters, transaction);
        if (primaryKey.Type == ColumnType.Uuid)
            return data[primaryKey.Name]!;

        return await scope.Db.Connection.ExecuteScalarAsync<long>("SELECT last_insert_rowid();", transaction: transaction);
    }

    private async Task InsertAsync(
        QueryScope scope,
        string tableName,
        Dictionary<string, object?> data,
        IDbTransaction? transaction = null)
    {
        if (data.Count == 0)
        {
            var defaultValuesSql = $"INSERT INTO \"{tableName}\" DEFAULT VALUES;";
            await scope.Db.Connection.ExecuteAsync(defaultValuesSql, transaction: transaction);
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
        await scope.Db.Connection.ExecuteAsync(sql, parameters, transaction);
    }

    public const int MaxBulkRows = 1000;

    public sealed record BulkRowResult(int Index, object? Id, string Action);

    /// <summary>
    /// Writes many rows in one transaction: every row or none. With
    /// <paramref name="upsertOn"/> (e.g. <c>["external_id"]</c>) a row whose key
    /// columns match an existing row updates it (only the given columns, like
    /// PATCH); the others are inserted. Each row is validated like a single
    /// write first; an error names the row (<c>rows[3]</c>).
    /// </summary>
    public async Task<IReadOnlyList<BulkRowResult>> BulkAsync(TableDef table, IReadOnlyList<JsonElement> rows, IReadOnlyList<string> upsertOn)
    {
        BeforeWrite(table);
        if (rows.Count == 0)
            throw new ApiValidationException("Send at least one row.", new Dictionary<string, object?> { ["field"] = "rows" });
        if (rows.Count > MaxBulkRows)
            throw new ApiValidationException($"At most {MaxBulkRows} rows per call; send the rest in another call.", new Dictionary<string, object?> { ["field"] = "rows" });

        var columns = GetWritableColumns(table);
        var keys = upsertOn.Select(name => columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ApiValidationException($"'{name}' is not a stored column of {table.Name}.", new Dictionary<string, object?> { ["field"] = "upsertOn" }))
            .ToList();
        var primaryKeyColumns = GetPrimaryKeyColumns(table);
        var singleKey = primaryKeyColumns.Count == 1 ? primaryKeyColumns[0] : null;

        // Rows with the same key in one call become one row (later values win), then
        // every row is validated against the table as it is now, before writing anything.
        var plans = new List<(List<int> Indexes, JsonObject Body)>();
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            try
            {
                if (rows[i].ValueKind != JsonValueKind.Object)
                    throw new ApiValidationException("Each row is an object of column values.");
                var body = JsonNode.Parse(rows[i].GetRawText())!.AsObject();
                if (keys.Count == 0)
                {
                    plans.Add(([i], body));
                    continue;
                }
                var data = ExtractColumnValues(rows[i], columns);
                foreach (var key in keys)
                {
                    if (!data.TryGetValue(key.Name, out var value) || value is null)
                        throw new ApiValidationException($"'{key.Name}' is needed to find the row (upsertOn).", new Dictionary<string, object?> { ["field"] = key.Name });
                }
                var keyText = JsonSerializer.Serialize(keys.Select(k => Convert.ToString(data[k.Name], CultureInfo.InvariantCulture)));
                if (byKey.TryGetValue(keyText, out var earlier))
                {
                    foreach (var (name, value) in body.ToList())
                    {
                        body.Remove(name);
                        plans[earlier].Body[name] = value;
                    }
                    plans[earlier].Indexes.Add(i);
                }
                else
                {
                    byKey[keyText] = plans.Count;
                    plans.Add(([i], body));
                }
            }
            catch (ApiException e)
            {
                throw RowError(i, e);
            }
        }

        var prepared = new List<(List<int> Indexes, Dictionary<string, object?> Data)>();
        foreach (var (indexes, body) in plans)
        {
            var element = JsonSerializer.Deserialize<JsonElement>(body.ToJsonString());
            try
            {
                var data = ExtractColumnValues(element, columns);
                var existing = keys.Count > 0 ? await FindByAsync(table, keys, data, null, null) : null;
                if (existing is null)
                    await _validator.ValidateCreateAsync(table, element);
                else
                    await _validator.ValidatePatchAsync(table, existing, element);
                prepared.Add((indexes, data));
            }
            catch (ApiException e)
            {
                throw RowError(indexes[^1], e);
            }
        }

        var results = new List<BulkRowResult>();
        var wantUpdates = singleKey is not null && keys.Count > 0 && await WantsAsync(table, "updated");
        var createdRows = new List<RowChange>();
        var updatedRows = new List<RowChange>();
        await using var scope = await _queryService.UseDataAsync();
        var connection = scope.Db.Connection;
        if (connection.State != ConnectionState.Open)
            connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (indexes, data) in prepared)
        {
            var existing = keys.Count > 0 ? await FindByAsync(table, keys, data, scope, transaction) : null;
            object? id;
            string action;
            if (existing is not null && singleKey is not null)
            {
                var changes = data.Where(pair => !keys.Any(k => string.Equals(k.Name, pair.Key, StringComparison.OrdinalIgnoreCase)))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                if (changes.Count > 0)
                {
                    var before = wantUpdates ? await ReadRowAsync(scope, table, SingleKey(singleKey, existing), transaction) : null;
                    await scope.Db.Query(table.Name).Where(singleKey.Name, existing).UpdateAsync(changes, transaction);
                    var key = Convert.ToString(existing, CultureInfo.InvariantCulture)!;
                    if (before is null)
                        updatedRows.Add(new RowChange(key, null, null));
                    else if (await ReadRowAsync(scope, table, SingleKey(singleKey, existing), transaction) is { } after && ChangedColumns(table, before, after) is { Count: > 0 } changed)
                        updatedRows.Add(new RowChange(key, before, after, changed));
                }
                (id, action) = (existing, "updated");
            }
            else if (existing is not null)
            {
                // A composite key (a link row) that exists already: nothing else to write.
                (id, action) = (null, "unchanged");
            }
            else if (singleKey is not null)
            {
                (id, action) = (await InsertAndGetIdAsync(scope, table.Name, singleKey, data, transaction), "inserted");
                createdRows.Add(new RowChange(Convert.ToString(id, CultureInfo.InvariantCulture)!, null, null));
            }
            else
            {
                await InsertAsync(scope, table.Name, data, transaction);
                (id, action) = (null, "inserted");
                createdRows.Add(new RowChange(string.Join("|", primaryKeyColumns.Select(k => $"{k.Name}:{Convert.ToString(data.GetValueOrDefault(k.Name), CultureInfo.InvariantCulture)}")), null, null));
            }
            // A key repeated in the call: the first row is added, the later ones change it.
            results.AddRange(indexes.Select((index, n) => new BulkRowResult(index, id, n == 0 ? action : "updated")));
        }
        transaction.Commit();
        Publish(table, "created", createdRows);
        Publish(table, "updated", updatedRows);
        return results.OrderBy(r => r.Index).ToList();
    }

    private static ApiValidationException RowError(int index, ApiException e)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal) { ["row"] = index };
        foreach (var (name, value) in e.Details ?? new Dictionary<string, object?>())
            details[name] = name == "field" ? $"rows[{index}].{value}" : value;
        details.TryAdd("field", $"rows[{index}]");
        return new ApiValidationException($"Row {index + 1}: {e.Message}", details);
    }

    /// <summary>The key of the row whose <paramref name="keys"/> columns hold these values, or null.</summary>
    private async Task<object?> FindByAsync(
        TableDef table,
        IReadOnlyList<ColumnDef> keys,
        IReadOnlyDictionary<string, object?> data,
        QueryScope? scope,
        IDbTransaction? transaction)
    {
        var primaryKey = GetPrimaryKeyColumns(table).Count == 1 ? GetPrimaryKeyColumn(table) : null;
        var conditions = string.Join(" AND ", keys.Select((k, i) => $"\"{k.Name}\" = @k{i}"));
        var parameters = new DynamicParameters();
        for (var i = 0; i < keys.Count; i++)
            parameters.Add($"k{i}", data[keys[i].Name]);
        var select = primaryKey is null ? "1" : $"\"{primaryKey.Name}\"";
        var sql = $"SELECT {select} FROM \"{table.Name}\" WHERE {conditions} LIMIT 1";
        if (scope is not null)
            return await scope.Db.Connection.ExecuteScalarAsync(sql, parameters, transaction);
        await using var own = await _queryService.UseDataAsync();
        return await own.Db.Connection.ExecuteScalarAsync(sql, parameters);
    }

    /// <summary>
    /// Inserts already validated rows (CRUD request bodies) in one transaction:
    /// either every row is written or none is. Used by the file import.
    /// </summary>
    public async Task<int> InsertManyAsync(TableDef table, IReadOnlyList<JsonElement> bodies) =>
        (await ImportManyAsync(table, bodies, [])).Inserted;

    /// <summary>
    /// Inserts and updates already validated rows in one transaction: every
    /// row is written or none is. An update is a PATCH body for the row with
    /// that primary key. Used by the file import.
    /// </summary>
    public async Task<(int Inserted, int Updated)> ImportManyAsync(
        TableDef table,
        IReadOnlyList<JsonElement> bodies,
        IReadOnlyList<(object Id, JsonElement Body)> updates)
    {
        if (bodies.Count == 0 && updates.Count == 0)
            return (0, 0);
        BeforeWrite(table);

        var columns = GetWritableColumns(table);
        var primaryKeyColumns = GetPrimaryKeyColumns(table);
        var generatedUuidKey = primaryKeyColumns.Count == 1 && primaryKeyColumns[0].Type == ColumnType.Uuid
            ? primaryKeyColumns[0]
            : null;

        // Row events (Step 5.12): the new rows' keys, and what updated rows were, only when a rule listens.
        var singleKey = primaryKeyColumns.Count == 1 ? primaryKeyColumns[0] : null;
        var wantCreated = singleKey is not null && bodies.Count > 0 && await WantsAsync(table, "created");
        var wantUpdated = updates.Count > 0 && await WantsAsync(table, "updated");
        var createdRows = new List<RowChange>();
        var updatedRows = new List<RowChange>();

        await using var scope = await _queryService.UseDataAsync();
        var connection = scope.Db.Connection;
        if (connection.State != ConnectionState.Open)
            connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var body in bodies)
        {
            var data = ExtractColumnValues(body, columns);
            if (generatedUuidKey is not null && (!data.TryGetValue(generatedUuidKey.Name, out var key) || key is null))
            {
                var generated = Guid.NewGuid();
                data[generatedUuidKey.Name] = _dbConfig.Provider == DatabaseProvider.Postgres ? generated : generated.ToString();
            }
            if (wantCreated)
                createdRows.Add(new RowChange(Convert.ToString(await InsertAndGetIdAsync(scope, table.Name, singleKey!, data, transaction), CultureInfo.InvariantCulture)!, null, null));
            else
                await InsertAsync(scope, table.Name, data, transaction);
        }
        if (updates.Count > 0)
        {
            var key = GetPrimaryKeyColumn(table);
            foreach (var (id, body) in updates)
            {
                var data = ExtractColumnValues(body, columns);
                if (data.Count == 0)
                    continue;
                var before = wantUpdated ? await ReadRowAsync(scope, table, SingleKey(key, id), transaction) : null;
                await scope.Db.Query(table.Name).Where(key.Name, id).UpdateAsync(data, transaction);
                if (before is not null && await ReadRowAsync(scope, table, SingleKey(key, id), transaction) is { } after && ChangedColumns(table, before, after) is { Count: > 0 } changed)
                    updatedRows.Add(new RowChange(Convert.ToString(id, CultureInfo.InvariantCulture)!, before, after, changed));
            }
        }
        transaction.Commit();
        // Nobody listens: the change still counts, for the conditions that look again after data changed.
        Publish(table, "created", wantCreated ? createdRows : bodies.Count > 0 ? [new RowChange("", null, null)] : []);
        Publish(table, "updated", wantUpdated ? updatedRows : updates.Count > 0 ? [new RowChange("", null, null)] : []);
        return (bodies.Count, updates.Count);
    }

    /// <summary>
    /// The rows of a table made from a saved query, all replaced at once (Step
    /// 5.8): readers see the old rows or the new ones, never half of them.
    /// </summary>
    internal async Task ReplaceAllAsync(TableDef table, IReadOnlyList<JsonElement> bodies)
    {
        var columns = GetWritableColumns(table);
        await using var scope = await _queryService.UseDataAsync();
        var connection = scope.Db.Connection;
        if (connection.State != ConnectionState.Open)
            connection.Open();
        using var transaction = connection.BeginTransaction();
        await scope.Db.Query(table.Name).DeleteAsync(transaction);
        foreach (var body in bodies)
            await InsertAsync(scope, table.Name, ExtractColumnValues(body, columns), transaction);
        transaction.Commit();
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
        DatabaseProvider provider,
        Func<string, TableDef?>? findTable = null)
    {
        if (string.IsNullOrWhiteSpace(search)) return;

        // Own columns are searched only when the table has text-like ones (a
        // purely numeric table ignores search, as before).
        var hasTextLikeColumns = table.Columns.Any(c =>
            c.Type is ColumnType.Text or ColumnType.Enum or ColumnType.File or ColumnType.Date or ColumnType.DateTime or ColumnType.Uuid);
        var searchableColumns = !hasTextLikeColumns
            ? []
            : table.Columns
                .Where(c => ColumnTypes.HasPhysicalColumn(c.Type) && c.Type is not (ColumnType.Json or ColumnType.Boolean))
                .Select(c => c.Name)
                .ToList();

        // A reference column is searched by the record it points to: an order
        // is found by its customer's name ("Globex"), not only by the id.
        var referenceSearches = findTable is null
            ? []
            : table.Columns
                .Where(c => c.Type == ColumnType.Reference && !string.IsNullOrWhiteSpace(c.References))
                .Select(c => BuildReferenceSearch(c, findTable(c.References!), provider))
                .Where(r => r is not null)
                .Cast<(string Sql, int Bindings)>()
                .ToList();

        if (searchableColumns.Count == 0 && referenceSearches.Count == 0) return;

        var terms = ExtractSearchTerms(search);

        if (terms.Count == 0) return;

        foreach (var term in terms)
        {
            var pattern = $"%{term}%";
            query.Where(q =>
            {
                var first = true;
                foreach (var column in searchableColumns)
                {
                    q = first
                        ? q.WhereRaw(BuildSearchClause(column, provider), pattern)
                        : q.OrWhereRaw(BuildSearchClause(column, provider), pattern);
                    first = false;
                }
                foreach (var (sql, bindings) in referenceSearches)
                {
                    var values = Enumerable.Repeat<object>(pattern, bindings).ToArray();
                    q = first ? q.WhereRaw(sql, values) : q.OrWhereRaw(sql, values);
                    first = false;
                }
                return q;
            });
        }
    }

    /// <summary>
    /// "fk IN (SELECT pk FROM target WHERE text_col LIKE ? OR ...)" over the
    /// target table's text and choice columns; null when it has none.
    /// </summary>
    private static (string Sql, int Bindings)? BuildReferenceSearch(
        ColumnDef column,
        TableDef? target,
        DatabaseProvider provider)
    {
        if (target is null) return null;
        var primaryKey = target.Columns.FirstOrDefault(c => c.PrimaryKey);
        if (primaryKey is null) return null;
        var textColumns = GetRecordLabelColumns(target)
            .Select(name => BuildSearchClause(name, provider))
            .ToList();
        if (textColumns.Count == 0) return null;

        var sql =
            $"{QuoteIdentifier(column.Name)} IN (SELECT {QuoteIdentifier(primaryKey.Name)} " +
            $"FROM {QuoteIdentifier(target.Name)} WHERE {string.Join(" OR ", textColumns)})";
        return (sql, textColumns.Count);
    }

    /// <summary>
    /// The columns that name a record in the UI: the table's recordLabel
    /// (column and optional secondary), otherwise the first text column,
    /// otherwise the first non-key column. Search matches what people see.
    /// </summary>
    internal static IReadOnlyList<string> GetRecordLabelColumns(TableDef table)
    {
        var physical = table.Columns.Where(c => ColumnTypes.HasPhysicalColumn(c.Type)).ToList();
        bool Exists(string? name) =>
            !string.IsNullOrWhiteSpace(name) &&
            physical.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        if (table.AdditionalData is not null &&
            table.AdditionalData.TryGetValue("recordLabel", out var label) &&
            label.ValueKind == JsonValueKind.Object)
        {
            var column = label.TryGetProperty("column", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var secondary = label.TryGetProperty("secondary", out var sc) && sc.ValueKind == JsonValueKind.String ? sc.GetString() : null;
            if (Exists(column))
            {
                var names = new List<string> { column! };
                if (Exists(secondary) && !string.Equals(secondary, column, StringComparison.OrdinalIgnoreCase))
                    names.Add(secondary!);
                return names;
            }
        }

        var firstText = physical.FirstOrDefault(c => c.Type == ColumnType.Text && !c.PrimaryKey);
        if (firstText is not null) return [firstText.Name];
        var firstNonKey = physical.FirstOrDefault(c => !c.PrimaryKey && c.Type is not (ColumnType.Json or ColumnType.Boolean));
        return firstNonKey is null ? [] : [firstNonKey.Name];
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
        DatabaseProvider provider,
        IReadOnlyDictionary<string, ComputedColumn>? computed = null)
    {
        if (sorts.Count == 0)
            return query;

        var columnLookup = table.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var sort in sorts)
        {
            if (computed is not null && computed.TryGetValue(sort.Column, out var formula))
            {
                query = query.OrderByRaw($"{formula.Sql} {(sort.Descending ? "DESC" : "ASC")}");
                continue;
            }

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
        DatabaseProvider? provider = null,
        IReadOnlyDictionary<string, ComputedColumn>? computed = null)
    {
        var columnsByName = table?.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var filter in filters)
        {
            if (computed is not null && computed.TryGetValue(filter.Column, out var formula))
            {
                ApplyComputedFilter(query, filter, formula, provider ?? DatabaseProvider.Sqlite);
                continue;
            }

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

    /// <summary>The list filter operators on a computed column's formula.</summary>
    private static void ApplyComputedFilter(SqlKata.Query query, FilterClause filter, ComputedColumn column, DatabaseProvider provider)
    {
        var value = ComputedColumns.FilterValue(filter.Value, column, provider);
        var compared = ComputedColumns.ComparableSql(column, provider);
        switch (filter.Operator)
        {
            case "eq":
                query.WhereRaw($"{compared} = ?", value);
                break;
            case "neq":
                query.WhereRaw($"NOT ({compared} = ?)", value);
                break;
            case "gt" or "gte" or "lt" or "lte":
                var op = filter.Operator switch { "gt" => ">", "gte" => ">=", "lt" => "<", _ => "<=" };
                query.WhereRaw($"{compared} {op} ?", value);
                break;
            case "like":
                query.WhereRaw($"LOWER(CAST({column.Sql} AS TEXT)) LIKE ?", (filter.Value ?? "").ToLowerInvariant());
                break;
            case "in":
                var values = (filter.Value ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(v => ComputedColumns.FilterValue(v, column, provider))
                    .ToArray();
                if (values.Length == 0)
                    query.WhereRaw("1 = 0");
                else
                    query.WhereRaw($"{compared} IN ({string.Join(", ", values.Select(_ => "?"))})", values!);
                break;
            case "null":
                query.WhereRaw($"{column.Sql} IS NULL");
                break;
            case "notnull":
                query.WhereRaw($"{column.Sql} IS NOT NULL");
                break;
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
            // Postgres compares NUMERIC / DOUBLE PRECISION / BOOLEAN columns only
            // with typed parameters ("operator does not exist: numeric < text").
            ColumnType.Float or ColumnType.Currency
                when decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                => provider == DatabaseProvider.Postgres ? number : (double)number,
            ColumnType.Boolean when value.Trim().ToLowerInvariant() is "true" or "1" => true,
            ColumnType.Boolean when value.Trim().ToLowerInvariant() is "false" or "0" => false,
            ColumnType.Date or ColumnType.DateTime when provider is not null
                => CoerceTemporalForProvider(value, column.Type, provider.Value),
            _ => value
        };
    }

    /// <summary>
    /// SQLite's CURRENT_TIMESTAMP (the default of a junction's created_at)
    /// stores UTC as "yyyy-MM-dd HH:mm:ss" without a zone, which clients read
    /// as local time. Values the app writes use a "T" and are left alone.
    /// </summary>
    private static readonly Regex SqliteCurrentTimestamp =
        new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(\.\d+)?$", RegexOptions.Compiled);

    private IDictionary<string, object?> ToResponseRow(TableDef table, object row)
    {
        var dict = ToDictionary(row);
        NormalizeSqliteTimestamps(table, dict, _dbConfig.Provider);
        _computed.Shape(table, dict);
        return dict;
    }

    /// <summary>Rewrites SQLite CURRENT_TIMESTAMP values of datetime columns as UTC ("...Z").</summary>
    internal static void NormalizeSqliteTimestamps(
        TableDef? table,
        IDictionary<string, object?> row,
        DatabaseProvider provider)
    {
        if (provider != DatabaseProvider.Sqlite || table is null)
            return;

        foreach (var column in table.Columns)
        {
            if (column.Type != ColumnType.DateTime)
                continue;
            if (row.TryGetValue(column.Name, out var value) &&
                value is string text &&
                SqliteCurrentTimestamp.IsMatch(text))
            {
                row[column.Name] = text.Replace(' ', 'T') + "Z";
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
