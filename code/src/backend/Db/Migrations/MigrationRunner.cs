using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Repere.Api.Db.Schema;

namespace Repere.Api.Db.Migrations;

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

        await ExecuteDataStatementsAsync(migration.SqlDownStatements);

        var previousModel = migration.VersionFrom == 0
            ? new ModelJson { Version = 0 }
            : await _modelJsonStore.GetVersionAsync(migration.VersionFrom);
        if (previousModel is null)
            throw new InvalidOperationException($"Cannot rollback migration {migration.Id}: schema version {migration.VersionFrom} no longer exists.");

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
        using var conn = await OpenDataConnectionAsync();
        using var tx = conn.BeginTransaction();

        try
        {
            foreach (var statement in statements)
            {
                await conn.ExecuteAsync(statement, transaction: tx);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
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

    private string GetMigrationsTableName() =>
        _systemPool.Provider == DatabaseProvider.Postgres ? "_system.migrations" : "migrations";

    private async Task<IDbConnection> OpenDataConnectionAsync()
    {
        return await _dataPool.GetConnectionAsync();
    }

    private async Task<IDbConnection> OpenSystemConnectionAsync()
    {
        return await _systemPool.GetConnectionAsync();
    }
}
