-- The closed-loop acceptance gate: an explicit witnessed-description read, a
-- refutation folded through the same consensus_upsert lane the feedback
-- frontends use, and the ANSWER MEASURABLY CHANGES. The default no-shape chat
-- path is tested separately because it is now the real dynamic forward pass,
-- not the legacy description/template route.
BEGIN;

DO $$
DECLARE
    rel_meta bytea := laplace.entity_type_id('RelationType');
    type_t   bytea := public.blake3_send(public.laplace_text_id('Type'));
    src      bytea := public.blake3_send(public.laplace_text_id('test/chat_loop/source'));
    w_dog    bytea := public.blake3_send(public.laplace_text_id('dog'));
    w_d      bytea := public.blake3_send(public.laplace_text_id('d'));
    w_p      bytea := public.blake3_send(public.laplace_text_id('p'));
    w_h      bytea := public.blake3_send(public.laplace_text_id('h'));
    w_c      bytea := public.blake3_send(public.laplace_text_id('c'));
    gloss1   bytea := public.blake3_send(public.laplace_text_id('G'));
    gloss2   bytea := public.blake3_send(public.laplace_text_id('B'));
    sense1   bytea := public.blake3_send(public.laplace_text_id('test/chat_loop/sense1'));
    sense2   bytea := public.blake3_send(public.laplace_text_id('test/chat_loop/sense2'));
    sense3   bytea := public.blake3_send(public.laplace_text_id('test/chat_loop/sense3'));
    synset1  bytea := public.blake3_send(public.laplace_text_id('test/chat_loop/synset1'));
    synset2  bytea := public.blake3_send(public.laplace_text_id('test/chat_loop/synset2'));
    synset3  bytea := public.blake3_send(public.laplace_text_id('test/chat_loop/synset3'));
    syn_bad  bytea := public.blake3_send(public.laplace_text_id('test/chat_loop/synset_bad'));
    k_sense  bytea := laplace.relation_type_id('HAS_SENSE');
    k_senseof bytea := laplace.relation_type_id('IS_SENSE_OF');
    k_def    bytea := laplace.relation_type_id('HAS_DEFINITION');
    k_member bytea := laplace.relation_type_id('IS_TRANSLATION_OF');
    k_isa    bytea := laplace.relation_type_id('IS_A');
    k_part   bytea := laplace.relation_type_id('HAS_PART');
    k_rel    bytea := laplace.relation_type_id('RELATED_TO');
    neutral  bigint := 1500000000000;
    sharp_rd bigint := 30000000000;
