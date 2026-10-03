using System.Text.Json;
using Dapper;
using Repere.Api.Db;
using Repere.Api.Db.Schema;
using Repere.Api.Exceptions;
using SqlKata.Execution;

namespace Repere.Api.Services;

/// <summary>
/// A single per-field validation error.
/// </summary>
public sealed record FieldError(string Field, string Message);

/// <summary>
/// Validates request bodies for create/update operations against the Model JSON schema.
/// Collects all errors before throwing to give callers a complete picture.
/// </summary>
public sealed class RequestBodyValidator
{
    private readonly QueryService _queryService;
    private readonly SchemaCache _schemaCache;
    private readonly DatabaseConfig _dbConfig;

    public RequestBodyValidator(QueryService queryService, SchemaCache schemaCache, DatabaseConfig dbConfig)
    {
        _queryService = queryService;
        _schemaCache = schemaCache;
        _dbConfig = dbConfig;
    }

    /// <summary>
    /// Validate a request body for a CREATE (POST) operation.
    /// All non-nullable columns without defaults and not PK must be present.
    /// </summary>
    public async Task ValidateCreateAsync(TableDef table, JsonElement body)
    {
        var errors = new List<FieldError>();

        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new ApiValidationException("Request body must be a JSON object.");
        }

        var bodyProps = GetBodyProperties(body);
        var allColumns = table.Columns;
        var writableColumns = allColumns
            .Where(CrudService.IsWritableColumn)
            .ToList();

        // Check for unknown columns
        ValidateUnknownColumns(bodyProps, allColumns, errors);

        // Check for computed columns being set
        ValidateComputedColumns(bodyProps, allColumns, errors);

        // Check required columns (non-nullable, no default, not PK)
        foreach (var col in writableColumns)
        {
            if (!col.Nullable && col.DefaultValue is null && col.InternalDefaultSql is null)
            {
                if (!bodyProps.ContainsKey(col.Name))
                {
                    errors.Add(new FieldError(col.Name, $"Required column '{col.Name}' is missing."));
                }
            }
        }

        // Type validation for provided values
        ValidateTypes(bodyProps, writableColumns, errors);

        // Enum validation
        ValidateEnums(bodyProps, writableColumns, errors);

        if (errors.Count > 0)
        {
            ThrowValidationErrors(errors);
        }

