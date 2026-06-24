-- Keystone test: does inserting a CONTIGUOUS hilbert range (hilbert-range partitioning) beat
-- full-range hilbert-sorted? If yes, partition by hilbert range, not id.lo%N.
-- 300k rows from the lowest contiguous hilbert band vs 300k sampled across the whole curve,
-- both inserted hilbert-ordered into the live 38M table, rolled back.
SET search_path = laplace, public;
\timing on
DROP TABLE IF EXISTS bench_narrow, bench_wide;

-- contiguous band: the 300k lowest-hilbert rows (one tight index region)
CREATE UNLOGGED TABLE bench_narrow AS
  SELECT entity_id, type, coord, hilbert_index, trajectory, n_constituents,
         alignment_residual, source_dim, observed_at
  FROM physicalities ORDER BY hilbert_index LIMIT 300000;
-- wide: 300k sampled across the whole curve (spans the whole index)
CREATE UNLOGGED TABLE bench_wide AS
  SELECT entity_id, type, coord, hilbert_index, trajectory, n_constituents,
         alignment_residual, source_dim, observed_at
  FROM physicalities TABLESAMPLE SYSTEM (2) LIMIT 300000;
ANALYZE bench_narrow; ANALYZE bench_wide;

\echo '==== NARROW contiguous hilbert band, 300k into live (rolled back) ===='
BEGIN;
INSERT INTO physicalities (id, entity_id, type, coord, hilbert_index, trajectory,
                           n_constituents, alignment_residual, source_dim, observed_at)
  SELECT decode(replace(gen_random_uuid()::text,'-',''),'hex'),
         entity_id, type, coord, hilbert_index, trajectory,
         n_constituents, alignment_residual, source_dim, observed_at
  FROM bench_narrow ORDER BY hilbert_index;
ROLLBACK;

\echo '==== WIDE full-range hilbert-sorted, 300k into live (rolled back) ===='
BEGIN;
INSERT INTO physicalities (id, entity_id, type, coord, hilbert_index, trajectory,
                           n_constituents, alignment_residual, source_dim, observed_at)
  SELECT decode(replace(gen_random_uuid()::text,'-',''),'hex'),
         entity_id, type, coord, hilbert_index, trajectory,
         n_constituents, alignment_residual, source_dim, observed_at
  FROM bench_wide ORDER BY hilbert_index;
ROLLBACK;
DROP TABLE bench_narrow, bench_wide;
