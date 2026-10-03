using Repere.Api.Db.Schema;

namespace Repere.Api.Db.Migrations;

public sealed class MigrationPlan
{
    public required IReadOnlyList<string> SqlUp { get; init; }
    public required IReadOnlyList<string> SqlDown { get; init; }
    public required bool IsDestructive { get; init; }
    public required IReadOnlyList<SchemaChange> Changes { get; init; }
    public bool RequiresConfirmation => IsDestructive;
}

/// <summary>
/// Turns a schema diff into forward/backward SQL for the configured provider.
/// Ordering lives in <see cref="SchemaChangeOrdering"/>, the provider-specific DDL in
/// <see cref="PostgresDdlGenerator"/> / <see cref="SqliteDdlGenerator"/>, relation and
/// junction-table handling in <see cref="RelationConversionPlanner"/>, and the shared
/// DDL building blocks in <see cref="DdlHelpers"/>.
/// </summary>
public class MigrationGenerator
{
    private readonly DatabaseProvider _provider;
    private readonly RelationResolver _relationResolver;
    private readonly PostgresDdlGenerator _postgres;
    private readonly SqliteDdlGenerator _sqlite;

    public MigrationGenerator(DatabaseConfig config, RelationResolver? relationResolver = null)
    {
        _provider = config.Provider;
        _relationResolver = relationResolver ?? new RelationResolver();

        var relations = new RelationConversionPlanner(_relationResolver);
        _postgres = new PostgresDdlGenerator(relations);
        _sqlite = new SqliteDdlGenerator(relations);
    }

    public MigrationPlan Generate(ModelJson oldModel, ModelJson newModel, IReadOnlyList<SchemaChange> changes)
    {
        foreach (var change in changes.Where(c => c.ChangeType == SchemaChangeType.AlterColumnType))
        {
            if (change.CoercionKind == CoercionKind.Impossible)
            {
                throw new InvalidOperationException(
                    $"Cannot generate migration for impossible type conversion on '{change.TableName}.{change.ColumnName}'.");
            }
        }

        var sqlUp = new List<string>();
        var sqlDown = new List<string>();

        var orderedChanges = SchemaChangeOrdering.OrderChangesForGeneration(_relationResolver, oldModel, newModel, changes);

        if (_provider == DatabaseProvider.Postgres)
            _postgres.Generate(oldModel, newModel, orderedChanges, sqlUp, sqlDown);
        else
            _sqlite.Generate(oldModel, newModel, orderedChanges, sqlUp, sqlDown);

        return new MigrationPlan
        {
            SqlUp = sqlUp,
            SqlDown = sqlDown,
            IsDestructive = changes.Any(c => c.IsDestructive),
            Changes = changes
        };
    }
}
