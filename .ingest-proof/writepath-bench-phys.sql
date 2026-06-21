-- Physicalities write-path A/B microbench. Same isolation as entities, but this table
-- carries 12 indexes incl a GiST nd-geometry index (physicalities_coord_gist) and a GIN
-- index — the heavy-index suspect for the 33k rows/s real rate.
\set ECHO none
SET client_min_messages = notice;
SET synchronous_commit = off;

\echo '==== SETUP: generating synthetic physicality rows (4D points) ===='
DROP TABLE IF EXISTS laplace.bench_psrc;
CREATE UNLOGGED TABLE laplace.bench_psrc AS
SELECT g::bigint AS rn,
       decode(md5(g::text), 'hex')                 AS id,
       decode(md5((g+1e9)::text), 'hex')           AS entity_id,
       decode('00000000000000000000000000000002','hex') AS source_id,
       1::smallint                                 AS type,
       ST_MakePoint(random(), random(), random(), random()) AS coord,
       decode(md5((g+2e9)::text), 'hex')           AS hilbert_index,
       1::int                                      AS n_constituents,
       now()                                       AS observed_at
FROM generate_series(1, 1000000) g;
CREATE INDEX ON laplace.bench_psrc(rn);

DROP TABLE IF EXISTS laplace.bench_pa;
DROP TABLE IF EXISTS laplace.bench_pb;
CREATE TABLE laplace.bench_pa (LIKE laplace.physicalities INCLUDING DEFAULTS INCLUDING INDEXES);
CREATE TABLE laplace.bench_pb (LIKE laplace.physicalities INCLUDING DEFAULTS INCLUDING INDEXES);

\echo '==== PATH A: per-batch temp + promote + drop ===='
DO $$
DECLARE
  bsz int := 65536; total bigint; lo bigint := 0; hi bigint;
  t0 timestamptz; t1 timestamptz; secs double precision; n bigint;
BEGIN
  SELECT count(*) INTO total FROM laplace.bench_psrc;
  t0 := clock_timestamp();
  WHILE lo < total LOOP
    hi := lo + bsz;
    CREATE TEMP TABLE pstg (LIKE laplace.physicalities INCLUDING DEFAULTS);
    INSERT INTO pstg (id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at)
      SELECT id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at
      FROM laplace.bench_psrc WHERE rn > lo AND rn <= hi;
    INSERT INTO laplace.bench_pa (id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at)
      SELECT id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at FROM pstg ORDER BY id
      ON CONFLICT DO NOTHING;
    DROP TABLE pstg;
    lo := hi;
  END LOOP;
  t1 := clock_timestamp();
  secs := EXTRACT(epoch FROM (t1 - t0));
  SELECT count(*) INTO n FROM laplace.bench_pa;
  RAISE NOTICE 'PHYS PATH A  rows=%  secs=%  rows_per_s=%', n, round(secs::numeric,2), round((n/secs)::numeric,0);
END $$;

\echo '==== PATH B: append-once + single sorted merge ===='
DO $$
DECLARE t0 timestamptz; t1 timestamptz; secs double precision; n bigint;
BEGIN
  t0 := clock_timestamp();
  CREATE UNLOGGED TABLE laplace.bench_pbstg (LIKE laplace.physicalities INCLUDING DEFAULTS);
  INSERT INTO laplace.bench_pbstg (id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at)
    SELECT id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at FROM laplace.bench_psrc;
  INSERT INTO laplace.bench_pb (id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at)
    SELECT id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at FROM laplace.bench_pbstg ORDER BY id
    ON CONFLICT DO NOTHING;
  DROP TABLE laplace.bench_pbstg;
  t1 := clock_timestamp();
  secs := EXTRACT(epoch FROM (t1 - t0));
  SELECT count(*) INTO n FROM laplace.bench_pb;
  RAISE NOTICE 'PHYS PATH B  rows=%  secs=%  rows_per_s=%', n, round(secs::numeric,2), round((n/secs)::numeric,0);
END $$;

\echo '==== per-index isolation: cost of building each index over 1M rows ===='
DO $$
DECLARE t0 timestamptz; secs double precision;
BEGIN
  CREATE UNLOGGED TABLE laplace.bench_pnoidx (LIKE laplace.physicalities INCLUDING DEFAULTS);
  t0 := clock_timestamp();
  INSERT INTO laplace.bench_pnoidx (id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at)
    SELECT id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at FROM laplace.bench_psrc;
  secs := EXTRACT(epoch FROM (clock_timestamp() - t0));
  RAISE NOTICE 'NO-INDEX insert 1M rows: secs=%  rows_per_s=%', round(secs::numeric,2), round((1000000/secs)::numeric,0);

  t0 := clock_timestamp();
  CREATE INDEX bni_gist ON laplace.bench_pnoidx USING gist (coord gist_geometry_ops_nd);
  RAISE NOTICE 'build GiST coord index: secs=%', round(EXTRACT(epoch FROM (clock_timestamp()-t0))::numeric,2);

  t0 := clock_timestamp();
  CREATE UNIQUE INDEX bni_pk ON laplace.bench_pnoidx (id);
  RAISE NOTICE 'build PK btree(id): secs=%', round(EXTRACT(epoch FROM (clock_timestamp()-t0))::numeric,2);

  t0 := clock_timestamp();
  CREATE INDEX bni_hil ON laplace.bench_pnoidx (hilbert_index);
  RAISE NOTICE 'build btree(hilbert): secs=%', round(EXTRACT(epoch FROM (clock_timestamp()-t0))::numeric,2);
  DROP TABLE laplace.bench_pnoidx;
END $$;

\echo '==== CLEANUP ===='
DROP TABLE IF EXISTS laplace.bench_psrc;
DROP TABLE IF EXISTS laplace.bench_pa;
DROP TABLE IF EXISTS laplace.bench_pb;
