using System.Text.Json;
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

public class MigrationGenerator
{
    private readonly DatabaseProvider _provider;
    private readonly RelationResolver _relationResolver;

    public MigrationGenerator(DatabaseConfig config, RelationResolver? relationResolver = null)
    {
        _provider = config.Provider;
        _relationResolver = relationResolver ?? new RelationResolver();
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

        var orderedChanges = OrderChangesForGeneration(oldModel, newModel, changes);

        if (_provider == DatabaseProvider.Postgres)
            GeneratePostgres(oldModel, newModel, orderedChanges, sqlUp, sqlDown);
        else
            GenerateSqlite(oldModel, newModel, orderedChanges, sqlUp, sqlDown);

        return new MigrationPlan
        {
            SqlUp = sqlUp,
            SqlDown = sqlDown,
            IsDestructive = changes.Any(c => c.IsDestructive),
            Changes = changes
        };
    }

    private void GeneratePostgres(
        ModelJson oldModel,
        ModelJson newModel,
        IReadOnlyList<SchemaChange> changes,
        List<string> sqlUp,
        List<string> sqlDown)
    {
        foreach (var change in changes)
        {
            switch (change.ChangeType)
            {
                case SchemaChangeType.RenameTable:
                    Append(sqlUp, sqlDown,
                        $"ALTER TABLE {Quote(change.PreviousTableName!)} RENAME TO {Quote(change.TableName)};",
                        $"ALTER TABLE {Quote(change.TableName)} RENAME TO {Quote(change.PreviousTableName!)};");
                    break;

                case SchemaChangeType.AddTable:
                {
                    var table = FindTable(newModel, change.TableName);
                    var up = new List<string> { BuildCreateTableSql(table, DatabaseProvider.Postgres, newModel) };
                    up.AddRange(BuildCreateIndexesSql(table));

                    var down = new[] { $"DROP TABLE {Quote(table.Name)};" };
                    Append(sqlUp, sqlDown, up, down);
                    break;
                }

                case SchemaChangeType.DropTable:
                {
                    var table = FindTable(oldModel, change.TableName);
                    var down = new List<string> { BuildCreateTableSql(table, DatabaseProvider.Postgres, oldModel) };
                    down.AddRange(BuildCreateIndexesSql(table));

                    Append(sqlUp, sqlDown,
                        $"DROP TABLE {Quote(table.Name)};",
                        down);
                    break;
                }

                case SchemaChangeType.AddColumn:
                {
                    var table = FindTable(newModel, change.TableName);
                    var column = FindColumn(table, change.ColumnName!);
                    var upStatements = new List<string>
                    {
                        $"ALTER TABLE {Quote(table.Name)} ADD COLUMN {BuildPostgresAddColumnDefinition(column, newModel)};"
                    };
                    upStatements.AddRange(BuildUniqueSupportSql(table.Name, column, true));

                    var downStatements = new List<string>();
                    downStatements.AddRange(BuildUniqueSupportSql(table.Name, column, false));
                    downStatements.Add($"ALTER TABLE {Quote(table.Name)} DROP COLUMN {Quote(column.Name)};");

                    Append(sqlUp, sqlDown, upStatements, downStatements);
                    break;
                }

                case SchemaChangeType.DropColumn:
                {
                    var table = FindTable(oldModel, change.TableName);
                    var column = FindColumn(table, change.ColumnName!);
                    var downStatements = new List<string>
                    {
                        $"ALTER TABLE {Quote(table.Name)} ADD COLUMN {BuildPostgresAddColumnDefinition(column, oldModel)};"
                    };
                    downStatements.AddRange(BuildUniqueSupportSql(table.Name, column, true));

                    Append(sqlUp, sqlDown,
                        $"ALTER TABLE {Quote(table.Name)} DROP COLUMN {Quote(column.Name)};",
                        downStatements);
                    break;
                }

                case SchemaChangeType.RenameColumn:
                    Append(sqlUp, sqlDown,
                        $"ALTER TABLE {Quote(change.TableName)} RENAME COLUMN {Quote(change.PreviousColumnName!)} TO {Quote(change.ColumnName!)};",
                        $"ALTER TABLE {Quote(change.TableName)} RENAME COLUMN {Quote(change.ColumnName!)} TO {Quote(change.PreviousColumnName!)};");
                    break;

                case SchemaChangeType.AlterColumnType:
                {
                    var oldColumn = FindColumnForChange(oldModel, change, useOld: true);
                    var newColumn = FindColumnForChange(newModel, change, useOld: false);

                    Append(sqlUp, sqlDown,
                        $"ALTER TABLE {Quote(change.TableName)} ALTER COLUMN {Quote(newColumn.Name)} TYPE {GetColumnDdlType(newColumn, DatabaseProvider.Postgres, newModel)} USING CAST({Quote(newColumn.Name)} AS {GetColumnDdlType(newColumn, DatabaseProvider.Postgres, newModel)});",
                        $"ALTER TABLE {Quote(change.TableName)} ALTER COLUMN {Quote(newColumn.Name)} TYPE {GetColumnDdlType(oldColumn, DatabaseProvider.Postgres, oldModel)} USING CAST({Quote(newColumn.Name)} AS {GetColumnDdlType(oldColumn, DatabaseProvider.Postgres, oldModel)});");
                    break;
                }

                case SchemaChangeType.AlterColumnNullability:
                {
                    var newColumn = FindColumnForChange(newModel, change, useOld: false);
                    var oldNullable = (bool)change.OldValue!;

                    Append(sqlUp, sqlDown,
                        $"ALTER TABLE {Quote(change.TableName)} ALTER COLUMN {Quote(newColumn.Name)} {(newColumn.Nullable ? "DROP" : "SET")} NOT NULL;",
                        $"ALTER TABLE {Quote(change.TableName)} ALTER COLUMN {Quote(newColumn.Name)} {(oldNullable ? "DROP" : "SET")} NOT NULL;");
                    break;
                }

                case SchemaChangeType.AlterColumnDefault:
                {
                    var oldColumn = FindColumnForChange(oldModel, change, useOld: true);
                    var newColumn = FindColumnForChange(newModel, change, useOld: false);

                    Append(sqlUp, sqlDown,
                        BuildAlterDefaultSql(change.TableName, newColumn, DatabaseProvider.Postgres),
                        BuildAlterDefaultSql(change.TableName, oldColumn, DatabaseProvider.Postgres));
                    break;
                }

                case SchemaChangeType.AlterColumnUnique:
                {
                    var oldUnique = (bool)change.OldValue!;
                    var newColumn = FindColumnForChange(newModel, change, useOld: false);
                    var oldColumn = FindColumnForChange(oldModel, change, useOld: true);

                    Append(sqlUp, sqlDown,
                        oldUnique
                            ? BuildDisableUniqueSql(change.TableName, newColumn.Name)
                            : BuildUniqueSupportSql(change.TableName, newColumn, true),
                        oldUnique
                            ? BuildUniqueSupportSql(change.TableName, oldColumn, true)
                            : BuildDisableUniqueSql(change.TableName, oldColumn.Name));
                    break;
                }

                case SchemaChangeType.AddIndex:
                {
                    var table = FindTable(newModel, change.TableName);
                    var index = FindIndex(table, change.IndexName!);
                    Append(sqlUp, sqlDown,
                        BuildCreateIndexSql(table.Name, index),
                        BuildDropIndexSql(index.Name));
                    break;
                }

                case SchemaChangeType.DropIndex:
                {
                    var table = FindTable(oldModel, change.TableName);
                    var index = FindIndex(table, change.IndexName!);
                    Append(sqlUp, sqlDown,
                        BuildDropIndexSql(index.Name),
                        BuildCreateIndexSql(table.Name, index));
                    break;
                }

                case SchemaChangeType.RenameIndex:
                    Append(sqlUp, sqlDown,
                        $"ALTER INDEX {Quote(change.OldValue!.ToString()!)} RENAME TO {Quote(change.NewValue!.ToString()!)};",
                        $"ALTER INDEX {Quote(change.NewValue!.ToString()!)} RENAME TO {Quote(change.OldValue!.ToString()!)};");
                    break;

                case SchemaChangeType.AlterIndex:
                {
                    var oldTable = FindTable(oldModel, change.PreviousTableName ?? change.TableName);
                    var newTable = FindTable(newModel, change.TableName);
                    var oldIndex = FindIndexByNameOrRename(oldTable, change.IndexName!, change.OldValue as IndexDef);
                    var newIndex = FindIndex(newTable, change.IndexName!);

                    Append(sqlUp, sqlDown,
                        new[]
                        {
                            BuildDropIndexSql(oldIndex.Name),
                            BuildCreateIndexSql(newTable.Name, newIndex)
                        },
                        new[]
                        {
                            BuildDropIndexSql(newIndex.Name),
                            BuildCreateIndexSql(oldTable.Name, oldIndex)
                        });
                    break;
                }

                case SchemaChangeType.AddRelation:
                case SchemaChangeType.DropRelation:
                case SchemaChangeType.AlterRelation:
                    AppendRelationSql(change, oldModel, newModel, DatabaseProvider.Postgres, sqlUp, sqlDown);
                    break;
            }
        }
    }

