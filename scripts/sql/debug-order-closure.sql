SET search_path = laplace, public;

WITH seed_ids AS (
    SELECT coalesce(array_agg(wid), '{}'::bytea[]) AS ids
    FROM (SELECT word_id(s) AS wid FROM unnest(ARRAY['capital']) AS s) q WHERE wid IS NOT NULL
),
order1 AS (
    SELECT c.object_id AS entity_id, eff_mu(c.rating, c.rd)::numeric AS w,
           row_number() OVER (PARTITION BY seed.wid ORDER BY eff_mu(c.rating, c.rd) DESC, c.object_id) AS rn
    FROM seed_ids si CROSS JOIN unnest(si.ids) AS seed(wid)
    JOIN consensus c ON c.subject_id = seed.wid AND c.type_id = relation_type_id('PRECEDES')
        AND c.object_id IS NOT NULL AND NOT refuted(c.rating, c.rd)
    JOIN entities e ON e.id = c.object_id AND e.tier = 2
),
order1_cut AS (SELECT entity_id, w, w::bigint AS weight FROM order1 WHERE rn <= 64),
order2 AS (
    SELECT c.object_id AS entity_id,
           (o.w * eff_mu(c.rating, c.rd)::numeric / 1000000000)::bigint AS weight,
           row_number() OVER (ORDER BY o.w * eff_mu(c.rating, c.rd) DESC, c.object_id) AS rn
    FROM order1_cut o
    JOIN consensus c ON c.subject_id = o.entity_id AND c.type_id = relation_type_id('PRECEDES')
        AND c.object_id IS NOT NULL AND NOT refuted(c.rating, c.rd)
    JOIN entities e ON e.id = c.object_id AND e.tier = 2
),
order2_cut AS (SELECT entity_id, weight FROM order2 WHERE rn <= 1500),
order_closure AS (
    SELECT entity_id, max(weight) AS weight,
           row_number() OVER (ORDER BY max(weight) DESC, entity_id) AS rn
    FROM (SELECT entity_id, weight FROM order1_cut UNION ALL SELECT entity_id, weight FROM order2_cut) u
    GROUP BY entity_id
)
SELECT 'closure' AS stage, render_text(entity_id, 40) AS surface, weight, rn FROM order_closure
WHERE render_text(entity_id, 40) IN ('France', 'Paris', 'of', 'capital', 'france')
   OR rn <= 8
UNION ALL
SELECT 'order2' AS stage, render_text(entity_id, 40), weight, rn FROM order2
WHERE render_text(entity_id, 40) IN ('France', 'Paris') OR rn BETWEEN 348 AND 355
ORDER BY stage, rn;
