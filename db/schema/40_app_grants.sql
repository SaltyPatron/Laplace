-- DECLARED GRANTS, application schema. Applied on every create and every deploy
-- (after 30_app.sql). GRANT and ALTER DEFAULT PRIVILEGES are idempotent; roles that do
-- not exist in this cluster are skipped.

DO $$
BEGIN
    IF to_regnamespace('app') IS NULL THEN
        RAISE EXCEPTION 'Schema app is not declared; 30_app.sql must be applied first';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_app') THEN
        EXECUTE 'GRANT USAGE ON SCHEMA app TO laplace_app';
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA app TO laplace_app';
        EXECUTE 'GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA app TO laplace_app';
        EXECUTE 'GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA app TO laplace_app';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA app
                 GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO laplace_app';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA app
                 GRANT USAGE, SELECT ON SEQUENCES TO laplace_app';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA app
                 GRANT EXECUTE ON FUNCTIONS TO laplace_app';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_readonly') THEN
        EXECUTE 'GRANT USAGE ON SCHEMA app TO laplace_readonly';
        EXECUTE 'GRANT SELECT ON ALL TABLES IN SCHEMA app TO laplace_readonly';
        EXECUTE 'GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA app TO laplace_readonly';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA app
                 GRANT SELECT ON TABLES TO laplace_readonly';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA app
                 GRANT USAGE, SELECT ON SEQUENCES TO laplace_readonly';
    END IF;
END $$;

-- Application runtime logins (provisioned outside this database, surviving its recreation):
-- the serving login of each installation (laplace_staging_app, laplace_desktop_app, ...).
-- Their grants live in this database, so a recreated database must get them back from this
-- declaration, not from the one-time host installer.
DO $$
DECLARE
    candidate text;
BEGIN
    FOR candidate IN
        SELECT rolname FROM pg_roles
        WHERE rolname LIKE 'laplace\_%\_app' ESCAPE '\' AND rolcanlogin
        ORDER BY rolname
    LOOP
        IF EXISTS (SELECT FROM pg_roles WHERE rolname = candidate
                   AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)) THEN
            RAISE EXCEPTION 'Application runtime must be an unprivileged role: %', candidate;
        END IF;
        IF NOT has_database_privilege(candidate, current_database(), 'CONNECT') THEN
            EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), candidate);
        END IF;
        IF NOT has_schema_privilege(candidate, 'app', 'USAGE') THEN
            EXECUTE format('GRANT USAGE ON SCHEMA app TO %I', candidate);
        END IF;
        EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA app TO %I', candidate);
        EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA app TO %I', candidate);
        EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', candidate);
        EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT USAGE, SELECT ON SEQUENCES TO %I', candidate);
    END LOOP;
END $$;
