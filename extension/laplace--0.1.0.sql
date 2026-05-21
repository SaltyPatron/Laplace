-- Laplace extension v0.1.0 — initialization
--
-- Per ADR 0023, this file is the source of truth for the substrate's
-- schema: the 'laplace' schema, the three core tables
-- (entities / physicalities / attestations), custom composite types,
-- GIST opclasses, SRFs, aggregates, and indexes.
--
-- This 0.1.0 release intentionally lands a MINIMAL surface:
--   - the 'laplace' schema (auto-created by the extension framework
--     because laplace.control sets schema = 'laplace')
--   - laplace_version() so CREATE EXTENSION succeeds and smoke tests work
--
-- The substrate's three core tables, types, and functions land in the
-- 0.1.0 → 0.2.0 upgrade script (laplace--0.1.0--0.2.0.sql) per the
-- DESIGN.md chunk plan. Each chunk that introduces new substrate objects
-- bumps default_version in laplace.control and ships its own upgrade SQL.

\echo Use "CREATE EXTENSION laplace" to load this file. \quit

-- Version probe — referenced by smoke tests + Layer-1 migration verification.
CREATE FUNCTION @extschema@.laplace_version()
    RETURNS text
    AS 'MODULE_PATHNAME', 'pg_laplace_version'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

COMMENT ON FUNCTION @extschema@.laplace_version() IS
    'Returns the Laplace engine version string (matches default_version in laplace.control)';

-- Future (each in its own laplace--A.B.C--D.E.F.sql upgrade script):
--   - CREATE TYPE laplace.entity_kind, laplace.attestation_kind, ...
--   - CREATE TABLE laplace.entities (...)
--     SELECT pg_extension_config_dump('laplace.entities', '');
--   - CREATE TABLE laplace.physicalities (...)
--     SELECT pg_extension_config_dump('laplace.physicalities', '');
--   - CREATE TABLE laplace.attestations (...)
--     SELECT pg_extension_config_dump('laplace.attestations', '');
--   - GIST indexes on geometry (Z+M) columns with gist_geometry_ops_nd
--   - Cascade-tier SRFs, Glicko-2 aggregate, A* helpers
