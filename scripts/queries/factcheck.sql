\set ON_ERROR_STOP 0
\timing on
SET search_path = laplace, public;

\echo ====== what is even ingested? ======
\echo 
SELECT * FROM source_counts ORDER BY evidence DESC;
\echo 
SELECT * FROM arena_counts ORDER BY relations DESC LIMIT 30;

\echo ====== does the substrate know about Paris / France? ======
\echo 
SELECT * FROM salient_facts(word_id('Paris'));
\echo 
SELECT * FROM salient_facts(word_id('France'));

\echo ====== the actual question: Paris <-> France ======
\echo 
SELECT * FROM relation_summary(word_id('Paris'), word_id('France'));
\echo 
SELECT * FROM links('Paris');
\echo 
SELECT * FROM relate_path(word_id('Paris'), word_id('France'), 7);
