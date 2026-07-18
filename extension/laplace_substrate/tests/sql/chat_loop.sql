-- The closed-loop acceptance gate: a walk-driven answer, a refutation folded
-- through the same consensus_upsert lane the feedback frontends use, and the
-- ANSWER MEASURABLY CHANGES. This is the loop that makes the substrate a mind
-- and not a lookup -- pinned at the SQL layer on a self-contained fixture.
BEGIN;
SET search_path = laplace, public;

DO $$
DECLARE
    rel_meta bytea := entity_type_id('RelationType');
    type_t   bytea := laplace_hash128_blake3('Type');
    src      bytea := laplace_hash128_blake3('test/chat_loop/source');
    w_dog    bytea := word_id('dog');
    w_p      bytea := word_id('p');
    w_h      bytea := word_id('h');
    w_c      bytea := word_id('c');
    gloss1   bytea := word_id('G');
    gloss2   bytea := word_id('B');
    sense1   bytea := laplace_hash128_blake3('test/chat_loop/sense1');
    synset1  bytea := laplace_hash128_blake3('test/chat_loop/synset1');
    synset2  bytea := laplace_hash128_blake3('test/chat_loop/synset2');
    synset3  bytea := laplace_hash128_blake3('test/chat_loop/synset3');
    syn_bad  bytea := laplace_hash128_blake3('test/chat_loop/synset_bad');
    k_sense  bytea := relation_type_id('HAS_SENSE');
    k_senseof bytea := relation_type_id('IS_SENSE_OF');
    k_def    bytea := relation_type_id('HAS_DEFINITION');
    k_member bytea := relation_type_id('IS_TRANSLATION_OF');
    k_isa    bytea := relation_type_id('IS_A');
    k_part   bytea := relation_type_id('HAS_PART');
    k_rel    bytea := relation_type_id('RELATED_TO');
    neutral  bigint := 1500000000000;
    sharp_rd bigint := 30000000000;
BEGIN
    INSERT INTO entities (id, tier, type_id, first_observed_by)
    VALUES (src, 0, type_t, NULL),
           (k_sense, 0, rel_meta, src), (k_senseof, 0, rel_meta, src),
           (k_def, 0, rel_meta, src), (k_member, 0, rel_meta, src),
           (k_isa, 0, rel_meta, src), (k_part, 0, rel_meta, src),
           (k_rel, 0, rel_meta, src),
           (w_dog, 2, type_t, src), (w_p, 0, type_t, src),
           (w_h, 0, type_t, src), (w_c, 0, type_t, src),
           (gloss1, 0, type_t, src), (gloss2, 0, type_t, src),
           (sense1, 0, type_t, src), (synset1, 0, type_t, src),
           (synset2, 0, type_t, src), (synset3, 0, type_t, src),
           (syn_bad, 0, type_t, src)
    ON CONFLICT DO NOTHING;

    PERFORM register_canonical('HAS_SENSE');
    PERFORM register_canonical('IS_SENSE_OF');
    PERFORM register_canonical('HAS_DEFINITION');
    PERFORM register_canonical('IS_TRANSLATION_OF');
    PERFORM register_canonical('IS_A');
    PERFORM register_canonical('HAS_PART');
    PERFORM register_canonical('RELATED_TO');
    PERFORM register_canonical('PRECEDES');

    INSERT INTO consensus (id, subject_id, type_id, object_id,
                           rating, rd, volatility, witness_count, last_observed_at)
    VALUES
      (consensus_id(w_dog,   k_sense,   sense1),  w_dog,   k_sense,   sense1,  neutral + 200000000000, sharp_rd, 60000000, 3, now()),
      (consensus_id(sense1,  k_senseof, synset1), sense1,  k_senseof, synset1, neutral + 200000000000, sharp_rd, 60000000, 3, now()),
      -- two competing definitions: gloss1 leads, gloss2 is the runner-up
      (consensus_id(synset1, k_def,     gloss1),  synset1, k_def,     gloss1,  neutral + 150000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset1, k_def,     gloss2),  synset1, k_def,     gloss2,  neutral + 140000000000, sharp_rd, 60000000, 2, now()),
      -- taxonomy: a two-hop confirmed chain plus a REFUTED branch the walk must skip
      (consensus_id(synset1, k_isa,     synset2), synset1, k_isa,     synset2, neutral + 120000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset2, k_isa,     synset3), synset2, k_isa,     synset3, neutral + 110000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset2, k_isa,     syn_bad), synset2, k_isa,     syn_bad, neutral - 300000000000, sharp_rd, 60000000, 3, now()),
      -- parts + kin
      (consensus_id(synset1, k_part,    w_h),     synset1, k_part,    w_h,     neutral + 110000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset1, k_part,    w_p),     synset1, k_part,    w_p,     neutral + 100000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset1, k_rel,     w_c),     synset1, k_rel,     w_c,     neutral + 100000000000, sharp_rd, 60000000, 2, now()),
      -- lemma members so realize() speaks for the synsets
      (consensus_id(synset1, k_member,  w_dog),   synset1, k_member,  w_dog,   neutral + 100000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset2, k_member,  w_c),     synset2, k_member,  w_c,     neutral + 100000000000, sharp_rd, 60000000, 2, now()),
      (consensus_id(synset3, k_member,  w_h),     synset3, k_member,  w_h,     neutral + 100000000000, sharp_rd, 60000000, 2, now());
