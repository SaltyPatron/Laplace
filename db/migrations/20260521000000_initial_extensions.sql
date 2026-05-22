-- Migration 20260521000000_initial_extensions
--
-- Layer-1 extension lifecycle orchestration. Per ADR 0021 (DbUp + Npgsql),
-- ADR 0023 (extension owns substrate schema; DbUp orchestrates), and
-- ADR 0025 (two extensions: laplace_geom + laplace_substrate).
--
-- Order:
--   1. postgis            (via laplace_priv.install_extension wrapper —
--                          postgis requires SUPERUSER; wrapper runs as
--                          postgres via SECURITY DEFINER)
--   2. laplace_geom       (trusted=true → laplace_admin installs directly;
--                          requires postgis)
--   3. laplace_substrate  (trusted=true → laplace_admin installs directly;
--                          requires laplace_geom; creates the `laplace`
--                          schema owned by laplace_admin)
--   4. Schema USAGE grants for laplace_app + laplace_readonly
--   5. Default privileges for future tables in the laplace schema
--
-- Prerequisite: Layer 0 bootstrap (sudo scripts/bootstrap-laplace-runner.sh
-- bootstrap) must have created the laplace_priv schema + wrappers and
-- updated the allowlist to include laplace_geom + laplace_substrate.
--
-- All operations are idempotent. DbUp records this script name in the
-- SchemaVersions table on success.

-- ============================================================
-- Step 1: postgis (superuser-required → via wrapper)
-- ============================================================
SELECT laplace_priv.install_extension('postgis');

-- ============================================================
-- Step 2: laplace_geom (trusted=true; direct CREATE EXTENSION)
-- ============================================================
-- laplace_geom.control declares trusted=true (per ADR 0025 + ADR 0034)
-- so laplace_admin (DB owner) can install it directly. Self-heal: if
-- a prior run mis-installed it (e.g., via a SECURITY DEFINER wrapper
-- leaving a postgres-owned objects), drop via wrapper + recreate. The
-- laplace_geom extension installs into the `public` schema per its
-- .control file — no schema ownership concerns.
CREATE EXTENSION IF NOT EXISTS laplace_geom;

-- ============================================================
-- Step 3: laplace_substrate (trusted=true; requires laplace_geom)
-- ============================================================
-- laplace_substrate declares schema='laplace' in its .control file.
-- PostgreSQL creates the schema at install time owned by the calling
-- user (laplace_admin). Self-heal: if a prior install put the schema
-- under postgres ownership, drop via wrapper and recreate.
DO $$
DECLARE
    schema_owner name;
BEGIN
    SELECT pg_catalog.pg_get_userbyid(nspowner) INTO schema_owner
    FROM pg_namespace WHERE nspname = 'laplace';
    IF schema_owner IS NOT NULL AND schema_owner <> 'laplace_admin' THEN
        PERFORM laplace_priv.drop_extension('laplace_substrate');
    END IF;
END $$;
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
-- Step 5: Default privileges for future extension-owned objects
-- ============================================================
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_app') THEN
        EXECUTE 'ALTER DEFAULT PRIVILEGES FOR ROLE laplace_admin IN SCHEMA laplace
                 GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO laplace_app';
        EXECUTE 'ALTER DEFAULT PRIVILEGES FOR ROLE laplace_admin IN SCHEMA laplace
                 GRANT USAGE ON SEQUENCES TO laplace_app';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_readonly') THEN
        EXECUTE 'ALTER DEFAULT PRIVILEGES FOR ROLE laplace_admin IN SCHEMA laplace
                 GRANT SELECT ON TABLES TO laplace_readonly';
        EXECUTE 'ALTER DEFAULT PRIVILEGES FOR ROLE laplace_admin IN SCHEMA laplace
                 GRANT USAGE ON SEQUENCES TO laplace_readonly';
    END IF;
END $$;
