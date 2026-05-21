-- Laplace extension v1.0.0 — initialization
--
-- Currently provides only laplace_version() so CREATE EXTENSION succeeds
-- and basic smoke-test queries work. Schema, types, custom 4D functions,
-- aggregates, and SRFs populate in Chunks 2–7 per DESIGN.md.

\echo Use "CREATE EXTENSION laplace" to load this file. \quit

CREATE FUNCTION laplace_version()
    RETURNS text
    AS 'MODULE_PATHNAME', 'pg_laplace_version'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

COMMENT ON FUNCTION laplace_version() IS
    'Returns the Laplace engine version string';
