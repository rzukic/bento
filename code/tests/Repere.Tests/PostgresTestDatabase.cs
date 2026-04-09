using System.Data;
using Npgsql;

namespace Repere.Tests;

public sealed record PostgresTestSettings
{
    public const string AdminConnectionEnvironmentVariable = "REPERE_TEST_POSTGRES_ADMIN_CONNECTION";

    public string AdminConnectionString { get; init; } = string.Empty;
    public string SystemRoleName { get; init; } = "repere_system";
    public string SystemRolePassword { get; init; } = "repere_system_dev";
    public string DataRoleName { get; init; } = "repere_data";
    public string DataRolePassword { get; init; } = "repere_data_dev";

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AdminConnectionEnvironmentVariable));

    public static PostgresTestSettings FromEnvironment()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable(AdminConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            throw new InvalidOperationException(
                $"Set {AdminConnectionEnvironmentVariable} to enable Postgres-backed integration tests.");
        }

        return new PostgresTestSettings
        {
            AdminConnectionString = adminConnectionString,
            SystemRoleName = Environment.GetEnvironmentVariable("REPERE_TEST_POSTGRES_SYSTEM_ROLE") ?? "repere_system",
            SystemRolePassword = Environment.GetEnvironmentVariable("REPERE_TEST_POSTGRES_SYSTEM_PASSWORD") ?? "repere_system_dev",
            DataRoleName = Environment.GetEnvironmentVariable("REPERE_TEST_POSTGRES_DATA_ROLE") ?? "repere_data",
            DataRolePassword = Environment.GetEnvironmentVariable("REPERE_TEST_POSTGRES_DATA_PASSWORD") ?? "repere_data_dev"
        };
    }

    public string BuildAdminConnectionString(string? databaseName = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Pooling = false
        };

        if (!string.IsNullOrWhiteSpace(databaseName))
        {
            builder.Database = databaseName;
        }

        return builder.ToString();
    }

    public string BuildSystemConnectionString(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Database = databaseName,
            Username = SystemRoleName,
            Password = SystemRolePassword
        };

        return builder.ToString();
    }

    public string BuildDataConnectionString(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Database = databaseName,
            Username = DataRoleName,
            Password = DataRolePassword
        };

        return builder.ToString();
    }
}

public sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly PostgresTestSettings _settings;

    private PostgresTestDatabase(PostgresTestSettings settings, string databaseName)
    {
        _settings = settings;
        DatabaseName = databaseName;
        SystemConnectionString = settings.BuildSystemConnectionString(databaseName);
        DataConnectionString = settings.BuildDataConnectionString(databaseName);
    }

    public string DatabaseName { get; }
    public string SystemConnectionString { get; }
    public string DataConnectionString { get; }

    public static async Task<PostgresTestDatabase> CreateAsync(PostgresTestSettings settings)
    {
        var databaseName = $"repere_test_{Guid.NewGuid():N}";
        var database = new PostgresTestDatabase(settings, databaseName);

        await using var adminConnection = new NpgsqlConnection(settings.BuildAdminConnectionString());
        await adminConnection.OpenAsync();

        await EnsureRolesAsync(adminConnection, settings);
        await adminConnection.ExecuteAsync(
            $"CREATE DATABASE {QuoteIdentifier(databaseName)};");
        await adminConnection.ExecuteAsync(
            $"""
            GRANT ALL PRIVILEGES ON DATABASE {QuoteIdentifier(databaseName)} TO {QuoteIdentifier(settings.SystemRoleName)};
            GRANT CONNECT ON DATABASE {QuoteIdentifier(databaseName)} TO {QuoteIdentifier(settings.DataRoleName)};
            """);

        await using var databaseAdminConnection = new NpgsqlConnection(settings.BuildAdminConnectionString(databaseName));
        await databaseAdminConnection.OpenAsync();
        await InitializeDatabaseAsync(databaseAdminConnection, settings);

        return database;
    }

    public async ValueTask DisposeAsync()
    {
        await using var adminConnection = new NpgsqlConnection(_settings.BuildAdminConnectionString());
        await adminConnection.OpenAsync();
        await adminConnection.ExecuteAsync(
            """
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = @DatabaseName
              AND pid <> pg_backend_pid();
            """,
            new { DatabaseName });
        await adminConnection.ExecuteAsync(
            $"DROP DATABASE IF EXISTS {QuoteIdentifier(DatabaseName)};");
    }

    private static async Task EnsureRolesAsync(IDbConnection connection, PostgresTestSettings settings)
    {
        await connection.ExecuteAsync(
            $"""
            DO $$
            BEGIN
                BEGIN
                    CREATE ROLE {QuoteIdentifier(settings.SystemRoleName)} WITH LOGIN PASSWORD '{EscapeLiteral(settings.SystemRolePassword)}';
                EXCEPTION
                    WHEN duplicate_object THEN NULL;
                END;

                BEGIN
                    CREATE ROLE {QuoteIdentifier(settings.DataRoleName)} WITH LOGIN PASSWORD '{EscapeLiteral(settings.DataRolePassword)}';
                EXCEPTION
                    WHEN duplicate_object THEN NULL;
                END;
            END
            $$;

            ALTER ROLE {QuoteIdentifier(settings.SystemRoleName)} WITH LOGIN PASSWORD '{EscapeLiteral(settings.SystemRolePassword)}';
            ALTER ROLE {QuoteIdentifier(settings.DataRoleName)} WITH LOGIN PASSWORD '{EscapeLiteral(settings.DataRolePassword)}';
            """);
    }

    private static async Task InitializeDatabaseAsync(IDbConnection connection, PostgresTestSettings settings)
    {
        await connection.ExecuteAsync(
            $"""
            CREATE SCHEMA IF NOT EXISTS _system AUTHORIZATION {QuoteIdentifier(settings.SystemRoleName)};

            GRANT ALL ON SCHEMA _system TO {QuoteIdentifier(settings.SystemRoleName)};
            GRANT ALL ON SCHEMA public TO {QuoteIdentifier(settings.SystemRoleName)};

            GRANT USAGE, CREATE ON SCHEMA public TO {QuoteIdentifier(settings.DataRoleName)};
            REVOKE ALL ON SCHEMA _system FROM {QuoteIdentifier(settings.DataRoleName)};
            """);
    }

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string EscapeLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
