-- Shared admission: mixed relations, repeated witnesses, unary/binary cells and
-- hot subjects. Exercise the database owner, including its nested SPI queries.
\set ON_ERROR_STOP on
\if :{?evidence_batch_explain}
\timing on
\endif
BEGIN;
SET LOCAL statement_timeout = '90s';
SET LOCAL pg_stat_statements.track = 'all';
CREATE TEMP TABLE evidence_batch_targets AS
SELECT n,
       decode(md5('evidence-batch/subject/' || (n / 4)), 'hex') AS s,
       decode(md5('evidence-batch/type/' || (n % 32)), 'hex') AS t,
       CASE WHEN n % 3 = 0 THEN NULL::bytea
            ELSE decode(md5('evidence-batch/object/' || n), 'hex') END AS o
FROM generate_series(1,4096) AS n;
INSERT INTO laplace.attestations
    (id,subject_id,type_id,object_id,source_id,outcome,last_observed_at,
     observation_count,sum_score_fp1e9,opponent_rating_fp1e9,opponent_rd_fp1e9)
SELECT decode(md5('evidence-batch/receipt/' || n || '/' || w), 'hex'),s,t,o,
       decode(md5('evidence-batch/source/' || w), 'hex'),2,
       '2026-01-01 00:00:00Z'::timestamptz + w * interval '1 second',
       w,w * 500000000::bigint,1500000000000::bigint + w * 10000000000::bigint,
       30000000000::bigint + w * 1000000000::bigint
FROM evidence_batch_targets CROSS JOIN generate_series(1,7) AS w;
ANALYZE laplace.attestations;
CREATE TEMP TABLE evidence_batch_input AS
SELECT array_agg(s ORDER BY t,n) AS s,array_agg(t ORDER BY t,n) AS t,
       array_agg(o ORDER BY t,n) AS o,array_agg(30000000000::bigint ORDER BY t,n) AS phi,
       array_agg(1::bigint ORDER BY t,n) AS games,
       array_agg(500000000::bigint ORDER BY t,n) AS sums,
       array_agg('2026-01-01 00:00:00Z'::timestamptz ORDER BY t,n) AS ts
FROM evidence_batch_targets;
\if :{?evidence_batch_explain}
EXPLAIN (ANALYZE,BUFFERS,WAL,SETTINGS)
\endif
SELECT consensus.merge_evidence(s,t,o,phi,games,sums,ts) FROM evidence_batch_input;
CREATE TEMP TABLE evidence_batch_expected AS
SELECT c.* FROM laplace.consensus c JOIN evidence_batch_targets b
 ON (c.subject_id,c.type_id)=(b.s,b.t) AND c.object_id IS NOT DISTINCT FROM b.o;
\if :{?evidence_batch_explain}
EXPLAIN (ANALYZE,BUFFERS,WAL,SETTINGS)
\endif
SELECT consensus.merge_evidence(s,t,o,phi,games,sums,ts) FROM evidence_batch_input;
SET LOCAL plan_cache_mode = 'force_generic_plan';
SELECT consensus.merge_evidence(s,t,o,phi,games,sums,ts) FROM evidence_batch_input;
SET LOCAL plan_cache_mode = 'force_custom_plan';
SELECT consensus.merge_evidence(s,t,o,phi,games,sums,ts) FROM evidence_batch_input;
SET LOCAL plan_cache_mode = 'auto';
DO $$
BEGIN
 IF (SELECT count(*) FROM evidence_batch_expected) <> 4096 THEN
   RAISE EXCEPTION 'mixed evidence batch lost cells';
 END IF;
 IF EXISTS (SELECT FROM evidence_batch_expected e
   LEFT JOIN laplace.consensus c USING(id,type_id,subject_id)
   WHERE c.id IS NULL OR (c.rating,c.rd,c.volatility,c.witness_count,c.last_observed_at)
     IS DISTINCT FROM (e.rating,e.rd,e.volatility,e.witness_count,e.last_observed_at)) THEN
   RAISE EXCEPTION 'matched evidence changed its exact folded state on replay';
 END IF;
 IF EXISTS (SELECT FROM evidence_batch_expected
   WHERE witness_count <> 28 OR last_observed_at <> '2026-01-01 00:00:07Z') THEN
   RAISE EXCEPTION 'mixed evidence batch lost witness counts or observation time';
 END IF;
 BEGIN
   CALL ops.evict_source(decode(md5('evidence-batch/source/1'),'hex'),p_batch=>0);
   RAISE EXCEPTION 'zero-width eviction was accepted';
 EXCEPTION WHEN invalid_parameter_value THEN NULL;
 END;
 BEGIN
   CALL ops.evict_source(decode('01','hex'));
   RAISE EXCEPTION 'malformed source identity was accepted';
 EXCEPTION WHEN invalid_parameter_value THEN NULL;
 END;
