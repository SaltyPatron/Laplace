-- Substrate law probes: structural invariants that must hold after ANY deposit.
-- Run by walk-verdict.cmd alongside the planes audit. Each probe prints PASS/FAIL;
-- a FAIL is a law violation, not a tuning note.
SET search_path = laplace, public;
\pset pager off

\echo '=== law probe: no unary wrappers (tier promotion requires composition) ==='
SELECT CASE WHEN count(*) = 0 THEN 'PASS' ELSE 'FAIL: ' || count(*) || ' unary wrappers' END
       AS unary_wrapper_probe
FROM physicalities p
JOIN entities e ON e.id = p.entity_id
WHERE p.type = 1 AND e.tier >= 3 AND p.n_constituents <= 1;

\echo '=== law probe: no ghost references (attested ids must be witnessed entities) ==='
SELECT CASE WHEN count(*) = 0 THEN 'PASS' ELSE 'FAIL: ' || count(*) || ' ghost references' END
       AS ghost_reference_probe
FROM (
    SELECT a.subject_id AS id FROM attestations a
    WHERE NOT EXISTS (SELECT 1 FROM entities e WHERE e.id = a.subject_id)
    UNION ALL
    SELECT a.object_id FROM attestations a
    WHERE a.object_id IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM entities e WHERE e.id = a.object_id)
    UNION ALL
    SELECT a.context_id FROM attestations a
    WHERE a.context_id IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM entities e WHERE e.id = a.context_id)
) g;

\echo '=== law probe: consensus endpoints witnessed (evidence-off deposits included) ==='
SELECT CASE WHEN count(*) = 0 THEN 'PASS' ELSE 'FAIL: ' || count(*) || ' unwitnessed consensus endpoints' END
       AS consensus_ghost_probe
FROM (
    SELECT c.subject_id AS id FROM consensus c
    WHERE NOT EXISTS (SELECT 1 FROM entities e WHERE e.id = c.subject_id)
    UNION ALL
    SELECT c.object_id FROM consensus c
    WHERE c.object_id IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM entities e WHERE e.id = c.object_id)
) g;

\echo '=== tier-witness histogram: content physicalities per source per tier ==='
\echo '(informational: a source carrying sentence data must show tier-3 content)'
SELECT coalesce(render(p.source_id), left(encode(p.source_id, 'hex'), 12)) AS source,
       e.tier, count(*) AS content_rows
FROM physicalities p
JOIN entities e ON e.id = p.entity_id
WHERE p.type = 1
GROUP BY p.source_id, e.tier
ORDER BY source, e.tier;
