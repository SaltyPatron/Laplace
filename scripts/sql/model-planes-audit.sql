




SET search_path = laplace, public;
\pset pager off

\echo '=== arena populations (consensus rows per behavioral relation type) ==='
SELECT t.name,
       count(*)                                   AS relations,
       sum(c.witness_count)                       AS games,
       round(avg((c.rating - 2*c.rd) / 1e9), 1)   AS avg_eff_mu
FROM (VALUES ('SIMILAR_TO'), ('ATTENDS'), ('OV_RELATES'), ('COMPLETES_TO')) t(name)
JOIN consensus c ON c.type_id = relation_type_id(t.name)
GROUP BY t.name ORDER BY t.name;

\echo '=== strongest SIMILAR_TO testimony (embedding-space neighbors, rendered) ==='
SELECT render(c.subject_id) AS a, render(c.object_id) AS b,
       eff_mu_display(c.rating, c.rd) AS eff_mu, c.witness_count
FROM consensus c
WHERE c.type_id = relation_type_id('SIMILAR_TO') AND c.object_id IS NOT NULL
ORDER BY (c.rating - 2*c.rd) DESC LIMIT 15;

\echo '=== strongest ATTENDS testimony (attention affinity, rendered) ==='
SELECT render(c.subject_id) AS attender, render(c.object_id) AS attended,
       eff_mu_display(c.rating, c.rd) AS eff_mu, c.witness_count
FROM consensus c
WHERE c.type_id = relation_type_id('ATTENDS') AND c.object_id IS NOT NULL
ORDER BY (c.rating - 2*c.rd) DESC LIMIT 15;

\echo '=== strongest COMPLETES_TO testimony (FFN completion, rendered) ==='
SELECT render(c.subject_id) AS token, render(c.object_id) AS completes_to,
       eff_mu_display(c.rating, c.rd) AS eff_mu, c.witness_count
FROM consensus c
WHERE c.type_id = relation_type_id('COMPLETES_TO') AND c.object_id IS NOT NULL
ORDER BY (c.rating - 2*c.rd) DESC LIMIT 15;

\echo '=== walk probe: one-hop completions from the strongest COMPLETES_TO subject ==='
WITH seed AS (
    SELECT c.subject_id FROM consensus c
    WHERE c.type_id = relation_type_id('COMPLETES_TO') AND c.object_id IS NOT NULL
    ORDER BY (c.rating - 2*c.rd) DESC LIMIT 1)
SELECT render(s.subject_id) AS seed, render(c.object_id) AS hop1,
       eff_mu_display(c.rating, c.rd) AS eff_mu
FROM seed s
JOIN consensus c ON c.subject_id = s.subject_id
                AND c.type_id = relation_type_id('COMPLETES_TO')
                AND c.object_id IS NOT NULL
ORDER BY (c.rating - 2*c.rd) DESC LIMIT 10;

\echo '=== fusion check: tokens carrying testimony in MORE than one behavioral arena ==='
SELECT render(c.subject_id) AS token, count(DISTINCT c.type_id) AS arenas,
       sum(c.witness_count) AS games
FROM consensus c
WHERE c.type_id IN (relation_type_id('SIMILAR_TO'), relation_type_id('ATTENDS'),
                    relation_type_id('OV_RELATES'), relation_type_id('COMPLETES_TO'))
GROUP BY c.subject_id
HAVING count(DISTINCT c.type_id) >= 3
ORDER BY games DESC LIMIT 10;
