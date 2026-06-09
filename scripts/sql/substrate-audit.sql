SET search_path = laplace, public;

\echo '=== substrate_health ==='
SELECT * FROM substrate_health();

\echo '=== fake_tier_bands (must be 0) ==='
SELECT fake_tier_band_count();

\echo '=== identity_law_violations (must be empty) ==='
SELECT count(*) AS violation_count FROM identity_law_violations();

\echo '=== compositional tier distribution ==='
SELECT * FROM compositional_tier_distribution();

\echo '=== substrate_counts ==='
SELECT * FROM substrate_counts();

\echo '=== multi_source_entity_count ==='
SELECT multi_source_entity_count();

\echo '=== word_id dog (hash law smoke) ==='
SELECT word_id('dog') IS NOT NULL AS dog_resolves;