        // Database checks (unique constraints, FK references) — require DB access
        await ValidateUniqueConstraintsAsync(table, bodyProps, writableColumns, existingId: null);
        await ValidateForeignKeyReferencesAsync(bodyProps, writableColumns);
    }

    /// <summary>
    /// Validate a request body for a full UPDATE (PUT) operation.
    /// All required columns must be present (same as create, since PUT replaces all fields).
    /// </summary>
    public async Task ValidateUpdateAsync(TableDef table, object id, JsonElement body)
    {
        // PUT has same validation as create, plus the row must exist
        var errors = new List<FieldError>();

        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new ApiValidationException("Request body must be a JSON object.");
        }

        var bodyProps = GetBodyProperties(body);
        var allColumns = table.Columns;
        var writableColumns = allColumns
            .Where(CrudService.IsWritableColumn)
            .ToList();

        ValidateUnknownColumns(bodyProps, allColumns, errors);
        ValidateComputedColumns(bodyProps, allColumns, errors);

        // For PUT, require all non-nullable columns because the request is a full replacement.
        // This keeps validation aligned with the write path, which clears omitted writable fields.
        foreach (var col in writableColumns)
        {
            if (!col.Nullable)
            {
                if (!bodyProps.ContainsKey(col.Name))
                {
                    errors.Add(new FieldError(col.Name, $"Required column '{col.Name}' is missing."));
                }
            }
        }

        ValidateTypes(bodyProps, writableColumns, errors);
        ValidateEnums(bodyProps, writableColumns, errors);

        if (errors.Count > 0)
        {
            ThrowValidationErrors(errors);
        }

        await ValidateUniqueConstraintsAsync(table, bodyProps, writableColumns, existingId: id);
        await ValidateForeignKeyReferencesAsync(bodyProps, writableColumns);
    }

    /// <summary>
    /// Validate a request body for a PATCH operation.
    /// Only validates provided columns (partial update).
    /// </summary>
    public async Task ValidatePatchAsync(TableDef table, object id, JsonElement body)
    {
        var errors = new List<FieldError>();

        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new ApiValidationException("Request body must be a JSON object.");
        }

        var bodyProps = GetBodyProperties(body);
        var allColumns = table.Columns;
        var writableColumns = allColumns
            .Where(CrudService.IsWritableColumn)
            .ToList();

        ValidateUnknownColumns(bodyProps, allColumns, errors);
        ValidateComputedColumns(bodyProps, allColumns, errors);

        // No required-column check for PATCH — only validate provided columns
        ValidateTypes(bodyProps, writableColumns, errors);
        ValidateEnums(bodyProps, writableColumns, errors);

        if (errors.Count > 0)
        {
            ThrowValidationErrors(errors);
        }

        await ValidateUniqueConstraintsAsync(table, bodyProps, writableColumns, existingId: id);
        await ValidateForeignKeyReferencesAsync(bodyProps, writableColumns);
    }

    private static Dictionary<string, JsonElement> GetBodyProperties(JsonElement body)
    {
        var props = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in body.EnumerateObject())
        {
            props[prop.Name] = prop.Value;
        }
        return props;
    }

    private static void ValidateUnknownColumns(
        Dictionary<string, JsonElement> bodyProps,
        List<ColumnDef> allColumns,
        List<FieldError> errors)
    {
        var knownNames = new HashSet<string>(
            allColumns.Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var key in bodyProps.Keys)
        {
            if (!knownNames.Contains(key))
            {
                errors.Add(new FieldError(key, $"Unknown column '{key}'."));
            }
        }
    }

    private static void ValidateComputedColumns(
        Dictionary<string, JsonElement> bodyProps,
        List<ColumnDef> allColumns,
        List<FieldError> errors)
    {
        foreach (var col in allColumns.Where(c => c.Type == ColumnType.Computed))
        {
            if (bodyProps.ContainsKey(col.Name))
            {
                errors.Add(new FieldError(col.Name, $"Column '{col.Name}' is computed and read-only."));
            }
        }
    }

    private static void ValidateTypes(
        Dictionary<string, JsonElement> bodyProps,
        List<ColumnDef> writableColumns,
        List<FieldError> errors)
    {
        foreach (var col in writableColumns)
        {
            if (!bodyProps.TryGetValue(col.Name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Null)
            {
                if (!col.Nullable)
                {
                    errors.Add(new FieldError(col.Name, $"Column '{col.Name}' cannot be null."));
                }
                continue;
            }

            switch (col.Type)
            {
                case ColumnType.Integer:
                    if (value.ValueKind != JsonValueKind.Number
                        && !(value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out _)))
                    {
                        errors.Add(new FieldError(col.Name, $"Column '{col.Name}' expects integer, got {DescribeJsonKind(value)}."));
                    }
                    break;

                case ColumnType.Reference:
                    if (value.ValueKind != JsonValueKind.Number &&
                        !(value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())))
                    {
                        errors.Add(new FieldError(col.Name, $"Column '{col.Name}' expects integer or UUID reference, got {DescribeJsonKind(value)}."));
                    }
                    break;

                case ColumnType.Uuid:
                    if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out _))
                    {
                        errors.Add(new FieldError(col.Name, $"Column '{col.Name}' expects UUID string, got {DescribeJsonKind(value)}."));
                    }
                    break;

                case ColumnType.Float or ColumnType.Currency:
                    if (value.ValueKind != JsonValueKind.Number
                        && !(value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out _)))
                    {
                        errors.Add(new FieldError(col.Name, $"Column '{col.Name}' expects number, got {DescribeJsonKind(value)}."));
                    }
                    break;

                case ColumnType.Boolean:
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                        && !(value.ValueKind == JsonValueKind.Number)
                        && !(value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out _)))
                    {
                        errors.Add(new FieldError(col.Name, $"Column '{col.Name}' expects boolean, got {DescribeJsonKind(value)}."));
                    }
                    break;

                case ColumnType.Text or ColumnType.Enum or ColumnType.File:
                    // Accept any value — strings, numbers, etc. are all coerced to string
                    break;

                case ColumnType.Date or ColumnType.DateTime:
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        errors.Add(new FieldError(col.Name, $"Column '{col.Name}' expects date string, got {DescribeJsonKind(value)}."));
                    }
                    break;

                case ColumnType.Json:
                    // Any valid JSON is accepted
                    break;
            }
        }
    }

    private static void ValidateEnums(
        Dictionary<string, JsonElement> bodyProps,
        List<ColumnDef> writableColumns,
        List<FieldError> errors)
    {
        foreach (var col in writableColumns.Where(c => c.Type == ColumnType.Enum && c.Values.Count > 0))
        {
            if (!bodyProps.TryGetValue(col.Name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Null)
                continue;

            var stringValue = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.GetRawText();

            if (stringValue is not null && !col.Values.Contains(stringValue, StringComparer.Ordinal))
            {
                errors.Add(new FieldError(col.Name,
                    $"Column '{col.Name}' value '{stringValue}' is not in the allowed set: [{string.Join(", ", col.Values)}]."));
            }
        }
    }

    private async Task ValidateUniqueConstraintsAsync(
        TableDef table,
        Dictionary<string, JsonElement> bodyProps,
        List<ColumnDef> writableColumns,
        object? existingId)
    {
        var uniqueColumns = writableColumns.Where(c => c.Unique).ToList();
        if (uniqueColumns.Count == 0)
            return;

        await using var scope = await _queryService.UseDataAsync();

        foreach (var col in uniqueColumns)
        {
            if (!bodyProps.TryGetValue(col.Name, out var value) || value.ValueKind == JsonValueKind.Null)
                continue;

            // Provider-aware so uuid-backed columns compare as uuid on Postgres
            // instead of failing with "operator does not exist: uuid = text".
            var coerced = col.Type switch
            {
                ColumnType.Reference => CoerceReferenceValue(value, col),
                ColumnType.Uuid => CoerceUuidValue(value),
                ColumnType.Date or ColumnType.DateTime when value.ValueKind == JsonValueKind.String
                    => CrudService.CoerceTemporalForProvider(value.GetString(), col.Type, _dbConfig.Provider),
                _ => CrudService.CoerceValueStatic(value, col.Type)
            };
            var query = scope.Db.Query(table.Name)
                .Where(col.Name, coerced)
                .SelectRaw("1");

            if (existingId is not null)
            {
                query = query.Where("id", "!=", existingId);
            }

            object? exists = await query.FirstOrDefaultAsync();
            if (exists is not null)
            {
                throw new ApiConflictException(
                    $"Another row already has {col.Name} '{coerced}'.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["column"] = col.Name,
                        ["value"] = coerced
                    });
            }
        }
    }

    private async Task ValidateForeignKeyReferencesAsync(
        Dictionary<string, JsonElement> bodyProps,
        List<ColumnDef> writableColumns)
    {
        var refColumns = writableColumns
            .Where(c => c.Type == ColumnType.Reference && !string.IsNullOrWhiteSpace(c.References))
            .ToList();

        if (refColumns.Count == 0)
            return;

        await using var scope = await _queryService.UseDataAsync();

        foreach (var col in refColumns)
        {
            if (!bodyProps.TryGetValue(col.Name, out var value) || value.ValueKind == JsonValueKind.Null)
                continue;

            var refId = CoerceReferenceValue(value, col);

            object? exists = await scope.Db.Query(col.References!)
                .Where("id", refId)
                .SelectRaw("1")
                .FirstOrDefaultAsync();

            if (exists is null)
            {
                throw new ApiUnprocessableEntityException(
                    $"Referenced row with id {refId} does not exist in table '{col.References}'.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["column"] = col.Name,
                        ["referencedTable"] = col.References,
                        ["referencedId"] = refId
                    });
            }
        }
    }

    private static string DescribeJsonKind(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => $"string '{element.GetString()}'",
            JsonValueKind.Number => $"number {element.GetRawText()}",
            JsonValueKind.True or JsonValueKind.False => $"boolean {element.GetRawText()}",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            _ => element.ValueKind.ToString()
        };
    }

    private object? CoerceUuidValue(JsonElement value)
    {
        var raw = value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
        if (raw is not null && Guid.TryParse(raw, out var guid))
            return _dbConfig.Provider == DatabaseProvider.Postgres ? guid : guid.ToString();

        return CrudService.CoerceValueStatic(value, ColumnType.Uuid);
    }

    private object? CoerceReferenceValue(JsonElement value, ColumnDef column)
    {
        var referencedPrimaryKey = !string.IsNullOrWhiteSpace(column.References)
            ? _schemaCache.GetTable(column.References)?.Columns.FirstOrDefault(c => c.PrimaryKey)
            : null;

        if (referencedPrimaryKey?.Type == ColumnType.Uuid)
        {
            var raw = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.GetRawText();
            if (raw is not null && Guid.TryParse(raw, out var guid))
                return _dbConfig.Provider == DatabaseProvider.Postgres ? guid : guid.ToString();
        }

        return CrudService.CoerceValueStatic(value, ColumnType.Reference);
    }

    private static void ThrowValidationErrors(List<FieldError> errors)
    {
        var message = errors.Count == 1
            ? errors[0].Message
            : $"Validation failed with {errors.Count} errors.";

        throw new ApiValidationException(message,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["errors"] = errors.Select(e => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["field"] = e.Field,
                    ["message"] = e.Message
                }).ToList()
            });
    }
}
