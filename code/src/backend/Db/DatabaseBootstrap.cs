using Dapper;

namespace Repere.Api.Db;

/// <summary>
/// Runs on startup to ensure system tables exist.
/// Idempotent — safe to run on every start.
/// </summary>
public class DatabaseBootstrap : IHostedService
{
    private readonly ISystemPool _systemPool;
    private readonly ILogger<DatabaseBootstrap> _logger;

    public DatabaseBootstrap(ISystemPool systemPool, ILogger<DatabaseBootstrap> logger)
    {
        _systemPool = systemPool;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running database bootstrap ({Provider})...", _systemPool.Provider);

        using var conn = await _systemPool.GetConnectionAsync();

        if (_systemPool.Provider == DatabaseProvider.Postgres)
            await BootstrapPostgres(conn);
        else
            await BootstrapSqlite(conn);

        _logger.LogInformation("Database bootstrap complete.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task BootstrapPostgres(System.Data.IDbConnection conn)
    {
        // System tables live in _system schema.
        // The schema and roles are created by init-db/01-roles-and-schemas.sql
        // (Docker) or manually for non-Docker Postgres setups.
        // Here we only create the tables themselves.

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.schema_versions (
                id              SERIAL PRIMARY KEY,
                model_json      JSONB NOT NULL,
                version         INTEGER NOT NULL DEFAULT 1,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.migrations (
                id              SERIAL PRIMARY KEY,
                version_from    INTEGER NOT NULL,
                version_to      INTEGER NOT NULL,
                sql_up          TEXT NOT NULL,
                sql_down        TEXT,
                applied_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.users (
                id              SERIAL PRIMARY KEY,
                username        VARCHAR(255) NOT NULL UNIQUE,
                password_hash   VARCHAR(255) NOT NULL,
                role            VARCHAR(50) NOT NULL DEFAULT 'admin',
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.api_keys (
                id              SERIAL PRIMARY KEY,
                name            VARCHAR(255) NOT NULL,
                key_hash        VARCHAR(255) NOT NULL UNIQUE,
                scopes          JSONB NOT NULL DEFAULT '[]',
                user_id         INTEGER REFERENCES _system.users(id),
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                expires_at      TIMESTAMPTZ
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.dashboards (
                id              SERIAL PRIMARY KEY,
                name            VARCHAR(255) NOT NULL,
                dashboard_json  JSONB NOT NULL,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.credentials (
                id              SERIAL PRIMARY KEY,
                name            VARCHAR(255) NOT NULL UNIQUE,
                encrypted_value TEXT NOT NULL,
                domain          VARCHAR(255),
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.jobs (
                id              SERIAL PRIMARY KEY,
                name            VARCHAR(255) NOT NULL,
                cron_expr       VARCHAR(100),
                job_type        VARCHAR(100) NOT NULL,
                config_json     JSONB NOT NULL DEFAULT '{}',
                enabled         BOOLEAN NOT NULL DEFAULT TRUE,
                last_run_at     TIMESTAMPTZ,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """);
    }

    private async Task BootstrapSqlite(System.Data.IDbConnection conn)
    {
        // SQLite: system.db is already a separate file (isolation by design).
        // Create the same tables without Postgres-specific types.

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS schema_versions (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                model_json      TEXT NOT NULL,
                version         INTEGER NOT NULL DEFAULT 1,
                created_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS migrations (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                version_from    INTEGER NOT NULL,
                version_to      INTEGER NOT NULL,
                sql_up          TEXT NOT NULL,
                sql_down        TEXT,
                applied_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS users (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                username        TEXT NOT NULL UNIQUE,
                password_hash   TEXT NOT NULL,
                role            TEXT NOT NULL DEFAULT 'admin',
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS api_keys (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT NOT NULL,
                key_hash        TEXT NOT NULL UNIQUE,
                scopes          TEXT NOT NULL DEFAULT '[]',
                user_id         INTEGER REFERENCES users(id),
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                expires_at      TEXT
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS dashboards (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT NOT NULL,
                dashboard_json  TEXT NOT NULL,
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS credentials (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT NOT NULL UNIQUE,
                encrypted_value TEXT NOT NULL,
                domain          TEXT,
                created_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS jobs (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT NOT NULL,
                cron_expr       TEXT,
                job_type        TEXT NOT NULL,
                config_json     TEXT NOT NULL DEFAULT '{}',
                enabled         INTEGER NOT NULL DEFAULT 1,
                last_run_at     TEXT,
                created_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """);
    }
}
