CREATE EXTENSION IF NOT EXISTS postgis;

CREATE EXTENSION IF NOT EXISTS laplace_geom;

CREATE EXTENSION IF NOT EXISTS laplace_substrate;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_app') THEN
        EXECUTE 'GRANT USAGE ON SCHEMA laplace TO laplace_app';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_readonly') THEN
        EXECUTE 'GRANT USAGE ON SCHEMA laplace TO laplace_readonly';
    END IF;
END $$;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_app') THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA laplace TO laplace_app';
        EXECUTE 'GRANT USAGE ON ALL SEQUENCES IN SCHEMA laplace TO laplace_app';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA laplace
                 GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO laplace_app';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA laplace
                 GRANT USAGE ON SEQUENCES TO laplace_app';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_readonly') THEN
        EXECUTE 'GRANT SELECT ON ALL TABLES IN SCHEMA laplace TO laplace_readonly';
        EXECUTE 'GRANT USAGE ON ALL SEQUENCES IN SCHEMA laplace TO laplace_readonly';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA laplace
                 GRANT SELECT ON TABLES TO laplace_readonly';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA laplace
                 GRANT USAGE ON SEQUENCES TO laplace_readonly';
    END IF;
END $$;
