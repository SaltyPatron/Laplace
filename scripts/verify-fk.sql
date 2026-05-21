-- scripts/verify-fk.sql
-- FK integrity checks for the substrate.
-- Run via: psql -d laplace -f scripts/verify-fk.sql
--
-- All counts must be 0 for the substrate's referential integrity to hold.
-- These tables don't exist until Chunk 2 (Geometry serde) creates the schema;
-- this script errors gracefully in that case.

\set ON_ERROR_STOP off

\echo
\echo === Orphan physicalities (entity_hash references missing entity) ===
SELECT count(*) AS orphans
FROM physicalities p
LEFT JOIN entities e ON e.hash = p.entity_hash
WHERE e.hash IS NULL;

\echo
\echo === Orphan attestations on subject_hash ===
SELECT count(*) AS orphans
FROM attestations a
LEFT JOIN entities e ON e.hash = a.subject_hash
WHERE e.hash IS NULL;

\echo
\echo === Orphan attestations on kind_hash ===
SELECT count(*) AS orphans
FROM attestations a
LEFT JOIN entities e ON e.hash = a.kind_hash
WHERE e.hash IS NULL;

\echo
\echo === Orphan attestations on source_hash ===
SELECT count(*) AS orphans
FROM attestations a
LEFT JOIN entities e ON e.hash = a.source_hash
WHERE e.hash IS NULL;

\echo
\echo === Orphan attestations on object_hash (where not NULL) ===
SELECT count(*) AS orphans
FROM attestations a
LEFT JOIN entities e ON e.hash = a.object_hash
WHERE a.object_hash IS NOT NULL AND e.hash IS NULL;

\echo
\echo === Orphan attestations on context_hash (where not NULL) ===
SELECT count(*) AS orphans
FROM attestations a
LEFT JOIN entities e ON e.hash = a.context_hash
WHERE a.context_hash IS NOT NULL AND e.hash IS NULL;

\echo
\echo === All counts above MUST be 0 for FK integrity to hold ===
