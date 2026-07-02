-- Post-reseed verification (P0.2): proves the Phase-0 fixes against regenerated data.
-- Run: psql -h localhost -U postgres -d laplace -P pager=off -f scripts/win/sql/_verify-reseed.sql
SET search_path = laplace, public;

\echo '=== 1. Issue 32 signature: SemLink PRECEDES must have sane counts + mixed outcomes ==='
SELECT a.outcome, count(*) n, min(a.observation_count) mn, max(a.observation_count) mx
FROM attestations a JOIN canonical_names cn ON cn.id = a.source_id
WHERE cn.name = 'substrate/source/SemLinkDecomposer/v1'
  AND relation_canonical(a.type_id) = 'PRECEDES'
GROUP BY 1 ORDER BY 1;
-- FAIL if: any observation_count is a 1e9 multiple, or every outcome is 0.

\echo '=== 2. Highway masks: non-zero bits at write time (the rc==1 fix) ==='
SELECT cn.name AS source,
       count(*) FILTER (WHERE a.highway_mask IS NULL)                          AS mask_null,
       count(*) FILTER (WHERE laplace_highway_popcount(a.highway_mask) = 0)    AS mask_zero,
       count(*) FILTER (WHERE laplace_highway_popcount(a.highway_mask) > 0)    AS mask_set
FROM attestations a JOIN canonical_names cn ON cn.id = a.source_id
GROUP BY 1 ORDER BY 1;
-- FAIL if mask_set is 0 for any seeded source.

\echo '=== 3. Mask bit agrees with the relation type (spot check) ==='
SELECT relation_canonical(a.type_id) rel,
       relation_highway_bit(a.type_id) expected_bit,
       laplace_highway_popcount(a.highway_mask) bits_set,
       laplace_highway_match(a.highway_mask, laplace_highway_band_mask(relation_highway_band(a.type_id))) band_match,
       count(*)
FROM attestations a
WHERE a.highway_mask IS NOT NULL
GROUP BY 1, 2, 3, 4
ORDER BY count(*) DESC LIMIT 12;
-- FAIL if band_match is false for governed relations.

\echo '=== 4. Plane selection over fresh consensus ==='
SELECT relation_canonical(type_id) rel, count(*) n, max(eff_mu) mx
FROM consensus_band_edges(2, NULL, 2000) GROUP BY 1 ORDER BY n DESC LIMIT 6;

\echo '=== 5. Substrate shape ==='
SELECT 'entities' t, count(*) FROM entities
UNION ALL SELECT 'physicalities', count(*) FROM physicalities
UNION ALL SELECT 'attestations', count(*) FROM attestations
UNION ALL SELECT 'consensus', count(*) FROM consensus;

\echo '=== 6. Model planes (after models stage): governed + populated ==='
SELECT relation_canonical(a.type_id) rel, count(*) n, sum(a.observation_count) obs
FROM attestations a JOIN canonical_names cn ON cn.id = a.source_id
WHERE cn.name LIKE 'substrate/source/model%'
GROUP BY 1 ORDER BY n DESC LIMIT 10;
-- Expect SIMILAR_TO/ATTENDS/OV_RELATES/COMPLETES_TO/CONTINUES_TO/MERGES_WITH rows,
-- every relation resolving via relation_highway_bit (no ungoverned NULLs).
