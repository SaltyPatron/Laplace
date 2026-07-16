-- bench-queries.sql — read-side benchmark cells for bench-matrix.ps1.
-- Format: each query is preceded by "-- name: <cell-name>" on its own line and
-- terminated by a semicolon. Add cells freely; the driver times whatever is here.
-- Keep every query valid against a freshly-seeded laplace_bench (floor + one UD
-- file + one PGN) so the matrix never depends on a full corpus.

-- name: consensus_count
SELECT count(*) FROM laplace.consensus;

-- name: attestation_count
SELECT count(*) FROM laplace.attestations;

-- name: entity_count
SELECT count(*) FROM laplace.entities;

-- name: word_id_lookup
SELECT laplace.word_id('water');

-- name: consensus_scan_recent
SELECT count(*) FROM laplace.consensus WHERE witness_count > 1;
