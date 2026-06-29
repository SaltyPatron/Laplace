\timing on
\set ON_ERROR_STOP on
BEGIN;

CREATE TEMP TABLE _novel_phys ON COMMIT DROP AS
SELECT gen_random_uuid()::bytea AS id,
       (SELECT id FROM laplace.entities LIMIT 1) AS entity_id,
       1::smallint AS type,
       point(0,0) AS coord,
       (random()*9223372036854775807)::bigint AS hilbert_index,
       NULL::bytea AS trajectory,
       1::smallint AS n_constituents,
       0.0::double precision AS alignment_residual,
       4::smallint AS source_dim,
       now() AS observed_at
FROM generate_series(1, 5000);

\echo '=== INSERT 5000 novel physicalities (live GiST + all indexes) ==='
EXPLAIN (ANALYZE, BUFFERS, VERBOSE)
INSERT INTO laplace.physicalities
  (id, entity_id, type, coord, hilbert_index, trajectory,
   n_constituents, alignment_residual, source_dim, observed_at)
SELECT id, entity_id, type, coord, hilbert_index, trajectory,
       n_constituents, alignment_residual, source_dim, observed_at
FROM _novel_phys
ORDER BY hilbert_index;

ROLLBACK;
