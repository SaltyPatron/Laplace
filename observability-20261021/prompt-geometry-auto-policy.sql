\set ON_ERROR_STOP on
\pset format unaligned
\pset tuples_only on
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
SET pg_stat_statements.track='all';
SET pg_stat_statements.track_planning=on;
SET plan_cache_mode=auto;
SET statement_timeout='60s';
SELECT pg_stat_statements_reset();
BEGIN;
INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
SELECT laplace.consensus_id(root,rel,target),root,rel,target,3000000000000,30000000000,60000000,5,now()
FROM (SELECT laplace.word_id('β') root,laplace.relation_type_id('CAUSES') rel) p
CROSS JOIN (SELECT laplace.word_id(value) target FROM unnest(ARRAY['κ','λ','μ','ν','ξ','ο','π','ρ']) value) c;
DO $probe$
BEGIN
 FOR attempt IN 1..8 LOOP
  PERFORM * FROM generation.forward_program('β',1,0,0.0,8,7,0,8,NULL,ARRAY[laplace.relation_type_id('CAUSES')]);
 END LOOP;
END $probe$;
SELECT jsonb_build_object('query_kind',CASE WHEN query LIKE 'WITH nearest%' THEN 'angular' ELSE 'hilbert' END,
 'calls',calls,'plans',plans,'planning_ms',round(total_plan_time::numeric,3),'execution_ms',round(total_exec_time::numeric,3))
FROM pg_stat_statements WHERE dbid=(SELECT oid FROM pg_database WHERE datname=current_database())
 AND (query LIKE 'WITH nearest AS MATERIALIZED%' OR query LIKE 'WITH same AS MATERIALIZED%') ORDER BY query;
ROLLBACK;
