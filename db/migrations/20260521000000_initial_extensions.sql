-- Migration 20260521000000_initial_extensions
--
-- Layer-1 extension lifecycle orchestration. Per ADR 0021 (DbUp + Npgsql)
-- and ADR 0023 (extension owns substrate schema; DbUp orchestrates).
--
-- This migration is intentionally short:
--   1. Install postgis via laplace_priv.install_extension wrapper
--      (postgis requires SUPERUSER for CREATE EXTENSION; the SECURITY
--       DEFINER wrapper provided by Layer-0 bootstrap lets laplace_admin
--       trigger the install via a postgres-owned function, allowlist-
--       bounded and database-scoped to 'laplace')
--   2. Install laplace via the same wrapper (consistent pattern, even
--      though laplace.control sets superuser = false and could be created
--      directly — using the wrapper dogfoods the canonical install path)
--   3. Schema-level USAGE grants for laplace_app + laplace_readonly on
--      the 'laplace' schema (created by the laplace extension's .sql)
--   4. Default privileges for future tables created by laplace_admin in
--      the laplace schema (via extension upgrade scripts per ADR 0023)
--
-- Prerequisite: Layer 0 bootstrap (sudo scripts/bootstrap-laplace-runner.sh
-- bootstrap) must have created the laplace_priv schema + wrappers. If they
-- are missing (e.g., after a db-nuke that dropped them along with the
-- database), bootstrap must be re-run before this migration succeeds.
--
-- All operations are idempotent. DbUp records this script name in the
-- SchemaVersions table on success so re-runs are no-ops by default; the
-- wrapper itself uses CREATE EXTENSION IF NOT EXISTS internally.

-- ============================================================
-- Step 1: postgis (superuser-required → via wrapper)
-- ============================================================
-- postgis is NOT trusted in stock packaging → laplace_admin can't install
-- it directly. The wrapper runs as postgres via SECURITY DEFINER. If
-- postgis is already installed (bootstrap installed it during Layer 0),
-- the IF NOT EXISTS inside the wrapper short-circuits to a NOTICE.
SELECT laplace_priv.install_extension('postgis');

-- ============================================================
-- Step 2: laplace (laplace.control says superuser = false → DIRECT install)
-- ============================================================
-- The laplace extension is marked superuser=false in its .control file
-- (per ADR 0023), so laplace_admin (DB owner, has CREATE on the laplace
-- DB) can install it directly via plain CREATE EXTENSION. Installing
-- DIRECTLY (not via SECURITY DEFINER wrapper) means the laplace schema
-- gets created with laplace_admin as owner — laplace_admin can then
-- grant USAGE on it to laplace_app + laplace_readonly in Step 3.
--
-- (Using the wrapper for laplace would make postgres the schema owner,
-- and the subsequent GRANT USAGE in this same migration would fail with
-- "permission denied for schema laplace" since laplace_admin wouldn't
-- own it.)
--
-- The laplace extension's binary + .control + .sql files must be installed
-- in PG's extension dirs before this runs. The CI flow:
--   cd extension && sudo make install PG_CONFIG=...   (bounded sudo per ADR 0019)
--   dotnet run --project app/Laplace.Migrations -- up
CREATE EXTENSION IF NOT EXISTS laplace;

-- Future: when bumping default_version in extension/laplace.control, a
-- separate timestamped migration file calls ALTER EXTENSION laplace UPDATE.
-- Substrate schema changes go in the extension's upgrade scripts (per ADR
-- 0023), not in DbUp migrations.

-- ============================================================
-- Step 3: Role USAGE grants on the 'laplace' schema
-- ============================================================
-- The schema is created by the laplace extension's .sql file (per ADR 0023).
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
-- Step 4: Default privileges for future extension-owned objects
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