END $$;
SELECT 'PASS: mixed native evidence batch retains exact state, 4096 cells, 114688 observations' AS result;

-- Operational rows may have zero observations, but they cannot become one
-- invented vote when a caller requests a rating refold of that cell.
INSERT INTO laplace.attestations
 (id,subject_id,type_id,object_id,source_id,outcome,last_observed_at,
  observation_count,sum_score_fp1e9,opponent_rating_fp1e9,opponent_rd_fp1e9)
SELECT decode(md5('evidence-batch/zero-receipt'),'hex'),s,t,o,
 decode(md5('evidence-batch/zero-source'),'hex'),2,'2026-01-01 00:00:08Z',
 0,0,1500000000000,30000000000 FROM evidence_batch_targets WHERE n=1;
DO $$ DECLARE target record;
BEGIN
 SELECT * INTO target FROM evidence_batch_targets WHERE n=1;
 BEGIN
   PERFORM consensus.refold_evidence_type(target.t,ARRAY[target.s],ARRAY[target.o]);
   RAISE EXCEPTION 'zero observations became an invented rating vote';
 EXCEPTION WHEN data_exception THEN
   IF SQLERRM NOT LIKE '%non-positive durable observation count%' THEN RAISE; END IF;
 END;
END $$;
DELETE FROM laplace.attestations WHERE id=decode(md5('evidence-batch/zero-receipt'),'hex');

-- Cached owner plans must not bypass parent permissions or row policies. The
-- ordinary ingest role may have a grant on the parent without leaf grants.
CREATE ROLE evidence_batch_reader;
GRANT USAGE ON SCHEMA laplace,consensus,ops TO evidence_batch_reader;
GRANT ALL ON ALL TABLES IN SCHEMA laplace TO evidence_batch_reader;
GRANT SELECT ON evidence_batch_input,evidence_batch_targets TO evidence_batch_reader;
DO $$ DECLARE child regclass;
BEGIN
 FOR child IN SELECT inhrelid::regclass FROM pg_inherits
   WHERE inhparent='laplace.attestations'::regclass LOOP
   EXECUTE format('REVOKE SELECT ON %s FROM evidence_batch_reader',child);
 END LOOP;
END $$;
SET LOCAL ROLE evidence_batch_reader;
SELECT consensus.merge_evidence(s,t,o,phi,games,sums,ts) FROM evidence_batch_input;
RESET ROLE;
ALTER TABLE laplace.attestations ENABLE ROW LEVEL SECURITY;
CREATE POLICY evidence_batch_deny ON laplace.attestations
    TO evidence_batch_reader USING (false);
SET LOCAL ROLE evidence_batch_reader;
DO $$ DECLARE target record;
BEGIN
 SELECT t,array_agg(s ORDER BY n) AS s,array_agg(o ORDER BY n) AS o INTO target
 FROM evidence_batch_targets GROUP BY t ORDER BY t LIMIT 1;
 BEGIN
   PERFORM consensus.refold_evidence_type(target.t,target.s,target.o);
   RAISE EXCEPTION 'native evidence read bypassed parent RLS';
 EXCEPTION WHEN insufficient_privilege THEN
   IF SQLERRM NOT LIKE '%complete visibility of retained testimony%' THEN RAISE; END IF;
 END;
 BEGIN
   PERFORM consensus.refold_after_retraction_type(target.t,target.s,target.o);
   RAISE EXCEPTION 'hidden evidence was treated as absent during retraction';
 EXCEPTION WHEN insufficient_privilege THEN NULL;
 END;
 BEGIN
   CALL ops.evict_source(decode(md5('evidence-batch/source/1'),'hex'),p_drain=>false);
   RAISE EXCEPTION 'source retraction ignored hidden retained testimony';
 EXCEPTION WHEN insufficient_privilege THEN NULL;
 END;
