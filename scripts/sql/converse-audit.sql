-- Conversational-capability audit for the laplace.converse() surface.
-- One-shot: psql -h localhost -U postgres -d laplace -P pager=off -f scripts/sql/converse-audit.sql
-- Companion to substrate-audit.sql (structural/ingestion health).
--
-- Validates, against the CURRENT ingested data:
--   * the C/C++ offload + perf-cache the conversational path depends on,
--   * which converse intents are live vs data-starved,
--   * the intent router, end-to-end replies, known logic bugs, and session memory.

SET search_path = laplace, public;
SET client_min_messages = warning;
\pset pager off

\echo ''
\echo '================ 0. NATIVE OFFLOAD + PERF-CACHE ================'
\echo '(if any of these fail, the .dll/.so is unloaded or the perfcache is unseeded;'
\echo ' every converse reply that reconstructs text or geometry is then degraded)'

\echo '-- 0a. native libraries loaded (both must return a version) --'
SELECT laplace.laplace_substrate_version() AS substrate_lib,
       public.laplace_geom_version()       AS geom_lib;

\echo '-- 0b. T0 perfcache wired (render_text leaves resolve via codepoint_for_id) --'
SELECT laplace.codepoint_for_id(laplace.canonical_id('A')) AS cp_of_A,
       CASE WHEN laplace.codepoint_for_id(laplace.canonical_id('A')) = 65
            THEN 'PASS' ELSE 'FAIL — laplace_substrate.perfcache_path unset/stale' END AS verdict;

\echo '-- 0c. render_text C offload round-trips codepoint, word, and batch --'
SELECT render_text(word_id('A'))                                  AS r_codepoint,
       render_text(word_id('cat'))                                AS r_word,
       render_text_batch(ARRAY[word_id('cat'), word_id('dog')])   AS r_batch,
       CASE WHEN render_text(word_id('cat')) = 'cat'
                 AND render_text_batch(ARRAY[word_id('cat'),word_id('dog')]) = ARRAY['cat','dog']
            THEN 'PASS' ELSE 'FAIL — C reconstruction broken' END AS verdict;

\echo '-- 0d. geom SIMD offload (relatedness/reason structural plane) --'
SELECT round(public.laplace_angular_distance_4d(
           (SELECT coord FROM physicalities WHERE coord IS NOT NULL LIMIT 1),
           (SELECT coord FROM physicalities WHERE coord IS NOT NULL OFFSET 1 LIMIT 1))::numeric, 4)
           AS sample_angular_distance;

\echo '-- 0e. generate_greedy / generate_tree C SRFs (walk/complete intents) --'
SELECT count(*) AS greedy_steps,
       CASE WHEN count(*) > 0 THEN 'PASS' ELSE 'WARN — no PRECEDES walk from "the"' END AS verdict
FROM generate_greedy(word_id('the'), NULL, 6);
SELECT count(*) AS tree_nodes,
       CASE WHEN count(*) > 0 THEN 'PASS' ELSE 'WARN — generate_tree empty' END AS verdict
FROM generate_tree(word_id('the'), NULL, 3, 4);

\echo '-- 0f. intent_preflight C batch existence bitmap (ingestion offload) is live --'
SELECT (intent_preflight(ARRAY[word_id('dog')], ARRAY[]::bytea[], ARRAY[]::bytea[])).entity_exists
           IS NOT NULL AS preflight_live,
       CASE WHEN (intent_preflight(ARRAY[word_id('dog')], ARRAY[]::bytea[], ARRAY[]::bytea[])).entity_exists
                 IS NOT NULL THEN 'PASS' ELSE 'FAIL — C preflight returned NULL' END AS verdict;

\echo '-- 0g. glicko2 score offload (eff_mu / ranking on every reply) --'
SELECT round(eff_mu_display(1500000000000, 350000000000), 3) AS sample_eff_mu;

\echo ''
\echo '================ 1. BACKING DATA PER INTENT ================'
\echo '(an intent is dead if its relation count is 0, regardless of code correctness)'
SELECT t.name AS relation,
       (SELECT count(*) FROM consensus c WHERE c.type_id = relation_type_id(t.name)) AS edges,
       CASE WHEN (SELECT count(*) FROM consensus c WHERE c.type_id = relation_type_id(t.name)) = 0
            THEN 'DEAD — no data' ELSE 'live' END AS status
FROM (VALUES
    ('HAS_DEFINITION'),('HAS_SENSE'),('IS_SENSE_OF'),('IS_SYNONYM_OF'),
    ('IS_ANTONYM_OF'),('IS_TRANSLATION_OF'),('HAS_EXAMPLE'),('IS_A'),
    ('HAS_PART'),('HAS_MEMBER'),('CAUSES'),('USED_FOR'),('PRECEDES'),
    ('FOLLOWS'),('COMPLETES_TO'),('HAS_POS')
) AS t(name)
ORDER BY edges DESC;

