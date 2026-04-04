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
                    var up = new List<string> { BuildCreateTableSql(table, DatabaseProvider.Postgres) };
                    up.AddRange(BuildCreateIndexesSql(table));

                    var down = new[] { $"DROP TABLE {Quote(table.Name)};" };
                    Append(sqlUp, sqlDown, up, down);
                    break;
                }

                case SchemaChangeType.DropTable:
                {
                    var table = FindTable(oldModel, change.TableName);
                    var down = new List<string> { BuildCreateTableSql(table, DatabaseProvider.Postgres) };
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
                        $"ALTER TABLE {Quote(table.Name)} ADD COLUMN {BuildPostgresAddColumnDefinition(column)};"
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
                        $"ALTER TABLE {Quote(table.Name)} ADD COLUMN {BuildPostgresAddColumnDefinition(column)};"
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
                        $"ALTER TABLE {Quote(change.TableName)} ALTER COLUMN {Quote(newColumn.Name)} TYPE {ColumnTypes.ToPostgres(newColumn.Type)} USING CAST({Quote(newColumn.Name)} AS {ColumnTypes.ToPostgres(newColumn.Type)});",
                        $"ALTER TABLE {Quote(change.TableName)} ALTER COLUMN {Quote(newColumn.Name)} TYPE {ColumnTypes.ToPostgres(oldColumn.Type)} USING CAST({Quote(newColumn.Name)} AS {ColumnTypes.ToPostgres(oldColumn.Type)});");
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
            var table = FindTable(newModel, change.TableName);
            Append(sqlUp, sqlDown,
                new[] { BuildCreateTableSql(table, DatabaseProvider.Sqlite) }.Concat(BuildCreateIndexesSql(table)),
                $"DROP TABLE {Quote(table.Name)};");
        }

        foreach (var tableName in rebuildTables)
        {
            var oldTable = TryFindMatchedOldTable(oldModel, newModel, tableName);
            var newTable = FindTable(newModel, tableName);
            if (oldTable is null)
                continue;

            var up = BuildSqliteRebuildStatements(oldTable, newTable);
            var down = BuildSqliteRebuildStatements(newTable, oldTable);
            Append(sqlUp, sqlDown, up, down);
        }

        foreach (var change in changes)
        {
            if (rebuildTables.Contains(change.TableName))
                continue;

            switch (change.ChangeType)
            {
                case SchemaChangeType.AddColumn:
                {
                    var table = FindTable(newModel, change.TableName);
                    var column = FindColumn(table, change.ColumnName!);
                    var upStatements = new List<string>
                    {
                        $"ALTER TABLE {Quote(table.Name)} ADD COLUMN {BuildSqliteAddColumnDefinition(column)};"
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
                    var table = FindTable(oldModel, change.TableName);
                    var down = new List<string> { BuildCreateTableSql(table, DatabaseProvider.Sqlite) };
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

    private void AppendRelationSql(
        SchemaChange change,
        ModelJson oldModel,
        ModelJson newModel,
        DatabaseProvider provider,
        List<string> sqlUp,
        List<string> sqlDown)
    {
        var relation = (change.NewValue as RelationDef) ?? (change.OldValue as RelationDef);
        if (relation is null)
            return;

        if (relation.Type == RelationType.OneToMany)
            return;

        var junction = _relationResolver.BuildJunctionTable(relation);

        switch (change.ChangeType)
        {
            case SchemaChangeType.AddRelation:
                Append(sqlUp, sqlDown,
                    BuildCreateTableSql(junction, provider),
                    $"DROP TABLE {Quote(junction.Name)};");
                break;
            case SchemaChangeType.DropRelation:
                Append(sqlUp, sqlDown,
                    $"DROP TABLE {Quote(junction.Name)};",
                    BuildCreateTableSql(junction, provider));
                break;
            case SchemaChangeType.AlterRelation:
            {
                var oldRelation = (RelationDef)change.OldValue!;
                var oldJunction = _relationResolver.BuildJunctionTable(oldRelation);
                if (JunctionTableEquals(oldJunction, junction))
                    break;
                Append(sqlUp, sqlDown,
                    new[]
                    {
                        $"DROP TABLE {Quote(oldJunction.Name)};",
                        BuildCreateTableSql(junction, provider)
                    },
                    new[]
                    {
                        $"DROP TABLE {Quote(junction.Name)};",
                        BuildCreateTableSql(oldJunction, provider)
                    });
                break;
            }
        }
    }

    private IReadOnlyList<string> BuildSqliteRebuildStatements(TableDef oldTable, TableDef newTable)
    {
        var tempTable = $"__tmp_{newTable.Name}";
        var statements = new List<string>
        {
            BuildCreateTableSql(newTable with { Name = tempTable }, DatabaseProvider.Sqlite)
        };

        var insertColumns = new List<string>();
        var selectExpressions = new List<string>();

        foreach (var newColumn in newTable.Columns.Where(c => ColumnTypes.HasPhysicalColumn(c.Type)))
        {
            insertColumns.Add(Quote(newColumn.Name));
            selectExpressions.Add(BuildSqliteSelectExpression(oldTable, newColumn));
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

    private string BuildSqliteSelectExpression(TableDef oldTable, ColumnDef newColumn)
    {
        var oldColumn = FindMatchedOldColumn(oldTable, newColumn);
        if (oldColumn is not null)
        {
            var source = Quote(oldColumn.Name);
            return oldColumn.Type == newColumn.Type
                ? source
                : $"CAST({source} AS {ColumnTypes.ToSqlite(newColumn.Type)}) AS {Quote(newColumn.Name)}";
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

    private string BuildCreateTableSql(TableDef table, DatabaseProvider provider)
    {
        var definitions = new List<string>();
        var physicalColumns = table.Columns.Where(c => ColumnTypes.HasPhysicalColumn(c.Type)).ToList();
        var primaryKeyColumns = physicalColumns.Where(c => c.PrimaryKey).Select(c => c.Name).ToList();
        var useCompositePrimaryKey = primaryKeyColumns.Count > 1;
        var useSqliteInlineIntegerPrimaryKey = provider == DatabaseProvider.Sqlite
            && primaryKeyColumns.Count == 1
            && physicalColumns.First(c => c.PrimaryKey).Type == ColumnType.Integer;

        foreach (var column in physicalColumns)
        {
            definitions.Add(BuildCreateTableColumnDefinition(column, provider, useCompositePrimaryKey, useSqliteInlineIntegerPrimaryKey));
        }

        if (useCompositePrimaryKey)
            definitions.Add($"PRIMARY KEY ({string.Join(", ", primaryKeyColumns.Select(Quote))})");

        return $"""
            CREATE TABLE {Quote(table.Name)} (
                {string.Join(",\n    ", definitions)}
            );
            """;
    }

    private string BuildCreateTableColumnDefinition(
        ColumnDef column,
        DatabaseProvider provider,
        bool useCompositePrimaryKey,
        bool useSqliteInlineIntegerPrimaryKey)
    {
        if (useSqliteInlineIntegerPrimaryKey && column.PrimaryKey && column.Type == ColumnType.Integer)
        {
            return $"{Quote(column.Name)} INTEGER PRIMARY KEY AUTOINCREMENT";
        }

        var parts = new List<string>
        {
            Quote(column.Name),
            ColumnTypes.ToDdl(column.Type, provider)
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

    private string BuildPostgresAddColumnDefinition(ColumnDef column)
    {
        var parts = new List<string> { BuildBaseAddColumnDefinition(column, DatabaseProvider.Postgres) };
        return string.Join(" ", parts);
    }

    private string BuildSqliteAddColumnDefinition(ColumnDef column)
    {
        return BuildBaseAddColumnDefinition(column, DatabaseProvider.Sqlite);
    }

    private string BuildBaseAddColumnDefinition(ColumnDef column, DatabaseProvider provider)
    {
        var parts = new List<string>
        {
            Quote(column.Name),
            ColumnTypes.ToDdl(column.Type, provider)
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
        return
        [
            enable
                ? $"CREATE UNIQUE INDEX {Quote(indexName)} ON {Quote(tableName)} ({Quote(column.Name)});"
                : $"DROP INDEX {Quote(indexName)};"
        ];
    }

    private IEnumerable<string> BuildDisableUniqueSql(string tableName, string columnName)
    {
        return [$"DROP INDEX {Quote(UniqueIndexName(tableName, columnName))};"];
    }

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
