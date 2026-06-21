-- Setup for the concurrent-GiST-insert scaling test.
\set ECHO none
SET client_min_messages = warning;
SET synchronous_commit = off;
DROP TABLE IF EXISTS laplace.bench_par_src;
CREATE UNLOGGED TABLE laplace.bench_par_src AS
SELECT g::bigint AS rn,
       decode(md5(g::text),'hex')           AS id,
       decode(md5((g+1e9)::text),'hex')     AS entity_id,
       decode('00000000000000000000000000000002','hex') AS source_id,
       1::smallint                          AS type,
       ST_MakePoint(random(),random(),random(),random()) AS coord,
       decode(md5((g+2e9)::text),'hex')     AS hilbert_index,
       1::int AS n_constituents, now() AS observed_at
FROM generate_series(1,2000000) g;
CREATE INDEX ON laplace.bench_par_src(rn);
DROP TABLE IF EXISTS laplace.bench_par;
CREATE TABLE laplace.bench_par (LIKE laplace.physicalities INCLUDING DEFAULTS INCLUDING INDEXES);
