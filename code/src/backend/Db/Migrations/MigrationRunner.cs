using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Bento.Api.Db.Schema;

namespace Bento.Api.Db.Migrations;

public sealed record MigrationHistoryEntry
{
    public int Id { get; init; }
    public int VersionFrom { get; init; }
    public int VersionTo { get; init; }
    public string SqlUp { get; init; } = string.Empty;
    public string? SqlDown { get; init; }
    public string AppliedAt { get; init; } = string.Empty;

    public DateTimeOffset? AppliedAtUtc =>
        DateTimeOffset.TryParse(AppliedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;

    public IReadOnlyList<string> SqlUpStatements =>
        string.IsNullOrWhiteSpace(SqlUp)
            ? []
            : JsonSerializer.Deserialize<List<string>>(SqlUp) ?? [];

    public IReadOnlyList<string> SqlDownStatements =>
        string.IsNullOrWhiteSpace(SqlDown)
            ? []
            : JsonSerializer.Deserialize<List<string>>(SqlDown) ?? [];
}

public sealed record MigrationApplyResult
{
    public required bool Applied { get; init; }
    public required int VersionFrom { get; init; }
    public required int VersionTo { get; init; }
    public required MigrationPlan Plan { get; init; }
}

public class MigrationRunner
{
    private readonly IDataPool _dataPool;
    private readonly ISystemPool _systemPool;
    private readonly ModelJsonStore _modelJsonStore;

    public MigrationRunner(IDataPool dataPool, ISystemPool systemPool, ModelJsonStore modelJsonStore)
    {
        _dataPool = dataPool;
        _systemPool = systemPool;
        _modelJsonStore = modelJsonStore;
    }

    public async Task<MigrationApplyResult> ApplyAsync(ModelJson currentModel, ModelJson newModel, MigrationPlan plan)
    {
        await ExecuteDataStatementsAsync(plan.SqlUp);

        try
        {
            using var systemConn = await OpenSystemConnectionAsync();
            using var systemTx = systemConn.BeginTransaction();

            await MigrateSavedViewReferencesAsync(systemConn, systemTx, plan.Changes);
            var savedModel = await _modelJsonStore.SaveAsync(systemConn, systemTx, newModel);
            await InsertMigrationHistoryAsync(systemConn, systemTx, currentModel.Version, savedModel.Version, plan.SqlUp, plan.SqlDown);

            systemTx.Commit();

            return new MigrationApplyResult
            {
                Applied = true,
                VersionFrom = currentModel.Version,
                VersionTo = savedModel.Version,
                Plan = plan
            };
        }
        catch (Exception ex)
        {
            await TryCompensateAsync(plan.SqlDown, "apply", ex);
            throw;
        }
    }

    public async Task<MigrationApplyResult?> RollbackLastAsync()
    {
        var results = await RollbackAsync(1);
        return results.FirstOrDefault();
    }

    public async Task<IReadOnlyList<MigrationApplyResult>> RollbackAsync(int steps)
    {
        if (steps <= 0)
            throw new ArgumentOutOfRangeException(nameof(steps), "Rollback steps must be greater than zero.");

        var results = new List<MigrationApplyResult>();
        for (var i = 0; i < steps; i++)
        {
            var result = await RollbackSingleAsync();
            if (result is null)
                break;

            results.Add(result);
        }

        return results;
    }