    private void GenerateSqlite(
        ModelJson oldModel,
        ModelJson newModel,
        IReadOnlyList<SchemaChange> changes,
        List<string> sqlUp,
        List<string> sqlDown)
    {
        var rebuildTables = GetSqliteRebuildTables(oldModel, newModel, changes);
        var relationConversions = GetSqliteOneToManyToManyToManyConversions(oldModel, newModel, changes);
        var reverseRelationConversions = GetSqliteManyToManyToOneToManyConversions(oldModel, newModel, changes);

        foreach (var conversion in relationConversions.Values)
        {
            Append(
                sqlUp,
                sqlDown,
                $"""
                CREATE TEMP TABLE {Quote(conversion.TempTableName)} AS
                SELECT {Quote(conversion.SourceForeignKeyColumn)} AS "parent_id", {Quote(conversion.SourcePrimaryKeyColumn)} AS "child_id"
                FROM {Quote(conversion.SourceTableName)}
                WHERE {Quote(conversion.SourceForeignKeyColumn)} IS NOT NULL;
                """,
                "");
        }

        foreach (var conversion in reverseRelationConversions.Values)
        {
            Append(
                sqlUp,
                sqlDown,
                new[]
                {
                    $"""
                    CREATE TEMP TABLE {Quote(conversion.TempTableName)} AS
                    SELECT {Quote(conversion.ParentJunctionColumn)} AS "parent_id", {Quote(conversion.ChildJunctionColumn)} AS "child_id"
                    FROM {Quote(conversion.JunctionTableName)};
                    """,
                    $"DROP TABLE {Quote(conversion.JunctionTableName)};"
                },
                "");
        }

        foreach (var change in changes.Where(c => c.ChangeType == SchemaChangeType.RenameTable))
        {
            if (rebuildTables.Contains(change.TableName))
                continue;

            Append(sqlUp, sqlDown,
                $"ALTER TABLE {Quote(change.PreviousTableName!)} RENAME TO {Quote(change.TableName)};",
                $"ALTER TABLE {Quote(change.TableName)} RENAME TO {Quote(change.PreviousTableName!)};");
        }

        foreach (var change in changes.Where(c => c.ChangeType == SchemaChangeType.AddTable))
        {
            if (change.ChangeType == SchemaChangeType.AddTable
                && relationConversions.Values.Any(conversion =>
                    string.Equals(conversion.JunctionTableName, change.TableName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var table = FindTable(newModel, change.TableName);
            Append(sqlUp, sqlDown,
                new[] { BuildCreateTableSql(table, DatabaseProvider.Sqlite, newModel) }.Concat(BuildCreateIndexesSql(table)),
                $"DROP TABLE {Quote(table.Name)};");
        }

        foreach (var tableName in rebuildTables)
        {
            var oldTable = TryFindMatchedOldTable(oldModel, newModel, tableName);
            var newTable = FindTable(newModel, tableName);
            if (oldTable is null)
                continue;

            var up = BuildSqliteRebuildStatements(oldTable, newTable, newModel);
            var down = BuildSqliteRebuildStatements(newTable, oldTable, oldModel);
            Append(sqlUp, sqlDown, up, down);
        }

        foreach (var change in changes)
        {
            if (rebuildTables.Contains(change.TableName)
                && change.ChangeType is not (SchemaChangeType.AddRelation or SchemaChangeType.DropRelation or SchemaChangeType.AlterRelation))
                continue;

            if (change.ChangeType == SchemaChangeType.AlterRelation
                && change.RelationName is not null
                && relationConversions.TryGetValue(change.RelationName, out var conversion))
            {
                AppendSqliteOneToManyToManyToManyConversion(change, oldModel, newModel, conversion, sqlUp, sqlDown);
                continue;
            }

            if (change.ChangeType == SchemaChangeType.AlterRelation
                && change.RelationName is not null
                && reverseRelationConversions.TryGetValue(change.RelationName, out var reverseConversion))
            {
                AppendSqliteManyToManyToOneToManyConversion(change, oldModel, newModel, reverseConversion, sqlUp, sqlDown);
                continue;
            }

            switch (change.ChangeType)
            {
                case SchemaChangeType.AddColumn:
                {
                    var table = FindTable(newModel, change.TableName);
                    var column = FindColumn(table, change.ColumnName!);
                    var upStatements = new List<string>
                    {
                        $"ALTER TABLE {Quote(table.Name)} ADD COLUMN {BuildSqliteAddColumnDefinition(column, newModel)};"
                    };
                    upStatements.AddRange(BuildUniqueSupportSql(table.Name, column, true));

                    var downStatements = new List<string>();
                    downStatements.AddRange(BuildUniqueSupportSql(table.Name, column, false));
                    downStatements.Add($"ALTER TABLE {Quote(table.Name)} DROP COLUMN {Quote(column.Name)};");

                    Append(sqlUp, sqlDown, upStatements, downStatements);
                    break;
                }

                case SchemaChangeType.AddIndex:
                {
                    var table = FindTable(newModel, change.TableName);
                    var index = FindIndex(table, change.IndexName!);
                    Append(sqlUp, sqlDown,
                        BuildCreateIndexSql(table.Name, index),
                        BuildDropIndexSql(index.Name));
                    break;
                }

                case SchemaChangeType.DropIndex:
                {
                    var table = FindTable(oldModel, change.TableName);
                    var index = FindIndex(table, change.IndexName!);
                    Append(sqlUp, sqlDown,
                        BuildDropIndexSql(index.Name),
                        BuildCreateIndexSql(table.Name, index));
                    break;
                }

                case SchemaChangeType.RenameIndex:
                {
                    var oldName = change.OldValue!.ToString()!;
                    var newName = change.NewValue!.ToString()!;
                    var table = FindTable(newModel, change.TableName);
                    var newIndex = FindIndex(table, newName);
                    Append(sqlUp, sqlDown,
                        new[]
                        {
                            BuildDropIndexSql(oldName),
                            BuildCreateIndexSql(table.Name, newIndex)
                        },
                        new[]
                        {
                            BuildDropIndexSql(newName),
                            BuildCreateIndexSql(table.Name, newIndex with { Name = oldName })
                        });
                    break;
                }

                case SchemaChangeType.AlterIndex:
                {
                    var oldTable = FindTable(oldModel, change.PreviousTableName ?? change.TableName);
                    var newTable = FindTable(newModel, change.TableName);
                    var oldIndex = FindIndexByNameOrRename(oldTable, change.IndexName!, change.OldValue as IndexDef);
                    var newIndex = FindIndex(newTable, change.IndexName!);

                    Append(sqlUp, sqlDown,
                        new[]
                        {
                            BuildDropIndexSql(oldIndex.Name),
                            BuildCreateIndexSql(newTable.Name, newIndex)
                        },
                        new[]
                        {
                            BuildDropIndexSql(newIndex.Name),
                            BuildCreateIndexSql(oldTable.Name, oldIndex)
                        });
                    break;
                }

                case SchemaChangeType.DropTable:
                {
                    if (reverseRelationConversions.Values.Any(conversion =>
                        string.Equals(conversion.JunctionTableName, change.TableName, StringComparison.OrdinalIgnoreCase)))
                    {
                        break;
                    }

                    var table = FindTable(oldModel, change.TableName);
                    var down = new List<string> { BuildCreateTableSql(table, DatabaseProvider.Sqlite, oldModel) };
                    down.AddRange(BuildCreateIndexesSql(table));

                    Append(sqlUp, sqlDown, $"DROP TABLE {Quote(table.Name)};", down);
                    break;
                }

                case SchemaChangeType.AddRelation:
                case SchemaChangeType.DropRelation:
                case SchemaChangeType.AlterRelation:
                    AppendRelationSql(change, oldModel, newModel, DatabaseProvider.Sqlite, sqlUp, sqlDown);
                    break;
            }
        }
    }

    private Dictionary<string, SqliteOneToManyToManyToManyConversion> GetSqliteOneToManyToManyToManyConversions(
        ModelJson oldModel,
        ModelJson newModel,
        IReadOnlyList<SchemaChange> changes)
    {
        var conversions = new Dictionary<string, SqliteOneToManyToManyToManyConversion>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes.Where(change => change.ChangeType == SchemaChangeType.AlterRelation))
        {
            var oldRelation = change.OldValue as RelationDef;
            var newRelation = change.NewValue as RelationDef;
            if (oldRelation is null || newRelation is null)
                continue;

            var mapping = ResolveOneToManyToManyToManyMapping(oldModel, newModel, oldRelation, newRelation);
            if (mapping is null)
                continue;

            conversions[oldRelation.Name] = new SqliteOneToManyToManyToManyConversion(
                RelationName: oldRelation.Name,
                TempTableName: $"__relcopy_{oldRelation.Name}",
                SourceTableName: mapping.SourceTableName,
                SourcePrimaryKeyColumn: mapping.SourcePrimaryKeyColumn,
                SourceForeignKeyColumn: mapping.SourceForeignKeyColumn,
                JunctionTableName: mapping.JunctionTableName,
                ParentJunctionColumn: mapping.ParentJunctionColumn,
                ChildJunctionColumn: mapping.ChildJunctionColumn);
        }

        return conversions;
    }

    private Dictionary<string, SqliteManyToManyToOneToManyConversion> GetSqliteManyToManyToOneToManyConversions(
        ModelJson oldModel,
        ModelJson newModel,
        IReadOnlyList<SchemaChange> changes)
    {
        var conversions = new Dictionary<string, SqliteManyToManyToOneToManyConversion>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes.Where(change => change.ChangeType == SchemaChangeType.AlterRelation))
        {
            var oldRelation = change.OldValue as RelationDef;
            var newRelation = change.NewValue as RelationDef;
            if (oldRelation is null || newRelation is null)
                continue;

            var mapping = ResolveManyToManyToOneToManyMapping(oldModel, newModel, oldRelation, newRelation);
            if (mapping is null)
                continue;

            conversions[oldRelation.Name] = new SqliteManyToManyToOneToManyConversion(
                RelationName: oldRelation.Name,
                TempTableName: $"__relcopy_{oldRelation.Name}",
                TargetTableName: mapping.TargetTableName,
                TargetPrimaryKeyColumn: mapping.TargetPrimaryKeyColumn,
                TargetForeignKeyColumn: mapping.TargetForeignKeyColumn,
                JunctionTableName: mapping.JunctionTableName,
                ParentJunctionColumn: mapping.ParentJunctionColumn,
                ChildJunctionColumn: mapping.ChildJunctionColumn);
        }

        return conversions;
    }

    private void AppendSqliteOneToManyToManyToManyConversion(
        SchemaChange change,
        ModelJson oldModel,
        ModelJson newModel,
        SqliteOneToManyToManyToManyConversion conversion,
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
            INSERT INTO {Quote(conversion.JunctionTableName)} ({Quote(conversion.ParentJunctionColumn)}, {Quote(conversion.ChildJunctionColumn)})
            SELECT "parent_id", "child_id"
            FROM {Quote(conversion.TempTableName)};
            """,
            $"DROP TABLE {Quote(conversion.TempTableName)};"
        };

        var downStatements = new List<string>(
            BuildManyToManyToOneToManyCopyStatements(oldModel, newModel, oldRelation, newRelation, DatabaseProvider.Sqlite))
        {
            $"DROP TABLE {Quote(conversion.JunctionTableName)};"
        };

        Append(sqlUp, sqlDown, upStatements, downStatements);
    }

    private void AppendSqliteManyToManyToOneToManyConversion(
        SchemaChange change,
        ModelJson oldModel,
        ModelJson newModel,
        SqliteManyToManyToOneToManyConversion conversion,
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
            UPDATE {Quote(conversion.TargetTableName)}
            SET {Quote(conversion.TargetForeignKeyColumn)} = (
                SELECT temp."parent_id"
                FROM {Quote(conversion.TempTableName)} AS temp
                WHERE temp."child_id" = {Quote(conversion.TargetTableName)}.{Quote(conversion.TargetPrimaryKeyColumn)}
                LIMIT 1
            )
            WHERE EXISTS (
                SELECT 1
                FROM {Quote(conversion.TempTableName)} AS temp
                WHERE temp."child_id" = {Quote(conversion.TargetTableName)}.{Quote(conversion.TargetPrimaryKeyColumn)}
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

    private void AppendRelationSql(
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

    private sealed record OneToManyToManyToManyCopyMapping(
        string SourceTableName,
        string SourcePrimaryKeyColumn,
        string SourceForeignKeyColumn,
        string JunctionTableName,
        string ParentJunctionColumn,
        string ChildJunctionColumn);

    private sealed record ManyToManyToOneToManyCopyMapping(
        string TargetTableName,
        string TargetPrimaryKeyColumn,
        string TargetForeignKeyColumn,
        string JunctionTableName,
        string ParentJunctionColumn,
        string ChildJunctionColumn);

    private sealed record SqliteOneToManyToManyToManyConversion(
        string RelationName,
        string TempTableName,
        string SourceTableName,
        string SourcePrimaryKeyColumn,
        string SourceForeignKeyColumn,
        string JunctionTableName,
        string ParentJunctionColumn,
        string ChildJunctionColumn);

    private sealed record SqliteManyToManyToOneToManyConversion(
        string RelationName,
        string TempTableName,
        string TargetTableName,
        string TargetPrimaryKeyColumn,
        string TargetForeignKeyColumn,
        string JunctionTableName,
        string ParentJunctionColumn,
        string ChildJunctionColumn);

    private IReadOnlyList<string> BuildSqliteRebuildStatements(TableDef oldTable, TableDef newTable, ModelJson model)
    {
        var tempTable = $"__tmp_{newTable.Name}";
        var statements = new List<string>
        {
            BuildCreateTableSql(newTable with { Name = tempTable }, DatabaseProvider.Sqlite, model)
        };

        var insertColumns = new List<string>();
        var selectExpressions = new List<string>();

        foreach (var newColumn in newTable.Columns.Where(c => ColumnTypes.HasPhysicalColumn(c.Type)))
        {
            insertColumns.Add(Quote(newColumn.Name));
            selectExpressions.Add(BuildSqliteSelectExpression(oldTable, newColumn, model));
        }

        statements.Add($"""
            INSERT INTO {Quote(tempTable)} ({string.Join(", ", insertColumns)})
            SELECT {string.Join(", ", selectExpressions)}
            FROM {Quote(oldTable.Name)};
            """);

        statements.Add($"DROP TABLE {Quote(oldTable.Name)};");
        statements.Add($"ALTER TABLE {Quote(tempTable)} RENAME TO {Quote(newTable.Name)};");
        statements.AddRange(BuildCreateIndexesSql(newTable));

        return statements;
    }

    private string BuildSqliteSelectExpression(TableDef oldTable, ColumnDef newColumn, ModelJson model)
    {
        var oldColumn = FindMatchedOldColumn(oldTable, newColumn);
        if (oldColumn is not null)
        {
            var source = Quote(oldColumn.Name);
            return oldColumn.Type == newColumn.Type
                ? source
                : $"CAST({source} AS {GetColumnDdlType(newColumn, DatabaseProvider.Sqlite, model)}) AS {Quote(newColumn.Name)}";
        }

        if (newColumn.DefaultValue.HasValue)
            return $"{FormatSqlLiteral(newColumn.DefaultValue.Value, newColumn.Type, DatabaseProvider.Sqlite)} AS {Quote(newColumn.Name)}";

        if (newColumn.PrimaryKey)
            return $"NULL AS {Quote(newColumn.Name)}";

        if (!newColumn.Nullable)
            return $"{ColumnTypes.DefaultLiteral(newColumn.Type)} AS {Quote(newColumn.Name)}";

        return $"NULL AS {Quote(newColumn.Name)}";
    }

    private HashSet<string> GetSqliteRebuildTables(
        ModelJson oldModel,
        ModelJson newModel,
        IReadOnlyList<SchemaChange> changes)
    {
        var rebuild = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in changes)
        {
            switch (change.ChangeType)
            {
                case SchemaChangeType.DropColumn:
                case SchemaChangeType.RenameColumn:
                case SchemaChangeType.AlterColumnType:
                case SchemaChangeType.AlterColumnNullability:
                case SchemaChangeType.AlterColumnDefault:
                case SchemaChangeType.AlterColumnUnique:
                    rebuild.Add(change.TableName);
                    break;

                case SchemaChangeType.AddColumn:
                {
                    var table = FindTable(newModel, change.TableName);
                    var column = FindColumn(table, change.ColumnName!);
                    var hasDefault = column.DefaultValue.HasValue;
                    if (column.Unique || column.PrimaryKey || !string.IsNullOrWhiteSpace(column.References) || (!column.Nullable && !hasDefault))
                        rebuild.Add(change.TableName);
                    break;
                }

                case SchemaChangeType.AlterRelation:
                case SchemaChangeType.DropRelation:
                case SchemaChangeType.AddRelation:
                {
                    var relation = (change.NewValue as RelationDef) ?? (change.OldValue as RelationDef);
                    if (relation?.Type == RelationType.OneToMany)
                        rebuild.Add(change.TableName);
                    break;
                }
            }
        }

        return rebuild;
    }

    private string BuildCreateTableSql(TableDef table, DatabaseProvider provider, ModelJson model)
    {
        var definitions = new List<string>();
        var physicalColumns = table.Columns.Where(c => ColumnTypes.HasPhysicalColumn(c.Type)).ToList();
        var primaryKeyColumns = physicalColumns.Where(c => c.PrimaryKey).Select(c => c.Name).ToList();
        EnsureSupportedPostgresPrimaryKey(table, provider, model, physicalColumns, primaryKeyColumns);
        var useCompositePrimaryKey = primaryKeyColumns.Count > 1;
        var useSqliteInlineIntegerPrimaryKey = provider == DatabaseProvider.Sqlite
            && primaryKeyColumns.Count == 1
            && physicalColumns.First(c => c.PrimaryKey).Type == ColumnType.Integer;
        var usePostgresInlineIntegerPrimaryKey = provider == DatabaseProvider.Postgres
            && primaryKeyColumns.Count == 1
            && physicalColumns.First(c => c.PrimaryKey).Type == ColumnType.Integer;

        foreach (var column in physicalColumns)
        {
            definitions.Add(BuildCreateTableColumnDefinition(
                column,
                provider,
                model,
                useCompositePrimaryKey,
                useSqliteInlineIntegerPrimaryKey,
                usePostgresInlineIntegerPrimaryKey));
        }

        if (useCompositePrimaryKey)
            definitions.Add($"PRIMARY KEY ({string.Join(", ", primaryKeyColumns.Select(Quote))})");

        return $"""
            CREATE TABLE {Quote(table.Name)} (
                {string.Join(",\n    ", definitions)}
            );
            """;
    }

    private static void EnsureSupportedPostgresPrimaryKey(
        TableDef table,
        DatabaseProvider provider,
        ModelJson model,
        IReadOnlyList<ColumnDef> physicalColumns,
        IReadOnlyList<string> primaryKeyColumns)
    {
        if (provider != DatabaseProvider.Postgres)
            return;

        if (IsInternalJunctionTable(table) || IsExplicitManyToManyJunctionTable(model, table.Name))
            return;

        if (primaryKeyColumns.Count != 1)
        {
            throw new InvalidOperationException(
                $"Postgres migrations require exactly one integer or uuid primary key column. Table '{table.Name}' has {primaryKeyColumns.Count} primary key columns.");
        }

        var primaryKeyColumn = physicalColumns.First(c => c.PrimaryKey);
        if (primaryKeyColumn.Type is not (ColumnType.Integer or ColumnType.Uuid))
        {
            throw new InvalidOperationException(
                $"Postgres migrations require the primary key column to use the integer or uuid type so generated inserts remain supported. Table '{table.Name}' uses '{primaryKeyColumn.Type}'.");
        }
    }

    private static bool IsInternalJunctionTable(TableDef table) =>
        table.Name.StartsWith("_jt_", StringComparison.Ordinal);

    private static bool IsExplicitManyToManyJunctionTable(ModelJson model, string tableName) =>
        model.Relations.Any(relation =>
            relation.Type == RelationType.ManyToMany &&
            string.Equals(relation.JunctionTable, tableName, StringComparison.OrdinalIgnoreCase));

    private static string GetColumnDdlType(ColumnDef column, DatabaseProvider provider, ModelJson model)
    {
        if (column.Type != ColumnType.Reference)
            return ColumnTypes.ToDdl(column.Type, provider);

        var referencedPrimaryKeyType = model.Tables
            .FirstOrDefault(t => string.Equals(t.Name, column.References, StringComparison.OrdinalIgnoreCase))
            ?.Columns
            .FirstOrDefault(c => c.PrimaryKey)
            ?.Type;

        return referencedPrimaryKeyType == ColumnType.Uuid
            ? ColumnTypes.ToDdl(ColumnType.Uuid, provider)
            : ColumnTypes.ToDdl(ColumnType.Reference, provider);
    }

    private string BuildCreateTableColumnDefinition(
        ColumnDef column,
        DatabaseProvider provider,
        ModelJson model,
        bool useCompositePrimaryKey,
        bool useSqliteInlineIntegerPrimaryKey,
        bool usePostgresInlineIntegerPrimaryKey)
    {
        if (useSqliteInlineIntegerPrimaryKey && column.PrimaryKey && column.Type == ColumnType.Integer)
        {
            return $"{Quote(column.Name)} INTEGER PRIMARY KEY AUTOINCREMENT";
        }

        if (usePostgresInlineIntegerPrimaryKey && column.PrimaryKey && column.Type == ColumnType.Integer)
        {
            return $"{Quote(column.Name)} BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY";
        }

        if (column.PrimaryKey && column.Type == ColumnType.Uuid)
        {
            return provider == DatabaseProvider.Postgres
                ? $"{Quote(column.Name)} UUID DEFAULT gen_random_uuid() PRIMARY KEY"
                : $"{Quote(column.Name)} TEXT NOT NULL PRIMARY KEY";
        }

        var parts = new List<string>
        {
            Quote(column.Name),
            GetColumnDdlType(column, provider, model)
        };

        if (!column.Nullable || column.PrimaryKey)
            parts.Add("NOT NULL");
        if (column.PrimaryKey && !useCompositePrimaryKey)
            parts.Add("PRIMARY KEY");
        if (column.Unique && !column.PrimaryKey)
            parts.Add("UNIQUE");
        var defaultSql = BuildDefaultSql(column, provider);
        if (!string.IsNullOrWhiteSpace(defaultSql))
            parts.Add($"DEFAULT {defaultSql}");
        if (!string.IsNullOrWhiteSpace(column.References))
            parts.Add($"REFERENCES {Quote(column.References)}({Quote("id")})");

        return string.Join(" ", parts);
    }

    private string BuildPostgresAddColumnDefinition(ColumnDef column, ModelJson model)
    {
        var parts = new List<string> { BuildBaseAddColumnDefinition(column, DatabaseProvider.Postgres, model) };
        return string.Join(" ", parts);
    }

    private string BuildSqliteAddColumnDefinition(ColumnDef column, ModelJson model)
    {
        return BuildBaseAddColumnDefinition(column, DatabaseProvider.Sqlite, model);
    }

    private string BuildBaseAddColumnDefinition(ColumnDef column, DatabaseProvider provider, ModelJson model)
    {
        var parts = new List<string>
        {
            Quote(column.Name),
            GetColumnDdlType(column, provider, model)
        };

        if (!column.Nullable)
            parts.Add("NOT NULL");
        var defaultSql = BuildDefaultSql(column, provider);
        if (!string.IsNullOrWhiteSpace(defaultSql))
            parts.Add($"DEFAULT {defaultSql}");
        if (!string.IsNullOrWhiteSpace(column.References))
            parts.Add($"REFERENCES {Quote(column.References)}({Quote("id")})");

        return string.Join(" ", parts);
    }

    private IEnumerable<string> BuildCreateIndexesSql(TableDef table)
    {
        return table.Indexes.Select(index => BuildCreateIndexSql(table.Name, index));
    }

    private string BuildCreateIndexSql(string tableName, IndexDef index)
    {
        var unique = index.Unique ? "UNIQUE " : string.Empty;
        var columns = string.Join(", ", index.Columns.Select(Quote));
        return $"CREATE {unique}INDEX {Quote(index.Name)} ON {Quote(tableName)} ({columns});";
    }

    private string BuildDropIndexSql(string indexName) => $"DROP INDEX {Quote(indexName)};";

    private IEnumerable<string> BuildUniqueSupportSql(string tableName, ColumnDef column, bool enable)
    {
        if (!column.Unique || column.PrimaryKey)
            return [];

        var indexName = UniqueIndexName(tableName, column.Name);
        return enable
            ? [$"CREATE UNIQUE INDEX {Quote(indexName)} ON {Quote(tableName)} ({Quote(column.Name)});"]
            : BuildDisableUniqueSql(tableName, column.Name);
    }

    /// <summary>
    /// A unique column is backed either by the named index this generator creates
    /// for ADD COLUMN / enable-unique changes, or by the inline UNIQUE constraint
    /// that CREATE TABLE emits (Postgres names it <c>&lt;table&gt;_&lt;column&gt;_key</c>).
    /// Disabling unique must drop whichever one exists, otherwise turning unique
    /// off for a column created with the table fails with "index does not exist".
    /// </summary>
    private IEnumerable<string> BuildDisableUniqueSql(string tableName, string columnName)
    {
        var statements = new List<string>
        {
            $"DROP INDEX IF EXISTS {Quote(UniqueIndexName(tableName, columnName))};"
        };

        if (_provider == DatabaseProvider.Postgres)
        {
            statements.Add(
                $"ALTER TABLE {Quote(tableName)} DROP CONSTRAINT IF EXISTS {Quote(InlineUniqueConstraintName(tableName, columnName))};");
        }

        return statements;
    }

    /// <summary>Postgres' default name for an inline <c>UNIQUE</c> column constraint.</summary>
    private static string InlineUniqueConstraintName(string tableName, string columnName) => $"{tableName}_{columnName}_key";

    private string BuildAlterDefaultSql(string tableName, ColumnDef column, DatabaseProvider provider)
    {
        var defaultSql = BuildDefaultSql(column, provider);
        return !string.IsNullOrWhiteSpace(defaultSql)
            ? $"ALTER TABLE {Quote(tableName)} ALTER COLUMN {Quote(column.Name)} SET DEFAULT {defaultSql};"
            : $"ALTER TABLE {Quote(tableName)} ALTER COLUMN {Quote(column.Name)} DROP DEFAULT;";
    }

    private static string UniqueIndexName(string tableName, string columnName) => $"ux_{tableName}_{columnName}";

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string? BuildDefaultSql(ColumnDef column, DatabaseProvider provider)
    {
        if (!string.IsNullOrWhiteSpace(column.InternalDefaultSql))
            return column.InternalDefaultSql;

        return column.DefaultValue.HasValue
            ? FormatSqlLiteral(column.DefaultValue.Value, column.Type, provider)
            : null;
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

    private static string FormatSqlLiteral(JsonElement element, ColumnType columnType, DatabaseProvider provider)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => "NULL",
            JsonValueKind.String => $"'{element.GetString()!.Replace("'", "''")}'",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => provider == DatabaseProvider.Postgres ? "TRUE" : "1",
            JsonValueKind.False => provider == DatabaseProvider.Postgres ? "FALSE" : "0",
            JsonValueKind.Object or JsonValueKind.Array when provider == DatabaseProvider.Postgres => $"'{element.GetRawText().Replace("'", "''")}'::jsonb",
            JsonValueKind.Object or JsonValueKind.Array => $"'{element.GetRawText().Replace("'", "''")}'",
            _ => throw new InvalidOperationException($"Unsupported default JSON value kind '{element.ValueKind}' for {columnType}.")
        };
    }

    private static TableDef FindTable(ModelJson model, string tableName)
    {
        return model.Tables.First(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
    }

    private static TableDef? TryFindMatchedOldTable(ModelJson oldModel, ModelJson newModel, string newTableName)
    {
        var newTable = FindTable(newModel, newTableName);
        if (!string.IsNullOrWhiteSpace(newTable.RenamedFrom))
        {
            return oldModel.Tables.FirstOrDefault(t => string.Equals(t.Name, newTable.RenamedFrom, StringComparison.OrdinalIgnoreCase));
        }

        return oldModel.Tables.FirstOrDefault(t => string.Equals(t.Name, newTableName, StringComparison.OrdinalIgnoreCase));
    }

    private static ColumnDef FindColumn(TableDef table, string columnName)
    {
        return table.Columns.First(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
    }

    private static ColumnDef FindColumnForChange(ModelJson model, SchemaChange change, bool useOld)
    {
        var tableName = useOld ? change.PreviousTableName ?? change.TableName : change.TableName;
        var columnName = useOld ? change.PreviousColumnName ?? change.ColumnName! : change.ColumnName!;
        var table = FindTable(model, tableName);
        return FindColumn(table, columnName);
    }

    private static IndexDef FindIndex(TableDef table, string indexName)
    {
        return table.Indexes.First(i => string.Equals(i.Name, indexName, StringComparison.OrdinalIgnoreCase));
    }

    private static IndexDef FindIndexByNameOrRename(TableDef table, string indexName, IndexDef? fallback)
    {
        return table.Indexes.FirstOrDefault(i => string.Equals(i.Name, indexName, StringComparison.OrdinalIgnoreCase))
            ?? fallback
            ?? throw new InvalidOperationException($"Index '{indexName}' not found on table '{table.Name}'.");
    }

    private static ColumnDef? FindMatchedOldColumn(TableDef oldTable, ColumnDef newColumn)
    {
        return oldTable.Columns.FirstOrDefault(c => string.Equals(c.Name, newColumn.Name, StringComparison.OrdinalIgnoreCase))
            ?? (!string.IsNullOrWhiteSpace(newColumn.RenamedFrom)
                ? oldTable.Columns.FirstOrDefault(c => string.Equals(c.Name, newColumn.RenamedFrom, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? oldTable.Columns.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.RenamedFrom)
                && string.Equals(c.RenamedFrom, newColumn.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static void Append(List<string> sqlUp, List<string> sqlDown, string up, string down)
    {
        sqlUp.Add(up);
        sqlDown.Insert(0, down);
    }

    private static void Append(List<string> sqlUp, List<string> sqlDown, IEnumerable<string> up, IEnumerable<string> down)
    {
        var upList = up.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var downList = down.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        sqlUp.AddRange(upList);
        downList.Reverse();
        foreach (var statement in downList)
            sqlDown.Insert(0, statement);
    }

    private static void Append(List<string> sqlUp, List<string> sqlDown, IEnumerable<string> up, string down)
    {
        Append(sqlUp, sqlDown, up, new[] { down });
    }

    private static void Append(List<string> sqlUp, List<string> sqlDown, string up, IEnumerable<string> down)
    {
        Append(sqlUp, sqlDown, new[] { up }, down);
    }

    private IReadOnlyList<SchemaChange> OrderChangesForGeneration(
        ModelJson oldModel,
        ModelJson newModel,
        IReadOnlyList<SchemaChange> changes)
    {
        var orderedTableNames = _relationResolver.OrderTables(newModel)
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
