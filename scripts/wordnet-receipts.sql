-- wordnet-receipts.sql — model-free dictionary Q&A by walking the deposited
-- forward-pass STEPS over consensus. No model in the path.
--
-- The steps (relation types) ARE the computation:
--   word -HAS_SENSE-> sense -IS_SENSE_OF-> synset    (sense resolution)
--   word -IS_SYNONYM_OF-> synset                     (synset membership)
--   synset -HAS_DEFINITION-> gloss                   (definition)
--   synset -IS_A-> synset (recursive)                (hypernym chain)
--   word -PRECEDES-> word                            (sequence / generation)
--
-- Re-run:  psql -U postgres -d laplace -P pager=off -f scripts/wordnet-receipts.sql
\pset pager off
\timing on

\echo
\echo ===== DEFINE: word -IS_SYNONYM_OF-> synset -HAS_DEFINITION-> gloss =====
SELECT left(definition, 90) AS definition, witnesses
FROM laplace.define(laplace.word_id('whale'));

\echo
\echo ===== same answer through the natural-language converse() router =====
SELECT left(reply, 90) AS reply FROM laplace.converse('what does whale mean');

\echo
\echo ===== SYNONYMS: co-members of the word''s synsets (incoming IS_SYNONYM_OF) =====
SELECT synonym FROM laplace.synonyms(laplace.word_id('whale'));

\echo
\echo ===== IS-A (hypernym chain): recursive walk up synset -IS_A-> synset =====
SELECT depth, hypernym, left(gloss, 55) AS gloss
FROM laplace.hypernyms(laplace.word_id('ship'), 6);

\echo
\echo ===== "what is a ship": define + rendered hypernym ladder, one router call =====
SELECT left(reply, 80) AS reply FROM laplace.converse('what is a ship') LIMIT 8;

\echo
\echo ===== GENERATION (recursive-CTE walk over PRECEDES, POS-gated) =====
SELECT step, token FROM laplace.generate('the white whale', 10, 3, 0.0);

\echo
\echo ===== GENERATION through the C engine (SPI / SIMD-AVX kernel) =====
SELECT step, laplace.label(entity_id) AS token, eff_mu
FROM laplace.generate_greedy(laplace.word_id('the'), laplace.relation_type_id('PRECEDES'), 8);
