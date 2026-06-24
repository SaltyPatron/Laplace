-- Reproducible benchmark: random vs hilbert-sorted vs heap+bulk-build, indexed bulk insert.
-- Confirms (or refutes) the "random-order cliff" thesis on the REAL physicalities index set.
-- Run:  psql -h localhost -U postgres -d laplace -P pager=off -f docs/bench/hilbert_bulk_insert_bench.sql
-- Hardware of record: i9-14900KS, 48GB, 2x 990 EVO RAID-0, PG18 native Windows.
-- All tables UNLOGGED + dropped at end; reads sample real rows, writes only throwaway tables.
\set N 300000
SET search_path = laplace, public;
\timing on

DROP TABLE IF EXISTS bench_src, bench_rand, bench_hilb, bench_heap;

-- realistic source rows (real geometry/hilbert/trajectory), no indexes
CREATE UNLOGGED TABLE bench_src AS
  SELECT id, entity_id, type, coord, hilbert_index, trajectory, n_constituents,
         alignment_residual, source_dim, observed_at
  FROM physicalities TABLESAMPLE SYSTEM (2) LIMIT :N;
ANALYZE bench_src;
SELECT count(*) AS src_rows FROM bench_src;

\echo '==== A: RANDOM-order bulk insert into fully-indexed table ===='
CREATE UNLOGGED TABLE bench_rand (LIKE physicalities INCLUDING GENERATED);
ALTER TABLE bench_rand ADD PRIMARY KEY (id);
CREATE INDEX ON bench_rand (entity_id);
CREATE INDEX ON bench_rand (type);
CREATE INDEX ON bench_rand USING gist (coord gist_geometry_ops_nd);
CREATE INDEX ON bench_rand USING btree (hilbert_index);
CREATE INDEX ON bench_rand USING gin (public.laplace_trajectory_constituent_ids(trajectory))
  WHERE type = 1 AND trajectory IS NOT NULL;
INSERT INTO bench_rand (id, entity_id, type, coord, hilbert_index, trajectory, n_constituents,
                        alignment_residual, source_dim, observed_at)
  SELECT id, entity_id, type, coord, hilbert_index, trajectory, n_constituents,
         alignment_residual, source_dim, observed_at
  FROM bench_src ORDER BY random();

\echo '==== B: HILBERT-sorted bulk insert into fully-indexed table ===='
CREATE UNLOGGED TABLE bench_hilb (LIKE physicalities INCLUDING GENERATED);
ALTER TABLE bench_hilb ADD PRIMARY KEY (id);
CREATE INDEX ON bench_hilb (entity_id);
CREATE INDEX ON bench_hilb (type);
CREATE INDEX ON bench_hilb USING gist (coord gist_geometry_ops_nd);
CREATE INDEX ON bench_hilb USING btree (hilbert_index);
CREATE INDEX ON bench_hilb USING gin (public.laplace_trajectory_constituent_ids(trajectory))
  WHERE type = 1 AND trajectory IS NOT NULL;
INSERT INTO bench_hilb (id, entity_id, type, coord, hilbert_index, trajectory, n_constituents,
                        alignment_residual, source_dim, observed_at)
  SELECT id, entity_id, type, coord, hilbert_index, trajectory, n_constituents,
         alignment_residual, source_dim, observed_at
  FROM bench_src ORDER BY hilbert_index;

\echo '==== C1: HEAP-only insert (no indexes) ===='
CREATE UNLOGGED TABLE bench_heap (LIKE physicalities INCLUDING GENERATED);
INSERT INTO bench_heap (id, entity_id, type, coord, hilbert_index, trajectory, n_constituents,
                        alignment_residual, source_dim, observed_at)
  SELECT id, entity_id, type, coord, hilbert_index, trajectory, n_constituents,
         alignment_residual, source_dim, observed_at
  FROM bench_src ORDER BY hilbert_index;

\echo '==== C2: BULK index build AFTER load (sorted heap) ===='
ALTER TABLE bench_heap ADD PRIMARY KEY (id);
CREATE INDEX ON bench_heap (entity_id);
CREATE INDEX ON bench_heap (type);
CREATE INDEX ON bench_heap USING gist (coord gist_geometry_ops_nd);
CREATE INDEX ON bench_heap USING btree (hilbert_index);
CREATE INDEX ON bench_heap USING gin (public.laplace_trajectory_constituent_ids(trajectory))
  WHERE type = 1 AND trajectory IS NOT NULL;

DROP TABLE IF EXISTS bench_src, bench_rand, bench_hilb, bench_heap;
