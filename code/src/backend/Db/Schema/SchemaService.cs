using Repere.Api.Db.Migrations;

namespace Repere.Api.Db.Schema;

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

    public async Task<SchemaApplyResult> ApplySchemaAsync(ModelJson requestedModel, bool confirmDestructive = false)
    {
        var validation = _validator.Validate(requestedModel);
        var currentModel = await _modelJsonStore.GetCurrentAsync() ?? new ModelJson { Version = 0 };

        if (!validation.IsValid)
        {
            return new SchemaApplyResult
            {
                Applied = false,
                RequiresConfirmation = false,
                CurrentModel = currentModel,
                TargetModel = validation.NormalizedModel,
                Errors = validation.Errors,
                Warnings = validation.Warnings,
                Changes = []
            };
        }

        var changes = _schemaDiffEngine.Diff(currentModel, validation.NormalizedModel);
        if (changes.Count == 0)
        {
            return new SchemaApplyResult
            {
                Applied = false,
                RequiresConfirmation = false,
                CurrentModel = currentModel,
                TargetModel = validation.NormalizedModel,
                Errors = validation.Errors,
                Warnings = validation.Warnings,
                Changes = changes
            };
        }

        var plan = _migrationGenerator.Generate(currentModel, validation.NormalizedModel, changes);
        if (plan.RequiresConfirmation && !confirmDestructive)
        {
            return new SchemaApplyResult
            {
                Applied = false,
                RequiresConfirmation = true,
                CurrentModel = currentModel,
                TargetModel = validation.NormalizedModel,
                Errors = validation.Errors,
                Warnings = validation.Warnings,
                Changes = changes,
                Plan = plan
            };
        }

        var migration = await _migrationRunner.ApplyAsync(currentModel, validation.NormalizedModel, plan);

        return new SchemaApplyResult
        {
            Applied = true,
            RequiresConfirmation = false,
            CurrentModel = currentModel,
            TargetModel = validation.NormalizedModel with { Version = migration.VersionTo },
            Errors = validation.Errors,
            Warnings = validation.Warnings,
            Changes = changes,
            Plan = plan,
            Migration = migration
        };
    }

    public Task<MigrationApplyResult?> RollbackLastAsync() => _migrationRunner.RollbackLastAsync();

    public Task<IReadOnlyList<MigrationApplyResult>> RollbackAsync(int steps) => _migrationRunner.RollbackAsync(steps);

    public Task<IReadOnlyList<MigrationHistoryEntry>> GetHistoryAsync() => _migrationRunner.GetHistoryAsync();
}
