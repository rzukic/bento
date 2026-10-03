using System.Text.Json;
using Bento.Api.Db.Schema;

namespace Bento.Api.Db.Migrations;

/// <summary>
/// Provider-neutral DDL building blocks shared by <see cref="PostgresDdlGenerator"/>,
/// <see cref="SqliteDdlGenerator"/> and <see cref="RelationConversionPlanner"/>:
/// CREATE TABLE / index / unique-support SQL, literal and identifier formatting,
/// model lookups, and the up/down statement list appenders.
/// </summary>
internal static class DdlHelpers
{
    // ---- Change handlers whose emitted SQL is identical for both providers ----

    internal static void AppendAddColumnSql(
        ModelJson newModel,
        SchemaChange change,
        DatabaseProvider provider,
        List<string> sqlUp,
        List<string> sqlDown)
    {
        var table = FindTable(newModel, change.TableName);
        var column = FindColumn(table, change.ColumnName!);
        var upStatements = new List<string>
        {
            $"ALTER TABLE {Quote(table.Name)} ADD COLUMN {BuildAddColumnDefinition(column, provider, newModel)};"
        };
        upStatements.AddRange(BuildUniqueSupportSql(table.Name, column, true, provider));

        var downStatements = new List<string>();
        downStatements.AddRange(BuildUniqueSupportSql(table.Name, column, false, provider));
        downStatements.Add($"ALTER TABLE {Quote(table.Name)} DROP COLUMN {Quote(column.Name)};");

        Append(sqlUp, sqlDown, upStatements, downStatements);
    }

    internal static void AppendAddIndexSql(ModelJson newModel, SchemaChange change, List<string> sqlUp, List<string> sqlDown)
    {
        var table = FindTable(newModel, change.TableName);
        var index = FindIndex(table, change.IndexName!);
        Append(sqlUp, sqlDown,
            BuildCreateIndexSql(table.Name, index),
            BuildDropIndexSql(index.Name));
    }

    internal static void AppendDropIndexSql(ModelJson oldModel, SchemaChange change, List<string> sqlUp, List<string> sqlDown)
    {
        var table = FindTable(oldModel, change.TableName);
        var index = FindIndex(table, change.IndexName!);
        Append(sqlUp, sqlDown,
            BuildDropIndexSql(index.Name),
            BuildCreateIndexSql(table.Name, index));
    }

