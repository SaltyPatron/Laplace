




SET search_path = laplace, public;
\pset pager off

\echo '=== arena populations (consensus rows per behavioral relation type) ==='
SELECT t.name,
       count(*)                                   AS relations,
       sum(c.witness_count)                       AS games,
       round(avg(consensus.eff_mu(c.rating, c.rd) / 1e9), 1) AS avg_eff_mu
FROM (VALUES ('SIMILAR_TO'), ('ATTENDS'), ('OV_RELATES'), ('COMPLETES_TO')) t(name)
JOIN consensus c ON c.type_id = relation_type_id(t.name)
GROUP BY t.name ORDER BY t.name;

\echo '=== strongest SIMILAR_TO testimony (embedding-space neighbors, rendered) ==='
SELECT (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(c.subject_id)::bytea],NULL)::public.blake3[]) lp_l) AS a, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(c.object_id)::bytea],NULL)::public.blake3[]) lp_l) AS b,
       consensus.eff_mu_display(c.rating, c.rd) AS eff_mu, c.witness_count
FROM consensus c
WHERE c.type_id = relation_type_id('SIMILAR_TO') AND c.object_id IS NOT NULL
ORDER BY consensus.eff_mu(c.rating, c.rd) DESC LIMIT 15;

\echo '=== strongest ATTENDS testimony (attention affinity, rendered) ==='
SELECT (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(c.subject_id)::bytea],NULL)::public.blake3[]) lp_l) AS attender, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(c.object_id)::bytea],NULL)::public.blake3[]) lp_l) AS attended,
       consensus.eff_mu_display(c.rating, c.rd) AS eff_mu, c.witness_count
FROM consensus c
WHERE c.type_id = relation_type_id('ATTENDS') AND c.object_id IS NOT NULL
ORDER BY consensus.eff_mu(c.rating, c.rd) DESC LIMIT 15;

\echo '=== strongest COMPLETES_TO testimony (FFN completion, rendered) ==='
SELECT (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(c.subject_id)::bytea],NULL)::public.blake3[]) lp_l) AS token, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(c.object_id)::bytea],NULL)::public.blake3[]) lp_l) AS completes_to,
       consensus.eff_mu_display(c.rating, c.rd) AS eff_mu, c.witness_count
FROM consensus c
WHERE c.type_id = relation_type_id('COMPLETES_TO') AND c.object_id IS NOT NULL
ORDER BY consensus.eff_mu(c.rating, c.rd) DESC LIMIT 15;

\echo '=== walk probe: one-hop completions from the strongest COMPLETES_TO subject ==='
WITH seed AS (
    SELECT c.subject_id FROM consensus c
    WHERE c.type_id = relation_type_id('COMPLETES_TO') AND c.object_id IS NOT NULL
    ORDER BY consensus.eff_mu(c.rating, c.rd) DESC LIMIT 1)
SELECT (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(s.subject_id)::bytea],NULL)::public.blake3[]) lp_l) AS seed, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(c.object_id)::bytea],NULL)::public.blake3[]) lp_l) AS hop1,
       consensus.eff_mu_display(c.rating, c.rd) AS eff_mu
FROM seed s
JOIN consensus c ON c.subject_id = s.subject_id
                AND c.type_id = relation_type_id('COMPLETES_TO')
                AND c.object_id IS NOT NULL
ORDER BY consensus.eff_mu(c.rating, c.rd) DESC LIMIT 10;

\echo '=== fusion check: tokens carrying testimony in MORE than one behavioral arena ==='
SELECT (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(c.subject_id)::bytea],NULL)::public.blake3[]) lp_l) AS token, count(DISTINCT c.type_id) AS arenas,
       sum(c.witness_count) AS games
FROM consensus c
WHERE c.type_id IN (relation_type_id('SIMILAR_TO'), relation_type_id('ATTENDS'),
                    relation_type_id('OV_RELATES'), relation_type_id('COMPLETES_TO'))
GROUP BY c.subject_id
HAVING count(DISTINCT c.type_id) >= 3
ORDER BY games DESC LIMIT 10;
