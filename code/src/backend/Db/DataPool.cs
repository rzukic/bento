using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bento.Api.Db;

public class DataPool : IDataPool, IDisposable
{
    private readonly DatabaseConfig _config;
    private readonly ILogger<DataPool>? _logger;
    private readonly NpgsqlDataSource? _pgDataSource;

    public DatabaseProvider Provider => _config.Provider;

    public DataPool(DatabaseConfig config, ILogger<DataPool>? logger = null)
    {
        _config = config;
        _logger = logger;

        if (config.Provider == DatabaseProvider.Postgres)
        {
            var builder = new NpgsqlDataSourceBuilder(config.DataConnection);
            builder.ConnectionStringBuilder.MinPoolSize = config.MinPoolSize;
            builder.ConnectionStringBuilder.MaxPoolSize = config.MaxPoolSize;
            builder.ConnectionStringBuilder.Timeout = config.ConnectionTimeoutSeconds;
            builder.ConnectionStringBuilder.CommandTimeout = config.QueryTimeoutSeconds;
            _pgDataSource = builder.Build();
            _logger?.LogInformation("DataPool configured for Postgres (pool {Min}-{Max})", config.MinPoolSize, config.MaxPoolSize);
        }
        else
        {
            Directory.CreateDirectory(config.DataConnection);
            _logger?.LogInformation("DataPool configured for SQLite at {Path}", config.DataConnection);
        }
    }

    public async Task<IDbConnection> GetConnectionAsync()
    {
        if (_config.Provider == DatabaseProvider.Postgres)
        {
            var conn = _pgDataSource!.CreateConnection();
            await conn.OpenAsync();
            // Lock to public schema only — bento_data role cannot access _system
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
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = _config.ConnectionTimeoutSeconds
            }.ToString();

            var conn = new SqliteConnection(connStr);
            await conn.OpenAsync();

            // SQLite pragmas — must be set per connection
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
            Formula.SqliteFormulaFunctions.Register(conn);

            return conn;
        }
    }

    public void Dispose()
    {
        _pgDataSource?.Dispose();
    }
}
