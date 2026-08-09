




SET search_path = laplace, public;
SET client_min_messages = warning;

\echo '=== POS values: universal UPOS (named substrate/pos/*) vs unnamed fragments ==='
SELECT coalesce(cn.name, '<UNNAMED ' || encode(c.object_id, 'hex') || '>') AS pos_value,
       count(*) AS edges
FROM consensus c
LEFT JOIN canonical_names cn ON cn.id = c.object_id
WHERE c.type_id = relation_type_id('HAS_POS')
GROUP BY 1 ORDER BY edges DESC;

\echo '=== POS coverage: how many distinct values are universal-named vs unnamed ==='
SELECT count(*) FILTER (WHERE cn.name IS NOT NULL) AS universal_named,
       count(*) FILTER (WHERE cn.name IS NULL)     AS unnamed_fragment
FROM (SELECT DISTINCT object_id FROM consensus WHERE type_id = relation_type_id('HAS_POS')) v
LEFT JOIN canonical_names cn ON cn.id = v.object_id;

\echo '=== XPOS: language-specific tagset channel (expected to be many) ==='
SELECT count(DISTINCT object_id) AS distinct_xpos_values,
       count(*)                  AS xpos_edges
FROM consensus WHERE type_id = relation_type_id('HAS_XPOS');

\echo '=== relation-TYPE fragmentation: unnamed types (no canonical) per source ==='
WITH g AS (SELECT DISTINCT type_id FROM consensus),
     u AS (SELECT type_id FROM g WHERE relation_canonical(type_id) IS NULL)
SELECT coalesce(cn.name, encode(a.source_id, 'hex')) AS source,
       count(DISTINCT a.type_id) AS unnamed_types,
       count(*)                  AS attestations
FROM attestations a
JOIN u ON u.type_id = a.type_id
LEFT JOIN canonical_names cn ON cn.id = a.source_id
GROUP BY a.source_id, cn.name
ORDER BY attestations DESC;

\echo '=== summary: named-vs-unnamed relation types overall ==='
WITH g AS (SELECT DISTINCT type_id FROM consensus)
SELECT count(*) FILTER (WHERE relation_canonical(type_id) IS NOT NULL) AS named_types,
       count(*) FILTER (WHERE relation_canonical(type_id) IS NULL)     AS unnamed_types
FROM g;
