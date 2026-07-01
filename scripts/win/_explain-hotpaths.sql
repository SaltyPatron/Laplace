\timing on
\set ON_ERROR_STOP on

BEGIN;

CREATE TEMP TABLE tmp_sample_ent ON COMMIT DROP AS
SELECT id FROM laplace.entities TABLESAMPLE SYSTEM (0.1) LIMIT 5000;

SELECT count(*) AS sample_ids FROM tmp_sample_ent;

\echo '=== 1) entities_exist_bitmap SQL (unnest JOIN) ==='
EXPLAIN (ANALYZE, BUFFERS, VERBOSE)
SELECT (u.ord - 1)::int
FROM unnest((SELECT array_agg(id) FROM tmp_sample_ent)) WITH ORDINALITY u(id, ord)
JOIN laplace.entities e ON e.id = u.id;

\echo '=== 2) content_descent_bitmap SQL (recursive NOT EXISTS per node) ==='
EXPLAIN (ANALYZE, BUFFERS, VERBOSE)
WITH RECURSIVE nodes(idx, id, parent) AS (
    SELECT (u.ord - 1)::int, u.id, -1
    FROM unnest((SELECT array_agg(id) FROM tmp_sample_ent)) WITH ORDINALITY u(id, ord)
),
descent AS (
    SELECT n.idx, NOT EXISTS (SELECT 1 FROM laplace.entities e WHERE e.id = n.id) AS novel
    FROM nodes n WHERE n.parent < 0
  UNION ALL
    SELECT c.idx, NOT EXISTS (SELECT 1 FROM laplace.entities e WHERE e.id = c.id)
    FROM descent d JOIN nodes c ON c.parent = d.idx WHERE d.novel
)
SELECT idx FROM descent WHERE novel;

\echo '=== 3) apply_batch entity anti-join (50k staging) ==='
CREATE TEMP TABLE _stage_ent ON COMMIT DROP AS
SELECT id, tier, type_id, first_observed_by, created_at
FROM laplace.entities TABLESAMPLE SYSTEM (1) LIMIT 50000;

EXPLAIN (ANALYZE, BUFFERS, VERBOSE)
SELECT d.id
FROM (SELECT DISTINCT ON (s.id) s.id FROM _stage_ent s ORDER BY s.id) d
LEFT JOIN laplace.entities e ON e.id = d.id
WHERE e.id IS NULL;

\echo '=== 4) physicalities anti-join only (10k, no insert) ==='
CREATE TEMP TABLE _stage_phys ON COMMIT DROP AS
SELECT id FROM laplace.physicalities TABLESAMPLE SYSTEM (0.2) LIMIT 10000;

EXPLAIN (ANALYZE, BUFFERS, VERBOSE)
SELECT s.id
FROM _stage_phys s
WHERE NOT EXISTS (SELECT 1 FROM laplace.physicalities p WHERE p.id = s.id);

\echo '=== 5) actual function: entities_exist_bitmap(5000 ids) ==='
EXPLAIN (ANALYZE, BUFFERS, VERBOSE)
SELECT laplace.entities_exist_bitmap((SELECT array_agg(id) FROM tmp_sample_ent));

ROLLBACK;
