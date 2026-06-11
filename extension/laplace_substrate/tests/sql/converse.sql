BEGIN;
SET search_path = laplace, public;

SELECT word_id('d') = laplace_hash128_blake3(convert_to('d','UTF8')) AS leaf_rule_holds;

SELECT word_id('dog') = laplace_hash128_blake3(
           ('\x01'::bytea
            || laplace_hash128_blake3(convert_to('d','UTF8'))
            || laplace_hash128_blake3(convert_to('o','UTF8'))
            || laplace_hash128_blake3(convert_to('g','UTF8')))) AS merkle_rule_holds;

SELECT word_id('') IS NULL AS empty_is_null;

DO $$
DECLARE
    type_t   bytea := laplace_hash128_blake3('substrate/type/Type/v1');
    src      bytea := laplace_hash128_blake3('test/converse/source');
    w_dog    bytea := word_id('dog');
    w_p      bytea := word_id('p');
    w_h      bytea := word_id('h');
    w_c      bytea := word_id('c');
    sense1   bytea := laplace_hash128_blake3('test/converse/sense1');
    synset1  bytea := laplace_hash128_blake3('test/converse/synset1');
    sense_b  bytea := laplace_hash128_blake3('test/converse/sense_b');
    synset_b bytea := laplace_hash128_blake3('test/converse/synset_b');
    synset2  bytea := laplace_hash128_blake3('test/converse/synset2');
    syn_bad  bytea := laplace_hash128_blake3('test/converse/synset_bad');
    gloss1   bytea := word_id('G');
    lang_en  bytea := laplace_hash128_blake3('test/converse/lang_en');
    lang_de  bytea := laplace_hash128_blake3('test/converse/lang_de');
    k_sense  bytea := relation_type_id('HAS_SENSE');
    k_senseof bytea := relation_type_id('IS_SENSE_OF');
    k_def    bytea := relation_type_id('HAS_DEFINITION');
    k_syn    bytea := relation_type_id('IS_SYNONYM_OF');
    k_member bytea := relation_type_id('IS_TRANSLATION_OF');
    k_lang   bytea := relation_type_id('HAS_LANGUAGE');
    k_isa    bytea := relation_type_id('IS_A');
    k_causes bytea := relation_type_id('CAUSES');
    k_anto   bytea := relation_type_id('IS_ANTONYM_OF');
    neutral  bigint := 1500000000000;
    sharp_rd bigint := 30000000000;
