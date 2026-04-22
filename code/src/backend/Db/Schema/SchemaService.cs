using System.Data.Common;
using Repere.Api.Db;
using Repere.Api.Db.Migrations;

namespace Repere.Api.Db.Schema;

public sealed record SchemaCastExample
{
    public object? SourceValue { get; init; }
    public object? CastValue { get; init; }
    public required bool IsValid { get; init; }
    public string? Error { get; init; }
}

public sealed record SchemaChangeCastPreview
{
    public required SchemaChangeType ChangeType { get; init; }
    public required string TableName { get; init; }
    public string? ColumnName { get; init; }
    public required IReadOnlyList<SchemaCastExample> Examples { get; init; }
}

public sealed record SchemaPreviewResult
{
    public required bool RequiresConfirmation { get; init; }
    public required ModelJson CurrentModel { get; init; }
    public required ModelJson TargetModel { get; init; }
    public required IReadOnlyList<SchemaValidationIssue> Errors { get; init; }
    public required IReadOnlyList<SchemaValidationIssue> Warnings { get; init; }
    public required IReadOnlyList<SchemaChange> Changes { get; init; }
    public required IReadOnlyList<SchemaChangeCastPreview> CastPreviews { get; init; }
    public MigrationPlan? Plan { get; init; }
}

public sealed record SchemaApplyResult
{
    public required bool Applied { get; init; }
    public required bool RequiresConfirmation { get; init; }
    public required ModelJson CurrentModel { get; init; }
    public required ModelJson TargetModel { get; init; }
    public required IReadOnlyList<SchemaValidationIssue> Errors { get; init; }
    public required IReadOnlyList<SchemaValidationIssue> Warnings { get; init; }
    public required IReadOnlyList<SchemaChange> Changes { get; init; }
    public MigrationPlan? Plan { get; init; }
    public MigrationApplyResult? Migration { get; init; }
}

public class SchemaService
{
    private readonly ModelJsonStore _modelJsonStore;
    private readonly SchemaValidator _validator;
    private readonly SchemaDiffEngine _schemaDiffEngine;
    private readonly MigrationGenerator _migrationGenerator;
    private readonly MigrationRunner _migrationRunner;
    private readonly IDataPool _dataPool;

    public SchemaService(
        ModelJsonStore modelJsonStore,
        SchemaValidator validator,
        SchemaDiffEngine schemaDiffEngine,
        MigrationGenerator migrationGenerator,
        MigrationRunner migrationRunner,
        IDataPool dataPool)
    {
        _modelJsonStore = modelJsonStore;
        _validator = validator;
        _schemaDiffEngine = schemaDiffEngine;
        _migrationGenerator = migrationGenerator;
        _migrationRunner = migrationRunner;
        _dataPool = dataPool;
    }

