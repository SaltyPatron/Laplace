SET search_path = laplace, public;
SET client_min_messages = warning;

\echo '=== substrate_health (must be ok) ==='
SELECT * FROM substrate_health();

\echo '=== identity law (fake tiers + violations must be 0) ==='
SELECT fake_tier_band_count() AS fake_tier_bands;
SELECT count(*) AS identity_violations FROM identity_law_violations();

\echo '=== layer completion markers ==='
SELECT layer, laplace.evidence_count(
           p_type => laplace.canonical_id('substrate/type/HasLayerCompleted/' || layer || '/v1')) > 0 AS completed
FROM generate_series(0, 3) AS layer
ORDER BY layer;

\echo '=== substrate_counts ==='
SELECT * FROM substrate_counts();

\echo '=== witnesses ingested (evidence + content per source) ==='
SELECT source, evidence, content FROM source_counts();

\echo '=== multi-source entities (frayed-edge substrate) ==='
SELECT multi_source_entity_count();

\echo '=== compositional tier distribution ==='
SELECT * FROM compositional_tier_distribution();

\echo '=== render_gaps (orphan consensus ids, top 10) ==='
SELECT count(*) AS gap_count FROM render_gaps(1000);
SELECT render(id), roles, refs FROM render_gaps(10);

