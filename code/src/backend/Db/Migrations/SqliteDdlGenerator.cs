using Repere.Api.Db.Schema;
using static Repere.Api.Db.Migrations.DdlHelpers;

namespace Repere.Api.Db.Migrations;

/// <summary>
/// Emits SQLite DDL for an ordered list of schema changes. SQLite cannot alter most
/// column properties in place, so affected tables are rebuilt through a temp table
/// (create, copy, drop, rename), and relation conversions touching a rebuilt table are
/// staged through temp tables before the rebuild.
/// </summary>
internal sealed class SqliteDdlGenerator
{
    private readonly RelationConversionPlanner _relations;

    public SqliteDdlGenerator(RelationConversionPlanner relations)
    {
        _relations = relations;
    }

    public void Generate(
        ModelJson oldModel,
        ModelJson newModel,
        IReadOnlyList<SchemaChange> changes,
        List<string> sqlUp,
        List<string> sqlDown)
    {
        var rebuildTables = GetSqliteRebuildTables(oldModel, newModel, changes);
        var relationConversions = _relations.GetSqliteOneToManyToManyToManyConversions(oldModel, newModel, changes);
        var reverseRelationConversions = _relations.GetSqliteManyToManyToOneToManyConversions(oldModel, newModel, changes);

        foreach (var conversion in relationConversions.Values)
        {
            Append(
                sqlUp,
                sqlDown,
                $"""
                CREATE TEMP TABLE {Quote(conversion.TempTableName)} AS
                SELECT {Quote(conversion.Mapping.SourceForeignKeyColumn)} AS "parent_id", {Quote(conversion.Mapping.SourcePrimaryKeyColumn)} AS "child_id"
                FROM {Quote(conversion.Mapping.SourceTableName)}
                WHERE {Quote(conversion.Mapping.SourceForeignKeyColumn)} IS NOT NULL;
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
                    SELECT {Quote(conversion.Mapping.ParentJunctionColumn)} AS "parent_id", {Quote(conversion.Mapping.ChildJunctionColumn)} AS "child_id"
                    FROM {Quote(conversion.Mapping.JunctionTableName)};
                    """,
                    $"DROP TABLE {Quote(conversion.Mapping.JunctionTableName)};"
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
                    string.Equals(conversion.Mapping.JunctionTableName, change.TableName, StringComparison.OrdinalIgnoreCase)))
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
                _relations.AppendSqliteOneToManyToManyToManyConversion(change, oldModel, newModel, conversion, sqlUp, sqlDown);
                continue;
            }

            if (change.ChangeType == SchemaChangeType.AlterRelation
                && change.RelationName is not null
                && reverseRelationConversions.TryGetValue(change.RelationName, out var reverseConversion))
            {
                _relations.AppendSqliteManyToManyToOneToManyConversion(change, oldModel, newModel, reverseConversion, sqlUp, sqlDown);
                continue;
            }

            switch (change.ChangeType)
            {
                case SchemaChangeType.AddColumn:
                    AppendAddColumnSql(newModel, change, DatabaseProvider.Sqlite, sqlUp, sqlDown);
                    break;

                case SchemaChangeType.AddIndex:
                    AppendAddIndexSql(newModel, change, sqlUp, sqlDown);
                    break;

                case SchemaChangeType.DropIndex:
                    AppendDropIndexSql(oldModel, change, sqlUp, sqlDown);
                    break;

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
                    AppendAlterIndexSql(oldModel, newModel, change, sqlUp, sqlDown);
                    break;

                case SchemaChangeType.DropTable:
                {
                    if (reverseRelationConversions.Values.Any(conversion =>
                        string.Equals(conversion.Mapping.JunctionTableName, change.TableName, StringComparison.OrdinalIgnoreCase)))
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
                    _relations.AppendRelationSql(change, oldModel, newModel, DatabaseProvider.Sqlite, sqlUp, sqlDown);
                    break;
            }
        }
    }

    private static IReadOnlyList<string> BuildSqliteRebuildStatements(TableDef oldTable, TableDef newTable, ModelJson model)
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

    private static string BuildSqliteSelectExpression(TableDef oldTable, ColumnDef newColumn, ModelJson model)
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

    private static HashSet<string> GetSqliteRebuildTables(
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
}
