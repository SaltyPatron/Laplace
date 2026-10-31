\set ON_ERROR_STOP on
SET statement_timeout='30s';
BEGIN;
CREATE TEMP TABLE probe_ids(name text,id bytea);
INSERT INTO probe_ids SELECT x,laplace.word_id(x) FROM unnest(ARRAY['β','γ','δ','λ','μ','ν']) x;
SELECT name,encode(id,'hex') AS id FROM probe_ids;
INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
SELECT laplace.consensus_id(laplace.word_id(s),laplace.relation_type_id(t),laplace.word_id(o)),
 laplace.word_id(s),laplace.relation_type_id(t),laplace.word_id(o),r,30000000000,60000000,5,now()
FROM (VALUES ('β','RELATED_TO','γ',3000000000000::bigint),('β','RELATED_TO','δ',2000000000000::bigint),
 ('γ','RELATED_TO','β',2000000000000::bigint),('δ','CAUSES','λ',2000000000000::bigint),('λ','CAUSES','μ',2000000000000::bigint)) c(s,t,o,r);
SELECT f.step,p.name,encode(f.entity,'hex') AS entity FROM generation.forward_prompt('β',2,0,0.0,8,7,1,8,NULL,ARRAY[laplace.relation_type_id('CAUSES')]) f LEFT JOIN probe_ids p ON p.id=f.entity ORDER BY f.step;
SELECT f.step,f.event,p.name,f.routing_round,f.ordered_context_count,f.declared_result,f.candidate_count
FROM generation.forward_trace('β',2,0,0.0,8,7,1,8,NULL,ARRAY[laplace.relation_type_id('CAUSES')]) f LEFT JOIN probe_ids p ON p.id=f.entity;
ROLLBACK;
