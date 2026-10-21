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
    private readonly Func<Task<Services.WorkspaceCalendar>>? _calendar;

    public DatabaseProvider Provider => _config.Provider;

    /// <param name="calendar">The workspace's calendar: TODAY() in formulas is a day in its time zone.</param>
    public DataPool(DatabaseConfig config, ILogger<DataPool>? logger = null, Func<Task<Services.WorkspaceCalendar>>? calendar = null)
    {
        _config = config;
        _logger = logger;
        _calendar = calendar;

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
        var calendar = _calendar is null ? Services.WorkspaceCalendar.Default : await _calendar();
        if (_config.Provider == DatabaseProvider.Postgres)
        {
            var conn = _pgDataSource!.CreateConnection();
            await conn.OpenAsync();
            // Lock to the workspace's schema (public for the default one) — bento_data
            // cannot access _system. bento.time_zone: the zone TODAY() reads (an IANA name, checked when saved).
            var schema = Services.Workspaces.WorkspaceContext.Current.DataSchema.Replace("\"", "\"\"");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SET search_path TO \"{schema}\"; SET bento.time_zone = '{calendar.ZoneId.Replace("'", "''")}';";
            await cmd.ExecuteNonQueryAsync();
            return conn;
        }
        else
        {
            var dbPath = Path.Combine(_config.DataConnection, Services.Workspaces.WorkspaceContext.Current.DataFile);
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
            Formula.SqliteFormulaFunctions.Register(conn, calendar.Zone);

            return conn;
        }
    }

    public void Dispose()
    {
        _pgDataSource?.Dispose();
    }
}