    public async Task<SchemaPreviewResult> PreviewSchemaAsync(ModelJson requestedModel)
    {
        var currentModel = await _modelJsonStore.GetCurrentAsync() ?? new ModelJson { Version = 0 };
        var requestedModelWithRenameReferences = CascadeTableRenameReferences(currentModel, requestedModel);
        var validation = _validator.Validate(requestedModelWithRenameReferences);

        if (!validation.IsValid)
        {
            return new SchemaPreviewResult
            {
                RequiresConfirmation = false,
                CurrentModel = currentModel,
                TargetModel = validation.NormalizedModel,
                Errors = validation.Errors,
                Warnings = validation.Warnings,
                Changes = [],
                CastPreviews = [],
                Plan = null
            };
        }

        var changes = _schemaDiffEngine.Diff(currentModel, validation.NormalizedModel);
        var unsupportedPrimaryKeyRenames = BuildPrimaryKeyRenameErrors(
            currentModel,
            validation.NormalizedModel,
            changes);
        if (unsupportedPrimaryKeyRenames.Count > 0)
        {
            return new SchemaPreviewResult
            {
                RequiresConfirmation = false,
                CurrentModel = currentModel,
                TargetModel = validation.NormalizedModel,
                Errors = [..validation.Errors, ..unsupportedPrimaryKeyRenames],
                Warnings = validation.Warnings,
                Changes = changes,
                CastPreviews = [],
                Plan = null
            };
        }

        if (changes.Count == 0)
        {
            return new SchemaPreviewResult
            {
                RequiresConfirmation = false,
                CurrentModel = currentModel,
                TargetModel = validation.NormalizedModel,
                Errors = validation.Errors,
                Warnings = validation.Warnings,
                Changes = changes,
                CastPreviews = [],
                Plan = CreateNoOpPlan(changes)
            };
        }

        try
        {
            var plan = _migrationGenerator.Generate(currentModel, validation.NormalizedModel, changes);
            var castPreviews = await BuildLossyCastPreviewsAsync(currentModel, validation.NormalizedModel, changes);

            return new SchemaPreviewResult
            {
                RequiresConfirmation = plan.RequiresConfirmation,
                CurrentModel = currentModel,
                TargetModel = validation.NormalizedModel,
                Errors = validation.Errors,
                Warnings = validation.Warnings,
                Changes = changes,
                CastPreviews = castPreviews,
                Plan = plan
            };
        }
        catch (InvalidOperationException ex)
        {
            var planningErrors = BuildMigrationPlanningErrors(validation.NormalizedModel, changes, ex);

            return new SchemaPreviewResult
            {
                RequiresConfirmation = false,
                CurrentModel = currentModel,
                TargetModel = validation.NormalizedModel,
                Errors = [..validation.Errors, ..planningErrors],
                Warnings = validation.Warnings,
                Changes = changes,
                CastPreviews = [],
                Plan = null
            };
        }
    }

    public async Task<SchemaApplyResult> ApplySchemaAsync(ModelJson requestedModel, bool confirmDestructive = false)
    {
        var preview = await PreviewSchemaAsync(requestedModel);

        if (preview.Errors.Count > 0)
        {
            return new SchemaApplyResult
            {
                Applied = false,
                RequiresConfirmation = false,
                CurrentModel = preview.CurrentModel,
                TargetModel = preview.TargetModel,
                Errors = preview.Errors,
                Warnings = preview.Warnings,
                Changes = preview.Changes,
                Plan = preview.Plan
            };
        }

        if (preview.Changes.Count == 0)
        {
            return new SchemaApplyResult
            {
                Applied = false,
                RequiresConfirmation = false,
                CurrentModel = preview.CurrentModel,
                TargetModel = preview.TargetModel,
                Errors = preview.Errors,
                Warnings = preview.Warnings,
                Changes = preview.Changes,
                Plan = preview.Plan
            };
        }

        if (preview.RequiresConfirmation && !confirmDestructive)
        {
            return new SchemaApplyResult
            {
                Applied = false,
                RequiresConfirmation = true,
                CurrentModel = preview.CurrentModel,
                TargetModel = preview.TargetModel,
                Errors = preview.Errors,
                Warnings = preview.Warnings,
                Changes = preview.Changes,
                Plan = preview.Plan
            };
        }

        var plan = preview.Plan
            ?? throw new InvalidOperationException("Schema preview did not produce a migration plan for an applicable schema change.");

        var persistedTargetModel = RemoveRenameHints(preview.TargetModel);
        var migration = await _migrationRunner.ApplyAsync(preview.CurrentModel, persistedTargetModel, plan);

        return new SchemaApplyResult
        {
            Applied = true,
            RequiresConfirmation = false,
            CurrentModel = preview.CurrentModel,
            TargetModel = persistedTargetModel with { Version = migration.VersionTo },
            Errors = preview.Errors,
            Warnings = preview.Warnings,
            Changes = preview.Changes,
            Plan = plan,
            Migration = migration
        };
    }

