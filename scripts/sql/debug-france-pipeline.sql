SET search_path = laplace, public;

WITH seed_ids AS (
    SELECT coalesce(array_agg(wid), '{}'::bytea[]) AS ids
    FROM (SELECT word_id(s) AS wid FROM unnest(ARRAY['capital']) AS s) q WHERE wid IS NOT NULL
),
rel_types AS (
    SELECT array_agg(relation_type_id(n)) AS ids FROM unnest(ARRAY[
        'IS_A','IS_SYNONYM_OF','IS_COORDINATE_TERM_WITH','IS_ANTONYM_OF',
        'HAS_PART','PART_OF','MEMBER_OF','HAS_MEMBER','DERIVATIONALLY_RELATED',
        'FORM_OF','HAS_HYPONYM','IS_HYPERNYM_OF','SIMILAR_TO','PERTAINS_TO',
        'HAS_SENSE','IS_SENSE_OF']) AS n
),
crawled AS (
    SELECT c.entity_id, c.weight, row_number() OVER (ORDER BY c.weight DESC, c.entity_id) AS rn
    FROM seed_ids si, rel_types rt
    CROSS JOIN LATERAL foundry_crawl(si.ids, 1500, 3, 64, rt.ids) c
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
order_lemma AS (SELECT NULL::bytea AS entity_id, 0::bigint AS weight WHERE false),
order_closure AS (
    SELECT entity_id, max(weight) AS weight,
           row_number() OVER (ORDER BY max(weight) DESC, entity_id) AS rn
    FROM (SELECT entity_id, weight FROM order1_cut UNION ALL SELECT entity_id, weight FROM order2_cut) u
    GROUP BY entity_id
),
scaffold AS (
    SELECT f.id AS entity_id, f.d::bigint AS weight, row_number() OVER (ORDER BY f.d DESC) AS rn
    FROM (
        SELECT c.subject_id AS id, count(*) AS d FROM consensus c
        JOIN entities e ON e.id = c.subject_id AND e.tier = 2
        WHERE c.type_id = relation_type_id('PRECEDES') AND c.object_id IS NOT NULL
        GROUP BY c.subject_id) f
),
picked AS (
    SELECT entity_id, weight, 2 AS pri FROM order_closure WHERE rn <= 375
    UNION ALL SELECT entity_id, weight, 1 AS pri FROM crawled WHERE rn <= 750
    UNION ALL SELECT entity_id, weight, 0 AS pri FROM scaffold WHERE rn <= 375
),
france_id AS (SELECT word_id('France') AS id),
picked_f AS (SELECT * FROM picked p, france_id f WHERE p.entity_id = f.id),
uniq AS (
    SELECT DISTINCT ON (entity_id) entity_id, weight, pri FROM picked ORDER BY entity_id, pri DESC, weight DESC
),
uniq_f AS (SELECT * FROM uniq u, france_id f WHERE u.entity_id = f.id),
ids_arr AS (
    SELECT array_agg(entity_id ORDER BY pri DESC, weight DESC) AS ids,
           array_agg(weight ORDER BY pri DESC, weight DESC) AS ws,
           array_agg(pri ORDER BY pri DESC, weight DESC) AS prs FROM uniq
),
rendered AS (
    SELECT u.entity_id, u.s, a.ws[u.ord::int] AS weight, a.prs[u.ord::int] AS pri
    FROM ids_arr a
    CROSS JOIN LATERAL unnest(a.ids, render_text_batch(a.ids, 8)) WITH ORDINALITY AS u(entity_id, s, ord)
    WHERE a.ids IS NOT NULL AND cardinality(a.ids) > 0
),
rendered_f AS (SELECT * FROM rendered r, france_id f WHERE r.entity_id = f.id),
clean AS (
    SELECT DISTINCT ON (n.s) n.entity_id, n.s, n.weight, n.pri FROM rendered n
    WHERE n.s IS NOT NULL AND btrim(n.s) <> '' AND NOT is_all_whitespace(n.s)
      AND position(' ' IN n.s) = 0 AND char_length(n.s) <= 40
      AND n.s ~ '[[:alpha:]]' AND n.s !~ '^i[0-9]+$'
    ORDER BY n.s, n.pri DESC, n.weight DESC
),
clean_f AS (SELECT * FROM clean c, france_id f WHERE c.entity_id = f.id OR c.s = 'France')
SELECT 'picked' AS stage, count(*) FROM picked_f
UNION ALL SELECT 'uniq', count(*) FROM uniq_f
UNION ALL SELECT 'rendered', count(*) FROM rendered_f
UNION ALL SELECT 'clean', count(*) FROM clean_f;

SELECT entity_id, s, weight, pri FROM rendered_f;
SELECT entity_id, s, weight, pri FROM clean_f;
