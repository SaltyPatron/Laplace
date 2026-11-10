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
