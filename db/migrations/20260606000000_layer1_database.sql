-- 20260606000000_layer1_database.sql
--
-- THE one DbUp script. Layer-1 orchestration ONLY: extensions, role grants,
-- default privileges. (2026-06-05 migrations refactor.)
--
-- THE LAW: the EXTENSION is the deployment unit — laplace_substrate ships its
-- complete schema, functions, AND readback seed (21_seed.sql.in: canonical
-- vocabulary + codepoint_render), exactly the postgis model (postgis ships
-- spatial_ref_sys). Migrations NEVER carry copies of extension objects: that
-- legacy convergence pattern produced 42P13 signature collisions, dollar-quote
-- parser breaks, and ~2,300 lines of drift-prone duplication before it was
-- retired. Greenfield substrate changes ship by editing the extension SQL and
-- rebuilding (nuke + up); post-greenfield, by versioned extension upgrade
-- scripts (laplace_substrate--A--B.sql, ALTER EXTENSION ... UPDATE).
--
-- laplace_admin is SUPERUSER (bootstrap_pg_roles) ⇒ CREATE EXTENSION runs
-- directly. All operations idempotent.

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
