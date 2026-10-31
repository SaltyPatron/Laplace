\set ON_ERROR_STOP on
\ir render-boundary-fixture.sql
SELECT realize.forward_text_batch(ARRAY[public.laplace_hash128_blake3('test/render-boundary/concept')]);
SELECT funcid::regprocedure,calls FROM pg_stat_xact_user_functions
WHERE funcid IN ('realize.batch(bytea[],bytea)'::regprocedure,'realize.render_text_batch(bytea[],integer)'::regprocedure)
ORDER BY funcid;
DO $check$
BEGIN
 IF COALESCE((SELECT calls FROM pg_stat_xact_user_functions WHERE funcid='realize.batch(bytea[],bytea)'::regprocedure),0)<>0 THEN
   RAISE EXCEPTION 'Completed selected surface entered compatibility realization';
 END IF;
 IF (SELECT calls FROM pg_stat_xact_user_functions WHERE funcid='realize.render_text_batch(bytea[],integer)'::regprocedure)<>1 THEN
   RAISE EXCEPTION 'Completed selected surface did not use one exact materialization';
 END IF;
 IF realize.forward_text_batch(ARRAY[public.laplace_hash128_blake3('test/render-boundary/named')],laplace.word_id('deu')) IS DISTINCT FROM ARRAY['b'] THEN
   RAISE EXCEPTION 'Requested language was lost on the unresolved name frontier';
 END IF;
 IF realize.forward_text_batch(ARRAY[public.laplace_hash128_blake3('test/render-boundary/concept'),NULL,public.laplace_hash128_blake3('test/render-boundary/concept')]) IS DISTINCT FROM ARRAY['b',NULL,'b'] THEN
   RAISE EXCEPTION 'Duplicate/null input positions were lost';
 END IF;
 IF realize.forward_text_batch(array_fill(public.laplace_hash128_blake3('test/render-boundary/concept'),ARRAY[2],ARRAY[0])) IS DISTINCT FROM ARRAY['b','b'] THEN
   RAISE EXCEPTION 'Non-default input bounds changed selected identities';
 END IF;
 IF realize.forward_text_batch(ARRAY[NULL::bytea]) IS DISTINCT FROM ARRAY[NULL::text] OR
    realize.forward_text_batch(ARRAY[]::bytea[]) IS DISTINCT FROM ARRAY[]::text[] OR
    realize.forward_text_batch(NULL::bytea[]) IS NOT NULL THEN
   RAISE EXCEPTION 'Degenerate input contract changed';
 END IF;
 IF realize.resolve_name(public.laplace_hash128_blake3('test/render-boundary/named'),laplace.word_id('deu')) IS DISTINCT FROM 'b' OR
    realize._synset_lemma(public.laplace_hash128_blake3('test/render-boundary/concept'),NULL) IS DISTINCT FROM 'b' THEN
   RAISE EXCEPTION 'Scalar adapter lost native name/lexicalization behavior';
 END IF;
 IF to_regprocedure('realize._has_name(bytea,bytea)') IS NOT NULL OR
    to_regprocedure('realize._translation(bytea,bytea)') IS NOT NULL THEN
   RAISE EXCEPTION 'Retired scalar implementation remains installed';
 END IF;
 BEGIN
   PERFORM realize.forward_text_batch(ARRAY[public.laplace_hash128_blake3('test/render-boundary/missing')]);
   RAISE EXCEPTION 'Missing output became successful text';
 EXCEPTION WHEN data_exception THEN
   IF SQLERRM NOT LIKE 'forward realization: selected entity % has no truthful text surface' THEN RAISE; END IF;
 END;
 BEGIN
   PERFORM realize.forward_text_batch(ARRAY[ARRAY[laplace.word_id('b')]]);
   RAISE EXCEPTION 'Multidimensional input was silently accepted';
 EXCEPTION WHEN raise_exception THEN
   IF SQLERRM <> 'forward realization: ids must be one-dimensional' THEN RAISE; END IF;
 END;
 RAISE NOTICE 'render boundary: selected ids first; one exact materialization; language, alignment and explicit failure preserved; retired bodies absent';
END
$check$;
ROLLBACK;
