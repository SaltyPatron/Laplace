-- Scale test: does HILBERT insert order help when the target index is LARGE (live 38M physicalities)?
-- 300k novel rows inserted into the real table, random vs hilbert order, each in a rolled-back tx.
-- This is where the random-I/O "cliff" would show (target index pages scattered vs contiguous).
-- Run: psql -h localhost -U postgres -d laplace -P pager=off -f docs/bench/hilbert_scale_insert_bench.sql
SET search_path = laplace, public;
\timing on
DROP TABLE IF EXISTS bench_src;
CREATE UNLOGGED TABLE bench_src AS
  SELECT entity_id, type, coord, hilbert_index, trajectory, n_constituents,
         alignment_residual, source_dim, observed_at
  FROM physicalities TABLESAMPLE SYSTEM (2) LIMIT 300000;
ANALYZE bench_src;
SELECT count(*) AS src_rows, (SELECT count(*) FROM physicalities) AS live_rows FROM bench_src;

\echo '==== SCALE-A: 300k novel rows into LIVE physicalities, RANDOM order (rolled back) ===='
BEGIN;
INSERT INTO physicalities (id, entity_id, type, coord, hilbert_index, trajectory,
                           n_constituents, alignment_residual, source_dim, observed_at)
  SELECT decode(replace(gen_random_uuid()::text,'-',''),'hex'),
         entity_id, type, coord, hilbert_index, trajectory,
         n_constituents, alignment_residual, source_dim, observed_at
  FROM bench_src ORDER BY random();
ROLLBACK;

\echo '==== SCALE-B: 300k novel rows into LIVE physicalities, HILBERT order (rolled back) ===='
BEGIN;
INSERT INTO physicalities (id, entity_id, type, coord, hilbert_index, trajectory,
                           n_constituents, alignment_residual, source_dim, observed_at)
  SELECT decode(replace(gen_random_uuid()::text,'-',''),'hex'),
         entity_id, type, coord, hilbert_index, trajectory,
         n_constituents, alignment_residual, source_dim, observed_at
  FROM bench_src ORDER BY hilbert_index;
ROLLBACK;
DROP TABLE bench_src;