END $$;

-- 1. The walk-driven fact engine: every fact family present, taxonomy multi-hop,
--    the refuted IS_A branch absent, provenance columns populated.
SELECT fact_kind, sentence, witnesses
FROM converse_facts(laplace_hash128_blake3('test/chat_loop/synset1'));

SELECT fact_kind, cardinality(path) AS path_len, cardinality(types) AS type_len
FROM converse_facts(laplace_hash128_blake3('test/chat_loop/synset1'))
WHERE fact_kind IN ('definition', 'taxonomy');

SELECT bool_and(sentence !~* 'synset_bad') AS refuted_branch_excluded
FROM converse_facts(laplace_hash128_blake3('test/chat_loop/synset1'));

-- 2. The prose wrapper weaves the fact rows (web drill-down row excluded).
SELECT converse_about(laplace_hash128_blake3('test/chat_loop/synset1')) AS about;

-- 3. A full chat turn lands on the walk-driven answer.
SELECT chat('what is a dog?') AS chat_reply;

-- 4. chat() is read-side: a session turn records session state but folds NOTHING
--    into consensus (the OODA close lives at the frontends, through the writer
--    spine, with evidence). 'dog p' are adjacent existing tokens -- the old
--    in-SQL close would have folded a PRECEDES cell for them.
SELECT chat('dog p', convert_to('loop1', 'UTF8')) IS NOT NULL AS session_chat_ran;
SELECT count(*) AS precedes_cells_written
FROM consensus c
WHERE c.type_id = relation_type_id('PRECEDES')
  AND c.subject_id = word_id('dog');
SELECT count(*) AS session_rows
FROM session_topics WHERE session_id = convert_to('loop1', 'UTF8');

-- 5. THE LOOP: refute the leading definition through the same consensus_upsert
--    lane the feedback frontends (/v1/feedback, laplace attest) use...
SELECT consensus_upsert(
    ARRAY[laplace_hash128_blake3('test/chat_loop/synset1')],
    ARRAY[relation_type_id('HAS_DEFINITION')],
    ARRAY[word_id('G')],
    ARRAY[glicko2_initial_rd()],
    ARRAY[200::bigint],
    ARRAY[0::bigint],
    ARRAY[now()]) AS refute_folded;

-- ...the refuted edge drops below the runner-up...
SELECT eff_mu_display(c1.rating, c1.rd) < eff_mu_display(c2.rating, c2.rd) AS refuted_below_runner_up
FROM consensus c1, consensus c2
WHERE c1.id = consensus_id(laplace_hash128_blake3('test/chat_loop/synset1'),
                           relation_type_id('HAS_DEFINITION'), word_id('G'))
  AND c2.id = consensus_id(laplace_hash128_blake3('test/chat_loop/synset1'),
                           relation_type_id('HAS_DEFINITION'), word_id('B'));

-- ...and the ANSWER CHANGES: the next walk reads the updated consensus.
SELECT sentence AS definition_after_refute
FROM converse_facts(laplace_hash128_blake3('test/chat_loop/synset1'))
WHERE fact_kind = 'definition';

SELECT chat('what is a dog?') AS chat_reply_after_refute;

ROLLBACK;
