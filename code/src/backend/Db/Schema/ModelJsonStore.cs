using Dapper;
using System.Data;

namespace Bento.Api.Db.Schema;

/// <summary>
/// Persists versioned Model JSON documents in the system schema.
/// </summary>
public class ModelJsonStore
{
    private readonly ISystemPool _systemPool;

    public ModelJsonStore(ISystemPool systemPool)
    {
        _systemPool = systemPool;
    }

    public async Task<ModelJson?> GetCurrentAsync()
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<SchemaVersionRow>(GetSelectCurrentSql());
        return row is null ? null : ParseRow(row);
    }

    public async Task<ModelJson?> GetVersionAsync(int version)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<SchemaVersionRow>(GetSelectVersionSql(), new { Version = version });
        return row is null ? null : ParseRow(row);
    }

    public async Task<ModelJson> SaveAsync(ModelJson model)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        using var tx = conn.BeginTransaction();
        var saved = await SaveAsync(conn, tx, model);
        tx.Commit();
        return saved;
    }

    public async Task<ModelJson> SaveAsync(IDbConnection conn, IDbTransaction? transaction, ModelJson model)
    {
        var ownsTransaction = transaction is null;
        using var localTransaction = ownsTransaction ? conn.BeginTransaction() : null;
        var activeTransaction = transaction ?? localTransaction!;

        try
        {
            var nextVersion = await AllocateNextVersionAsync(conn, activeTransaction);
            var versionedModel = model with { Version = nextVersion };
            var json = ModelJsonSerializer.Serialize(versionedModel);

            await conn.ExecuteAsync(GetInsertSql(), new
            {
                Version = versionedModel.Version,
                ModelJson = json
            }, activeTransaction);

            if (ownsTransaction)
                localTransaction!.Commit();

            return versionedModel;
        }
        catch
        {
            if (ownsTransaction)
                localTransaction?.Rollback();

            throw;
        }
    }

    /// <summary>
    /// Update the current (latest) version's stored model JSON in place without
    /// allocating a new version. Used for metadata-only changes (layout
    /// position, color, and other schema-irrelevant table additional data) so
    /// they are durable without inflating the migration history.
    /// </summary>
    public async Task<ModelJson?> UpdateCurrentMetadataAsync(ModelJson model)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        using var tx = conn.BeginTransaction();
        var current = await conn.QuerySingleOrDefaultAsync<SchemaVersionRow>(GetSelectCurrentSql(), transaction: tx);

        if (current is null)
        {
            return null;
        }

        var versionedModel = model with { Version = current.Version };
        var json = ModelJsonSerializer.Serialize(versionedModel);
        await conn.ExecuteAsync(GetUpdateModelJsonSql(), new
        {
            Version = versionedModel.Version,
            ModelJson = json
        }, tx);
        tx.Commit();

        return versionedModel;
    }

    public async Task DeleteVersionAsync(int version)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        await DeleteVersionAsync(conn, null, version);
    }

    public async Task DeleteVersionAsync(IDbConnection conn, IDbTransaction? transaction, int version)
    {
        await conn.ExecuteAsync($"DELETE FROM {GetSchemaVersionsTableName()} WHERE version = @Version;", new { Version = version }, transaction);
    }

    private ModelJson ParseRow(SchemaVersionRow row)
    {
        var parsed = ManyToManyJunctions.MakeExplicit(ModelJsonSerializer.Parse(row.ModelJson));
        return parsed.Version == row.Version ? parsed : parsed with { Version = row.Version };
    }

    private string GetSelectCurrentSql()
    {
        var table = GetSchemaVersionsTableName();
        return _systemPool.Provider == DatabaseProvider.Postgres
            ? $"SELECT version, model_json::text AS ModelJson FROM {table} ORDER BY version DESC LIMIT 1;"
            : $"SELECT version, model_json AS ModelJson FROM {table} ORDER BY version DESC LIMIT 1;";
    }

    private string GetSelectVersionSql()
    {
        var table = GetSchemaVersionsTableName();
        return _systemPool.Provider == DatabaseProvider.Postgres
            ? $"SELECT version, model_json::text AS ModelJson FROM {table} WHERE version = @Version LIMIT 1;"
            : $"SELECT version, model_json AS ModelJson FROM {table} WHERE version = @Version LIMIT 1;";
    }

    private async Task<int> AllocateNextVersionAsync(IDbConnection conn, IDbTransaction transaction)
    {
        var counterTable = GetSchemaVersionCountersTableName();

        if (_systemPool.Provider == DatabaseProvider.Postgres)
        {
            return await conn.ExecuteScalarAsync<int>($"""
                UPDATE {counterTable}
                SET next_version = next_version + 1
                WHERE id = 1
                RETURNING next_version - 1;
                """, transaction: transaction);
        }

        await conn.ExecuteAsync($"""
            UPDATE {counterTable}
            SET next_version = next_version + 1
            WHERE id = 1;
            """, transaction: transaction);

        return await conn.ExecuteScalarAsync<int>($"""
            SELECT next_version - 1
            FROM {counterTable}
            WHERE id = 1;
            """, transaction: transaction);
    }

    private string GetInsertSql()
    {
        var table = GetSchemaVersionsTableName();
        return _systemPool.Provider == DatabaseProvider.Postgres
            ? $"INSERT INTO {table} (model_json, version) VALUES (CAST(@ModelJson AS JSONB), @Version);"
            : $"INSERT INTO {table} (model_json, version) VALUES (@ModelJson, @Version);";
    }

    private string GetUpdateModelJsonSql()
    {
        var table = GetSchemaVersionsTableName();
        return _systemPool.Provider == DatabaseProvider.Postgres
            ? $"UPDATE {table} SET model_json = CAST(@ModelJson AS JSONB) WHERE version = @Version;"
            : $"UPDATE {table} SET model_json = @ModelJson WHERE version = @Version;";
    }

    private string GetSchemaVersionsTableName() =>
        _systemPool.Provider == DatabaseProvider.Postgres ? "_system.schema_versions" : "schema_versions";

    private string GetSchemaVersionCountersTableName() =>
        _systemPool.Provider == DatabaseProvider.Postgres ? "_system.schema_version_counters" : "schema_version_counters";

    private sealed class SchemaVersionRow
    {
        public int Version { get; set; }
        public string ModelJson { get; set; } = string.Empty;
    }
}