    internal static void AppendAlterIndexSql(
        ModelJson oldModel,
        ModelJson newModel,
        SchemaChange change,
        List<string> sqlUp,
        List<string> sqlDown)
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
    }

    // ---- CREATE TABLE ----

    internal static string BuildCreateTableSql(TableDef table, DatabaseProvider provider, ModelJson model)
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

    internal static string GetColumnDdlType(ColumnDef column, DatabaseProvider provider, ModelJson model)
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

    private static string BuildCreateTableColumnDefinition(
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

    internal static string BuildAddColumnDefinition(ColumnDef column, DatabaseProvider provider, ModelJson model)
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

    // ---- Indexes and unique support ----

    internal static IEnumerable<string> BuildCreateIndexesSql(TableDef table)
    {
        return table.Indexes.Select(index => BuildCreateIndexSql(table.Name, index));
    }

    internal static string BuildCreateIndexSql(string tableName, IndexDef index)
    {
        var unique = index.Unique ? "UNIQUE " : string.Empty;
        var columns = string.Join(", ", index.Columns.Select(Quote));
        return $"CREATE {unique}INDEX {Quote(index.Name)} ON {Quote(tableName)} ({columns});";
    }

    internal static string BuildDropIndexSql(string indexName) => $"DROP INDEX {Quote(indexName)};";

    internal static IEnumerable<string> BuildUniqueSupportSql(string tableName, ColumnDef column, bool enable, DatabaseProvider provider)
    {
        if (!column.Unique || column.PrimaryKey)
            return [];

        var indexName = UniqueIndexName(tableName, column.Name);
        return enable
            ? [$"CREATE UNIQUE INDEX {Quote(indexName)} ON {Quote(tableName)} ({Quote(column.Name)});"]
            : BuildDisableUniqueSql(tableName, column.Name, provider);
    }

    /// <summary>
    /// A unique column is backed either by the named index this generator creates
    /// for ADD COLUMN / enable-unique changes, or by the inline UNIQUE constraint
    /// that CREATE TABLE emits (Postgres names it <c>&lt;table&gt;_&lt;column&gt;_key</c>).
    /// Disabling unique must drop whichever one exists, otherwise turning unique
    /// off for a column created with the table fails with "index does not exist".
    /// </summary>
    internal static IEnumerable<string> BuildDisableUniqueSql(string tableName, string columnName, DatabaseProvider provider)
    {
        var statements = new List<string>
        {
            $"DROP INDEX IF EXISTS {Quote(UniqueIndexName(tableName, columnName))};"
        };

        if (provider == DatabaseProvider.Postgres)
        {
            statements.Add(
                $"ALTER TABLE {Quote(tableName)} DROP CONSTRAINT IF EXISTS {Quote(InlineUniqueConstraintName(tableName, columnName))};");
        }

        return statements;
    }

    /// <summary>Postgres' default name for an inline <c>UNIQUE</c> column constraint.</summary>
    private static string InlineUniqueConstraintName(string tableName, string columnName) => $"{tableName}_{columnName}_key";

    internal static string BuildAlterDefaultSql(string tableName, ColumnDef column, DatabaseProvider provider)
    {
        var defaultSql = BuildDefaultSql(column, provider);
        return !string.IsNullOrWhiteSpace(defaultSql)
            ? $"ALTER TABLE {Quote(tableName)} ALTER COLUMN {Quote(column.Name)} SET DEFAULT {defaultSql};"
            : $"ALTER TABLE {Quote(tableName)} ALTER COLUMN {Quote(column.Name)} DROP DEFAULT;";
    }

    private static string UniqueIndexName(string tableName, string columnName) => $"ux_{tableName}_{columnName}";

    // ---- Quoting and literals ----

    internal static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string? BuildDefaultSql(ColumnDef column, DatabaseProvider provider)
    {
        if (!string.IsNullOrWhiteSpace(column.InternalDefaultSql))
            return column.InternalDefaultSql;

        return column.DefaultValue.HasValue
            ? FormatSqlLiteral(column.DefaultValue.Value, column.Type, provider)
            : null;
    }

    internal static string FormatSqlLiteral(JsonElement element, ColumnType columnType, DatabaseProvider provider)
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

    // ---- Model lookups ----

    internal static TableDef FindTable(ModelJson model, string tableName)
    {
        return model.Tables.First(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
    }

    internal static TableDef? TryFindMatchedOldTable(ModelJson oldModel, ModelJson newModel, string newTableName)
    {
        var newTable = FindTable(newModel, newTableName);
        if (!string.IsNullOrWhiteSpace(newTable.RenamedFrom))
        {
            return oldModel.Tables.FirstOrDefault(t => string.Equals(t.Name, newTable.RenamedFrom, StringComparison.OrdinalIgnoreCase));
        }

        return oldModel.Tables.FirstOrDefault(t => string.Equals(t.Name, newTableName, StringComparison.OrdinalIgnoreCase));
    }

    internal static ColumnDef FindColumn(TableDef table, string columnName)
    {
        return table.Columns.First(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
    }

    internal static ColumnDef FindColumnForChange(ModelJson model, SchemaChange change, bool useOld)
    {
        var tableName = useOld ? change.PreviousTableName ?? change.TableName : change.TableName;
        var columnName = useOld ? change.PreviousColumnName ?? change.ColumnName! : change.ColumnName!;
        var table = FindTable(model, tableName);
        return FindColumn(table, columnName);
    }

    internal static IndexDef FindIndex(TableDef table, string indexName)
    {
        return table.Indexes.First(i => string.Equals(i.Name, indexName, StringComparison.OrdinalIgnoreCase));
    }

    private static IndexDef FindIndexByNameOrRename(TableDef table, string indexName, IndexDef? fallback)
    {
        return table.Indexes.FirstOrDefault(i => string.Equals(i.Name, indexName, StringComparison.OrdinalIgnoreCase))
            ?? fallback
            ?? throw new InvalidOperationException($"Index '{indexName}' not found on table '{table.Name}'.");
    }

    internal static ColumnDef? FindMatchedOldColumn(TableDef oldTable, ColumnDef newColumn)
    {
        return oldTable.Columns.FirstOrDefault(c => string.Equals(c.Name, newColumn.Name, StringComparison.OrdinalIgnoreCase))
            ?? (!string.IsNullOrWhiteSpace(newColumn.RenamedFrom)
                ? oldTable.Columns.FirstOrDefault(c => string.Equals(c.Name, newColumn.RenamedFrom, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? oldTable.Columns.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.RenamedFrom)
                && string.Equals(c.RenamedFrom, newColumn.Name, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Up/down statement appenders (down statements are prepended so the down script runs in reverse) ----

    internal static void Append(List<string> sqlUp, List<string> sqlDown, string up, string down)
    {
        sqlUp.Add(up);
        sqlDown.Insert(0, down);
    }

    internal static void Append(List<string> sqlUp, List<string> sqlDown, IEnumerable<string> up, IEnumerable<string> down)
    {
        var upList = up.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var downList = down.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        sqlUp.AddRange(upList);
        downList.Reverse();
        foreach (var statement in downList)
            sqlDown.Insert(0, statement);
    }

    internal static void Append(List<string> sqlUp, List<string> sqlDown, IEnumerable<string> up, string down)
    {
        Append(sqlUp, sqlDown, up, new[] { down });
    }

    internal static void Append(List<string> sqlUp, List<string> sqlDown, string up, IEnumerable<string> down)
    {
        Append(sqlUp, sqlDown, new[] { up }, down);
    }
}
