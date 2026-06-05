-- converse.sql — the conversational read surface (15_converse.sql.in).
--
-- Guards three contracts:
--   1. word_id() replicates the engine merkle rule EXACTLY:
--      leaf = BLAKE3-128(UTF-8(cp)); composite = BLAKE3-128(0x01 ‖ child ids)
--      (engine/core/src/hash128.c MERKLE_DOMAIN = 0x01).
--   2. prompt resolution: prompt_words / resolve_last_word find ingested
--      words (with lowercase fallback) and NULL never-ingested ones.
--   3. the ranked-μ reads (senses/define/synonyms/translations/respond)
--      compose over consensus rows and answer in the prompt's own witnessed
--      language — proven over a rolled-back micro-substrate whose renderable
--      leaves are single codepoints (render_text needs no trajectory there).
--
-- Run inside a transaction and ROLLBACK — leaves no rows.

BEGIN;
SET search_path = laplace, public;

-- ── 1. word_id: the merkle rule, computed two independent ways ─────────────
SELECT word_id('d') = laplace_hash128_blake3(convert_to('d','UTF8')) AS leaf_rule_holds;

SELECT word_id('dog') = laplace_hash128_blake3(
           ('\x01'::bytea
            || laplace_hash128_blake3(convert_to('d','UTF8'))
            || laplace_hash128_blake3(convert_to('o','UTF8'))
            || laplace_hash128_blake3(convert_to('g','UTF8')))) AS merkle_rule_holds;

SELECT word_id('') IS NULL AS empty_is_null;

-- ── micro-substrate: dog → sense → synset → gloss 'G'; members 'p'(en) 'h'(de) ──
DO $$
DECLARE
    type_t   bytea := laplace_hash128_blake3('substrate/type/Type/v1');  -- bootstrapped meta-type
    src      bytea := laplace_hash128_blake3('test/converse/source');
    w_dog    bytea := word_id('dog');
    w_p      bytea := word_id('p');     -- codepoint leaf member, English
    w_h      bytea := word_id('h');     -- codepoint leaf member, German
    sense1   bytea := laplace_hash128_blake3('test/converse/sense1');
    synset1  bytea := laplace_hash128_blake3('test/converse/synset1');
    gloss1   bytea := word_id('G');     -- codepoint leaf gloss: renders 'G'
    lang_en  bytea := laplace_hash128_blake3('test/converse/lang_en');
    lang_de  bytea := laplace_hash128_blake3('test/converse/lang_de');
    k_sense  bytea := kind_id('HAS_SENSE');
    k_senseof bytea := kind_id('IS_SENSE_OF');
    k_def    bytea := kind_id('DEFINES');
    k_member bytea := kind_id('IS_TRANSLATION_OF');
    k_lang   bytea := kind_id('HAS_LANGUAGE');
    neutral  bigint := 1500000000000;
    sharp_rd bigint := 30000000000;
BEGIN
    -- entities (self-consistent FKs); kinds outside the canonical 16 are inserted here
    INSERT INTO entities (id, tier, type_id, first_observed_by)
    VALUES (src, 0, type_t, NULL),
           (k_sense, 0, type_t, src), (k_senseof, 0, type_t, src), (k_def, 0, type_t, src),
           (w_dog, 2, type_t, src), (w_p, 0, type_t, src), (w_h, 0, type_t, src),
           (sense1, 250, type_t, src), (synset1, 250, type_t, src),
           (gloss1, 0, type_t, src),
           (lang_en, 250, type_t, src), (lang_de, 250, type_t, src)
    ON CONFLICT DO NOTHING;

    -- render_text leaves + readable language labels
    INSERT INTO codepoint_render (id, cp)
    VALUES (gloss1, ascii('G')), (w_p, ascii('p')), (w_h, ascii('h'))
    ON CONFLICT DO NOTHING;
    INSERT INTO canonical_names (id, name)
    VALUES (lang_en, 'test/lang/en'), (lang_de, 'test/lang/de')
    ON CONFLICT DO NOTHING;

    -- consensus micro-arena:
    --   dog —HAS_SENSE→ sense1 —IS_SENSE_OF→ synset1 —DEFINES→ 'G'
    --   synset1 —IS_TRANSLATION_OF→ {dog, p, h}
    --   dog/p —HAS_LANGUAGE→ en ; h —HAS_LANGUAGE→ de
    INSERT INTO consensus (id, subject_id, kind_id, object_id,
                           rating, rd, volatility, witness_count, last_observed_at)
    VALUES
      (consensus_id(w_dog,  k_sense,   sense1),  w_dog,  k_sense,   sense1,  neutral + 200000000000, sharp_rd, 60000000, 3, now()),
      (consensus_id(sense1, k_senseof, synset1), sense1, k_senseof, synset1, neutral + 200000000000, sharp_rd, 60000000, 3, now()),
      (consensus_id(synset1, k_def,    gloss1),  synset1, k_def,    gloss1,  neutral + 150000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset1, k_member, w_dog),   synset1, k_member, w_dog,   neutral + 100000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset1, k_member, w_p),     synset1, k_member, w_p,     neutral +  90000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset1, k_member, w_h),     synset1, k_member, w_h,     neutral +  80000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(w_dog, k_lang, lang_en),     w_dog,  k_lang, lang_en,    neutral + 300000000000, sharp_rd, 60000000, 9, now()),
      (consensus_id(w_p,   k_lang, lang_en),     w_p,    k_lang, lang_en,    neutral + 250000000000, sharp_rd, 60000000, 5, now()),
      (consensus_id(w_h,   k_lang, lang_de),     w_h,    k_lang, lang_de,    neutral + 250000000000, sharp_rd, 60000000, 5, now());
END $$;

-- ── 2. prompt resolution ───────────────────────────────────────────────────
SELECT word, (id IS NOT NULL) AS resolved FROM prompt_words('what is a Dog') ORDER BY ord;

SELECT resolve_last_word('what is a dog') = word_id('dog') AS last_word_is_dog;
SELECT resolve_last_word('zzzunknownzzz') IS NULL AS unknown_is_null;

-- ── 3. ranked-μ reads over the micro-arena ─────────────────────────────────
SELECT count(*) AS dog_senses FROM senses(word_id('dog'));

SELECT definition, witnesses FROM define(word_id('dog'));

-- synonyms answer in dog's own witnessed language: p (en) yes, h (de) no
SELECT synonym FROM synonyms(word_id('dog'));

-- translations cross every witnessed language: p AND h
SELECT translation, language FROM translations(word_id('dog')) ORDER BY translation;

-- ── 4. respond(): routing + no-consensus honesty ───────────────────────────
SELECT reply, witnesses FROM respond('what does dog mean?');
SELECT reply FROM respond('synonyms of dog');
SELECT reply FROM respond('translate dog');
SELECT reply FROM respond('what is zzzunknownzzz');
SELECT reply FROM respond('translate h');   -- held word, no senses from h itself

ROLLBACK;
