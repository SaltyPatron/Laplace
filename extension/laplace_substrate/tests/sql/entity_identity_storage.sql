BEGIN;

SELECT p.partstrat = 'h' AS entities_hash_partitioned
FROM pg_partitioned_table p
WHERE p.partrelid = 'laplace.entities'::regclass;

SELECT array_agg(a.attname ORDER BY u.ord) = ARRAY['id']::name[] AS entities_pk_is_id_only
FROM pg_constraint c
CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS u(attnum, ord)
JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = u.attnum
WHERE c.conrelid = 'laplace.entities'::regclass AND c.contype = 'p';

SELECT to_regclass('laplace.entity_interpretations') IS NULL AS interpretations_absent;

DO $$
DECLARE
    a bytea := public.laplace_hash128_blake3('test/entity-identity/a');
    b bytea := public.laplace_hash128_blake3('test/entity-identity/b');
    ta bytea := public.laplace_hash128_blake3('test/entity-identity/type-a');
    tb bytea := public.laplace_hash128_blake3('test/entity-identity/type-b');
BEGIN
    -- Entities are source-independent content: one insert per content id, the
    -- row itself carrying its tier/type projection. Sources witness through
    -- attestations, never through a column here.
    INSERT INTO laplace.entities(id,tier,type_id)
    VALUES (a, 3, tb), (b, 1, ta);
END $$;

WITH ids AS (
    SELECT public.laplace_hash128_blake3('test/entity-identity/a') AS a,
           public.laplace_hash128_blake3('test/entity-identity/b') AS b
)
SELECT count(*) = 2 AS one_row_per_content_id
FROM laplace.entities e, ids
WHERE e.id IN (ids.a, ids.b);

WITH ids AS (
    SELECT public.laplace_hash128_blake3('test/entity-identity/a') AS a,
           public.laplace_hash128_blake3('test/entity-identity/b') AS b
)
SELECT (SELECT tier FROM laplace.entities WHERE id = ids.a) = 3
       AND (SELECT tier FROM laplace.entities WHERE id = ids.b) = 1
       AS row_projection_matches_insert
FROM ids;

ROLLBACK;
