-- Migration 20260521000000_initial_extensions
--
-- Layer-1 extension lifecycle orchestration. (DbUp + Npgsql),
-- (extension owns substrate schema; DbUp orchestrates), and
-- (two extensions: laplace_geom + laplace_substrate).
--
-- laplace_admin is a SUPERUSER (set by bootstrap_pg_roles). That means we
-- can `CREATE EXTENSION` directly here — no SECURITY DEFINER wrapper, no
-- custom template database, no ownership-transfer dance. Standard PG
-- pattern for a dedicated-cluster substrate (mirrors AWS rds_superuser
-- / GCP cloudsqlsuperuser conventions).
--
-- Order:
--   1. postgis            (not trusted — requires SUPERUSER; we are one)
--   2. laplace_geom       (requires postgis)
--   3. laplace_substrate  (requires laplace_geom; creates `laplace` schema)
--   4. Schema USAGE grants for laplace_app + laplace_readonly
--   5. Default privileges for future tables in the laplace schema
--
-- All operations are idempotent (IF NOT EXISTS, DO blocks with role checks).
-- DbUp records this script name in the schemaversions table on success.

-- ============================================================
-- Step 1: postgis
-- ============================================================
CREATE EXTENSION IF NOT EXISTS postgis;

-- ============================================================
-- Step 2: laplace_geom (requires postgis)
-- ============================================================
CREATE EXTENSION IF NOT EXISTS laplace_geom;

-- ============================================================
-- Step 3: laplace_substrate (requires laplace_geom; creates `laplace` schema)
-- ============================================================
CREATE EXTENSION IF NOT EXISTS laplace_substrate;

-- ============================================================
-- Step 4: Role USAGE grants on the 'laplace' schema
-- ============================================================
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_app') THEN
        EXECUTE 'GRANT USAGE ON SCHEMA laplace TO laplace_app';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_readonly') THEN
        EXECUTE 'GRANT USAGE ON SCHEMA laplace TO laplace_readonly';
    END IF;
END $$;

-- ============================================================
-- Step 5: Existing + default privileges for app + readonly roles
-- ============================================================
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
