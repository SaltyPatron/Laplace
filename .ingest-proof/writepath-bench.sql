-- Write-path A/B microbenchmark.
-- Isolates the ONE variable: per-batch temp-table + promote (current ApplyManyAsync
-- StageAndInsertManyAsync) vs append-once + single sorted bulk-merge (SubstrateStagingMerge).
-- Identical synthetic rows, identical target indexes, same hardware. Server-side only
-- (excludes client COPY/round-trips, which only ADD to the per-batch path's overhead).
\set ECHO none
SET client_min_messages = notice;
SET synchronous_commit = off;

\echo '==== SETUP: generating synthetic entity rows ===='
DROP TABLE IF EXISTS laplace.bench_src;
CREATE UNLOGGED TABLE laplace.bench_src AS
SELECT g::bigint AS rn,
       decode(md5(g::text), 'hex')                      AS id,         -- 16 random-ish bytes
       (g % 6)::smallint                                AS tier,
       decode('00000000000000000000000000000001','hex') AS type_id,
       NULL::bytea                                      AS first_observed_by,
       now()                                            AS created_at
FROM generate_series(1, 2000000) g;
CREATE INDEX ON laplace.bench_src(rn);

DROP TABLE IF EXISTS laplace.bench_a;
DROP TABLE IF EXISTS laplace.bench_b;
CREATE TABLE laplace.bench_a (LIKE laplace.entities INCLUDING DEFAULTS INCLUDING INDEXES);
CREATE TABLE laplace.bench_b (LIKE laplace.entities INCLUDING DEFAULTS INCLUDING INDEXES);

\echo '==== PATH A: per-batch temp-table create + promote + drop (current path) ===='
DO $$
DECLARE
  bsz   int := 65536;
  total bigint;
  lo    bigint := 0;
  hi    bigint;
  t0 timestamptz; t1 timestamptz; secs double precision; n bigint;
BEGIN
  SELECT count(*) INTO total FROM laplace.bench_src;
  t0 := clock_timestamp();
  WHILE lo < total LOOP
    hi := lo + bsz;
    CREATE TEMP TABLE stg (LIKE laplace.entities INCLUDING DEFAULTS);
    INSERT INTO stg (id,tier,type_id,first_observed_by,created_at)
      SELECT id,tier,type_id,first_observed_by,created_at
      FROM laplace.bench_src WHERE rn > lo AND rn <= hi;
    INSERT INTO laplace.bench_a (id,tier,type_id,first_observed_by,created_at)
      SELECT id,tier,type_id,first_observed_by,created_at FROM stg ORDER BY id
      ON CONFLICT DO NOTHING;
    DROP TABLE stg;
    lo := hi;
  END LOOP;
  t1 := clock_timestamp();
  secs := EXTRACT(epoch FROM (t1 - t0));
  SELECT count(*) INTO n FROM laplace.bench_a;
  RAISE NOTICE 'PATH A  rows=%  secs=%  rows_per_s=%', n, round(secs::numeric,2), round((n/secs)::numeric,0);
END $$;

\echo '==== PATH B: append-all-once + single sorted bulk merge (staging path) ===='
DO $$
DECLARE
  t0 timestamptz; t1 timestamptz; secs double precision; n bigint;
BEGIN
  t0 := clock_timestamp();
  CREATE UNLOGGED TABLE laplace.bench_bstg (LIKE laplace.entities INCLUDING DEFAULTS);
  INSERT INTO laplace.bench_bstg (id,tier,type_id,first_observed_by,created_at)
    SELECT id,tier,type_id,first_observed_by,created_at FROM laplace.bench_src;
  INSERT INTO laplace.bench_b (id,tier,type_id,first_observed_by,created_at)
    SELECT id,tier,type_id,first_observed_by,created_at FROM laplace.bench_bstg ORDER BY id
    ON CONFLICT DO NOTHING;
  DROP TABLE laplace.bench_bstg;
  t1 := clock_timestamp();
  secs := EXTRACT(epoch FROM (t1 - t0));
  SELECT count(*) INTO n FROM laplace.bench_b;
  RAISE NOTICE 'PATH B  rows=%  secs=%  rows_per_s=%', n, round(secs::numeric,2), round((n/secs)::numeric,0);
END $$;

\echo '==== CLEANUP ===='
DROP TABLE IF EXISTS laplace.bench_src;
DROP TABLE IF EXISTS laplace.bench_a;
DROP TABLE IF EXISTS laplace.bench_b;