\echo ''
\echo '================ 2. INTENT ROUTER COVERAGE ================'
\echo '(does route_prompt classify each canonical prompt to the expected intent?)'
SELECT r.intent AS got_intent, t.expect AS expected, t.prompt,
       CASE WHEN r.intent = t.expect THEN 'ok' ELSE 'ROUTE MISMATCH' END AS verdict
FROM (VALUES
    ('what is a dog',                 'what_is'),
    ('define justice',                'define'),
    ('synonyms of happy',             'synonyms'),
    ('antonyms of cold',              'related'),
    ('translate house',              'translate'),
    ('examples of run',               'examples'),
    ('parts of a car',                'related'),
    ('what causes rust',              'related_in'),
    ('what is water used for',        'related'),
    ('is a dog an animal',            'is_a'),
    ('how are cat and dog related',   'reason'),
    ('tell me about democracy',       'describe'),
    ('what comes after the',          'related'),
    ('walk from river',               'walk'),
    ('why did the chicken cross',     'fallback')
) AS t(prompt, expect)
CROSS JOIN LATERAL route_prompt(t.prompt) r
ORDER BY verdict DESC, t.expect;

\echo ''
\echo '================ 3. END-TO-END REPLY PER INTENT ================'
\echo '(reply rows + first line; "I hold ... yet" = starved; EMPTY = hard miss)'
SELECT t.prompt,
       count(*) AS reply_rows,
       left(min(rep.reply), 64) AS first_reply,
       CASE WHEN count(*) = 0 THEN 'EMPTY'
            WHEN bool_or(rep.reply LIKE 'I hold%yet.') THEN 'STARVED'
            ELSE 'answered' END AS verdict
FROM (VALUES
    ('what is a dog'),('define justice'),('synonyms of happy'),
    ('antonyms of cold'),('translate house'),('examples of run'),
    ('parts of a car'),('what causes rust'),('what is water used for'),
    ('is a dog an animal'),('how are cat and dog related'),
    ('tell me about democracy'),('what comes after the')
) AS t(prompt)
LEFT JOIN LATERAL respond(t.prompt, NULL) rep ON true
GROUP BY t.prompt
ORDER BY verdict, t.prompt;

\echo ''
\echo '================ 4. KNOWN-BUG ASSERTIONS ================'
\echo '(each row should read PASS once fixed; FAIL marks a live defect)'

\echo '-- 4a. synonyms() direction: word is keyed as OBJECT of IS_SYNONYM_OF, not subject --'
SELECT
    (SELECT count(*) FROM synonyms(word_id('happy'), 10)) AS via_function,
    (SELECT count(DISTINCT other.object_id)
       FROM consensus mine
       JOIN consensus other ON other.subject_id = mine.subject_id
                           AND other.type_id = relation_type_id('IS_SYNONYM_OF')
                           AND other.object_id <> word_id('happy')
      WHERE mine.object_id = word_id('happy')
        AND mine.type_id = relation_type_id('IS_SYNONYM_OF')) AS available_co_members,
    CASE WHEN (SELECT count(*) FROM synonyms(word_id('happy'),10)) > 0
         THEN 'PASS' ELSE 'FAIL — synonyms() queries word as subject; data keys it as object' END AS verdict;

\echo '-- 4b. article-leak: "parts of a car" must resolve to car, not the article "a" --'
SELECT label(resolve_topic('a car', NULL)) AS resolved_topic,
       CASE WHEN resolve_topic('a car', NULL) = word_id('car')
            THEN 'PASS' ELSE 'FAIL — leading article wins leftmost tie-break in resolve_phrase' END AS verdict;

\echo '-- 4c. describe() dedup: no (type,fact) pair should repeat for one topic --'
SELECT (SELECT count(*) FROM describe(word_id('democracy'), NULL)) AS rows,
       (SELECT count(DISTINCT (type, fact)) FROM describe(word_id('democracy'), NULL)) AS distinct_rows,
       CASE WHEN (SELECT count(*) FROM describe(word_id('democracy'),NULL))
               = (SELECT count(DISTINCT (type,fact)) FROM describe(word_id('democracy'),NULL))
            THEN 'PASS' ELSE 'WARN — describe() emits duplicate facts' END AS verdict;

\echo ''
\echo '================ 5. SESSION CONTINUITY (pronoun follow-up) ================'
\echo '(turn 2 "and its synonyms?" must resolve against turn 1 topic, not error)'
BEGIN;
SELECT 'turn1' AS turn, left(reply,52) AS reply FROM converse('define happy', '\x9001'::bytea) LIMIT 1;
SELECT 'turn2' AS turn, left(reply,52) AS reply FROM converse('and its synonyms?', '\x9001'::bytea) LIMIT 1;
ROLLBACK;

\echo ''
\echo '================ AUDIT COMPLETE ================'
