INSERT INTO laplace.attestations
(id,subject_id,type_id,object_id,source_id,outcome,last_observed_at,observation_count,sum_score_fp1e9,opponent_rd_fp1e9)
SELECT decode(md5('cli-evict/receipt/'||name),'hex'),decode(md5('cli-evict/subject'),'hex'),
 laplace.relation_type_id(name),decode(md5('cli-evict/object/'||name),'hex'),
 laplace.source_id('CliEvictionAcceptance'),2,'2026-01-01Z',1,1000000000,30000000000
FROM unnest(ARRAY['IS_A','HAS_NAME','HAS_LANGUAGE']) name;