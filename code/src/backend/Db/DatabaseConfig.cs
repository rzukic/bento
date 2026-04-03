namespace Repere.Api.Db;

public enum DatabaseProvider
{
    Sqlite,
    Postgres
}

public class DatabaseConfig
{
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>
    /// Postgres: connection string for the privileged role (repere_system).
    /// SQLite: directory path where system.db will be stored.
    /// </summary>
    public string SystemConnection { get; set; } = "./data";

    /// <summary>
    /// Postgres: connection string for the restricted role (repere_data).
    /// SQLite: directory path where data.db will be stored.
    /// </summary>
    public string DataConnection { get; set; } = "./data";

    public int MinPoolSize { get; set; } = 2;
    public int MaxPoolSize { get; set; } = 20;
    public int ConnectionTimeoutSeconds { get; set; } = 30;
}
