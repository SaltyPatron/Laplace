-- psql -v execution_library=/absolute/path/laplace_execution_<digest>.so
\set ON_ERROR_STOP on
BEGIN;
SET LOCAL statement_timeout = '60s';
SET LOCAL lock_timeout = '2s';
CREATE FUNCTION pg_temp.native_refold(bytea,bytea[],bytea[]) RETURNS bigint
AS :'execution_library', 'pg_laplace_consensus_refold_evidence_type'
LANGUAGE C VOLATILE SET plan_cache_mode=force_custom_plan;
CREATE TEMP TABLE fold_targets AS
SELECT i, public.laplace_hash128_blake3(convert_to('test/sql-batch/subject/'||i,'UTF8')) s,
       CASE WHEN i%3=0 THEN NULL::bytea ELSE public.laplace_hash128_blake3(convert_to('test/sql-batch/object/'||i,'UTF8')) END o
FROM generate_series(1,512) i;
CREATE TEMP TABLE fold_input AS
SELECT laplace.relation_type_id('IS_A') t,array_agg(s ORDER BY i) s,array_agg(o ORDER BY i) o
FROM fold_targets;
INSERT INTO laplace.attestations
    (id,subject_id,type_id,object_id,source_id,outcome,last_observed_at,
     observation_count,sum_score_fp1e9,opponent_rating_fp1e9,opponent_rd_fp1e9)
SELECT public.laplace_hash128_blake3(convert_to('test/sql-batch/evidence/'||f.i||'/'||g,'UTF8')),
       f.s, b.t,f.o,public.laplace_hash128_blake3(convert_to('test/sql-batch/source/'||g,'UTF8')),
       (g%3)::smallint,'2026-01-01'::timestamptz + g*interval '1 second',
       g, g*500000000::bigint,
       CASE WHEN g=1 THEN 0 ELSE 1500000000000::bigint + g*10000000000::bigint END,
       30000000000::bigint + g*1000000000::bigint
FROM fold_targets f CROSS JOIN fold_input b CROSS JOIN generate_series(1,7) g;
EXPLAIN (ANALYZE, BUFFERS, WAL, SETTINGS)
SELECT consensus.refold_evidence_type(t,s,o) FROM fold_input;
CREATE TEMP TABLE expected_fold AS
SELECT c.* FROM laplace.consensus c JOIN fold_targets f ON c.subject_id=f.s
WHERE c.type_id=(SELECT t FROM fold_input);
EXPLAIN (ANALYZE, BUFFERS, WAL, SETTINGS)
SELECT pg_temp.native_refold(t,s,o) FROM fold_input;
DO $$
BEGIN
    IF EXISTS (
        SELECT FROM expected_fold e
        LEFT JOIN laplace.consensus c ON (c.id,c.type_id,c.subject_id)=(e.id,e.type_id,e.subject_id)
        WHERE (c.rating,c.rd,c.volatility,c.witness_count,c.last_observed_at)
          IS DISTINCT FROM (e.rating,e.rd,e.volatility,e.witness_count,e.last_observed_at)
    ) THEN RAISE EXCEPTION 'native evidence fold differs from aggregate reference'; END IF;
    UPDATE laplace.attestations SET fold_replayable=false
    WHERE type_id=(SELECT t FROM fold_input) AND subject_id=(SELECT s FROM fold_targets WHERE i=1);
    BEGIN
        PERFORM pg_temp.native_refold(t,s,o) FROM fold_input;
        RAISE EXCEPTION 'mixed transient cell was incorrectly replayed';
    EXCEPTION WHEN data_exception THEN NULL;
    END;
END $$;
ROLLBACK;