    private async Task<MigrationApplyResult?> RollbackSingleAsync()
    {
        var currentModel = await _modelJsonStore.GetCurrentAsync();
        if (currentModel is null || currentModel.Version == 0)
            return null;

        var migration = await GetLatestForwardMigrationForVersionAsync(currentModel.Version);
        if (migration is null)
            return null;

        // Resolve the target model before touching the data schema: if the
        // previous version is gone we must fail without having rolled back the
        // tables, otherwise schema and Model JSON would diverge.
        var previousModel = migration.VersionFrom == 0
            ? new ModelJson { Version = 0 }
            : await _modelJsonStore.GetVersionAsync(migration.VersionFrom);
        if (previousModel is null)
            throw new InvalidOperationException($"Cannot rollback migration {migration.Id}: schema version {migration.VersionFrom} no longer exists.");

        await ExecuteDataStatementsAsync(migration.SqlDownStatements);

        try
        {
            using var systemConn = await OpenSystemConnectionAsync();
            using var systemTx = systemConn.BeginTransaction();

            await _modelJsonStore.DeleteVersionAsync(systemConn, systemTx, currentModel.Version);
            await InsertMigrationHistoryAsync(systemConn, systemTx, migration.VersionTo, migration.VersionFrom, migration.SqlDownStatements, migration.SqlUpStatements);

            systemTx.Commit();
        }
        catch (Exception ex)
        {
            await TryCompensateAsync(migration.SqlUpStatements, "rollback", ex);
            throw;
        }

        return new MigrationApplyResult
        {
            Applied = true,
            VersionFrom = migration.VersionTo,
            VersionTo = migration.VersionFrom,
            Plan = new MigrationPlan
            {
                SqlUp = migration.SqlDownStatements,
                SqlDown = migration.SqlUpStatements,
                IsDestructive = true,
                Changes = []
            }
        };
    }

    public async Task<IReadOnlyList<MigrationHistoryEntry>> GetHistoryAsync()
    {
        using var conn = await OpenSystemConnectionAsync();
        var rows = await conn.QueryAsync<MigrationHistoryEntry>($"SELECT id, version_from AS VersionFrom, version_to AS VersionTo, sql_up AS SqlUp, sql_down AS SqlDown, applied_at AS AppliedAt FROM {GetMigrationsTableName()} ORDER BY id DESC;");
        return rows.ToList();
    }

    private async Task<MigrationHistoryEntry?> GetLatestForwardMigrationForVersionAsync(int version)
    {
        using var conn = await OpenSystemConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<MigrationHistoryEntry>(
            $"SELECT id, version_from AS VersionFrom, version_to AS VersionTo, sql_up AS SqlUp, sql_down AS SqlDown, applied_at AS AppliedAt FROM {GetMigrationsTableName()} WHERE version_to = @Version AND version_to > version_from ORDER BY id DESC LIMIT 1;",
            new { Version = version });
    }