    public Task<MigrationApplyResult?> RollbackLastAsync() => _migrationRunner.RollbackLastAsync();

    public Task<IReadOnlyList<MigrationApplyResult>> RollbackAsync(int steps) => _migrationRunner.RollbackAsync(steps);

    public Task<IReadOnlyList<MigrationHistoryEntry>> GetHistoryAsync() => _migrationRunner.GetHistoryAsync();

    private static MigrationPlan CreateNoOpPlan(IReadOnlyList<SchemaChange> changes)
    {
        return new MigrationPlan
        {
            SqlUp = [],
            SqlDown = [],
            IsDestructive = false,
            Changes = changes
        };
    }

    private static IReadOnlyList<SchemaValidationIssue> BuildPrimaryKeyRenameErrors(
        ModelJson currentModel,
        ModelJson targetModel,
        IReadOnlyList<SchemaChange> changes)
    {
        return changes
            .Where(change => change.ChangeType == SchemaChangeType.RenameColumn)
            .Select(change => BuildPrimaryKeyRenameError(currentModel, targetModel, change))
            .Where(issue => issue is not null)
            .Cast<SchemaValidationIssue>()
            .ToList();
    }

    private static SchemaValidationIssue? BuildPrimaryKeyRenameError(
        ModelJson currentModel,
        ModelJson targetModel,
        SchemaChange change)
    {
        var oldTable = TryFindTable(currentModel, change.PreviousTableName ?? change.TableName);
        var newTable = TryFindTable(targetModel, change.TableName);
        var oldColumn = oldTable is null ? null : TryFindColumn(oldTable, change.PreviousColumnName ?? change.ColumnName);
        var newColumn = newTable is null ? null : TryFindColumn(newTable, change.ColumnName);

        if (oldColumn?.PrimaryKey != true && newColumn?.PrimaryKey != true)
            return null;

        var tableName = newTable?.Name ?? change.TableName;
        return new SchemaValidationIssue(
            "primary_key_rename_unsupported",
            $"Primary key column rename is not supported for table '{tableName}'. Keep the primary key column named 'id'.",
            FindColumnNamePath(targetModel, change.TableName, change.ColumnName));
    }

    private static ModelJson CascadeTableRenameReferences(ModelJson currentModel, ModelJson requestedModel)
    {
        var tableRenames = BuildTableRenameMap(currentModel, requestedModel);
        if (tableRenames.Count == 0)
            return requestedModel;

        var changed = false;
        var tables = requestedModel.Tables
            .Select(table =>
            {
                var columnsChanged = false;
                var columns = table.Columns
                    .Select(column =>
                    {
                        var references = RewriteOptionalTableReference(column.References, tableRenames);
                        if (string.Equals(references, column.References, StringComparison.Ordinal))
                            return column;

                        columnsChanged = true;
                        return column with { References = references };
                    })
                    .ToList();

                if (!columnsChanged)
                    return table;

                changed = true;
                return table with { Columns = columns };
            })
            .ToList();

        var relations = requestedModel.Relations
            .Select(relation =>
            {
                var from = RewriteRequiredTableReference(relation.From, tableRenames);
                var to = RewriteRequiredTableReference(relation.To, tableRenames);
                if (
                    string.Equals(from, relation.From, StringComparison.Ordinal) &&
                    string.Equals(to, relation.To, StringComparison.Ordinal))
                {
                    return relation;
                }

                changed = true;
                return relation with { From = from, To = to };
            })
            .ToList();

        return changed
            ? requestedModel with { Tables = tables, Relations = relations }
            : requestedModel;
    }

