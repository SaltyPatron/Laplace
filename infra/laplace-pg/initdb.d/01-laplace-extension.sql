-- laplace-pg first-boot init: load PostGIS + laplace_pg substrate extension into
-- the laplace database. Runs ONCE when PGDATA is empty (Docker postgres image
-- convention). Re-running by hand is safe via CREATE EXTENSION IF NOT EXISTS.
--
-- Substrate's GEOMETRY4D type family is INDEPENDENT of PostGIS per CLAUDE.md
-- invariant 5; the laplace_pg extension does NOT require postgis. PostGIS is
-- enabled here as ADDITIVE infrastructure available alongside laplace's 4D
-- types — used by naturally-low-dim modality decomposers (Geo / Network /
-- TimeSeries / Cad / Music) that have natural 2D / 3D representations.
\echo 'laplace init: enabling postgis (additive infrastructure)...'

CREATE EXTENSION IF NOT EXISTS postgis;

\echo 'laplace init: enabling laplace_pg extension...'

CREATE EXTENSION IF NOT EXISTS laplace_pg;

-- Smoke sanity: one round-trip through the canonical identity surface so the
-- extension is verified as loaded before anything else runs.
\echo 'laplace init: smoke testing laplace.hash_atom...'
SELECT length(laplace.hash_atom('\xdeadbeef'::bytea)) AS blake3_byte_length;

\echo 'laplace init: GEOMETRY4D smoke...'
SELECT '(1.0 0.0 0.0 0.0)'::point4d <-> '(0.0 1.0 0.0 0.0)'::point4d AS s3_quaternion_distance;

\echo 'laplace init: extension ready.'