END $$;
RESET ROLE;
-- Partial visibility is more dangerous than an empty policy: the old path
-- successfully overwrote the global standing using just the visible witness.
ALTER POLICY evidence_batch_deny ON laplace.attestations
    USING (source_id=decode(md5('evidence-batch/source/1'),'hex'));
SET LOCAL ROLE evidence_batch_reader;
DO $$ DECLARE target record;
BEGIN
 SELECT t,array_agg(s ORDER BY n) AS s,array_agg(o ORDER BY n) AS o INTO target
 FROM evidence_batch_targets GROUP BY t ORDER BY t LIMIT 1;
 BEGIN
   PERFORM consensus.refold_evidence_type(target.t,target.s,target.o);
   RAISE EXCEPTION 'partial testimony overwrote the global standing';
 EXCEPTION WHEN insufficient_privilege THEN NULL;
 END;
END $$;
RESET ROLE;
ALTER TABLE laplace.attestations DISABLE ROW LEVEL SECURITY;
-- Cached exact-leaf priors and updates still need the parent's authority.
ALTER TABLE laplace.consensus ENABLE ROW LEVEL SECURITY;
CREATE POLICY evidence_batch_standing_deny ON laplace.consensus
    TO evidence_batch_reader USING (false);
SET LOCAL ROLE evidence_batch_reader;
DO $$ BEGIN
 BEGIN
   PERFORM consensus.merge_evidence(s,t,o,phi,games,sums,ts) FROM evidence_batch_input;
   RAISE EXCEPTION 'cached native mutation bypassed consensus parent RLS';
 EXCEPTION WHEN insufficient_privilege THEN NULL;
 END;
END $$;
RESET ROLE;
ALTER TABLE laplace.consensus DISABLE ROW LEVEL SECURITY;
REVOKE UPDATE ON laplace.consensus FROM evidence_batch_reader;
SET LOCAL ROLE evidence_batch_reader;
DO $$ BEGIN
 BEGIN
   PERFORM consensus.merge_evidence(s,t,o,phi,games,sums,ts) FROM evidence_batch_input;
   RAISE EXCEPTION 'cached native mutation bypassed consensus parent UPDATE permission';
 EXCEPTION WHEN insufficient_privilege THEN NULL;
 END;
END $$;
RESET ROLE;
GRANT UPDATE ON laplace.consensus TO evidence_batch_reader;
REVOKE SELECT ON laplace.consensus FROM evidence_batch_reader;
SET LOCAL ROLE evidence_batch_reader;
DO $$ BEGIN
 BEGIN
   PERFORM consensus.merge_evidence(s,t,o,phi,games,sums,ts) FROM evidence_batch_input;
   RAISE EXCEPTION 'cached native mutation bypassed consensus parent SELECT permission';
 EXCEPTION WHEN insufficient_privilege THEN NULL;
 END;
END $$;
RESET ROLE;
GRANT SELECT ON laplace.consensus TO evidence_batch_reader;
REVOKE SELECT ON laplace.attestations FROM evidence_batch_reader;
SET LOCAL ROLE evidence_batch_reader;
DO $$ DECLARE target record;
BEGIN
 SELECT t,array_agg(s ORDER BY n) AS s,array_agg(o ORDER BY n) AS o INTO target
 FROM evidence_batch_targets GROUP BY t ORDER BY t LIMIT 1;
 BEGIN
   PERFORM consensus.refold_evidence_type(target.t,target.s,target.o);
   RAISE EXCEPTION 'native evidence read bypassed parent SELECT permission';
 EXCEPTION WHEN insufficient_privilege THEN NULL;
 END;
END $$;
RESET ROLE;
SELECT 'PASS: parent-only grant, parent RLS and denied SELECT retain caller semantics' AS result;
DO $$ BEGIN
 IF EXISTS (SELECT FROM evidence_batch_expected e
   LEFT JOIN laplace.consensus c USING(id,type_id,subject_id)
   WHERE c.id IS NULL OR (c.rating,c.rd,c.volatility,c.witness_count,c.last_observed_at)
     IS DISTINCT FROM (e.rating,e.rd,e.volatility,e.witness_count,e.last_observed_at)) THEN
   RAISE EXCEPTION 'refused filtered or unauthorized mutation changed the global standing';
 END IF;
END $$;
ROLLBACK;
