BEGIN;
SET LOCAL track_functions = 'all';
INSERT INTO laplace.entities(id,tier,type_id)
SELECT laplace.word_id(code),2,laplace.entity_type_id('Language')
FROM (VALUES ('eng'),('deu')) AS l(code) ON CONFLICT DO NOTHING;
INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
SELECT laplace.consensus_id(s,t,o),s,t,o,r,30000000000,60000000,1,now()
FROM (VALUES
 (laplace.word_id('b'),laplace.relation_type_id('HAS_SENSE'),public.laplace_hash128_blake3('test/render-boundary/concept'),1700000000000::bigint),
 (public.laplace_hash128_blake3('test/render-boundary/named'),laplace.relation_type_id('HAS_NAME'),laplace.word_id('a'),1800000000000::bigint),
 (public.laplace_hash128_blake3('test/render-boundary/named'),laplace.relation_type_id('HAS_NAME'),laplace.word_id('b'),1700000000000::bigint),
 (laplace.word_id('a'),laplace.relation_type_id('HAS_LANGUAGE'),laplace.word_id('eng'),1700000000000::bigint),
 (laplace.word_id('b'),laplace.relation_type_id('HAS_LANGUAGE'),laplace.word_id('deu'),1700000000000::bigint)
) AS fixture(s,t,o,r);