BEGIN
    INSERT INTO laplace.entities (id, tier, type_id)
    VALUES (src, 0, type_t),
           (k_sense, 0, rel_meta), (k_senseof, 0, rel_meta),
           (k_def, 0, rel_meta), (k_member, 0, rel_meta),
           (k_isa, 0, rel_meta), (k_part, 0, rel_meta),
           (k_rel, 0, rel_meta),
           (w_dog, 2, type_t), (w_d, 0, type_t), (w_p, 0, type_t),
           (w_h, 0, type_t), (w_c, 0, type_t),
           (gloss1, 0, type_t), (gloss2, 0, type_t),
           (sense1, 0, type_t), (sense2, 0, type_t),
           (sense3, 0, type_t), (synset1, 0, type_t),
           (synset2, 0, type_t), (synset3, 0, type_t),
           (syn_bad, 0, type_t)
    ON CONFLICT DO NOTHING;

    PERFORM realize.register_canonical('HAS_SENSE');
    PERFORM realize.register_canonical('IS_SENSE_OF');
    PERFORM realize.register_canonical('HAS_DEFINITION');
    PERFORM realize.register_canonical('IS_TRANSLATION_OF');
    PERFORM realize.register_canonical('IS_A');
    PERFORM realize.register_canonical('HAS_PART');
    PERFORM realize.register_canonical('RELATED_TO');
    PERFORM realize.register_canonical('PRECEDES');

    INSERT INTO laplace.consensus (id, subject_id, type_id, object_id,
                           rating, rd, volatility, witness_count, last_observed_at)
    VALUES
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[w_dog, k_sense, sense1]::public.blake3[], NULL))),  w_dog,   k_sense,   sense1,  neutral + 200000000000, sharp_rd, 60000000, 3, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[sense1, k_senseof, synset1]::public.blake3[], NULL))), sense1,  k_senseof, synset1, neutral + 200000000000, sharp_rd, 60000000, 3, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[w_dog, k_sense, synset1]::public.blake3[], NULL))), w_dog, k_sense, synset1, neutral + 200000000000, sharp_rd, 60000000, 3, now()),
      -- two competing definitions: gloss1 leads, gloss2 is the runner-up
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[synset1, k_def, gloss1]::public.blake3[], NULL))),  synset1, k_def,     gloss1,  neutral + 150000000000, sharp_rd, 60000000, 2, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[synset1, k_def, gloss2]::public.blake3[], NULL))),  synset1, k_def,     gloss2,  neutral + 140000000000, sharp_rd, 60000000, 2, now()),
      -- taxonomy: a two-hop confirmed chain plus a REFUTED branch the walk must skip
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[synset1, k_isa, synset2]::public.blake3[], NULL))), synset1, k_isa,     synset2, neutral + 120000000000, sharp_rd, 60000000, 2, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[synset2, k_isa, synset3]::public.blake3[], NULL))), synset2, k_isa,     synset3, neutral + 110000000000, sharp_rd, 60000000, 2, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[synset2, k_isa, syn_bad]::public.blake3[], NULL))), synset2, k_isa,     syn_bad, neutral - 300000000000, sharp_rd, 60000000, 3, now()),
      -- parts + kin
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[synset1, k_part, w_h]::public.blake3[], NULL))),     synset1, k_part,    w_h,     neutral + 110000000000, sharp_rd, 60000000, 2, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[synset1, k_part, w_p]::public.blake3[], NULL))),     synset1, k_part,    w_p,     neutral + 100000000000, sharp_rd, 60000000, 2, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[synset1, k_rel, w_c]::public.blake3[], NULL))),     synset1, k_rel,     w_c,     neutral + 100000000000, sharp_rd, 60000000, 2, now()),
      -- the lemma mesh: word -[HAS_SENSE]-> sense -[IS_SENSE_OF]-> synset is the
      -- family resolve_name's synset-lemma arm walks; single-codepoint lemma
      -- words (d/c/h) render without constituent rows. 'dog' has no constituents
      -- in this fixture, so its surface abstains and 'd' speaks for synset1 --
      -- which also pins that the arm never falls through to the gloss.
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[w_d, k_sense, sense1]::public.blake3[], NULL))),        w_d,     k_sense,   sense1,  neutral + 150000000000, sharp_rd, 60000000, 2, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[w_d, k_sense, synset1]::public.blake3[], NULL))), w_d, k_sense, synset1, neutral + 150000000000, sharp_rd, 60000000, 2, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[w_c, k_sense, sense2]::public.blake3[], NULL))),        w_c,     k_sense,   sense2,  neutral + 150000000000, sharp_rd, 60000000, 2, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[sense2, k_senseof, synset2]::public.blake3[], NULL))),  sense2,  k_senseof, synset2, neutral + 150000000000, sharp_rd, 60000000, 2, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[w_c, k_sense, synset2]::public.blake3[], NULL))), w_c, k_sense, synset2, neutral + 150000000000, sharp_rd, 60000000, 2, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[w_h, k_sense, sense3]::public.blake3[], NULL))),        w_h,     k_sense,   sense3,  neutral + 150000000000, sharp_rd, 60000000, 2, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[sense3, k_senseof, synset3]::public.blake3[], NULL))),  sense3,  k_senseof, synset3, neutral + 150000000000, sharp_rd, 60000000, 2, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[w_h, k_sense, synset3]::public.blake3[], NULL))), w_h, k_sense, synset3, neutral + 150000000000, sharp_rd, 60000000, 2, now()),
      -- translation members stay for the p_lang-driven arm (not walked when lang is NULL)
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[synset1, k_member, w_dog]::public.blake3[], NULL))),   synset1, k_member,  w_dog,   neutral + 100000000000, sharp_rd, 60000000, 2, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[synset2, k_member, w_c]::public.blake3[], NULL))),     synset2, k_member,  w_c,     neutral + 100000000000, sharp_rd, 60000000, 2, now()),
      (public.blake3_send(public.laplace_compose(array_remove(ARRAY[synset3, k_member, w_h]::public.blake3[], NULL))),     synset3, k_member,  w_h,     neutral + 100000000000, sharp_rd, 60000000, 2, now());
END $$;

-- 1. The walk-driven fact engine: every fact family present, taxonomy multi-hop,
--    the refuted IS_A branch absent, provenance columns populated.
SELECT fact_kind, sentence, witnesses
FROM converse.facts(public.blake3_send(public.laplace_text_id('test/chat_loop/synset1')));

