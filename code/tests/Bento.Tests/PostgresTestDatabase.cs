using System.Data;
using Npgsql;

namespace Bento.Tests;

public sealed record PostgresTestSettings
{
    public const string AdminConnectionEnvironmentVariable = "BENTO_TEST_POSTGRES_ADMIN_CONNECTION";

    public string AdminConnectionString { get; init; } = string.Empty;
    public string SystemRoleName { get; init; } = "bento_system";
    public string SystemRolePassword { get; init; } = "bento_system_test_secret";
    public string DataRoleName { get; init; } = "bento_data";
    public string DataRolePassword { get; init; } = "bento_data_test_secret";

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
            SystemRoleName = Environment.GetEnvironmentVariable("BENTO_TEST_POSTGRES_SYSTEM_ROLE") ?? "bento_system",
            SystemRolePassword = Environment.GetEnvironmentVariable("BENTO_TEST_POSTGRES_SYSTEM_PASSWORD") ?? "bento_system_test_secret",
            DataRoleName = Environment.GetEnvironmentVariable("BENTO_TEST_POSTGRES_DATA_ROLE") ?? "bento_data",
            DataRolePassword = Environment.GetEnvironmentVariable("BENTO_TEST_POSTGRES_DATA_PASSWORD") ?? "bento_data_test_secret"
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
        var databaseName = $"bento_test_{Guid.NewGuid():N}";
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

    // Tests start in parallel; concurrent CREATE/ALTER ROLE fails with "tuple concurrently updated".
    private static readonly SemaphoreSlim RolesLock = new(1, 1);
    private static bool _rolesReady;

    private static async Task EnsureRolesAsync(IDbConnection connection, PostgresTestSettings settings)
    {
        await RolesLock.WaitAsync();
        try
        {
            if (!_rolesReady)
            {
                await CreateRolesAsync(connection, settings);
                _rolesReady = true;
            }
        }
        finally
        {
            RolesLock.Release();
        }
    }

    private static async Task CreateRolesAsync(IDbConnection connection, PostgresTestSettings settings)
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
            ALTER DEFAULT PRIVILEGES FOR ROLE {QuoteIdentifier(settings.SystemRoleName)} IN SCHEMA public
                GRANT ALL ON TABLES TO {QuoteIdentifier(settings.SystemRoleName)};
            ALTER DEFAULT PRIVILEGES FOR ROLE {QuoteIdentifier(settings.SystemRoleName)} IN SCHEMA public
                GRANT ALL ON SEQUENCES TO {QuoteIdentifier(settings.SystemRoleName)};

            GRANT USAGE ON SCHEMA public TO {QuoteIdentifier(settings.DataRoleName)};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {QuoteIdentifier(settings.DataRoleName)};
            ALTER DEFAULT PRIVILEGES FOR ROLE {QuoteIdentifier(settings.SystemRoleName)} IN SCHEMA public
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {QuoteIdentifier(settings.DataRoleName)};
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {QuoteIdentifier(settings.DataRoleName)};
            ALTER DEFAULT PRIVILEGES FOR ROLE {QuoteIdentifier(settings.SystemRoleName)} IN SCHEMA public
                GRANT USAGE, SELECT ON SEQUENCES TO {QuoteIdentifier(settings.DataRoleName)};
            REVOKE ALL ON SCHEMA _system FROM {QuoteIdentifier(settings.DataRoleName)};
            """);
    }

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string EscapeLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
