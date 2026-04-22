using System.Text.Json;

namespace Repere.Api.Db.Schema;

public enum SchemaChangeType
{
    AddTable,
    DropTable,
    RenameTable,
    AlterTableMetadata,
    AddColumn,
    DropColumn,
    RenameColumn,
    AlterColumnMetadata,
    AlterColumnType,
    AlterColumnNullability,
    AlterColumnDefault,
    AlterColumnUnique,
    AddRelation,
    DropRelation,
    AlterRelation,
    AddIndex,
    DropIndex,
    RenameIndex,
    AlterIndex
}

public sealed record SchemaChange
{
    public SchemaChangeType ChangeType { get; init; }
    public string TableName { get; init; } = string.Empty;
    public string? PreviousTableName { get; init; }
    public string? ColumnName { get; init; }
    public string? PreviousColumnName { get; init; }
    public string? RelationName { get; init; }
    public string? IndexName { get; init; }
    public object? OldValue { get; init; }
    public object? NewValue { get; init; }
    public CoercionKind? CoercionKind { get; init; }
    public bool IsDestructive { get; init; }
}

public sealed class SchemaDiffEngine
{
    public IReadOnlyList<SchemaChange> Diff(ModelJson oldModel, ModelJson newModel)
    {
        var changes = new List<SchemaChange>();

        var tableMatches = MatchTables(oldModel.Tables, newModel.Tables);

        foreach (var match in tableMatches.Matched)
        {
            if (!string.Equals(match.OldItem.Name, match.NewItem.Name, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.RenameTable,
                    TableName = match.NewItem.Name,
                    PreviousTableName = match.OldItem.Name,
                    OldValue = match.OldItem.Name,
                    NewValue = match.NewItem.Name
                });
            }

            AddMetadataChange(
                changes,
                SchemaChangeType.AlterTableMetadata,
                match.OldItem.DisplayName,
                match.NewItem.DisplayName,
                match.OldItem.Description,
                match.NewItem.Description,
                tableName: match.NewItem.Name,
                previousTableName: match.OldItem.Name);

            changes.AddRange(DiffColumns(match.OldItem, match.NewItem));
            changes.AddRange(DiffIndexes(match.OldItem, match.NewItem));
        }

        foreach (var addedTable in tableMatches.Added)
        {
            changes.Add(new SchemaChange
            {
                ChangeType = SchemaChangeType.AddTable,
                TableName = addedTable.Name,
                NewValue = addedTable
            });
        }

        foreach (var removedTable in tableMatches.Removed)
        {
            changes.Add(new SchemaChange
            {
                ChangeType = SchemaChangeType.DropTable,
                TableName = removedTable.Name,
                OldValue = removedTable,
                IsDestructive = true
            });
        }

        changes.AddRange(DiffRelations(oldModel.Relations, newModel.Relations));

