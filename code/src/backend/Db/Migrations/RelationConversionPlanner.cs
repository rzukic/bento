using Repere.Api.Db.Schema;
using static Repere.Api.Db.Migrations.DdlHelpers;

namespace Repere.Api.Db.Migrations;

/// <summary>
/// Column mapping for copying a 1:N relation's foreign keys into an N:M junction table.
/// </summary>
internal sealed record OneToManyToManyToManyCopyMapping(
    string SourceTableName,
    string SourcePrimaryKeyColumn,
    string SourceForeignKeyColumn,
    string JunctionTableName,
    string ParentJunctionColumn,
    string ChildJunctionColumn);

/// <summary>
/// Column mapping for collapsing an N:M junction table back into a 1:N foreign key column.
/// </summary>
internal sealed record ManyToManyToOneToManyCopyMapping(
    string TargetTableName,
    string TargetPrimaryKeyColumn,
    string TargetForeignKeyColumn,
    string JunctionTableName,
    string ParentJunctionColumn,
    string ChildJunctionColumn);

/// <summary>
/// A relation conversion that SQLite must stage through a temp table (because the
/// affected table is rebuilt in the same migration), paired with its copy mapping.
/// </summary>
internal sealed record SqliteRelationConversion<TMapping>(
    string RelationName,
    string TempTableName,
    TMapping Mapping);

/// <summary>
/// Plans and emits the SQL for relation changes: junction table create/drop for N:M
/// relations, and the data-preserving 1:N to N:M (and back) conversions, including the
/// SQLite temp-table staging used when the owning table is rebuilt in the same migration.
/// </summary>
internal sealed class RelationConversionPlanner
{
    private readonly RelationResolver _relationResolver;

    public RelationConversionPlanner(RelationResolver relationResolver)
    {
        _relationResolver = relationResolver;
    }

    internal Dictionary<string, SqliteRelationConversion<OneToManyToManyToManyCopyMapping>> GetSqliteOneToManyToManyToManyConversions(
        ModelJson oldModel,
        ModelJson newModel,
        IReadOnlyList<SchemaChange> changes)
    {
        var conversions = new Dictionary<string, SqliteRelationConversion<OneToManyToManyToManyCopyMapping>>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes.Where(change => change.ChangeType == SchemaChangeType.AlterRelation))
        {
            var oldRelation = change.OldValue as RelationDef;
            var newRelation = change.NewValue as RelationDef;
            if (oldRelation is null || newRelation is null)
                continue;

            var mapping = ResolveOneToManyToManyToManyMapping(oldModel, newModel, oldRelation, newRelation);
            if (mapping is null)
                continue;

            conversions[oldRelation.Name] = new SqliteRelationConversion<OneToManyToManyToManyCopyMapping>(
                RelationName: oldRelation.Name,
                TempTableName: $"__relcopy_{oldRelation.Name}",
                Mapping: mapping);
        }

