using Dapper;
using System.Data;

namespace Repere.Api.Db.Schema;

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
        return await SaveAsync(conn, null, model);
    }

    public async Task<ModelJson> SaveAsync(IDbConnection conn, IDbTransaction? transaction, ModelJson model)
    {
        var nextVersion = await conn.ExecuteScalarAsync<int>(GetNextVersionSql());
        var versionedModel = model with { Version = nextVersion };
        var json = ModelJsonSerializer.Serialize(versionedModel);

        await conn.ExecuteAsync(GetInsertSql(), new
        {
            Version = versionedModel.Version,
            ModelJson = json
        }, transaction);

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
        var parsed = ModelJsonSerializer.Parse(row.ModelJson);
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

    private string GetNextVersionSql()
    {
        var table = GetSchemaVersionsTableName();
        return $"SELECT COALESCE(MAX(version), 0) + 1 FROM {table};";
    }

    private string GetInsertSql()
    {
        var table = GetSchemaVersionsTableName();
        return _systemPool.Provider == DatabaseProvider.Postgres
            ? $"INSERT INTO {table} (model_json, version) VALUES (CAST(@ModelJson AS JSONB), @Version);"
            : $"INSERT INTO {table} (model_json, version) VALUES (@ModelJson, @Version);";
    }

    private string GetSchemaVersionsTableName() =>
        _systemPool.Provider == DatabaseProvider.Postgres ? "_system.schema_versions" : "schema_versions";

    private sealed class SchemaVersionRow
    {
        public int Version { get; set; }
        public string ModelJson { get; set; } = string.Empty;
    }
}
