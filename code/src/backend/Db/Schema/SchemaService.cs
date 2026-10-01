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

public sealed record SchemaChangeImpact
{
    public required SchemaChangeType ChangeType { get; init; }
    public required string TableName { get; init; }
    public string? RelationName { get; init; }
    public required string Kind { get; init; }
    public required string ObjectType { get; init; }
    public required string ObjectName { get; init; }
    public long? RowCount { get; init; }
    public IReadOnlyList<IReadOnlyDictionary<string, object?>>? SampleRows { get; init; }
    public IReadOnlyList<object?>? SampleValues { get; init; }
    public required string Message { get; init; }
}

public sealed record SchemaPreviewResult
{
    public required bool RequiresConfirmation { get; init; }
    public bool MetadataOnly { get; init; }
    public required ModelJson CurrentModel { get; init; }
    public required ModelJson TargetModel { get; init; }
    public required IReadOnlyList<SchemaValidationIssue> Errors { get; init; }
    public required IReadOnlyList<SchemaValidationIssue> Warnings { get; init; }
    public required IReadOnlyList<SchemaChange> Changes { get; init; }
    public required IReadOnlyList<SchemaChangeCastPreview> CastPreviews { get; init; }
    public required IReadOnlyList<SchemaChangeImpact> Impacts { get; init; }
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
    public bool MetadataOnly { get; init; }
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
                Impacts = [],
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
                Impacts = [],
                Plan = null
            };
        }

        if (changes.Count == 0)
        {
            var canonicalTarget = RemoveRenameHints(validation.NormalizedModel);
            var metadataOnly = currentModel.Version > 0
                && !ModelJsonSerializer.AreCanonicallyEqual(currentModel, canonicalTarget);

            return new SchemaPreviewResult
            {
                RequiresConfirmation = false,
                MetadataOnly = metadataOnly,
                CurrentModel = currentModel,
                TargetModel = validation.NormalizedModel,
                Errors = validation.Errors,
                Warnings = validation.Warnings,
                Changes = changes,
                CastPreviews = [],
                Impacts = [],
                Plan = CreateNoOpPlan(changes)
            };
        }

        try
        {
            var plan = _migrationGenerator.Generate(currentModel, validation.NormalizedModel, changes);
            var castPreviews = await BuildLossyCastPreviewsAsync(currentModel, validation.NormalizedModel, changes);
            var impacts = await BuildChangeImpactsAsync(currentModel, changes);

            return new SchemaPreviewResult
            {
                RequiresConfirmation = plan.RequiresConfirmation,
                CurrentModel = currentModel,
                TargetModel = validation.NormalizedModel,
                Errors = validation.Errors,
                Warnings = validation.Warnings,
                Changes = changes,
                CastPreviews = castPreviews,
                Impacts = impacts,
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
                Impacts = [],
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
            if (preview.MetadataOnly)
            {
                var canonicalTarget = RemoveRenameHints(preview.TargetModel);
                var savedMetadata = await _modelJsonStore.UpdateCurrentMetadataAsync(canonicalTarget);
                if (savedMetadata is not null)
                {
                    return new SchemaApplyResult
                    {
                        Applied = true,
                        RequiresConfirmation = false,
                        CurrentModel = preview.CurrentModel,
                        TargetModel = savedMetadata,
                        Errors = preview.Errors,
                        Warnings = preview.Warnings,
                        Changes = preview.Changes,
                        MetadataOnly = true,
                        Plan = preview.Plan
                    };
                }
            }

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

    private async Task<IReadOnlyList<SchemaChangeImpact>> BuildChangeImpactsAsync(
        ModelJson currentModel,
        IReadOnlyList<SchemaChange> changes)
    {
        var impactChanges = changes
            .Where(change => change.ChangeType is SchemaChangeType.DropColumn or SchemaChangeType.DropTable or SchemaChangeType.DropRelation or SchemaChangeType.AlterRelation)
            .ToList();

        if (impactChanges.Count == 0)
        {
            return [];
        }

        var impacts = new List<SchemaChangeImpact>();
        DbConnection? dbConnection = null;

        foreach (var change in impactChanges)
        {
            if (change.ChangeType == SchemaChangeType.DropColumn
                && !string.IsNullOrWhiteSpace(change.ColumnName))
            {
                dbConnection ??= await GetPreviewDbConnectionAsync();
                var impact = await BuildDropColumnImpactAsync(dbConnection, currentModel, change);
                if (impact is not null)
                {
                    impacts.Add(impact);
                }

                continue;
            }

            if (change.ChangeType == SchemaChangeType.DropTable)
            {
                dbConnection ??= await GetPreviewDbConnectionAsync();
                var impact = await BuildDropTableImpactAsync(dbConnection, currentModel, change);
                if (impact is not null)
                {
                    impacts.Add(impact);
                }

                continue;
            }

            if (change.ChangeType == SchemaChangeType.DropRelation
                && change.OldValue is RelationDef droppedRelation
                && droppedRelation.Type == RelationType.ManyToMany)
            {
                dbConnection ??= await GetPreviewDbConnectionAsync();
                impacts.Add(await BuildManyToManyJunctionImpactAsync(
                    dbConnection,
                    change,
                    droppedRelation,
                    "many_to_many_junction_drop",
                    $"Applying this change drops the junction rows stored for relation \"{droppedRelation.Name}\"."));
                continue;
            }

            if (change.ChangeType != SchemaChangeType.AlterRelation
                || change.OldValue is not RelationDef oldRelation
                || change.NewValue is not RelationDef newRelation)
            {
                continue;
            }

            if (oldRelation.Type == RelationType.OneToMany
                && newRelation.Type == RelationType.OneToMany
                && RelationEndpointOrColumnChanged(oldRelation, newRelation))
            {
                impacts.Add(new SchemaChangeImpact
                {
                    ChangeType = change.ChangeType,
                    TableName = change.TableName,
                    RelationName = change.RelationName,
                    Kind = "one_to_many_relation_retarget",
                    ObjectType = "relation",
                    ObjectName = newRelation.Name,
                    Message = $"Relation \"{newRelation.Name}\" will point from \"{newRelation.From}\" to \"{newRelation.To}\" using column \"{newRelation.Column}\". Existing rows are preserved unless another preview change drops a column."
                });
                continue;
            }

            if (change.IsDestructive && oldRelation.Type == RelationType.ManyToMany)
            {
                dbConnection ??= await GetPreviewDbConnectionAsync();
                impacts.Add(await BuildManyToManyJunctionImpactAsync(
                    dbConnection,
                    change,
                    oldRelation,
                    "many_to_many_junction_rebuild",
                    $"Applying this change rebuilds the junction table for relation \"{oldRelation.Name}\" and deletes its stored junction rows."));
            }
        }

        if (dbConnection is not null)
        {
            await dbConnection.DisposeAsync();
        }

        return impacts;
    }

    private async Task<DbConnection> GetPreviewDbConnectionAsync()
    {
        var connection = await _dataPool.GetConnectionAsync();
        return connection as DbConnection
            ?? throw new InvalidOperationException("Schema preview relation impact sampling requires a DbConnection.");
    }

    private async Task<SchemaChangeImpact> BuildManyToManyJunctionImpactAsync(
        DbConnection connection,
        SchemaChange change,
        RelationDef relation,
        string kind,
        string message)
    {
        var junction = new RelationResolver().BuildJunctionTable(relation);
        long? rowCount = null;
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? sampleRows = null;

        try
        {
            rowCount = await LoadTableRowCountAsync(connection, junction.Name);
            sampleRows = await LoadSampleRowsAsync(connection, junction, 5);
        }
        catch
        {
            // Preview should still describe the destructive relation change if
            // sampling fails because the junction table is unavailable.
        }

        return new SchemaChangeImpact
        {
            ChangeType = change.ChangeType,
            TableName = change.TableName,
            RelationName = change.RelationName,
            Kind = kind,
            ObjectType = "table",
            ObjectName = junction.Name,
            RowCount = rowCount,
            SampleRows = sampleRows,
            Message = message
        };
    }

    private async Task<SchemaChangeImpact?> BuildDropColumnImpactAsync(
        DbConnection connection,
        ModelJson currentModel,
        SchemaChange change)
    {
        var table = TryFindTable(currentModel, change.PreviousTableName ?? change.TableName);
        var column = table is null ? null : TryFindColumn(table, change.PreviousColumnName ?? change.ColumnName);
        if (table is null || column is null || string.IsNullOrWhiteSpace(change.ColumnName))
        {
            return null;
        }

        long? rowCount = null;
        IReadOnlyList<object?>? sampleValues = null;

        try
        {
            rowCount = await LoadTableRowCountAsync(connection, table.Name);
            sampleValues = await LoadSampleValuesAsync(connection, table, column.Name, 5);
        }
        catch
        {
            // Keep the destructive drop-column warning even if samples cannot
            // be loaded from the current database.
        }

        var qualifiedName = $"{table.Name}.{column.Name}";
        return new SchemaChangeImpact
        {
            ChangeType = change.ChangeType,
            TableName = change.TableName,
            RelationName = change.RelationName,
            Kind = "column_drop",
            ObjectType = "column",
            ObjectName = qualifiedName,
            RowCount = rowCount,
            SampleValues = sampleValues,
            Message = $"Applying this change drops column \"{qualifiedName}\" and removes its stored values."
        };
    }

    private async Task<SchemaChangeImpact?> BuildDropTableImpactAsync(
        DbConnection connection,
        ModelJson currentModel,
        SchemaChange change)
    {
        var table = TryFindTable(currentModel, change.PreviousTableName ?? change.TableName);
        if (table is null)
        {
            return null;
        }

        long? rowCount = null;
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? sampleRows = null;

        try
        {
            rowCount = await LoadTableRowCountAsync(connection, table.Name);
            sampleRows = await LoadSampleRowsAsync(connection, table, 5);
        }
        catch
        {
            // Keep the destructive drop-table warning even if samples cannot
            // be loaded from the current database.
        }

        return new SchemaChangeImpact
        {
            ChangeType = change.ChangeType,
            TableName = change.TableName,
            RelationName = change.RelationName,
            Kind = "table_drop",
            ObjectType = "table",
            ObjectName = table.Name,
            RowCount = rowCount,
            SampleRows = sampleRows,
            Message = $"Applying this change drops table \"{table.Name}\" and removes its stored rows."
        };
    }

    private static bool RelationEndpointOrColumnChanged(RelationDef oldRelation, RelationDef newRelation)
    {
        return !string.Equals(oldRelation.From, newRelation.From, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(oldRelation.To, newRelation.To, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(oldRelation.Column, newRelation.Column, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long> LoadTableRowCountAsync(DbConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Quote(tableName)};";
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value);
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> LoadSampleRowsAsync(
        DbConnection connection,
        TableDef table,
        int limit)
    {
        var columns = table.Columns
            .Where(column => ColumnTypes.HasPhysicalColumn(column.Type))
            .Select(column => column.Name)
            .ToList();

        if (columns.Count == 0)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {string.Join(", ", columns.Select(Quote))}
            FROM {Quote(table.Name)}
            {BuildStableOrderByClause(table)}
            LIMIT {limit};
            """;

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(row);
        }

        return rows;
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
