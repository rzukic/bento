using Repere.Api.Db;
using Repere.Api.Db.Migrations;

namespace Repere.Api.Db.Schema;

public sealed record SchemaPreviewResult
{
    public required bool RequiresConfirmation { get; init; }
    public required ModelJson CurrentModel { get; init; }
    public required ModelJson TargetModel { get; init; }
    public required IReadOnlyList<SchemaValidationIssue> Errors { get; init; }
    public required IReadOnlyList<SchemaValidationIssue> Warnings { get; init; }
    public required IReadOnlyList<SchemaChange> Changes { get; init; }
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

    public SchemaService(
        ModelJsonStore modelJsonStore,
        SchemaValidator validator,
        SchemaDiffEngine schemaDiffEngine,
        MigrationGenerator migrationGenerator,
        MigrationRunner migrationRunner)
    {
        _modelJsonStore = modelJsonStore;
        _validator = validator;
        _schemaDiffEngine = schemaDiffEngine;
        _migrationGenerator = migrationGenerator;
        _migrationRunner = migrationRunner;
    }

    public async Task<SchemaPreviewResult> PreviewSchemaAsync(ModelJson requestedModel)
    {
        var validation = _validator.Validate(requestedModel);
        var currentModel = await _modelJsonStore.GetCurrentAsync() ?? new ModelJson { Version = 0 };

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
                Plan = null
            };
        }

        var changes = _schemaDiffEngine.Diff(currentModel, validation.NormalizedModel);
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
                Plan = CreateNoOpPlan(changes)
            };
        }

        try
        {
            var plan = _migrationGenerator.Generate(currentModel, validation.NormalizedModel, changes);

            return new SchemaPreviewResult
            {
                RequiresConfirmation = plan.RequiresConfirmation,
                CurrentModel = currentModel,
                TargetModel = validation.NormalizedModel,
                Errors = validation.Errors,
                Warnings = validation.Warnings,
                Changes = changes,
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

        var migration = await _migrationRunner.ApplyAsync(preview.CurrentModel, preview.TargetModel, plan);

        return new SchemaApplyResult
        {
            Applied = true,
            RequiresConfirmation = false,
            CurrentModel = preview.CurrentModel,
            TargetModel = preview.TargetModel with { Version = migration.VersionTo },
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

    private static string FindColumnTypePath(ModelJson model, string tableName, string? columnName)
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
                    return $"tables[{tableIndex}].columns[{columnIndex}].type";
            }

            return $"tables[{tableIndex}]";
        }

        return "tables";
    }
}
