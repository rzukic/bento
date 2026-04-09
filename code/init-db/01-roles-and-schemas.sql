-- Dual-role setup for Repère (R1.8)
-- Runs once when the Postgres container is first created.

-- Schema isolation
CREATE SCHEMA IF NOT EXISTS _system;

-- Privileged role: full access to everything
CREATE ROLE repere_system WITH LOGIN PASSWORD 'repere_system_dev';
GRANT ALL ON SCHEMA _system TO repere_system;
GRANT ALL ON SCHEMA public TO repere_system;
ALTER DEFAULT PRIVILEGES IN SCHEMA _system GRANT ALL ON TABLES TO repere_system;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO repere_system;

-- Restricted role: user data only, no access to _system
CREATE ROLE repere_data WITH LOGIN PASSWORD 'repere_data_dev';
GRANT USAGE, CREATE ON SCHEMA public TO repere_data;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO repere_data;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO repere_data;
REVOKE ALL ON SCHEMA _system FROM repere_data;
