\set ON_ERROR_STOP on
BEGIN;
CREATE TEMP TABLE facet_probe AS
SELECT public.laplace_hash128_blake3(convert_to('test/query-facets/'||i,'UTF8')) AS id,i
FROM generate_series(1,200) g(i);
INSERT INTO laplace.entities(id,tier,type_id)
SELECT id,4,laplace.entity_type_id('Document') FROM facet_probe;
INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
SELECT laplace.consensus_id(p.id,laplace.relation_type_id('IS_TYPED_AS'),v.type_id),p.id,
       laplace.relation_type_id('IS_TYPED_AS'),v.type_id,1500000000000+v.rank,30000000000,60000000,1,now()
FROM facet_probe p CROSS JOIN (VALUES (laplace.entity_type_id('Word'),1::bigint),(laplace.entity_type_id('Sentence'),2::bigint),(NULL::bytea,3::bigint)) v(type_id,rank)
WHERE p.i%3<>0 OR v.rank<3;
DO $facet_parity$
DECLARE differences bigint;
BEGIN
 WITH expected AS (
   SELECT e.id,e.tier,COALESCE((SELECT c.object_id FROM laplace.consensus c
     WHERE c.subject_id=e.id AND c.type_id=ANY(consensus.relation_family_ids('IS_TYPED_AS'))
     ORDER BY c.rating DESC,c.object_id ASC LIMIT 1),e.type_id) AS type_id
   FROM laplace.entities e JOIN facet_probe p ON p.id=e.id
 ), actual AS (
   SELECT entity_id AS id,tier,type_id FROM ops.entity_facets_batch(ARRAY(SELECT id FROM facet_probe))
 ), differences AS ((SELECT * FROM actual EXCEPT SELECT * FROM expected)
                   UNION ALL (SELECT * FROM expected EXCEPT SELECT * FROM actual))
 SELECT count(*) INTO differences FROM differences;
 IF differences<>0 THEN RAISE EXCEPTION 'set facet typing differs from the prior ranked rule for % rows',differences; END IF;
 RAISE NOTICE '200 native facet rows match prior typing, including strongest NULL fallback';
END $facet_parity$;
EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) SELECT * FROM ops.entity_facets_batch(ARRAY(SELECT id FROM facet_probe));
ROLLBACK;
LOAD 'auto_explain';
SET auto_explain.log_min_duration=0;
SET auto_explain.log_nested_statements=on;
SET auto_explain.log_analyze=on;
SET auto_explain.log_format=json;
SET auto_explain.log_level=notice;
SET client_min_messages=notice;
\echo GENERIC
SET plan_cache_mode=force_generic_plan;
SELECT * FROM realize.display_label_batch(ARRAY[decode('b7b840fb843e7b1098cd32a101a2978d','hex'),decode('be7353b51f2d16f48fcdbe7d88b48e34','hex')]);
\echo CUSTOM
SET plan_cache_mode=force_custom_plan;
SELECT * FROM realize.display_label_batch(ARRAY[decode('b7b840fb843e7b1098cd32a101a2978d','hex'),decode('be7353b51f2d16f48fcdbe7d88b48e34','hex')]);
RESET plan_cache_mode;
RESET auto_explain.log_min_duration;
SELECT count(*) AS remaining_private_fixture_rows FROM laplace.consensus;
