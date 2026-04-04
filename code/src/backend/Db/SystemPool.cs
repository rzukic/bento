using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Repere.Api.Db;

public class SystemPool : ISystemPool, IDisposable
{
    private readonly DatabaseConfig _config;
    private readonly ILogger<SystemPool>? _logger;
    private readonly NpgsqlDataSource? _pgDataSource;

    public DatabaseProvider Provider => _config.Provider;

    public SystemPool(DatabaseConfig config, ILogger<SystemPool>? logger = null)
    {
        _config = config;
        _logger = logger;

        if (config.Provider == DatabaseProvider.Postgres)
        {
            var builder = new NpgsqlDataSourceBuilder(config.SystemConnection);
            builder.ConnectionStringBuilder.MinPoolSize = config.MinPoolSize;
            builder.ConnectionStringBuilder.MaxPoolSize = config.MaxPoolSize;
            builder.ConnectionStringBuilder.Timeout = config.ConnectionTimeoutSeconds;
            _pgDataSource = builder.Build();
            _logger?.LogInformation("SystemPool configured for Postgres (pool {Min}-{Max})", config.MinPoolSize, config.MaxPoolSize);
        }
        else
        {
            Directory.CreateDirectory(config.SystemConnection);
            _logger?.LogInformation("SystemPool configured for SQLite at {Path}", config.SystemConnection);
        }
    }

    public async Task<IDbConnection> GetConnectionAsync()
    {
        if (_config.Provider == DatabaseProvider.Postgres)
        {
            var conn = _pgDataSource!.CreateConnection();
            await conn.OpenAsync();
            // Ensure we operate in the _system schema
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SET search_path TO _system, public;";
            await cmd.ExecuteNonQueryAsync();
            return conn;
        }
        else
        {
            var dbPath = Path.Combine(_config.SystemConnection, "system.db");
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

            return conn;
        }
    }

    public void Dispose()
    {
        _pgDataSource?.Dispose();
    }
}
