-- The remote runtime login survives database recreation; schema ACLs do not.
-- Restore its database-scoped access contract from runtime/database-role.py.
DO $$
DECLARE
    runtime_role constant text := 'laplace_runtime';
    namespace record;
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = runtime_role) THEN
        RETURN;
    END IF;
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = runtime_role
               AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)) THEN
        RAISE EXCEPTION 'Substrate runtime must be an unprivileged role';
    END IF;
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), runtime_role);
    FOR namespace IN
        SELECT nspname FROM pg_namespace
        WHERE nspname NOT LIKE 'pg_%' AND nspname NOT IN ('information_schema', 'app')
    LOOP
        EXECUTE format('GRANT USAGE ON SCHEMA %I TO %I', namespace.nspname, runtime_role);
        EXECUTE format('GRANT %s ON ALL TABLES IN SCHEMA %I TO %I',
            CASE WHEN namespace.nspname = 'public' THEN 'SELECT' ELSE 'SELECT,INSERT,UPDATE,DELETE' END,
            namespace.nspname, runtime_role);
        EXECUTE format('GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA %I TO %I', namespace.nspname, runtime_role);
        EXECUTE format('GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA %I TO %I', namespace.nspname, runtime_role);
        IF namespace.nspname <> 'public' THEN
            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO %I',
                namespace.nspname, runtime_role);
            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT USAGE,SELECT ON SEQUENCES TO %I',
                namespace.nspname, runtime_role);
        END IF;
    END LOOP;
END $$;
