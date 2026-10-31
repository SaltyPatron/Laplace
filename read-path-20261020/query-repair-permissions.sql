\set ON_ERROR_STOP on
BEGIN;
CREATE ROLE laplace_query_permission_20261020 NOLOGIN;
GRANT USAGE ON SCHEMA laplace TO laplace_query_permission_20261020;
GRANT SELECT ON ALL TABLES IN SCHEMA laplace TO laplace_query_permission_20261020;
INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
SELECT laplace.consensus_id(s,t,o),s,t,o,1500000000000,30000000000,60000000,1,now()
FROM (VALUES(public.laplace_hash128_blake3('test/read-permission/concept'),laplace.relation_type_id('HAS_NAME'),laplace.word_id('a')),
            (laplace.word_id('b'),laplace.relation_type_id('HAS_SENSE'),public.laplace_hash128_blake3('test/read-permission/concept'))) v(s,t,o);
ALTER TABLE laplace.consensus ENABLE ROW LEVEL SECURITY;
CREATE POLICY query_permission_false ON laplace.consensus USING (false);
SET LOCAL ROLE laplace_query_permission_20261020;
DO $rls$
BEGIN
 IF (realize.resolve_name_batch(ARRAY[public.laplace_hash128_blake3('test/read-permission/concept')]))[1] IS DISTINCT FROM 'a' THEN
  RAISE EXCEPTION 'native pruning changed the existing SQL view authorization under RLS';
 END IF;
END $rls$;
RESET ROLE;
REVOKE SELECT ON laplace.consensus FROM laplace_query_permission_20261020;
SET LOCAL ROLE laplace_query_permission_20261020;
DO $view_grant$
BEGIN
 IF (realize.lexicalize_batch(ARRAY[public.laplace_hash128_blake3('test/read-permission/concept')]))[1] IS DISTINCT FROM 'b' THEN
  RAISE EXCEPTION 'native pruning denied an otherwise permitted SQL view read';
 END IF;
END $view_grant$;
RESET ROLE;
ROLLBACK;
SELECT count(*) AS surviving_probe_roles FROM pg_roles WHERE rolname='laplace_query_permission_20261020';
