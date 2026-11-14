-- UCD recipe field tokens stay authoritative in serving classification.
BEGIN;
DO $source_predicates$
DECLARE
    separator bytea := public.blake3_send(public.laplace_text_id('test/source-predicates/separator'));
    legacy bytea := public.blake3_send(public.laplace_text_id('test/source-predicates/retired-predicate'));
    source bytea := public.blake3_send(public.laplace_text_id('test/source-predicates/source'));
    category bytea := laplace.property_value_id('General_Category','Space_Separator');
    letter bytea := public.blake3_send(public.laplace_text_id('A'));
    actual int[];
BEGIN
    INSERT INTO laplace.entities(id,tier,type_id)
    SELECT id,0,laplace.entity_type_id('Codepoint')
    FROM unnest(ARRAY[separator,legacy,source,category]) i(id);
    INSERT INTO laplace.attestations
        (id,subject_id,type_id,object_id,source_id,outcome,last_observed_at,
         observation_count,sum_score_fp1e9,opponent_rd_fp1e9)
    VALUES
        (public.blake3_send(public.laplace_text_id('test/source-predicates/gc')),separator,
         laplace.relation_type_id('gc'),category,source,2,now(),1,1000000000,30000000000),
        (public.blake3_send(public.laplace_text_id('test/source-predicates/old')),legacy,
         laplace.relation_type_id('HAS_CHARACTER_PROPERTY'),category,source,2,now(),1,1000000000,30000000000);
    -- The separator set is Native: General_Category Zs, Zl, Zp and Cc read from the
    -- UCD-built Unicode floor (tier-0 flags), plus CRLF. It no longer reads gc claims
    -- from laplace.attestations, so the attested fixture ids above are not in it; the
    -- UCD's gc field stays authoritative through the floor instead.
    IF NOT public.laplace_text_id(' ')=ANY(public.laplace_separator_ids())          -- Zs
       OR NOT public.laplace_text_id(U&'\2028')=ANY(public.laplace_separator_ids())  -- Zl
       OR NOT public.laplace_text_id(U&'\2029')=ANY(public.laplace_separator_ids())  -- Zp
       OR NOT public.laplace_text_id(E'\t')=ANY(public.laplace_separator_ids())      -- Cc
       OR NOT public.laplace_text_id(E'\r\n')=ANY(public.laplace_separator_ids())    -- CRLF
       OR public.laplace_text_id('A')=ANY(public.laplace_separator_ids())            -- Lu
       OR public.laplace_text_id('_')=ANY(public.laplace_separator_ids()) THEN       -- Pc
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