        return conversions;
    }

    internal Dictionary<string, SqliteRelationConversion<ManyToManyToOneToManyCopyMapping>> GetSqliteManyToManyToOneToManyConversions(
        ModelJson oldModel,
        ModelJson newModel,
        IReadOnlyList<SchemaChange> changes)
    {
        var conversions = new Dictionary<string, SqliteRelationConversion<ManyToManyToOneToManyCopyMapping>>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes.Where(change => change.ChangeType == SchemaChangeType.AlterRelation))
        {
            var oldRelation = change.OldValue as RelationDef;
            var newRelation = change.NewValue as RelationDef;
            if (oldRelation is null || newRelation is null)
                continue;

            var mapping = ResolveManyToManyToOneToManyMapping(oldModel, newModel, oldRelation, newRelation);
            if (mapping is null)
                continue;

            conversions[oldRelation.Name] = new SqliteRelationConversion<ManyToManyToOneToManyCopyMapping>(
                RelationName: oldRelation.Name,
                TempTableName: $"__relcopy_{oldRelation.Name}",
                Mapping: mapping);
        }

        return conversions;
    }

    internal void AppendSqliteOneToManyToManyToManyConversion(
        SchemaChange change,
        ModelJson oldModel,
        ModelJson newModel,
        SqliteRelationConversion<OneToManyToManyToManyCopyMapping> conversion,
        List<string> sqlUp,
        List<string> sqlDown)
    {
        var oldRelation = (RelationDef)change.OldValue!;
        var newRelation = (RelationDef)change.NewValue!;
        var newJunction = _relationResolver.BuildJunctionTable(newRelation);
        var newJunctionTable = HasExplicitJunctionTable(newModel, newJunction.Name)
            ? FindTable(newModel, newJunction.Name)
            : newJunction;

        var upStatements = new List<string>
        {
            BuildCreateTableSql(newJunctionTable, DatabaseProvider.Sqlite, newModel),
            $"""
            INSERT INTO {Quote(conversion.Mapping.JunctionTableName)} ({Quote(conversion.Mapping.ParentJunctionColumn)}, {Quote(conversion.Mapping.ChildJunctionColumn)})
            SELECT "parent_id", "child_id"
            FROM {Quote(conversion.TempTableName)};
            """,
            $"DROP TABLE {Quote(conversion.TempTableName)};"
        };

        var downStatements = new List<string>(
            BuildManyToManyToOneToManyCopyStatements(oldModel, newModel, oldRelation, newRelation, DatabaseProvider.Sqlite))
        {
            $"DROP TABLE {Quote(conversion.Mapping.JunctionTableName)};"
        };

        Append(sqlUp, sqlDown, upStatements, downStatements);
    }

    internal void AppendSqliteManyToManyToOneToManyConversion(
        SchemaChange change,
        ModelJson oldModel,
        ModelJson newModel,
        SqliteRelationConversion<ManyToManyToOneToManyCopyMapping> conversion,
        List<string> sqlUp,
        List<string> sqlDown)
    {
        var oldRelation = (RelationDef)change.OldValue!;
        var newRelation = (RelationDef)change.NewValue!;
        var oldJunction = _relationResolver.BuildJunctionTable(oldRelation);
        var oldJunctionTable = HasExplicitJunctionTable(oldModel, oldJunction.Name)
            ? FindTable(oldModel, oldJunction.Name)
            : oldJunction;

        var upStatements = new List<string>
        {
            $"""
            UPDATE {Quote(conversion.Mapping.TargetTableName)}
            SET {Quote(conversion.Mapping.TargetForeignKeyColumn)} = (
                SELECT temp."parent_id"
                FROM {Quote(conversion.TempTableName)} AS temp
                WHERE temp."child_id" = {Quote(conversion.Mapping.TargetTableName)}.{Quote(conversion.Mapping.TargetPrimaryKeyColumn)}
                LIMIT 1
            )
            WHERE EXISTS (
                SELECT 1
                FROM {Quote(conversion.TempTableName)} AS temp
                WHERE temp."child_id" = {Quote(conversion.Mapping.TargetTableName)}.{Quote(conversion.Mapping.TargetPrimaryKeyColumn)}
            );
            """,
            $"DROP TABLE {Quote(conversion.TempTableName)};"
        };

        var downStatements = new List<string>
        {
            BuildCreateTableSql(oldJunctionTable, DatabaseProvider.Sqlite, oldModel)
        };
        downStatements.AddRange(BuildOneToManyToManyToManyCopyStatements(newModel, oldModel, newRelation, oldRelation));

        Append(sqlUp, sqlDown, upStatements, downStatements);
    }

    internal void AppendRelationSql(
        SchemaChange change,
        ModelJson oldModel,
        ModelJson newModel,
        DatabaseProvider provider,
        List<string> sqlUp,
        List<string> sqlDown)
    {
        switch (change.ChangeType)
        {
            case SchemaChangeType.AddRelation:
            {
                var relation = change.NewValue as RelationDef;
                if (relation?.Type != RelationType.ManyToMany)
                    break;

                var junction = _relationResolver.BuildJunctionTable(relation);
                if (HasExplicitJunctionTable(newModel, junction.Name))
                    break;

                Append(sqlUp, sqlDown,
                    BuildCreateTableSql(junction, provider, newModel),
                    $"DROP TABLE {Quote(junction.Name)};");
                break;
            }
            case SchemaChangeType.DropRelation:
            {
                var relation = change.OldValue as RelationDef;
                if (relation?.Type != RelationType.ManyToMany)
                    break;

                var junction = _relationResolver.BuildJunctionTable(relation);
                if (HasExplicitJunctionTable(oldModel, junction.Name))
                    break;

                Append(sqlUp, sqlDown,
                    $"DROP TABLE {Quote(junction.Name)};",
                    BuildCreateTableSql(junction, provider, oldModel));
                break;
            }
            case SchemaChangeType.AlterRelation:
            {
                var oldRelation = (RelationDef)change.OldValue!;
                var newRelation = (RelationDef)change.NewValue!;
                if (oldRelation.Type == RelationType.OneToMany && newRelation.Type == RelationType.OneToMany)
                    break;

                if (oldRelation.Type == RelationType.OneToMany)
                {
                    var addedJunction = _relationResolver.BuildJunctionTable(newRelation);
                    var upStatements = new List<string>();
                    if (!HasExplicitJunctionTable(newModel, addedJunction.Name))
                        upStatements.Add(BuildCreateTableSql(addedJunction, provider, newModel));
                    upStatements.AddRange(BuildOneToManyToManyToManyCopyStatements(oldModel, newModel, oldRelation, newRelation));

                    var downStatements = new List<string>(BuildManyToManyToOneToManyCopyStatements(oldModel, newModel, oldRelation, newRelation, provider));
                    if (!HasExplicitJunctionTable(newModel, addedJunction.Name))
                        downStatements.Add($"DROP TABLE {Quote(addedJunction.Name)};");

                    Append(sqlUp, sqlDown, upStatements, downStatements);
                    break;
                }

                var oldJunction = _relationResolver.BuildJunctionTable(oldRelation);
                if (newRelation.Type == RelationType.OneToMany)
                {
                    var upStatements = new List<string>(
                        BuildManyToManyToOneToManyCopyStatements(oldModel, newModel, oldRelation, newRelation, provider));
                    if (!HasExplicitJunctionTable(oldModel, oldJunction.Name))
                        upStatements.Add($"DROP TABLE {Quote(oldJunction.Name)};");

                    var downStatements = new List<string>();
                    if (!HasExplicitJunctionTable(oldModel, oldJunction.Name))
                        downStatements.Add(BuildCreateTableSql(oldJunction, provider, oldModel));
                    downStatements.AddRange(BuildOneToManyToManyToManyCopyStatements(newModel, oldModel, newRelation, oldRelation));

                    Append(sqlUp, sqlDown, upStatements, downStatements);
                    break;
                }

                var newJunction = _relationResolver.BuildJunctionTable(newRelation);
                if (JunctionTableEquals(oldJunction, newJunction))
                    break;

                if (HasExplicitJunctionTable(oldModel, oldJunction.Name) || HasExplicitJunctionTable(newModel, newJunction.Name))
                    break;

                Append(sqlUp, sqlDown,
                    new[]
                    {
                        $"DROP TABLE {Quote(oldJunction.Name)};",
                        BuildCreateTableSql(newJunction, provider, newModel)
                    },
                    new[]
                    {
                        $"DROP TABLE {Quote(newJunction.Name)};",
                        BuildCreateTableSql(oldJunction, provider, oldModel)
                    });
                break;
            }
        }
    }

    private static bool HasExplicitJunctionTable(ModelJson model, string junctionTableName)
    {
        return model.Tables.Any(table =>
            string.Equals(table.Name, junctionTableName, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> BuildOneToManyToManyToManyCopyStatements(
        ModelJson oldModel,
        ModelJson newModel,
        RelationDef oldRelation,
        RelationDef newRelation)
    {
        var mapping = ResolveOneToManyToManyToManyMapping(oldModel, newModel, oldRelation, newRelation);
        if (mapping is null)
            return [];

        return
        [
            $"""
            INSERT INTO {Quote(mapping.JunctionTableName)} ({Quote(mapping.ParentJunctionColumn)}, {Quote(mapping.ChildJunctionColumn)})
            SELECT {Quote(mapping.SourceForeignKeyColumn)}, {Quote(mapping.SourcePrimaryKeyColumn)}
            FROM {Quote(mapping.SourceTableName)}
            WHERE {Quote(mapping.SourceForeignKeyColumn)} IS NOT NULL;
            """
        ];
    }

    private IEnumerable<string> BuildManyToManyToOneToManyCopyStatements(
        ModelJson oldModel,
        ModelJson newModel,
        RelationDef oldRelation,
        RelationDef newRelation,
        DatabaseProvider provider)
    {
        var mapping = ResolveManyToManyToOneToManyMapping(oldModel, newModel, oldRelation, newRelation);
        if (mapping is null)
            return [];

        if (provider == DatabaseProvider.Postgres)
        {
            return
            [
                $"""
                UPDATE {Quote(mapping.TargetTableName)} AS target
                SET {Quote(mapping.TargetForeignKeyColumn)} = source.parent_id
                FROM (
                    SELECT {Quote(mapping.ChildJunctionColumn)} AS child_id, MIN({Quote(mapping.ParentJunctionColumn)}) AS parent_id
                    FROM {Quote(mapping.JunctionTableName)}
                    GROUP BY {Quote(mapping.ChildJunctionColumn)}
                ) AS source
                WHERE target.{Quote(mapping.TargetPrimaryKeyColumn)} = source.child_id;
                """
            ];
        }

        return
        [
            $"""
            UPDATE {Quote(mapping.TargetTableName)}
            SET {Quote(mapping.TargetForeignKeyColumn)} = (
                SELECT junction.{Quote(mapping.ParentJunctionColumn)}
                FROM {Quote(mapping.JunctionTableName)} AS junction
                WHERE junction.{Quote(mapping.ChildJunctionColumn)} = {Quote(mapping.TargetTableName)}.{Quote(mapping.TargetPrimaryKeyColumn)}
                LIMIT 1
            )
            WHERE EXISTS (
                SELECT 1
                FROM {Quote(mapping.JunctionTableName)} AS junction
                WHERE junction.{Quote(mapping.ChildJunctionColumn)} = {Quote(mapping.TargetTableName)}.{Quote(mapping.TargetPrimaryKeyColumn)}
            );
            """
        ];
    }

    private ManyToManyToOneToManyCopyMapping? ResolveManyToManyToOneToManyMapping(
        ModelJson oldModel,
        ModelJson newModel,
        RelationDef oldRelation,
        RelationDef newRelation)
    {
        if (oldRelation.Type != RelationType.ManyToMany || newRelation.Type != RelationType.OneToMany)
            return null;

        if (string.IsNullOrWhiteSpace(newRelation.Column))
            return null;

        var targetTable = FindTable(newModel, newRelation.To);
        var targetPrimaryKey = targetTable.Columns.FirstOrDefault(column => column.PrimaryKey);
        if (targetPrimaryKey is null)
            return null;

        var oldJunction = _relationResolver.BuildJunctionTable(oldRelation);
        if (oldJunction.Columns.Count < 2)
            return null;

        var firstJunctionColumn = oldJunction.Columns[0];
        var secondJunctionColumn = oldJunction.Columns[1];

        if (string.Equals(firstJunctionColumn.References, newRelation.From, StringComparison.OrdinalIgnoreCase)
            && string.Equals(secondJunctionColumn.References, newRelation.To, StringComparison.OrdinalIgnoreCase))
        {
            return new ManyToManyToOneToManyCopyMapping(
                TargetTableName: newRelation.To,
                TargetPrimaryKeyColumn: targetPrimaryKey.Name,
                TargetForeignKeyColumn: newRelation.Column,
                JunctionTableName: oldJunction.Name,
                ParentJunctionColumn: firstJunctionColumn.Name,
                ChildJunctionColumn: secondJunctionColumn.Name);
        }

        if (string.Equals(firstJunctionColumn.References, newRelation.To, StringComparison.OrdinalIgnoreCase)
            && string.Equals(secondJunctionColumn.References, newRelation.From, StringComparison.OrdinalIgnoreCase))
        {
            return new ManyToManyToOneToManyCopyMapping(
                TargetTableName: newRelation.To,
                TargetPrimaryKeyColumn: targetPrimaryKey.Name,
                TargetForeignKeyColumn: newRelation.Column,
                JunctionTableName: oldJunction.Name,
                ParentJunctionColumn: secondJunctionColumn.Name,
                ChildJunctionColumn: firstJunctionColumn.Name);
        }

        return null;
    }

    private OneToManyToManyToManyCopyMapping? ResolveOneToManyToManyToManyMapping(
        ModelJson oldModel,
        ModelJson newModel,
        RelationDef oldRelation,
        RelationDef newRelation)
    {
        if (oldRelation.Type != RelationType.OneToMany || newRelation.Type != RelationType.ManyToMany)
            return null;

        if (string.IsNullOrWhiteSpace(oldRelation.Column))
            return null;

        var sourceTable = FindTable(oldModel, oldRelation.To);
        var sourcePrimaryKey = sourceTable.Columns.FirstOrDefault(column => column.PrimaryKey);
        if (sourcePrimaryKey is null)
            return null;

        var newJunction = _relationResolver.BuildJunctionTable(newRelation);
        if (newJunction.Columns.Count < 2)
            return null;

        var firstJunctionColumn = newJunction.Columns[0];
        var secondJunctionColumn = newJunction.Columns[1];
        var sourceForeignKeyColumn = oldRelation.Column;

        if (string.Equals(firstJunctionColumn.References, oldRelation.From, StringComparison.OrdinalIgnoreCase)
            && string.Equals(secondJunctionColumn.References, oldRelation.To, StringComparison.OrdinalIgnoreCase))
        {
            return new OneToManyToManyToManyCopyMapping(
                SourceTableName: oldRelation.To,
                SourcePrimaryKeyColumn: sourcePrimaryKey.Name,
                SourceForeignKeyColumn: sourceForeignKeyColumn,
                JunctionTableName: newJunction.Name,
                ParentJunctionColumn: firstJunctionColumn.Name,
                ChildJunctionColumn: secondJunctionColumn.Name);
        }

        if (string.Equals(firstJunctionColumn.References, oldRelation.To, StringComparison.OrdinalIgnoreCase)
            && string.Equals(secondJunctionColumn.References, oldRelation.From, StringComparison.OrdinalIgnoreCase))
        {
            return new OneToManyToManyToManyCopyMapping(
                SourceTableName: oldRelation.To,
                SourcePrimaryKeyColumn: sourcePrimaryKey.Name,
                SourceForeignKeyColumn: sourceForeignKeyColumn,
                JunctionTableName: newJunction.Name,
                ParentJunctionColumn: secondJunctionColumn.Name,
                ChildJunctionColumn: firstJunctionColumn.Name);
        }

        return null;
    }

    private static bool JunctionTableEquals(TableDef left, TableDef right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase))
            return false;
        if (left.Columns.Count != right.Columns.Count)
            return false;

        for (var i = 0; i < left.Columns.Count; i++)
        {
            var leftColumn = left.Columns[i];
            var rightColumn = right.Columns[i];
            if (!string.Equals(leftColumn.Name, rightColumn.Name, StringComparison.OrdinalIgnoreCase)
                || leftColumn.Type != rightColumn.Type
                || !string.Equals(leftColumn.References, rightColumn.References, StringComparison.OrdinalIgnoreCase)
                || leftColumn.PrimaryKey != rightColumn.PrimaryKey
                || leftColumn.Nullable != rightColumn.Nullable)
            {
                return false;
            }
        }

        return true;
    }
}
