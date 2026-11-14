SET search_path = laplace, public;
SET client_min_messages = warning;

\echo '=== substrate_health (must be ok) ==='
SELECT * FROM substrate_health();

\echo '=== identity law (violations must be 0) ==='
SELECT count(*) AS identity_violations FROM identity_law_violations();

\echo '=== layer completion (any witness) ==='
SELECT layer, ops.layer_completed(NULL, layer) AS completed
FROM generate_series(0, 3) AS layer
ORDER BY layer;

\echo '=== substrate_counts ==='
SELECT * FROM ops.substrate_counts();

\echo '=== witnesses ingested (evidence + content per source) ==='
SELECT source, evidence, content FROM ops.source_counts();

\echo '=== multi-source entities (frayed-edge substrate) ==='
SELECT ops.multi_source_entity_count();

\echo '=== entities per tier (heights; every entity is a composition) ==='
SELECT e.tier, count(*) AS n FROM public.entity e GROUP BY e.tier ORDER BY e.tier;

\echo '=== realize.render_gaps(orphan consensus ids, top 10) ==='
SELECT count(*) AS gap_count FROM realize.render_gaps(1000);
SELECT realize.render(id), roles, refs FROM realize.render_gaps(10);

