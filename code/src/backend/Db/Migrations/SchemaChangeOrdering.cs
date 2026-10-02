using Repere.Api.Db.Schema;

namespace Repere.Api.Db.Migrations;

/// <summary>
/// Orders schema changes into generation phases (renames, creates, column/index
/// alterations, relation changes, drops) so emitted DDL is dependency-safe.
/// </summary>
internal static class SchemaChangeOrdering
{
    internal static IReadOnlyList<SchemaChange> OrderChangesForGeneration(
        RelationResolver relationResolver,
        ModelJson oldModel,
        ModelJson newModel,
        IReadOnlyList<SchemaChange> changes)
    {
        var orderedTableNames = relationResolver.OrderTables(newModel)
            .Select((table, index) => new { table.Name, Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        return changes
            .Select((change, index) => new { Change = change, Index = index })
            .OrderBy(item => GetGenerationPhase(item.Change))
            .ThenBy(item => item.Change.ChangeType == SchemaChangeType.AddTable && orderedTableNames.TryGetValue(item.Change.TableName, out var addIndex)
                ? addIndex
                : int.MaxValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Change)
            .ToList();
    }

    private static int GetGenerationPhase(SchemaChange change) => change.ChangeType switch
    {
        SchemaChangeType.RenameTable => 0,
        SchemaChangeType.AddTable => 1,
        SchemaChangeType.AddColumn => 2,
        SchemaChangeType.RenameColumn => 2,
        SchemaChangeType.AlterColumnType => 2,
        SchemaChangeType.AlterColumnNullability => 2,
        SchemaChangeType.AlterColumnDefault => 2,
        SchemaChangeType.AlterColumnUnique => 2,
        SchemaChangeType.AddIndex => 2,
        SchemaChangeType.RenameIndex => 2,
        SchemaChangeType.AlterIndex => 2,
        SchemaChangeType.AddRelation => 3,
        SchemaChangeType.AlterRelation => 3,
        SchemaChangeType.DropRelation => 4,
        SchemaChangeType.DropIndex => 4,
        SchemaChangeType.DropColumn => 4,
        SchemaChangeType.DropTable => 5,
        _ => 10
    };
}