    private async Task ExecuteDataStatementsAsync(IReadOnlyList<string> statements)
    {
        var isSqlite = _dataPool.Provider == DatabaseProvider.Sqlite;

        using var conn = await OpenMigrationConnectionAsync();

        // SQLite cannot ALTER most column properties, so the generator rebuilds
        // the table: create temp, copy rows, DROP the old table, rename. With
        // foreign_keys=ON that DROP runs an implicit DELETE and fails as soon as
        // a child table still references the parent rows. SQLite's documented
        // procedure is to turn enforcement off for the duration (the pragma is a
        // no-op inside a transaction, so it must happen before BEGIN), run the
        // statements, and verify integrity with foreign_key_check before commit.
        if (isSqlite)
        {
            await conn.ExecuteAsync("PRAGMA foreign_keys=OFF;");
        }

        try
        {
            using var tx = conn.BeginTransaction();

            try
            {
                foreach (var statement in statements)
                {
                    await conn.ExecuteAsync(statement, transaction: tx);
                }

                if (isSqlite)
                {
                    await EnsureNoSqliteForeignKeyViolationsAsync(conn, tx);
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        finally
        {
            if (isSqlite)
            {
                await conn.ExecuteAsync("PRAGMA foreign_keys=ON;");
            }
        }
    }

    private static async Task EnsureNoSqliteForeignKeyViolationsAsync(IDbConnection conn, IDbTransaction tx)
    {
        // Columns: table, rowid, parent, fkid. Read dynamically: with zero rows
        // SQLite reports no column types and Dapper cannot map a typed record.
        var violations = (await conn.QueryAsync("PRAGMA foreign_key_check;", transaction: tx))
            .Cast<IDictionary<string, object?>>()
            .Select(row => (
                Table: row.TryGetValue("table", out var table) ? table?.ToString() ?? "?" : "?",
                Parent: row.TryGetValue("parent", out var parent) ? parent?.ToString() ?? "?" : "?"))
            .ToList();
        if (violations.Count == 0)
            return;

        var summary = string.Join(", ", violations
            .GroupBy(v => (v.Table, v.Parent))
            .Select(g => $"{g.Key.Table} -> {g.Key.Parent} ({g.Count()} row(s))"));

        throw new InvalidOperationException(
            $"Migration would leave {violations.Count} foreign key violation(s) and was rolled back: {summary}.");
    }

    private async Task TryCompensateAsync(IReadOnlyList<string> statements, string operation, Exception originalException)
    {
        if (statements.Count == 0)
        {
            throw new InvalidOperationException(
                $"Migration {operation} failed after the data schema changed, and there was no compensation plan available.",
                originalException);
        }

        try
        {
            await ExecuteDataStatementsAsync(statements);
        }
        catch (Exception compensationException)
        {
            throw new AggregateException(
                $"Migration {operation} failed and the compensation attempt also failed. Manual reconciliation may be required.",
                originalException,
                compensationException);
        }
    }

    private async Task InsertMigrationHistoryAsync(
        IDbConnection conn,
        IDbTransaction transaction,
        int versionFrom,
        int versionTo,
        IReadOnlyList<string> sqlUp,
        IReadOnlyList<string> sqlDown)
    {
        await conn.ExecuteAsync(
            $"INSERT INTO {GetMigrationsTableName()} (version_from, version_to, sql_up, sql_down) VALUES (@VersionFrom, @VersionTo, @SqlUp, @SqlDown);",
            new
            {
                VersionFrom = versionFrom,
                VersionTo = versionTo,
                SqlUp = JsonSerializer.Serialize(sqlUp),
                SqlDown = JsonSerializer.Serialize(sqlDown)
            },
            transaction);
    }

    private async Task MigrateSavedViewReferencesAsync(
        IDbConnection conn,
        IDbTransaction transaction,
        IReadOnlyList<SchemaChange> changes)
    {
        var tableRenames = changes
            .Where(change => change.ChangeType == SchemaChangeType.RenameTable)
            .Where(change => !string.IsNullOrWhiteSpace(change.PreviousTableName))
            .ToDictionary(
                change => change.PreviousTableName!,
                change => change.TableName,
                StringComparer.OrdinalIgnoreCase);

        var columnRenames = changes
            .Where(change => change.ChangeType == SchemaChangeType.RenameColumn)
            .Where(change => !string.IsNullOrWhiteSpace(change.PreviousColumnName))
            .ToList();

        if (tableRenames.Count == 0 && columnRenames.Count == 0)
            return;

        foreach (var rename in tableRenames)
        {
            foreach (var tableName in new[] { GetPreferencesTableName(), GetImportProfilesTableName() })
            {
                await conn.ExecuteAsync(
                    $"UPDATE {tableName} SET table_name = @NewTableName, updated_at = {SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now")} WHERE table_name = @OldTableName;",
                    new
                    {
                        OldTableName = rename.Key,
                        NewTableName = rename.Value,
                        Now = UtcNowIso()
                    },
                    transaction);
            }
        }

        var views = await conn.QueryAsync<SystemViewReferenceRow>(
            $"SELECT id AS Id, table_name AS TableName, config AS Config FROM {GetViewsTableName()};",
            transaction: transaction);

        foreach (var view in views)
        {
            var nextTableName = tableRenames.TryGetValue(view.TableName, out var renamedTable)
                ? renamedTable
                : view.TableName;
            var columnRenameMap = BuildColumnRenameMapForView(view.TableName, nextTableName, columnRenames);
            var nextConfig = RewriteSavedViewConfigColumnReferences(view.Config, columnRenameMap);

            if (
                string.Equals(nextTableName, view.TableName, StringComparison.Ordinal) &&
                string.Equals(nextConfig, view.Config, StringComparison.Ordinal))
            {
                continue;
            }

            await conn.ExecuteAsync(
                $"UPDATE {GetViewsTableName()} SET table_name = @TableName, config = {SystemSqlDialect.JsonParam(_systemPool.Provider, "@Config")}, updated_at = {SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now")} WHERE id = @Id;",
                new
                {
                    view.Id,
                    TableName = nextTableName,
                    Config = nextConfig,
                    Now = UtcNowIso()
                },
                transaction);
        }
    }

