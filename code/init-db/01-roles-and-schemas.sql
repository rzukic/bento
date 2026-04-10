-- Dual-role setup for Repère (R1.8)
-- Runs once when the Postgres container is first created.
\set ON_ERROR_STOP on
\getenv repere_system_password REPERE_SYSTEM_PASSWORD
\getenv repere_data_password REPERE_DATA_PASSWORD

\if :{?repere_system_password}
\else
\echo 'REPERE_SYSTEM_PASSWORD is required for Postgres role bootstrap.'
\quit 1
\endif

\if :{?repere_data_password}
\else
\echo 'REPERE_DATA_PASSWORD is required for Postgres role bootstrap.'
\quit 1
\endif

-- Schema isolation
CREATE SCHEMA IF NOT EXISTS _system;

-- Privileged role: full access to everything
SELECT format('CREATE ROLE repere_system WITH LOGIN PASSWORD %L', :'repere_system_password')
WHERE NOT EXISTS (
    SELECT 1 FROM pg_roles WHERE rolname = 'repere_system'
) \gexec
ALTER ROLE repere_system WITH LOGIN PASSWORD :'repere_system_password';
GRANT ALL ON SCHEMA _system TO repere_system;
GRANT ALL ON SCHEMA public TO repere_system;
ALTER DEFAULT PRIVILEGES FOR ROLE repere_system IN SCHEMA _system GRANT ALL ON TABLES TO repere_system;
ALTER DEFAULT PRIVILEGES FOR ROLE repere_system IN SCHEMA public GRANT ALL ON TABLES TO repere_system;
ALTER DEFAULT PRIVILEGES FOR ROLE repere_system IN SCHEMA public GRANT ALL ON SEQUENCES TO repere_system;

-- Restricted role: user data only, no access to _system
SELECT format('CREATE ROLE repere_data WITH LOGIN PASSWORD %L', :'repere_data_password')
WHERE NOT EXISTS (
    SELECT 1 FROM pg_roles WHERE rolname = 'repere_data'
) \gexec
ALTER ROLE repere_data WITH LOGIN PASSWORD :'repere_data_password';
GRANT USAGE ON SCHEMA public TO repere_data;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO repere_data;
ALTER DEFAULT PRIVILEGES FOR ROLE repere_system IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO repere_data;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO repere_data;
ALTER DEFAULT PRIVILEGES FOR ROLE repere_system IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO repere_data;
REVOKE ALL ON SCHEMA _system FROM repere_data;
