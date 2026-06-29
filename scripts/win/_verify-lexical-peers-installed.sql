\set ON_ERROR_STOP on
\echo '=== lexical peer functions present ==='
SELECT pg_get_functiondef('laplace.lexical_peers(bytea)'::regprocedure) LIKE '%word_case_variant_ids%' AS lexical_peers_ok;
SELECT pg_get_functiondef('laplace.word_case_map_surface(bytea,text)'::regprocedure) LIKE '%grapheme_case_target%' AS case_map_ok;
SELECT pg_get_functiondef('laplace.word_shape_peers(bytea,double precision)'::regprocedure) LIKE '%word_case_class_surface%' AS shape_peers_case_class_ok;

\echo '=== peer-expanded define installed ==='
SELECT pg_get_functiondef('laplace.define(bytea,int)'::regprocedure) LIKE '%lexical_peers%' AS define_expands_peers;

\echo '=== no resolve_word_id lower() hack ==='
SELECT NOT EXISTS (
    SELECT 1 FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
    WHERE n.nspname = 'laplace' AND p.proname = 'resolve_word_id'
) AS no_resolve_word_id;

\echo '=== identity resolution stays exact (prompt_words) ==='
SELECT pg_get_functiondef('laplace.prompt_words(text)'::regprocedure) NOT LIKE '%lower(%' AS prompt_words_no_lower;
SELECT pg_get_functiondef('laplace.prompt_words(text)'::regprocedure) NOT LIKE '%resolve_word_id%' AS prompt_words_no_hack;

\echo '=== substrate seeded? ==='
SELECT (SELECT count(*) FROM laplace.entities) AS entity_count;
