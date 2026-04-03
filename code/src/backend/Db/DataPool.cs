using System.Data;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Repere.Api.Db;

public class DataPool : IDataPool, IDisposable
{
    private readonly DatabaseConfig _config;
    private readonly NpgsqlDataSource? _pgDataSource;

    public DatabaseProvider Provider => _config.Provider;

    public DataPool(DatabaseConfig config)
    {
        _config = config;

        if (config.Provider == DatabaseProvider.Postgres)
        {
            var builder = new NpgsqlDataSourceBuilder(config.DataConnection);
            _pgDataSource = builder.Build();
        }
        else
        {
            Directory.CreateDirectory(config.DataConnection);
        }
    }

    public async Task<IDbConnection> GetConnectionAsync()
    {
        if (_config.Provider == DatabaseProvider.Postgres)
        {
            var conn = _pgDataSource!.CreateConnection();
            await conn.OpenAsync();
            // Lock to public schema only — repere_data role cannot access _system
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SET search_path TO public;";
            await cmd.ExecuteNonQueryAsync();
            return conn;
        }
        else
        {
            var dbPath = Path.Combine(_config.DataConnection, "data.db");
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            var conn = new SqliteConnection(connStr);
            await conn.OpenAsync();
            return conn;
        }
    }

    public void Dispose()
    {
        _pgDataSource?.Dispose();
    }
}
