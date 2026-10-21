-- UCD recipe field tokens stay authoritative in serving classification.
BEGIN;
DO $source_predicates$
DECLARE
    separator bytea := public.laplace_hash128_blake3('test/source-predicates/separator');
    legacy bytea := public.laplace_hash128_blake3('test/source-predicates/retired-predicate');
    source bytea := public.laplace_hash128_blake3('test/source-predicates/source');
    category bytea := laplace.property_value_id('General_Category','Space_Separator');
    letter bytea := laplace.word_id('A');
    actual int[];
BEGIN
    INSERT INTO laplace.entities(id,tier,type_id)
    SELECT id,0,laplace.entity_type_id('Codepoint')
    FROM unnest(ARRAY[separator,legacy,source,category]) i(id);
    INSERT INTO laplace.attestations
        (id,subject_id,type_id,object_id,source_id,outcome,last_observed_at,
         observation_count,sum_score_fp1e9,opponent_rd_fp1e9)
    VALUES
        (public.laplace_hash128_blake3('test/source-predicates/gc'),separator,
         laplace.relation_type_id('gc'),category,source,2,now(),1,1000000000,30000000000),
        (public.laplace_hash128_blake3('test/source-predicates/old'),legacy,
         laplace.relation_type_id('HAS_CHARACTER_PROPERTY'),category,source,2,now(),1,1000000000,30000000000);
    IF NOT separator=ANY(generation.separator_ids())
       OR legacy=ANY(generation.separator_ids()) THEN
        RAISE EXCEPTION 'Separator reader did not follow the source gc predicate';
    END IF;
    SELECT array_agg(vocab_idx ORDER BY vocab_idx) INTO actual
    FROM generation.corpus_whitespace_vocab_indices(ARRAY[separator,letter,separator]);
    IF actual IS DISTINCT FROM ARRAY[0,2]::int[] THEN
        RAISE EXCEPTION 'Whitespace classification lost source claims or duplicate ordinals: %',actual;
    END IF;
END
$source_predicates$;
ROLLBACK;