    private static Dictionary<string, string> BuildTableRenameMap(ModelJson currentModel, ModelJson requestedModel)
    {
        var currentTableNames = currentModel.Tables
            .Where(table => !string.IsNullOrWhiteSpace(table.Name))
            .Select(table => table.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedTableNames = requestedModel.Tables
            .Where(table => !string.IsNullOrWhiteSpace(table.Name))
            .Select(table => table.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tableRenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in requestedModel.Tables)
        {
            if (
                string.IsNullOrWhiteSpace(table.Name) ||
                string.IsNullOrWhiteSpace(table.RenamedFrom) ||
                string.Equals(table.Name, table.RenamedFrom, StringComparison.OrdinalIgnoreCase) ||
                requestedTableNames.Contains(table.RenamedFrom) ||
                !currentTableNames.Contains(table.RenamedFrom))
            {
                continue;
            }

            tableRenames[table.RenamedFrom] = table.Name;
        }

        return tableRenames;
    }

    private static string RewriteRequiredTableReference(string value, IReadOnlyDictionary<string, string> tableRenames)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return tableRenames.TryGetValue(value, out var renamedValue)
            ? renamedValue
            : value;
    }

    private static string? RewriteOptionalTableReference(string? value, IReadOnlyDictionary<string, string> tableRenames)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return tableRenames.TryGetValue(value, out var renamedValue)
            ? renamedValue
            : value;
    }

    private static ModelJson RemoveRenameHints(ModelJson model)
    {
        return model with
        {
            Tables = model.Tables
                .Select(table => table with
                {
                    RenamedFrom = null,
                    Columns = table.Columns
                        .Select(column => column with { RenamedFrom = null })
                        .ToList(),
                    Indexes = table.Indexes
                        .Select(index => index with { RenamedFrom = null })
                        .ToList()
                })
                .ToList()
        };
    }

    private static IReadOnlyList<SchemaValidationIssue> BuildMigrationPlanningErrors(
        ModelJson normalizedModel,
        IReadOnlyList<SchemaChange> changes,
        InvalidOperationException exception)
    {
        var impossibleTypeChanges = changes
            .Where(change => change.ChangeType == SchemaChangeType.AlterColumnType && change.CoercionKind == CoercionKind.Impossible)
            .ToList();

        if (impossibleTypeChanges.Count == 0)
        {
            return
            [
                new SchemaValidationIssue(
                    "migration_plan_generation_failed",
                    exception.Message,
                    "schema")
            ];
        }

        return impossibleTypeChanges
            .Select(change => new SchemaValidationIssue(
                "impossible_type_conversion",
                $"Column '{change.TableName}.{change.ColumnName}' cannot be converted from '{change.OldValue}' to '{change.NewValue}'.",
                FindColumnTypePath(normalizedModel, change.TableName, change.ColumnName)))
            .ToList();
    }

    private async Task<IReadOnlyList<SchemaChangeCastPreview>> BuildLossyCastPreviewsAsync(
        ModelJson currentModel,
        ModelJson targetModel,
        IReadOnlyList<SchemaChange> changes)
    {
        var lossyTypeChanges = changes
            .Where(change =>
                change.ChangeType == SchemaChangeType.AlterColumnType
                && change.CoercionKind == CoercionKind.Lossy
                && !string.IsNullOrWhiteSpace(change.ColumnName))
            .ToList();

        if (lossyTypeChanges.Count == 0)
        {
            return [];
        }

        using var connection = await _dataPool.GetConnectionAsync();
        var dbConnection = connection as DbConnection
            ?? throw new InvalidOperationException("Schema preview cast sampling requires a DbConnection.");

        var previews = new List<SchemaChangeCastPreview>();

        foreach (var change in lossyTypeChanges)
        {
            var oldTable = TryFindTable(currentModel, change.PreviousTableName ?? change.TableName);
            var newTable = TryFindTable(targetModel, change.TableName);
            var oldColumn = oldTable is null ? null : TryFindColumn(oldTable, change.PreviousColumnName ?? change.ColumnName);
            var newColumn = newTable is null ? null : TryFindColumn(newTable, change.ColumnName);

            if (oldTable is null || newTable is null || oldColumn is null || newColumn is null)
            {
                continue;
            }

            IReadOnlyList<object?> sampleValues;
            try
            {
                sampleValues = await LoadSampleValuesAsync(dbConnection, oldTable, oldColumn.Name, 5);
            }
            catch
            {
                continue;
            }

            if (sampleValues.Count == 0)
            {
                continue;
            }

            var examples = new List<SchemaCastExample>(sampleValues.Count);
            foreach (var sampleValue in sampleValues)
            {
                examples.Add(await TryCastSampleValueAsync(dbConnection, sampleValue, newColumn.Type));
            }

            previews.Add(new SchemaChangeCastPreview
            {
                ChangeType = change.ChangeType,
                TableName = change.TableName,
                ColumnName = change.ColumnName,
                Examples = examples
            });
        }

        return previews;
    }

    private async Task<IReadOnlyList<object?>> LoadSampleValuesAsync(
        DbConnection connection,
        TableDef table,
        string columnName,
        int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Quote(columnName)}
            FROM {Quote(table.Name)}
            {BuildStableOrderByClause(table)}
            LIMIT {limit};
            """;

        var values = new List<object?>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.IsDBNull(0) ? null : reader.GetValue(0));
        }

        return values;
    }

    private async Task<SchemaCastExample> TryCastSampleValueAsync(
        DbConnection connection,
        object? sourceValue,
        ColumnType targetType)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT CAST(@value AS {ColumnTypes.ToDdl(targetType, _dataPool.Provider)});";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "@value";
            parameter.Value = sourceValue ?? DBNull.Value;
            command.Parameters.Add(parameter);

            var castValue = await command.ExecuteScalarAsync();
            return new SchemaCastExample
            {
                SourceValue = sourceValue,
                CastValue = castValue is DBNull ? null : castValue,
                IsValid = true,
                Error = null
            };
        }
        catch (Exception ex)
        {
            return new SchemaCastExample
            {
                SourceValue = sourceValue,
                CastValue = null,
                IsValid = false,
                Error = ex.Message
            };
        }
    }

    private static string BuildStableOrderByClause(TableDef table)
    {
        var primaryKeys = table.Columns
            .Where(column => column.PrimaryKey && ColumnTypes.HasPhysicalColumn(column.Type))
            .Select(column => Quote(column.Name))
            .ToList();

        if (primaryKeys.Count > 0)
        {
            return $"ORDER BY {string.Join(", ", primaryKeys)}";
        }

        var fallbackColumn = table.Columns.FirstOrDefault(column => ColumnTypes.HasPhysicalColumn(column.Type));
        return fallbackColumn is null
            ? string.Empty
            : $"ORDER BY {Quote(fallbackColumn.Name)}";
    }

    private static TableDef? TryFindTable(ModelJson model, string? tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return null;
        }

        return model.Tables.FirstOrDefault(table =>
            string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase));
    }

    private static ColumnDef? TryFindColumn(TableDef table, string? columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return null;
        }

        return table.Columns.FirstOrDefault(column =>
            string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase));
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string FindColumnNamePath(ModelJson model, string tableName, string? columnName)
    {
        return FindColumnPropertyPath(model, tableName, columnName, "name");
    }

    private static string FindColumnTypePath(ModelJson model, string tableName, string? columnName)
    {
        return FindColumnPropertyPath(model, tableName, columnName, "type");
    }

    private static string FindColumnPropertyPath(ModelJson model, string tableName, string? columnName, string propertyName)
    {
        for (var tableIndex = 0; tableIndex < model.Tables.Count; tableIndex++)
        {
            var table = model.Tables[tableIndex];
            if (!string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrWhiteSpace(columnName))
                return $"tables[{tableIndex}]";

            for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
            {
                var column = table.Columns[columnIndex];
                if (string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase))
                    return $"tables[{tableIndex}].columns[{columnIndex}].{propertyName}";
            }

            return $"tables[{tableIndex}]";
        }

        return "tables";
    }
}