BEGIN
    INSERT INTO entities (id, tier, type_id, first_observed_by)
    VALUES (src, 0, type_t, NULL),
           (k_sense, 0, type_t, src), (k_senseof, 0, type_t, src), (k_def, 0, type_t, src),
           (k_syn, 0, type_t, src),
           (k_causes, 0, type_t, src), (k_anto, 0, type_t, src),
           (w_dog, 2, type_t, src), (w_p, 0, type_t, src), (w_h, 0, type_t, src),
           (w_c, 0, type_t, src),
           (sense1, 0, type_t, src), (synset1, 0, type_t, src),
           (sense_b, 0, type_t, src), (synset_b, 0, type_t, src),
           (synset2, 0, type_t, src), (syn_bad, 0, type_t, src),
           (gloss1, 0, type_t, src),
           (lang_en, 0, type_t, src), (lang_de, 0, type_t, src)
    ON CONFLICT DO NOTHING;

    INSERT INTO codepoint_render (id, cp)
    VALUES (gloss1, ascii('G')), (w_p, ascii('p')), (w_h, ascii('h')), (w_c, ascii('c'))
    ON CONFLICT DO NOTHING;
    INSERT INTO canonical_names (id, name)
    VALUES (lang_en, 'test/lang/en'), (lang_de, 'test/lang/de')
    ON CONFLICT DO NOTHING;
    PERFORM register_canonical('substrate/type/IS_A/v1');
    PERFORM register_canonical('substrate/type/CAUSES/v1');
    PERFORM register_canonical('substrate/type/IS_ANTONYM_OF/v1');
    PERFORM register_canonical('substrate/type/HAS_DEFINITION/v1');
    PERFORM register_canonical('substrate/type/IS_SYNONYM_OF/v1');
    PERFORM register_canonical('substrate/type/IS_TRANSLATION_OF/v1');
    PERFORM register_canonical('substrate/type/HAS_LANGUAGE/v1');

    INSERT INTO consensus (id, subject_id, type_id, object_id,
                           rating, rd, volatility, witness_count, last_observed_at)
    VALUES
      (consensus_id(w_dog,  k_sense,   sense1),  w_dog,  k_sense,   sense1,  neutral + 200000000000, sharp_rd, 60000000, 3, now()),
      (consensus_id(sense1, k_senseof, synset1), sense1, k_senseof, synset1, neutral + 200000000000, sharp_rd, 60000000, 3, now()),
      (consensus_id(w_dog,  k_sense,   sense_b), w_dog,  k_sense,   sense_b, neutral + 250000000000, sharp_rd, 60000000, 4, now()),
      (consensus_id(sense_b, k_senseof, synset_b), sense_b, k_senseof, synset_b, neutral + 200000000000, sharp_rd, 60000000, 3, now()),
      (consensus_id(synset1, k_def,    gloss1),  synset1, k_def,    gloss1,  neutral + 150000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset1, k_member, w_dog),   synset1, k_member, w_dog,   neutral + 100000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset1, k_member, w_p),     synset1, k_member, w_p,     neutral +  90000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset1, k_member, w_h),     synset1, k_member, w_h,     neutral +  80000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(w_dog,  k_syn,     synset1), w_dog,  k_syn,     synset1, neutral + 100000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(w_p,    k_syn,     synset1), w_p,    k_syn,     synset1, neutral +  90000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(w_h,    k_syn,     synset1), w_h,    k_syn,     synset1, neutral +  80000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset1, k_isa,    synset2), synset1, k_isa,    synset2, neutral + 120000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset2, k_member, w_c),     synset2, k_member, w_c,     neutral + 100000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset2, k_isa,    syn_bad), synset2, k_isa,    syn_bad, neutral - 300000000000, sharp_rd, 60000000, 3, now()),
      (consensus_id(w_dog,  k_causes,  w_h),     w_dog,  k_causes,  w_h,     neutral + 110000000000, sharp_rd, 60000000, 1, now()),
      (consensus_id(w_h,    k_causes,  synset1), w_h,    k_causes,  synset1, neutral + 200000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset1, k_anto,   w_h),     synset1, k_anto,   w_h,     neutral +  95000000000, sharp_rd, 60000000, 1, now()),
      (consensus_id(w_dog, k_lang, lang_en),     w_dog,  k_lang, lang_en,    neutral + 300000000000, sharp_rd, 60000000, 9, now()),
      (consensus_id(w_p,   k_lang, lang_en),     w_p,    k_lang, lang_en,    neutral + 250000000000, sharp_rd, 60000000, 5, now()),
      (consensus_id(w_c,   k_lang, lang_en),     w_c,    k_lang, lang_en,    neutral + 250000000000, sharp_rd, 60000000, 5, now()),
      (consensus_id(w_h,   k_lang, lang_de),     w_h,    k_lang, lang_de,    neutral + 250000000000, sharp_rd, 60000000, 5, now());
END $$;

SELECT word, (id IS NOT NULL) AS resolved FROM prompt_words('what is a Dog') ORDER BY ord;

SELECT resolve_phrase('sort a list') = word_id('sort') AS phrase_prefers_leftmost;
SELECT resolve_phrase('what is a dog') = word_id('dog') AS phrase_finds_dog;
SELECT resolve_last_word('what is a dog') = word_id('dog') AS last_word_is_dog;
SELECT resolve_last_word('zzzunknownzzz') IS NULL AS unknown_is_null;

SELECT count(*) AS dog_senses FROM senses(word_id('dog'));

SELECT definition, witnesses FROM define(word_id('dog'));

SELECT synonym FROM synonyms(word_id('dog'));

SELECT translation, language FROM translations(word_id('dog')) ORDER BY translation;