SELECT fact_kind, cardinality(path) AS path_len, cardinality(types) AS type_len
FROM converse.facts(public.blake3_send(public.laplace_text_id('test/chat_loop/synset1')))
WHERE fact_kind IN ('definition', 'taxonomy');

SELECT bool_and(sentence !~* 'synset_bad') AS refuted_branch_excluded
FROM converse.facts(public.blake3_send(public.laplace_text_id('test/chat_loop/synset1')));

SELECT count(*) = 0 AS zero_listing_capacity_is_empty
FROM converse.facts(
    public.blake3_send(public.laplace_text_id('test/chat_loop/synset1')), NULL, 0, 0)
WHERE fact_kind IN ('parts', 'kin');

-- 2. The prose wrapper weaves the fact rows (web drill-down row excluded).
SELECT converse.about(public.blake3_send(public.laplace_text_id('test/chat_loop/synset1'))) AS about;

-- 3. The default no-shape route is the real dynamic forward pass. This tiny
-- fixture contains routed graph facts but no physical or completion testimony.
-- Neither renderable graph state nor a text scaffold may fabricate a selection.
-- The text adapter must return exactly the canonical program's selected surface.
SELECT converse.chat('what is a dog?') IS NOT DISTINCT FROM (
    SELECT string_agg(g.entity, '' ORDER BY g.step)
    FROM converse.forward_turn('what is a dog?') g)
       AS default_chat_forward_parity;
SELECT converse.chat('what is a dog?') IS NULL
       AS unsupported_output_is_not_fabricated;

-- The closed-loop ranking assertion names the description shape it actually
-- tests. A feedback-induced definition change must affect that same read.
SELECT converse.chat('what is a dog?', NULL::bytea, NULL::bytea, 'describe') AS chat_reply;

-- 4. converse.chat() is read-side: a session turn records session state but folds NOTHING
--    into consensus (the OODA close lives at the frontends, through the writer
--    spine, with evidence). 'dog p' are adjacent existing tokens -- the old
--    in-SQL close would have folded a PRECEDES cell for them.
SELECT converse.chat('dog p', public.blake3_send(public.laplace_text_id('loop1')), NULL::bytea, 'describe') IS NOT NULL AS session_chat_ran;
SELECT count(*) AS precedes_cells_written
FROM laplace.consensus c
WHERE c.type_id = laplace.relation_type_id('PRECEDES')
  AND c.subject_id = public.blake3_send(public.laplace_text_id('dog'));
SELECT count(*) AS session_rows
FROM converse.session_topics WHERE session_id = public.blake3_send(public.laplace_text_id('loop1'));

-- 5. THE LOOP: refute the leading definition through the same consensus_upsert
--    lane the feedback frontends (/v1/feedback, laplace attest) use...
SELECT consensus.upsert(
    ARRAY[public.blake3_send(public.laplace_text_id('test/chat_loop/synset1'))],
    ARRAY[laplace.relation_type_id('HAS_DEFINITION')],
    ARRAY[public.blake3_send(public.laplace_text_id('G'))],
    ARRAY[350000000000::bigint],
    ARRAY[200::bigint],
    ARRAY[0::bigint],
    ARRAY[now()]) AS refute_folded;

-- ...the refuted edge drops below the runner-up...
SELECT consensus.eff_mu_display(c1.rating, c1.rd) < consensus.eff_mu_display(c2.rating, c2.rd) AS refuted_below_runner_up
FROM laplace.consensus c1, laplace.consensus c2
WHERE c1.id = public.blake3_send(public.laplace_compose(array_remove(ARRAY[public.blake3_send(public.laplace_text_id('test/chat_loop/synset1')), laplace.relation_type_id('HAS_DEFINITION'), public.blake3_send(public.laplace_text_id('G'))]::public.blake3[], NULL)))
  AND c2.id = public.blake3_send(public.laplace_compose(array_remove(ARRAY[public.blake3_send(public.laplace_text_id('test/chat_loop/synset1')), laplace.relation_type_id('HAS_DEFINITION'), public.blake3_send(public.laplace_text_id('B'))]::public.blake3[], NULL)));

-- ...and the ANSWER CHANGES through the same explicit description read.
SELECT sentence AS definition_after_refute
FROM converse.facts(public.blake3_send(public.laplace_text_id('test/chat_loop/synset1')))
WHERE fact_kind = 'definition';

SELECT converse.chat('what is a dog?', NULL::bytea, NULL::bytea, 'describe')
       AS chat_reply_after_refute;

ROLLBACK;
