\set ON_ERROR_STOP on
\echo '=== entity counts ==='
SELECT count(*) AS entities FROM laplace.entities;
SELECT count(*) AS codepoints_t0
FROM laplace.entities e
JOIN laplace.physicalities p ON p.entity_id = e.id AND p.type = 1
WHERE e.tier = 0;

\echo '=== layer completion markers ==='
SELECT s.decomposer,
       s.layer,
       laplace.evidence_count(
           laplace.canonical_id('substrate/type/HasLayerCompleted/' || s.layer::text || '/v1'),
           laplace.source_id(s.decomposer)) > 0 AS layer_marker,
       laplace.evidence_count(p_source => laplace.source_id(s.decomposer)) AS attestations
FROM (VALUES
    ('UnicodeDecomposer', 0),
    ('ISO639Decomposer', 1),
    ('CILIDecomposer', 2),
    ('WordNetDecomposer', 2)
) AS s(decomposer, layer);

\echo '=== spot checks ==='
SELECT laplace.entity_exists(laplace.word_id('king')) AS wordnet_king;
SELECT laplace.entity_exists(laplace.word_id('eng')) AS iso_eng;
SELECT count(*) FILTER (WHERE laplace.evidence_count(p_source => laplace.source_id('WiktionaryDecomposer')) > 0) AS wiktionary_attestations
FROM (SELECT 1) x;
SELECT laplace.evidence_count(p_source => laplace.source_id('WiktionaryDecomposer')) AS wiktionary_evidence;