SELECT reply, witnesses FROM respond('what does dog mean?');
SELECT reply FROM respond('synonyms of dog');
SELECT reply FROM respond('translate dog');
SELECT reply FROM respond('what is zzzunknownzzz');
SELECT reply FROM respond('translate h');

SELECT word FROM prompt_state('what is a Dog') ORDER BY ord;

SELECT support,
       object_id = laplace_hash128_blake3('test/converse/lang_en') AS is_lang_en
FROM expansion(ARRAY[word_id('dog'), word_id('p')])
LIMIT 1;

SELECT (SELECT sn.synset_id FROM senses(word_id('dog')) sn LIMIT 1)
       = laplace_hash128_blake3('test/converse/synset_b') AS plain_top_is_b;
SELECT (SELECT sn.synset_id FROM senses(word_id('dog'), ARRAY[word_id('h')]) sn LIMIT 1)
       = laplace_hash128_blake3('test/converse/synset1') AS context_flips_to_1;

SELECT realize(word_id('p'), NULL) AS leaf_realizes;
SELECT realize(laplace_hash128_blake3('test/converse/synset1'),
               laplace_hash128_blake3('test/converse/lang_en')) AS synset_realizes_member;
SELECT type_label(relation_type_id('IS_A')) AS isa_label;
SELECT realize_path(ARRAY[laplace_hash128_blake3('test/converse/synset1'),
                          laplace_hash128_blake3('test/converse/synset2')],
                    ARRAY[relation_type_id('IS_A')],
                    laplace_hash128_blake3('test/converse/lang_en')) AS realized_path;

SELECT type, fact, witnesses FROM describe(word_id('dog'));
SELECT reply FROM respond('antonyms of dog');
SELECT fact FROM related_in(laplace_hash128_blake3('test/converse/synset1'), relation_type_id('CAUSES'));

SELECT reply FROM respond('is a dog a c?');
SELECT reply FROM respond('is h a c?');

SELECT g.step, type_label(g.type_id) AS rel_type,
       g.entity_id = laplace_hash128_blake3('test/converse/synset2') AS is_synset2
FROM generate_greedy(laplace_hash128_blake3('test/converse/synset1'), relation_type_id('IS_A')) g;
SELECT count(*) AS tree_nodes
FROM generate_tree(laplace_hash128_blake3('test/converse/synset1'), relation_type_id('IS_A'), 4, 5);
SELECT reply, eff_mu FROM respond('walk p');

SELECT reply, witnesses FROM converse('what does dog mean?', convert_to('s1', 'UTF8'));
SELECT reply FROM converse('what about its synonyms?', convert_to('s1', 'UTF8'));
SELECT reply, witnesses FROM converse('and its causes?', convert_to('s1', 'UTF8'));
SELECT ord, prompt, resolved_id = word_id('dog') AS topic_is_dog
FROM converse_turns WHERE session_id = convert_to('s1', 'UTF8') ORDER BY ord;

SELECT plane AS reason_plane FROM reason(word_id('dog'), word_id('h'));

SELECT array_agg(missing_arena ORDER BY missing_arena) AS dog_gaps FROM gaps(word_id('dog'));

SELECT bool_and(status = 'confirmed') AS isa_confirmed
FROM epistemic_status(word_id('dog')) WHERE type = 'is a';

SELECT plane AS rel_plane, usage AS rel_usage FROM relatedness(word_id('dog'), word_id('h'));

SELECT reply LIKE '%antonym%' AS related_reply_mentions_antonym
FROM respond('how are dog and h related');

SELECT holder, type, fact FROM contrast(word_id('dog'), word_id('c'))
ORDER BY holder, type, fact;

SELECT count(*) >= 0 AS hypernyms_runs
FROM hypernyms(word_id('dog'), 4);

SELECT cardinality(path) > 1 AS isa_path_found
FROM isa_path(word_id('dog'), word_id('c'));

SELECT reply LIKE 'Yes%' AS cascade_via_isa
FROM respond('is a dog a c?');

-- NULL session must default to the backend-pid session (the converse.cmd path);
-- this branch is otherwise unreachable from tests that pass sessions explicitly
SELECT count(*) >= 1 AS null_session_converse_runs
FROM converse('what does dog mean?');

ROLLBACK;