    private static Dictionary<string, string> BuildColumnRenameMapForView(
        string currentViewTableName,
        string nextViewTableName,
        IReadOnlyList<SchemaChange> columnRenames)
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in columnRenames)
        {
            var previousTableName = change.PreviousTableName ?? change.TableName;
            if (
                !string.Equals(previousTableName, currentViewTableName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(change.TableName, nextViewTableName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            renames[change.PreviousColumnName!] = change.ColumnName!;
        }

        return renames;
    }

    private static string RewriteSavedViewConfigColumnReferences(
        string config,
        IReadOnlyDictionary<string, string> columnRenames)
    {
        if (columnRenames.Count == 0 || string.IsNullOrWhiteSpace(config))
            return config;

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(config) as JsonObject;
        }
        catch (JsonException)
        {
            return config;
        }

        if (root is null)
            return config;

        var changed = false;
        changed |= RewriteColumnPropertyArrays(root, columnRenames, "hiddenColumnNames", "columnOrder", "cardFieldNames");
        changed |= RewriteColumnProperty(root, columnRenames, "kanbanColumn");
        changed |= RewriteColumnClauseArray(root, columnRenames, "sort");
        changed |= RewriteColumnClauseArray(root, columnRenames, "filters");

        return changed ? root.ToJsonString() : config;
    }

    private static bool RewriteColumnPropertyArrays(
        JsonObject root,
        IReadOnlyDictionary<string, string> columnRenames,
        params string[] propertyNames)
    {
        var changed = false;

        foreach (var propertyName in propertyNames)
        {
            if (root[propertyName] is not JsonArray values)
                continue;

            for (var i = 0; i < values.Count; i++)
            {
                if (!TryGetJsonString(values[i], out var value))
                    continue;

                if (!columnRenames.TryGetValue(value, out var renamedValue))
                    continue;

                values[i] = renamedValue;
                changed = true;
            }
        }

        return changed;
    }

    private static bool RewriteColumnProperty(
        JsonObject root,
        IReadOnlyDictionary<string, string> columnRenames,
        string propertyName)
    {
        if (!TryGetJsonString(root[propertyName], out var value))
            return false;

        if (!columnRenames.TryGetValue(value, out var renamedValue))
            return false;

        root[propertyName] = renamedValue;
        return true;
    }

    private static bool RewriteColumnClauseArray(
        JsonObject root,
        IReadOnlyDictionary<string, string> columnRenames,
        string propertyName)
    {
        if (root[propertyName] is not JsonArray clauses)
            return false;

        var changed = false;
        foreach (var clause in clauses.OfType<JsonObject>())
        {
            changed |= RewriteColumnProperty(clause, columnRenames, "column");
        }

        return changed;
    }

    private static bool TryGetJsonString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is null)
            return false;

        try
        {
            var stringValue = node.GetValue<string>();
            if (stringValue is null)
                return false;

            value = stringValue;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private string GetMigrationsTableName() =>
        _systemPool.Provider == DatabaseProvider.Postgres ? "_system.migrations" : "migrations";

    private string GetViewsTableName() =>
        _systemPool.Provider == DatabaseProvider.Postgres ? "_system.system_views" : "system_views";

    private string GetPreferencesTableName() =>
        _systemPool.Provider == DatabaseProvider.Postgres
            ? "_system.user_table_explorer_preferences"
            : "user_table_explorer_preferences";

    private string GetImportProfilesTableName() =>
        _systemPool.Provider == DatabaseProvider.Postgres ? "_system.import_profiles" : "import_profiles";

    private static string UtcNowIso() =>
        DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private async Task<IDbConnection> OpenDataConnectionAsync()
    {
        return await _dataPool.GetConnectionAsync();
    }

    private async Task<IDbConnection> OpenMigrationConnectionAsync()
    {
        if (_systemPool.Provider != DatabaseProvider.Postgres)
            return await OpenDataConnectionAsync();

        var conn = await OpenSystemConnectionAsync();
        await conn.ExecuteAsync("SET search_path TO public, _system;");
        return conn;
    }

    private async Task<IDbConnection> OpenSystemConnectionAsync()
    {
        return await _systemPool.GetConnectionAsync();
    }

    private sealed record SystemViewReferenceRow
    {
        public required int Id { get; init; }
        public required string TableName { get; init; }
        public required string Config { get; init; }
    }
}
