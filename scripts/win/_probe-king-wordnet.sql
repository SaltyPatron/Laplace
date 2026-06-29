\set ON_ERROR_STOP on
\echo '=== wordnet-only king probe ==='
SELECT laplace.entity_exists(laplace.word_id('king')) AS king_seeded;
SELECT count(*) AS king_senses FROM laplace.senses(laplace.word_id('king'));
SELECT count(*) AS define_king_rows FROM laplace.define(laplace.word_id('king'), 5);
SELECT laplace.entity_exists(laplace.word_id('King')) AS king_cap_seeded;
SELECT laplace.word_case_map_surface(laplace.word_id('King'), 'lower') AS lower_surface_if_cap_exists;
