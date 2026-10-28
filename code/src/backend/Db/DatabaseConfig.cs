namespace Bento.Api.Db;

public enum DatabaseProvider
{
    Sqlite,
    Postgres
}

public class DatabaseConfig
{
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>
    /// Postgres: connection string for the privileged role (bento_system).
    /// SQLite: directory path where system.db will be stored.
    /// </summary>
    public string SystemConnection { get; set; } = "./data";

    /// <summary>
    /// Postgres: connection string for the restricted role (bento_data).
    /// SQLite: directory path where data.db will be stored.
    /// </summary>
    public string DataConnection { get; set; } = "./data";

    public int MinPoolSize { get; set; } = 2;
    public int MaxPoolSize { get; set; } = 20;
    public int ConnectionTimeoutSeconds { get; set; } = 30;
    /// <summary>How long a query may run on the data database before it is stopped (both providers).</summary>
    public int QueryTimeoutSeconds { get; set; } = 30;
}
