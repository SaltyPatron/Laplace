-- Migration 20260521000000_initial_extensions
--
-- Layer-1 extension lifecycle orchestration. Per ADR 0021 (DbUp + Npgsql)
-- and ADR 0023 (extension owns substrate schema; DbUp orchestrates).
--
-- This migration is intentionally short:
--   1. CREATE EXTENSION postgis      — required by RULES.md R2 (we extend PostGIS)
--   2. CREATE EXTENSION laplace      — the extension's own .sql files own the
--                                      schema, tables, types, functions
--   3. Schema-level role grants      — USAGE for app + readonly on the
--                                      laplace schema (the schema is created
--                                      by the extension's .sql file)
--   4. Default privileges            — so future tables created by
--                                      laplace_admin in the laplace schema
--                                      pick up the right grants automatically
--
-- All operations are idempotent. DbUp records this script name in the
-- SchemaVersions table on success so re-runs are no-ops by default; the SQL
-- itself is also re-entrant via IF NOT EXISTS / OR REPLACE / DO $$ blocks.

CREATE EXTENSION IF NOT EXISTS postgis;

-- The laplace extension's binary + .control + .sql files must be installed
-- in PG's extension dirs before this runs. The CI flow:
--   cd extension && sudo make install PG_CONFIG=...   (bounded sudo per ADR 0019)
--   dotnet run --project app/Laplace.Migrations -- up
CREATE EXTENSION IF NOT EXISTS laplace;

-- Future: when bumping default_version in extension/laplace.control, this
-- migration can include a parallel ALTER EXTENSION laplace UPDATE; DbUp's
-- idempotency comes from script-name tracking + IF NOT EXISTS, not from
-- re-running ALTER EXTENSION on already-current installs. When that day
-- comes, the upgrade goes into a NEW migration file, not this one.

-- Role grants for the laplace schema (the schema itself is created by the
-- extension's .sql file).
DO $$
BEGIN
    -- USAGE so app + readonly roles can resolve schema-qualified names
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_app') THEN
        EXECUTE 'GRANT USAGE ON SCHEMA laplace TO laplace_app';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_readonly') THEN
        EXECUTE 'GRANT USAGE ON SCHEMA laplace TO laplace_readonly';
    END IF;
END $$;

-- Default privileges: when laplace_admin creates new tables/sequences in
-- the laplace schema (via future extension upgrades), app + readonly pick
-- them up automatically.
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