        return changes;
    }

    private static IReadOnlyList<SchemaChange> DiffColumns(TableDef oldTable, TableDef newTable)
    {
        var changes = new List<SchemaChange>();
        var matches = MatchItems(oldTable.Columns, newTable.Columns, c => c.Name, c => c.RenamedFrom);

        foreach (var match in matches.Matched)
        {
            var oldColumn = match.OldItem;
            var newColumn = match.NewItem;

            if (!string.Equals(oldColumn.Name, newColumn.Name, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.RenameColumn,
                    TableName = newTable.Name,
                    PreviousTableName = oldTable.Name,
                    ColumnName = newColumn.Name,
                    PreviousColumnName = oldColumn.Name,
                    OldValue = oldColumn.Name,
                    NewValue = newColumn.Name
                });
            }

            AddMetadataChange(
                changes,
                SchemaChangeType.AlterColumnMetadata,
                oldColumn.DisplayName,
                newColumn.DisplayName,
                oldColumn.Description,
                newColumn.Description,
                tableName: newTable.Name,
                previousTableName: oldTable.Name,
                columnName: newColumn.Name,
                previousColumnName: oldColumn.Name);

            if (oldColumn.Type != newColumn.Type)
            {
                var coercion = ColumnTypes.GetCoercion(oldColumn.Type, newColumn.Type);
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.AlterColumnType,
                    TableName = newTable.Name,
                    PreviousTableName = oldTable.Name,
                    ColumnName = newColumn.Name,
                    PreviousColumnName = oldColumn.Name,
                    OldValue = oldColumn.Type,
                    NewValue = newColumn.Type,
                    CoercionKind = coercion,
                    IsDestructive = coercion is CoercionKind.Lossy or CoercionKind.Impossible
                });
            }

            if (oldColumn.Nullable != newColumn.Nullable)
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.AlterColumnNullability,
                    TableName = newTable.Name,
                    PreviousTableName = oldTable.Name,
                    ColumnName = newColumn.Name,
                    PreviousColumnName = oldColumn.Name,
                    OldValue = oldColumn.Nullable,
                    NewValue = newColumn.Nullable
                });
            }

            var oldDefault = NormalizeJson(oldColumn.DefaultValue);
            var newDefault = NormalizeJson(newColumn.DefaultValue);
            if (!string.Equals(oldDefault, newDefault, StringComparison.Ordinal))
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.AlterColumnDefault,
                    TableName = newTable.Name,
                    PreviousTableName = oldTable.Name,
                    ColumnName = newColumn.Name,
                    PreviousColumnName = oldColumn.Name,
                    OldValue = oldDefault,
                    NewValue = newDefault
                });
            }

            if (oldColumn.Unique != newColumn.Unique)
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.AlterColumnUnique,
                    TableName = newTable.Name,
                    PreviousTableName = oldTable.Name,
                    ColumnName = newColumn.Name,
                    PreviousColumnName = oldColumn.Name,
                    OldValue = oldColumn.Unique,
                    NewValue = newColumn.Unique
                });
            }
        }

        foreach (var addedColumn in matches.Added)
        {
            changes.Add(new SchemaChange
            {
                ChangeType = SchemaChangeType.AddColumn,
                TableName = newTable.Name,
                PreviousTableName = oldTable.Name,
                ColumnName = addedColumn.Name,
                NewValue = addedColumn
            });
        }

        foreach (var removedColumn in matches.Removed)
        {
            changes.Add(new SchemaChange
            {
                ChangeType = SchemaChangeType.DropColumn,
                TableName = oldTable.Name,
                PreviousTableName = oldTable.Name,
                ColumnName = removedColumn.Name,
                OldValue = removedColumn,
                IsDestructive = true
            });
        }

        return changes;
    }

    private static IReadOnlyList<SchemaChange> DiffIndexes(TableDef oldTable, TableDef newTable)
    {
        var changes = new List<SchemaChange>();
        var matches = MatchItems(oldTable.Indexes, newTable.Indexes, i => i.Name, i => i.RenamedFrom);

        foreach (var match in matches.Matched)
        {
            var oldIndex = match.OldItem;
            var newIndex = match.NewItem;

            if (!string.Equals(oldIndex.Name, newIndex.Name, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.RenameIndex,
                    TableName = newTable.Name,
                    PreviousTableName = oldTable.Name,
                    IndexName = newIndex.Name,
                    OldValue = oldIndex.Name,
                    NewValue = newIndex.Name
                });
            }

            if (oldIndex.Unique != newIndex.Unique || !oldIndex.Columns.SequenceEqual(newIndex.Columns, StringComparer.OrdinalIgnoreCase))
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.AlterIndex,
                    TableName = newTable.Name,
                    PreviousTableName = oldTable.Name,
                    IndexName = newIndex.Name,
                    OldValue = oldIndex,
                    NewValue = newIndex
                });
            }
        }

        foreach (var addedIndex in matches.Added)
        {
            changes.Add(new SchemaChange
            {
                ChangeType = SchemaChangeType.AddIndex,
                TableName = newTable.Name,
                PreviousTableName = oldTable.Name,
                IndexName = addedIndex.Name,
                NewValue = addedIndex
            });
        }

        foreach (var removedIndex in matches.Removed)
        {
            changes.Add(new SchemaChange
            {
                ChangeType = SchemaChangeType.DropIndex,
                TableName = oldTable.Name,
                PreviousTableName = oldTable.Name,
                IndexName = removedIndex.Name,
                OldValue = removedIndex
            });
        }

        return changes;
    }

    private static IReadOnlyList<SchemaChange> DiffRelations(IReadOnlyList<RelationDef> oldRelations, IReadOnlyList<RelationDef> newRelations)
    {
        var changes = new List<SchemaChange>();
        var oldByName = oldRelations.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var newByName = newRelations.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var relation in newRelations)
        {
            if (!oldByName.TryGetValue(relation.Name, out var oldRelation))
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.AddRelation,
                    TableName = relation.To,
                    PreviousTableName = relation.From,
                    RelationName = relation.Name,
                    NewValue = relation
                });
                continue;
            }

            if (!RelationEquals(oldRelation, relation))
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.AlterRelation,
                    TableName = relation.To,
                    PreviousTableName = relation.From,
                    RelationName = relation.Name,
                    OldValue = oldRelation,
                    NewValue = relation
                });
            }
        }

        foreach (var relation in oldRelations)
        {
            if (newByName.ContainsKey(relation.Name))
                continue;

            changes.Add(new SchemaChange
            {
                ChangeType = SchemaChangeType.DropRelation,
                TableName = relation.To,
                PreviousTableName = relation.From,
                RelationName = relation.Name,
                OldValue = relation
            });
        }

        return changes;
    }

    private static MatchResult<TableDef> MatchTables(IReadOnlyList<TableDef> oldTables, IReadOnlyList<TableDef> newTables)
    {
        return MatchItems(oldTables, newTables, t => t.Name, t => t.RenamedFrom);
    }

    private static MatchResult<T> MatchItems<T>(
        IReadOnlyList<T> oldItems,
        IReadOnlyList<T> newItems,
        Func<T, string> currentName,
        Func<T, string?> renamedFrom)
    {
        var matched = new List<ItemMatch<T>>();
        var unmatchedOld = oldItems.ToDictionary(currentName, StringComparer.OrdinalIgnoreCase);
        var added = new List<T>();

        foreach (var newItem in newItems)
        {
            var newName = currentName(newItem);
            if (unmatchedOld.Remove(newName, out var bySameName))
            {
                matched.Add(new ItemMatch<T>(bySameName, newItem));
                continue;
            }

            var previousName = renamedFrom(newItem);
            if (!string.IsNullOrWhiteSpace(previousName) &&
                unmatchedOld.Remove(previousName, out var byRename))
            {
                matched.Add(new ItemMatch<T>(byRename, newItem));
                continue;
            }

            added.Add(newItem);
        }

        return new MatchResult<T>(matched, added, unmatchedOld.Values.ToList());
    }

    private static bool RelationEquals(RelationDef left, RelationDef right)
    {
        return left.Type == right.Type
            && string.Equals(left.From, right.From, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.To, right.To, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.FromAccessor, right.FromAccessor, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ToAccessor, right.ToAccessor, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Column, right.Column, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.FromColumn, right.FromColumn, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ToColumn, right.ToColumn, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddMetadataChange(
        List<SchemaChange> changes,
        SchemaChangeType changeType,
        string? oldDisplayName,
        string? newDisplayName,
        string? oldDescription,
        string? newDescription,
        string tableName,
        string previousTableName,
        string? columnName = null,
        string? previousColumnName = null)
    {
        var displayNameChanged = !string.Equals(oldDisplayName, newDisplayName, StringComparison.Ordinal);
        var descriptionChanged = !string.Equals(oldDescription, newDescription, StringComparison.Ordinal);
        if (!displayNameChanged && !descriptionChanged)
            return;

        changes.Add(new SchemaChange
        {
            ChangeType = changeType,
            TableName = tableName,
            PreviousTableName = previousTableName,
            ColumnName = columnName,
            PreviousColumnName = previousColumnName,
            OldValue = BuildMetadataValue(oldDisplayName, oldDescription),
            NewValue = BuildMetadataValue(newDisplayName, newDescription)
        });
    }

    private static IReadOnlyDictionary<string, string?> BuildMetadataValue(string? displayName, string? description)
    {
        return new Dictionary<string, string?>
        {
            ["displayName"] = displayName,
            ["description"] = description
        };
    }

    private static string? NormalizeJson(JsonElement? value)
    {
        if (!value.HasValue)
            return null;

        return value.Value.ValueKind == JsonValueKind.Null ? null : value.Value.GetRawText();
    }

    private sealed record MatchResult<T>(
        IReadOnlyList<ItemMatch<T>> Matched,
        IReadOnlyList<T> Added,
        IReadOnlyList<T> Removed);

    private sealed record ItemMatch<T>(T OldItem, T NewItem);
}
