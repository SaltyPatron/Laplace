-- The substrate runtime writes the substrate, so it also refreshes the planner statistics
-- of what it wrote (Laplace#1879). Since PostgreSQL 17, ANALYZE needs MAINTAIN on the table
-- and on every leaf partition; without it each one is skipped with only a WARNING, and the
-- site's counts (pg_class.reltuples, pg_stats) read a seeded database as empty.
-- Existing tables (every partition) are granted here; tables created later get it from
-- default privileges, for this migration's role and for every owner of a substrate table.
-- runtime/database-role.py grants the same at provisioning.
DO $$
DECLARE
    runtime_role constant text := 'laplace_runtime';
    namespace record;
    owner_role text;
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = runtime_role) THEN
        RETURN;
    END IF;
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = runtime_role
               AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)) THEN
        RAISE EXCEPTION 'Substrate runtime must be an unprivileged role';
    END IF;
    FOR namespace IN
        SELECT oid, nspname, pg_get_userbyid(nspowner) AS nspowner FROM pg_namespace
        WHERE nspname NOT LIKE 'pg_%' AND nspname NOT IN ('information_schema', 'app', 'public')
    LOOP
        EXECUTE format('GRANT MAINTAIN ON ALL TABLES IN SCHEMA %I TO %I', namespace.nspname, runtime_role);
        EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT MAINTAIN ON TABLES TO %I',
            namespace.nspname, runtime_role);
        FOR owner_role IN
            SELECT namespace.nspowner
            UNION
            SELECT DISTINCT pg_get_userbyid(c.relowner) FROM pg_class c
            WHERE c.relnamespace = namespace.oid AND c.relkind IN ('r', 'p')
        LOOP
            IF owner_role <> current_user AND owner_role <> runtime_role
               AND pg_has_role(owner_role, 'MEMBER') THEN
                EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I GRANT MAINTAIN ON TABLES TO %I',
                    owner_role, namespace.nspname, runtime_role);
            ELSIF owner_role <> current_user AND owner_role <> runtime_role THEN
                RAISE WARNING 'cannot set default MAINTAIN for tables % creates in %: % is not a member of it',
                    owner_role, namespace.nspname, current_user;
            END IF;
        END LOOP;
    END LOOP;
END $$;
