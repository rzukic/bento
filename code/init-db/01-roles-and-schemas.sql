-- Dual-role setup for Bento (R1.8)
-- Runs once when the Postgres container is first created.
-- Role names default to bento_system / bento_data; installs created before the
-- rename pass BENTO_SYSTEM_ROLE=repere_system and BENTO_DATA_ROLE=repere_data.
\set ON_ERROR_STOP on
\getenv bento_system_password BENTO_SYSTEM_PASSWORD
\getenv bento_data_password BENTO_DATA_PASSWORD
\getenv system_role BENTO_SYSTEM_ROLE
\getenv data_role BENTO_DATA_ROLE

\if :{?bento_system_password}
\else
\echo 'BENTO_SYSTEM_PASSWORD is required for Postgres role bootstrap.'
\quit 1
\endif

\if :{?bento_data_password}
\else
\echo 'BENTO_DATA_PASSWORD is required for Postgres role bootstrap.'
\quit 1
\endif

\if :{?system_role}
\else
\set system_role bento_system
\endif

\if :{?data_role}
\else
\set data_role bento_data
\endif

-- Schema isolation
CREATE SCHEMA IF NOT EXISTS _system;

-- Privileged role: full access to everything
SELECT format('CREATE ROLE %I WITH LOGIN PASSWORD %L', :'system_role', :'bento_system_password')
WHERE NOT EXISTS (
    SELECT 1 FROM pg_roles WHERE rolname = :'system_role'
) \gexec
ALTER ROLE :"system_role" WITH LOGIN PASSWORD :'bento_system_password';
GRANT ALL ON SCHEMA _system TO :"system_role";
GRANT ALL ON SCHEMA public TO :"system_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"system_role" IN SCHEMA _system GRANT ALL ON TABLES TO :"system_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"system_role" IN SCHEMA public GRANT ALL ON TABLES TO :"system_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"system_role" IN SCHEMA public GRANT ALL ON SEQUENCES TO :"system_role";

-- Restricted role: user data only, no access to _system
SELECT format('CREATE ROLE %I WITH LOGIN PASSWORD %L', :'data_role', :'bento_data_password')
WHERE NOT EXISTS (
    SELECT 1 FROM pg_roles WHERE rolname = :'data_role'
) \gexec
ALTER ROLE :"data_role" WITH LOGIN PASSWORD :'bento_data_password';
GRANT USAGE ON SCHEMA public TO :"data_role";
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO :"data_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"system_role" IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"data_role";
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO :"data_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"system_role" IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO :"data_role";
REVOKE ALL ON SCHEMA _system FROM :"data_role";
