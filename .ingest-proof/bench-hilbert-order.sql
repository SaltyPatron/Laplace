-- Does presenting physicalities in Hilbert (spatial) order vs random (content-id) order
-- fix the live-GiST insert cost? PostGIS sorts geometry by Hilbert curve by default, so
-- ORDER BY coord == spatial-locality order == what the perfcache-computed hilbert_index gives.
\set ECHO none
SET client_min_messages = notice;
SET synchronous_commit = off;
SET maintenance_work_mem = '2GB';

DROP TABLE IF EXISTS laplace.bench_hsrc;
CREATE UNLOGGED TABLE laplace.bench_hsrc AS
SELECT decode(md5(g::text),'hex')           AS id,
       decode(md5((g+1e9)::text),'hex')     AS entity_id,
       decode('00000000000000000000000000000002','hex') AS source_id,
       1::smallint                          AS type,
       ST_MakePoint(random(),random(),random(),random()) AS coord,
       decode(md5((g+2e9)::text),'hex')     AS hilbert_index,
       1::int AS n_constituents, now() AS observed_at
FROM generate_series(1,1000000) g;

DROP TABLE IF EXISTS laplace.bench_hrand;
DROP TABLE IF EXISTS laplace.bench_hsort;
CREATE TABLE laplace.bench_hrand (LIKE laplace.physicalities INCLUDING DEFAULTS INCLUDING INDEXES);
CREATE TABLE laplace.bench_hsort (LIKE laplace.physicalities INCLUDING DEFAULTS INCLUDING INDEXES);

\echo '==== RANDOM ORDER (insert sorted by content-id == spatially random) ===='
DO $$
DECLARE t0 timestamptz; secs double precision;
BEGIN
  t0 := clock_timestamp();
  INSERT INTO laplace.bench_hrand (id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at)
    SELECT id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at
    FROM laplace.bench_hsrc ORDER BY id ON CONFLICT DO NOTHING;
  secs := EXTRACT(epoch FROM (clock_timestamp()-t0));
  RAISE NOTICE 'RANDOM-ORDER  secs=%  rows_per_s=%', round(secs::numeric,2), round((1000000/secs)::numeric,0);
END $$;

\echo '==== HILBERT ORDER (insert sorted by coord == spatial locality) ===='
DO $$
DECLARE t0 timestamptz; secs double precision;
BEGIN
  t0 := clock_timestamp();
  INSERT INTO laplace.bench_hsort (id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at)
    SELECT id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at
    FROM laplace.bench_hsrc ORDER BY coord ON CONFLICT DO NOTHING;
  secs := EXTRACT(epoch FROM (clock_timestamp()-t0));
  RAISE NOTICE 'HILBERT-ORDER secs=%  rows_per_s=%', round(secs::numeric,2), round((1000000/secs)::numeric,0);
END $$;

DROP TABLE IF EXISTS laplace.bench_hsrc;
DROP TABLE IF EXISTS laplace.bench_hrand;
DROP TABLE IF EXISTS laplace.bench_hsort;
