using Dapper;

namespace Bento.Api.Db;

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
            CREATE UNIQUE INDEX IF NOT EXISTS ux_schema_versions_version
            ON _system.schema_versions(version);
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.schema_version_counters (
                id              INTEGER PRIMARY KEY CHECK (id = 1),
                next_version    INTEGER NOT NULL
            );
            """);

        await conn.ExecuteAsync("""
            INSERT INTO _system.schema_version_counters (id, next_version)
            SELECT 1, COALESCE(MAX(version), 0) + 1
            FROM _system.schema_versions
            ON CONFLICT (id) DO NOTHING;
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
            CREATE TABLE IF NOT EXISTS _system.auth_sessions (
                id                      SERIAL PRIMARY KEY,
                user_id                 INTEGER NOT NULL REFERENCES _system.users(id),
                refresh_token_hash      VARCHAR(255) NOT NULL UNIQUE,
                expires_at              TIMESTAMPTZ NOT NULL,
                last_used_at            TIMESTAMPTZ,
                revoked_at              TIMESTAMPTZ,
                replaced_by_session_id  INTEGER REFERENCES _system.auth_sessions(id),
                created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
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

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.system_views (
                id              SERIAL PRIMARY KEY,
                name            VARCHAR(255) NOT NULL,
                table_name      VARCHAR(255) NOT NULL,
                owner_user_id   INTEGER NOT NULL REFERENCES _system.users(id) ON DELETE CASCADE,
                type            VARCHAR(50) NOT NULL DEFAULT 'table',
                config          JSONB NOT NULL DEFAULT '{}',
                is_shared       BOOLEAN NOT NULL DEFAULT FALSE,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """);

        await conn.ExecuteAsync("""
            CREATE INDEX IF NOT EXISTS idx_system_views_table_name
            ON _system.system_views(table_name);
            """);

        await conn.ExecuteAsync("""
            CREATE INDEX IF NOT EXISTS idx_system_views_owner
            ON _system.system_views(owner_user_id);
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.user_table_explorer_preferences (
                user_id         INTEGER NOT NULL REFERENCES _system.users(id) ON DELETE CASCADE,
                table_name      VARCHAR(255) NOT NULL,
                default_view_id INTEGER REFERENCES _system.system_views(id) ON DELETE SET NULL,
                auto_save       BOOLEAN NOT NULL DEFAULT FALSE,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (user_id, table_name)
            );
            """);

        await conn.ExecuteAsync("""
            CREATE INDEX IF NOT EXISTS idx_user_table_explorer_preferences_table_name
            ON _system.user_table_explorer_preferences(table_name);
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.import_profiles (
                id              SERIAL PRIMARY KEY,
                name            VARCHAR(255) NOT NULL,
                table_name      VARCHAR(255) NOT NULL,
                config          JSONB NOT NULL DEFAULT '{}',
                created_by      INTEGER,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """);

        await conn.ExecuteAsync("""
            CREATE INDEX IF NOT EXISTS idx_import_profiles_table_name
            ON _system.import_profiles(table_name);
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.saved_queries (
                id              SERIAL PRIMARY KEY,
                name            VARCHAR(255) NOT NULL,
                source_table    VARCHAR(255) NOT NULL,
                config          JSONB NOT NULL DEFAULT '{}',
                owner_user_id   INTEGER,
                is_shared       BOOLEAN NOT NULL DEFAULT FALSE,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """);

        await conn.ExecuteAsync("""
            CREATE INDEX IF NOT EXISTS idx_saved_queries_source_table
            ON _system.saved_queries(source_table);
            """);

        // Custom functions (Step 4.1): the current version, and every saved version.
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.functions (
                id              SERIAL PRIMARY KEY,
                name            VARCHAR(63) NOT NULL UNIQUE,
                description     TEXT,
                arguments       JSONB NOT NULL DEFAULT '[]',
                result_type     VARCHAR(20) NOT NULL,
                code            TEXT NOT NULL,
                version         INTEGER NOT NULL DEFAULT 1,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.function_versions (
                id              SERIAL PRIMARY KEY,
                function_name   VARCHAR(63) NOT NULL,
                version         INTEGER NOT NULL,
                arguments       JSONB NOT NULL DEFAULT '[]',
                result_type     VARCHAR(20) NOT NULL,
                code            TEXT NOT NULL,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE (function_name, version)
            );
            """);

        // Dashboards (Step 3.3): the table predates owners and sharing.
        await conn.ExecuteAsync("""
            ALTER TABLE _system.dashboards ADD COLUMN IF NOT EXISTS owner_user_id INTEGER;
            ALTER TABLE _system.dashboards ADD COLUMN IF NOT EXISTS is_shared BOOLEAN NOT NULL DEFAULT FALSE;
            """);

        // API keys (Part 3 R3.3): the table predates prefixes, last use and revocation.
        await conn.ExecuteAsync("""
            ALTER TABLE _system.api_keys ADD COLUMN IF NOT EXISTS prefix VARCHAR(32);
            ALTER TABLE _system.api_keys ADD COLUMN IF NOT EXISTS last_used_at TIMESTAMPTZ;
            ALTER TABLE _system.api_keys ADD COLUMN IF NOT EXISTS revoked_at TIMESTAMPTZ;
            """);

        // Step 4.2: functions that call APIs; credentials with descriptions; price sources.
        await conn.ExecuteAsync("""
            ALTER TABLE _system.functions ADD COLUMN IF NOT EXISTS allowed_hosts JSONB NOT NULL DEFAULT '[]';
            ALTER TABLE _system.functions ADD COLUMN IF NOT EXISTS credentials JSONB NOT NULL DEFAULT '[]';
            ALTER TABLE _system.function_versions ADD COLUMN IF NOT EXISTS allowed_hosts JSONB NOT NULL DEFAULT '[]';
            ALTER TABLE _system.function_versions ADD COLUMN IF NOT EXISTS credentials JSONB NOT NULL DEFAULT '[]';
            ALTER TABLE _system.credentials ADD COLUMN IF NOT EXISTS description TEXT;
            ALTER TABLE _system.credentials ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ;
            ALTER TABLE _system.credentials ADD COLUMN IF NOT EXISTS last_used_at TIMESTAMPTZ;
            ALTER TABLE _system.functions ADD COLUMN IF NOT EXISTS mode VARCHAR(10) NOT NULL DEFAULT 'row';
            ALTER TABLE _system.function_versions ADD COLUMN IF NOT EXISTS mode VARCHAR(10) NOT NULL DEFAULT 'row';
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.forms (
                id              SERIAL PRIMARY KEY,
                name            VARCHAR(255) NOT NULL,
                table_name      VARCHAR(255) NOT NULL,
                form_json       JSONB NOT NULL,
                owner_user_id   INTEGER,
                is_shared       BOOLEAN NOT NULL DEFAULT FALSE,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.price_sources (
                id              SERIAL PRIMARY KEY,
                name            VARCHAR(255) NOT NULL UNIQUE,
                config          JSONB NOT NULL DEFAULT '{}',
                enabled         BOOLEAN NOT NULL DEFAULT TRUE,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.price_source_runs (
                id              SERIAL PRIMARY KEY,
                source_id       INTEGER NOT NULL,
                kind            VARCHAR(20) NOT NULL,
                started_at      TIMESTAMPTZ NOT NULL,
                finished_at     TIMESTAMPTZ,
                requests        INTEGER NOT NULL DEFAULT 0,
                rows_written    INTEGER NOT NULL DEFAULT 0,
                last_quote_at   TIMESTAMPTZ,
                error           TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_price_source_runs_source ON _system.price_source_runs(source_id, started_at);
            """);
        // MCP (Step 4.6): OAuth clients registered by MCP clients, the grants users
        // approved for them, their codes and tokens (hashed), and the call log.
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.oauth_clients (
                id              SERIAL PRIMARY KEY,
                client_id       VARCHAR(64) NOT NULL UNIQUE,
                name            VARCHAR(255) NOT NULL,
                redirect_uris   JSONB NOT NULL DEFAULT '[]',
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE IF NOT EXISTS _system.oauth_grants (
                id              SERIAL PRIMARY KEY,
                client_id       VARCHAR(64) NOT NULL,
                user_id         INTEGER NOT NULL REFERENCES _system.users(id),
                scope           VARCHAR(20) NOT NULL,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                last_used_at    TIMESTAMPTZ,
                revoked_at      TIMESTAMPTZ
            );
            CREATE TABLE IF NOT EXISTS _system.oauth_tokens (
                id              SERIAL PRIMARY KEY,
                grant_id        INTEGER NOT NULL,
                kind            VARCHAR(10) NOT NULL,
                token_hash      VARCHAR(64) NOT NULL UNIQUE,
                redirect_uri    TEXT,
                code_challenge  VARCHAR(128),
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                expires_at      TIMESTAMPTZ NOT NULL,
                used_at         TIMESTAMPTZ
            );
            CREATE INDEX IF NOT EXISTS idx_oauth_tokens_grant ON _system.oauth_tokens(grant_id);
            CREATE TABLE IF NOT EXISTS _system.mcp_calls (
                id              SERIAL PRIMARY KEY,
                called_at       TIMESTAMPTZ NOT NULL,
                user_id         INTEGER,
                via             VARCHAR(20) NOT NULL,
                credential      VARCHAR(255),
                tool            VARCHAR(100) NOT NULL,
                arguments       TEXT,
                ok              BOOLEAN NOT NULL,
                error           TEXT,
                duration_ms     INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_mcp_calls_called_at ON _system.mcp_calls(called_at);
            ALTER TABLE _system.oauth_grants ADD COLUMN IF NOT EXISTS permissions TEXT;
            """);

        // AI (Step 4.7): settings (key/value) and every model call with its tokens and cost.
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _system.ai_settings (
                key             VARCHAR(100) PRIMARY KEY,
                value           TEXT
            );
            CREATE TABLE IF NOT EXISTS _system.ai_usage (
                id                  SERIAL PRIMARY KEY,
                called_at           TIMESTAMPTZ NOT NULL,
                user_id             INTEGER,
                feature             VARCHAR(50) NOT NULL,
                model               VARCHAR(100) NOT NULL,
                input_tokens        BIGINT NOT NULL DEFAULT 0,
                output_tokens       BIGINT NOT NULL DEFAULT 0,
                cache_read_tokens   BIGINT NOT NULL DEFAULT 0,
                cache_write_tokens  BIGINT NOT NULL DEFAULT 0,
                cost_usd            NUMERIC(12, 6) NOT NULL DEFAULT 0,
                tool_calls          INTEGER NOT NULL DEFAULT 0,
                ok                  BOOLEAN NOT NULL,
                error               TEXT,
                duration_ms         INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_ai_usage_called_at ON _system.ai_usage(called_at);
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
            CREATE UNIQUE INDEX IF NOT EXISTS ux_schema_versions_version
            ON schema_versions(version);
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS schema_version_counters (
                id              INTEGER PRIMARY KEY CHECK (id = 1),
                next_version    INTEGER NOT NULL
            );
            """);

        await conn.ExecuteAsync("""
            INSERT OR IGNORE INTO schema_version_counters (id, next_version)
            SELECT 1, COALESCE(MAX(version), 0) + 1
            FROM schema_versions;
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
            CREATE TABLE IF NOT EXISTS auth_sessions (
                id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id                 INTEGER NOT NULL REFERENCES users(id),
                refresh_token_hash      TEXT NOT NULL UNIQUE,
                expires_at              TEXT NOT NULL,
                last_used_at            TEXT,
                revoked_at              TEXT,
                replaced_by_session_id  INTEGER REFERENCES auth_sessions(id),
                created_at              TEXT NOT NULL DEFAULT (datetime('now'))
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

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS system_views (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT NOT NULL,
                table_name      TEXT NOT NULL,
                owner_user_id   INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                type            TEXT NOT NULL DEFAULT 'table',
                config          TEXT NOT NULL DEFAULT '{}',
                is_shared       INTEGER NOT NULL DEFAULT 0,
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """);

        await conn.ExecuteAsync("""
            CREATE INDEX IF NOT EXISTS idx_system_views_table_name
            ON system_views(table_name);
            """);

        await conn.ExecuteAsync("""
            CREATE INDEX IF NOT EXISTS idx_system_views_owner
            ON system_views(owner_user_id);
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS user_table_explorer_preferences (
                user_id         INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                table_name      TEXT NOT NULL,
                default_view_id INTEGER REFERENCES system_views(id) ON DELETE SET NULL,
                auto_save       INTEGER NOT NULL DEFAULT 0,
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at      TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (user_id, table_name)
            );
            """);

        await conn.ExecuteAsync("""
            CREATE INDEX IF NOT EXISTS idx_user_table_explorer_preferences_table_name
            ON user_table_explorer_preferences(table_name);
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS import_profiles (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT NOT NULL,
                table_name      TEXT NOT NULL,
                config          TEXT NOT NULL DEFAULT '{}',
                created_by      INTEGER,
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """);

        await conn.ExecuteAsync("""
            CREATE INDEX IF NOT EXISTS idx_import_profiles_table_name
            ON import_profiles(table_name);
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS saved_queries (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT NOT NULL,
                source_table    TEXT NOT NULL,
                config          TEXT NOT NULL DEFAULT '{}',
                owner_user_id   INTEGER,
                is_shared       INTEGER NOT NULL DEFAULT 0,
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """);

        await conn.ExecuteAsync("""
            CREATE INDEX IF NOT EXISTS idx_saved_queries_source_table
            ON saved_queries(source_table);
            """);

        // Custom functions (Step 4.1): the current version, and every saved version.
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS functions (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT NOT NULL UNIQUE,
                description     TEXT,
                arguments       TEXT NOT NULL DEFAULT '[]',
                result_type     TEXT NOT NULL,
                code            TEXT NOT NULL,
                version         INTEGER NOT NULL DEFAULT 1,
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS function_versions (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                function_name   TEXT NOT NULL,
                version         INTEGER NOT NULL,
                arguments       TEXT NOT NULL DEFAULT '[]',
                result_type     TEXT NOT NULL,
                code            TEXT NOT NULL,
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE (function_name, version)
            );
            """);

        // Dashboards (Step 3.3): the table predates owners and sharing; SQLite
        // has no ADD COLUMN IF NOT EXISTS.
        var dashboardColumns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('dashboards');"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!dashboardColumns.Contains("owner_user_id"))
            await conn.ExecuteAsync("ALTER TABLE dashboards ADD COLUMN owner_user_id INTEGER;");
        if (!dashboardColumns.Contains("is_shared"))
            await conn.ExecuteAsync("ALTER TABLE dashboards ADD COLUMN is_shared INTEGER NOT NULL DEFAULT 0;");

        // API keys (Part 3 R3.3): prefixes, last use and revocation came later.
        var apiKeyColumns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('api_keys');"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, type) in new[] { ("prefix", "TEXT"), ("last_used_at", "TEXT"), ("revoked_at", "TEXT") })
        {
            if (!apiKeyColumns.Contains(column))
                await conn.ExecuteAsync($"ALTER TABLE api_keys ADD COLUMN {column} {type};");
        }

        // Step 4.2: functions that call APIs; credentials with descriptions; price sources.
        foreach (var (table, column, type) in new[]
        {
            ("functions", "allowed_hosts", "TEXT NOT NULL DEFAULT '[]'"),
            ("functions", "credentials", "TEXT NOT NULL DEFAULT '[]'"),
            ("function_versions", "allowed_hosts", "TEXT NOT NULL DEFAULT '[]'"),
            ("function_versions", "credentials", "TEXT NOT NULL DEFAULT '[]'"),
            ("credentials", "description", "TEXT"),
            ("credentials", "updated_at", "TEXT"),
            ("credentials", "last_used_at", "TEXT"),
            ("functions", "mode", "TEXT NOT NULL DEFAULT 'row'"),
            ("function_versions", "mode", "TEXT NOT NULL DEFAULT 'row'")
        })
        {
            var existing = (await conn.QueryAsync<string>($"SELECT name FROM pragma_table_info('{table}');")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!existing.Contains(column))
                await conn.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {type};");
        }

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS forms (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT NOT NULL,
                table_name      TEXT NOT NULL,
                form_json       TEXT NOT NULL,
                owner_user_id   INTEGER,
                is_shared       INTEGER NOT NULL DEFAULT 0,
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS price_sources (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT NOT NULL UNIQUE,
                config          TEXT NOT NULL DEFAULT '{}',
                enabled         INTEGER NOT NULL DEFAULT 1,
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS price_source_runs (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                source_id       INTEGER NOT NULL,
                kind            TEXT NOT NULL,
                started_at      TEXT NOT NULL,
                finished_at     TEXT,
                requests        INTEGER NOT NULL DEFAULT 0,
                rows_written    INTEGER NOT NULL DEFAULT 0,
                last_quote_at   TEXT,
                error           TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_price_source_runs_source ON price_source_runs(source_id, started_at);
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS oauth_clients (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                client_id       TEXT NOT NULL UNIQUE,
                name            TEXT NOT NULL,
                redirect_uris   TEXT NOT NULL DEFAULT '[]',
                created_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS oauth_grants (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                client_id       TEXT NOT NULL,
                user_id         INTEGER NOT NULL REFERENCES users(id),
                scope           TEXT NOT NULL,
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                last_used_at    TEXT,
                revoked_at      TEXT
            );
            CREATE TABLE IF NOT EXISTS oauth_tokens (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                grant_id        INTEGER NOT NULL,
                kind            TEXT NOT NULL,
                token_hash      TEXT NOT NULL UNIQUE,
                redirect_uri    TEXT,
                code_challenge  TEXT,
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                expires_at      TEXT NOT NULL,
                used_at         TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_oauth_tokens_grant ON oauth_tokens(grant_id);
            CREATE TABLE IF NOT EXISTS mcp_calls (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                called_at       TEXT NOT NULL,
                user_id         INTEGER,
                via             TEXT NOT NULL,
                credential      TEXT,
                tool            TEXT NOT NULL,
                arguments       TEXT,
                ok              INTEGER NOT NULL,
                error           TEXT,
                duration_ms     INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_mcp_calls_called_at ON mcp_calls(called_at);
            """);

        // Step 4.6: per-area permissions of a connected app (JSON list), added after the first release.
        var grantColumns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('oauth_grants');")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!grantColumns.Contains("permissions"))
            await conn.ExecuteAsync("ALTER TABLE oauth_grants ADD COLUMN permissions TEXT;");

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS ai_settings (
                key             TEXT PRIMARY KEY,
                value           TEXT
            );
            CREATE TABLE IF NOT EXISTS ai_usage (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                called_at           TEXT NOT NULL,
                user_id             INTEGER,
                feature             TEXT NOT NULL,
                model               TEXT NOT NULL,
                input_tokens        INTEGER NOT NULL DEFAULT 0,
                output_tokens       INTEGER NOT NULL DEFAULT 0,
                cache_read_tokens   INTEGER NOT NULL DEFAULT 0,
                cache_write_tokens  INTEGER NOT NULL DEFAULT 0,
                cost_usd            REAL NOT NULL DEFAULT 0,
                tool_calls          INTEGER NOT NULL DEFAULT 0,
                ok                  INTEGER NOT NULL,
                error               TEXT,
                duration_ms         INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_ai_usage_called_at ON ai_usage(called_at);
            """);
    }
}
