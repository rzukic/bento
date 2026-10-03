using Bento.Api.Db.Schema;
using static Bento.Api.Db.Migrations.DdlHelpers;

namespace Bento.Api.Db.Migrations;

/// <summary>
/// Emits Postgres DDL for an ordered list of schema changes. Postgres supports every
/// change in place through ALTER TABLE, so no table rebuilds are needed.
/// </summary>
internal sealed class PostgresDdlGenerator
{
    private readonly RelationConversionPlanner _relations;

    public PostgresDdlGenerator(RelationConversionPlanner relations)
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
                    AppendAddColumnSql(newModel, change, DatabaseProvider.Postgres, sqlUp, sqlDown);
                    break;

                case SchemaChangeType.DropColumn:
                {
                    var table = FindTable(oldModel, change.TableName);
                    var column = FindColumn(table, change.ColumnName!);
                    var downStatements = new List<string>
                    {
                        $"ALTER TABLE {Quote(table.Name)} ADD COLUMN {BuildAddColumnDefinition(column, DatabaseProvider.Postgres, oldModel)};"
                    };
                    downStatements.AddRange(BuildUniqueSupportSql(table.Name, column, true, DatabaseProvider.Postgres));

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
                            ? BuildDisableUniqueSql(change.TableName, newColumn.Name, DatabaseProvider.Postgres)
                            : BuildUniqueSupportSql(change.TableName, newColumn, true, DatabaseProvider.Postgres),
                        oldUnique
                            ? BuildUniqueSupportSql(change.TableName, oldColumn, true, DatabaseProvider.Postgres)
                            : BuildDisableUniqueSql(change.TableName, oldColumn.Name, DatabaseProvider.Postgres));
                    break;
                }

                case SchemaChangeType.AddIndex:
                    AppendAddIndexSql(newModel, change, sqlUp, sqlDown);
                    break;

                case SchemaChangeType.DropIndex:
                    AppendDropIndexSql(oldModel, change, sqlUp, sqlDown);
                    break;

                case SchemaChangeType.RenameIndex:
                    Append(sqlUp, sqlDown,
                        $"ALTER INDEX {Quote(change.OldValue!.ToString()!)} RENAME TO {Quote(change.NewValue!.ToString()!)};",
                        $"ALTER INDEX {Quote(change.NewValue!.ToString()!)} RENAME TO {Quote(change.OldValue!.ToString()!)};");
                    break;

                case SchemaChangeType.AlterIndex:
                    AppendAlterIndexSql(oldModel, newModel, change, sqlUp, sqlDown);
                    break;

                case SchemaChangeType.AddRelation:
                case SchemaChangeType.DropRelation:
                case SchemaChangeType.AlterRelation:
                    _relations.AppendRelationSql(change, oldModel, newModel, DatabaseProvider.Postgres, sqlUp, sqlDown);
                    break;
            }
        }
    }
}
