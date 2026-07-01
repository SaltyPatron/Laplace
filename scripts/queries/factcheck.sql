\set ON_ERROR_STOP 0
\timing on
SET search_path = laplace, public;

\echo ====== what is even ingested? ======
\echo --- sources (which corpora contributed evidence) ---
SELECT * FROM source_counts ORDER BY evidence DESC;
\echo --- relational layers present (relation types by volume) ---
SELECT * FROM arena_counts ORDER BY relations DESC LIMIT 30;

\echo ====== does the substrate know about Paris / France? ======
\echo --- salient facts about Paris ---
SELECT * FROM salient_facts(word_id('Paris'));
\echo --- salient facts about France ---
SELECT * FROM salient_facts(word_id('France'));

\echo ====== the actual question: Paris <-> France ======
\echo --- relation_summary(Paris, France) ---
SELECT * FROM relation_summary(word_id('Paris'), word_id('France'));
\echo --- links out of Paris (text API) ---
SELECT * FROM links('Paris');
\echo --- relate_path Paris -> France ---
SELECT * FROM relate_path(word_id('Paris'), word_id('France'), 7);
