\set ON_ERROR_STOP on
SET search_path = laplace, public;

\echo '=== WORD IDS ==='
SELECT w AS surface,
       encode(word_id(w), 'hex') AS word_id_hex,
       word_id(w) IS NOT NULL AS resolved
FROM unnest(ARRAY['capital','france','Paris','paris','France','of','city']) AS w;

\echo ''
\echo '=== ENTITY FACETS ==='
SELECT (SELECT NULLIF(lp_l.content,lp_l.entity::text) FROM public.laplace_labels(array_remove(ARRAY[(word_id(w))::bytea],NULL)::public.blake3[]) lp_l) AS surface, e.tier, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(e.type_id)::bytea],NULL)::public.blake3[]) lp_l) AS type
FROM unnest(ARRAY['capital','france','Paris','paris']) AS w
JOIN entities e ON e.id = word_id(w);

\echo ''
\echo '=== CORPUS FREQUENCY RANK (trajectory occurrence) ==='
WITH w AS (
    SELECT tc.entity_id AS id, count(*) AS c
    FROM (
        SELECT DISTINCT ON (p.entity_id) p.trajectory
        FROM physicalities p JOIN entities e ON e.id = p.entity_id AND e.tier > 2
        WHERE p.type = 1 AND p.trajectory IS NOT NULL
        ORDER BY p.entity_id, p.source_id LIMIT 400000
    ) t
    CROSS JOIN LATERAL laplace_trajectory_constituents(t.trajectory) tc
    JOIN entities e ON e.id = tc.entity_id AND e.tier = 2
    GROUP BY tc.entity_id
),
ranked AS (
    SELECT (SELECT NULLIF(lp_l.content,lp_l.entity::text) FROM public.laplace_labels(array_remove(ARRAY[(id)::bytea],NULL)::public.blake3[]) lp_l) AS surface, c,
           row_number() OVER (ORDER BY c DESC) AS freq_rank
    FROM w
)
SELECT * FROM ranked
WHERE surface IN ('capital','france','Paris','paris','France','the','of','is','brain','king')
ORDER BY freq_rank;

\echo ''
\echo '=== PRECEDES: what FOLLOWS each word (consensus collocates) ==='
SELECT 'capital' AS seed, * FROM structural.collocates('capital', 25);
SELECT 'france' AS seed, * FROM structural.collocates('france', 25);
SELECT 'Paris' AS seed, * FROM structural.collocates('Paris', 25);
SELECT 'paris' AS seed, * FROM structural.collocates('paris', 25);

\echo ''
\echo '=== PRECEDES: what PRECEDES each word (consensus in-edges) ==='
SELECT (SELECT NULLIF(lp_l.content,lp_l.entity::text) FROM public.laplace_labels(array_remove(ARRAY[(c.subject_id)::bytea],NULL)::public.blake3[]) lp_l) AS precedes,
       (SELECT NULLIF(lp_l.content,lp_l.entity::text) FROM public.laplace_labels(array_remove(ARRAY[(c.object_id)::bytea],NULL)::public.blake3[]) lp_l) AS word,
       consensus.eff_mu_display(c.rating, c.rd) AS mu,
       c.witness_count
FROM consensus c
WHERE c.object_id IN (word_id('capital'), word_id('france'), word_id('Paris'), word_id('paris'))
  AND c.type_id = relation_type_id('PRECEDES')
  AND NOT consensus.refuted(c.rating, c.rd)
ORDER BY consensus.eff_mu(c.rating, c.rd) DESC
LIMIT 40;

\echo ''
\echo '=== capital -> france / Paris PRECEDES edges specifically ==='
SELECT (SELECT NULLIF(lp_l.content,lp_l.entity::text) FROM public.laplace_labels(array_remove(ARRAY[(c.subject_id)::bytea],NULL)::public.blake3[]) lp_l) AS subject,
       (SELECT NULLIF(lp_l.content,lp_l.entity::text) FROM public.laplace_labels(array_remove(ARRAY[(c.object_id)::bytea],NULL)::public.blake3[]) lp_l) AS object,
       consensus.eff_mu_display(c.rating, c.rd) AS mu,
       c.witness_count
FROM consensus c
WHERE c.type_id = relation_type_id('PRECEDES')
  AND (
    (c.subject_id = word_id('capital') AND (SELECT NULLIF(lp_l.content,lp_l.entity::text) FROM public.laplace_labels(array_remove(ARRAY[(c.object_id)::bytea],NULL)::public.blake3[]) lp_l) IN ('france','Paris','paris','France','of'))
    OR (c.subject_id = word_id('of') AND c.object_id IN (word_id('france'), word_id('Paris'), word_id('paris')))
    OR ((SELECT NULLIF(lp_l.content,lp_l.entity::text) FROM public.laplace_labels(array_remove(ARRAY[(c.subject_id)::bytea],NULL)::public.blake3[]) lp_l) = 'capital' AND (SELECT NULLIF(lp_l.content,lp_l.entity::text) FROM public.laplace_labels(array_remove(ARRAY[(c.object_id)::bytea],NULL)::public.blake3[]) lp_l) IN ('france','Paris','paris'))
  )
ORDER BY consensus.eff_mu(c.rating, c.rd) DESC;

\echo ''
\echo '=== TRAJECTORY BIGRAMS (direct from geometry, not folded PRECEDES) ==='
WITH targets AS (
    SELECT unnest(ARRAY[word_id('capital'), word_id('france'), word_id('Paris'), word_id('paris')]) AS tid
),
t AS (
    SELECT DISTINCT ON (p.entity_id) p.entity_id AS sid, p.trajectory
    FROM physicalities p JOIN entities e ON e.id = p.entity_id AND e.tier > 2
    WHERE p.type = 1 AND p.trajectory IS NOT NULL
      AND laplace_trajectory_constituent_ids(p.trajectory) && (SELECT array_agg(tid) FROM targets)
    ORDER BY p.entity_id, p.source_id
    LIMIT 50000
),
seq AS (
    SELECT t.sid, tc.ordinal AS ord, tc.entity_id AS wid
    FROM t CROSS JOIN LATERAL laplace_trajectory_constituents(t.trajectory) tc
    JOIN entities e ON e.id = tc.entity_id AND e.tier = 2
),
pairs AS (
    SELECT (SELECT NULLIF(lp_l.content,lp_l.entity::text) FROM public.laplace_labels(array_remove(ARRAY[(a.wid)::bytea],NULL)::public.blake3[]) lp_l) AS cur,
           (SELECT NULLIF(lp_l.content,lp_l.entity::text) FROM public.laplace_labels(array_remove(ARRAY[(b.wid)::bytea],NULL)::public.blake3[]) lp_l) AS nxt,
           count(*) AS c
    FROM seq a
    JOIN seq b ON a.sid = b.sid AND b.ord = a.ord + 1
    WHERE a.wid IN (SELECT tid FROM targets)
    GROUP BY a.wid, b.wid
    ORDER BY c DESC
    LIMIT 30
)
SELECT * FROM pairs;

\echo ''
\echo '=== TRAJECTORY TRIGRAMS capital * * ==='
WITH t AS (
    SELECT DISTINCT ON (p.entity_id) p.entity_id AS sid, p.trajectory
    FROM physicalities p
    WHERE p.type = 1 AND p.trajectory IS NOT NULL
      AND laplace_trajectory_constituent_ids(p.trajectory) @> ARRAY[word_id('capital')]
    ORDER BY p.entity_id, p.source_id LIMIT 20000
),
seq AS (
    SELECT t.sid, tc.ordinal AS ord, (SELECT NULLIF(lp_l.content,lp_l.entity::text) FROM public.laplace_labels(array_remove(ARRAY[(tc.entity_id)::bytea],NULL)::public.blake3[]) lp_l) AS w
    FROM t CROSS JOIN LATERAL laplace_trajectory_constituents(t.trajectory) tc
    JOIN entities e ON e.id = tc.entity_id AND e.tier = 2
),
tri AS (
    SELECT a.w AS w1, b.w AS w2, c.w AS w3, count(*) AS cnt
    FROM seq a
    JOIN seq b ON a.sid = b.sid AND b.ord = a.ord + 1
    JOIN seq c ON a.sid = c.sid AND c.ord = a.ord + 2
    WHERE a.w = 'capital'
    GROUP BY a.w, b.w, c.w
    ORDER BY cnt DESC
    LIMIT 25
)
SELECT * FROM tri;

\echo ''
\echo '=== RECALL TRAJECTORIES (sentences containing word) ==='
SELECT 'capital' AS word, answer FROM generation.recall_trajectories('capital', 8);
SELECT 'france' AS word, answer FROM generation.recall_trajectories('france', 8);
SELECT 'Paris' AS word, answer FROM generation.recall_trajectories('Paris', 8);

\echo ''
\echo '=== ATTESTATIONS OUT (top 30 per word) ==='
SELECT 'capital' AS word, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(a.type_id)::bytea],NULL)::public.blake3[]) lp_l) AS rel, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(a.object_id)::bytea],NULL)::public.blake3[]) lp_l) AS object,
       a.outcome, a.observation_count, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(a.source_id)::bytea],NULL)::public.blake3[]) lp_l) AS source
FROM attestations_out(word_id('capital'), 30) a;
SELECT 'france' AS word, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(a.type_id)::bytea],NULL)::public.blake3[]) lp_l) AS rel, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(a.object_id)::bytea],NULL)::public.blake3[]) lp_l) AS object,
       a.outcome, a.observation_count, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(a.source_id)::bytea],NULL)::public.blake3[]) lp_l) AS source
FROM attestations_out(word_id('france'), 30) a;
SELECT 'Paris' AS word, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(a.type_id)::bytea],NULL)::public.blake3[]) lp_l) AS rel, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(a.object_id)::bytea],NULL)::public.blake3[]) lp_l) AS object,
       a.outcome, a.observation_count, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(a.source_id)::bytea],NULL)::public.blake3[]) lp_l) AS source
FROM attestations_out(word_id('Paris'), 30) a;

\echo ''
\echo '=== ATTESTATIONS IN (top 20 per word) ==='
SELECT 'capital' AS word, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(a.subject_id)::bytea],NULL)::public.blake3[]) lp_l) AS subject, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(a.type_id)::bytea],NULL)::public.blake3[]) lp_l) AS rel,
       a.outcome, a.observation_count
FROM attestations_in(word_id('capital'), 20) a;
SELECT 'france' AS word, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(a.subject_id)::bytea],NULL)::public.blake3[]) lp_l) AS subject, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(a.type_id)::bytea],NULL)::public.blake3[]) lp_l) AS rel,
       a.outcome, a.observation_count
FROM attestations_in(word_id('france'), 20) a;
SELECT 'Paris' AS word, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(a.subject_id)::bytea],NULL)::public.blake3[]) lp_l) AS subject, (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(a.type_id)::bytea],NULL)::public.blake3[]) lp_l) AS rel,
       a.outcome, a.observation_count
FROM attestations_in(word_id('Paris'), 20) a;

\echo ''
\echo '=== CONSENSUS OUT READABLE (all relation types, top 25) ==='
SELECT 'capital' AS word, * FROM consensus_out_readable(word_id('capital'), 25);
SELECT 'france' AS word, * FROM consensus_out_readable(word_id('france'), 25);
SELECT 'Paris' AS word, * FROM consensus_out_readable(word_id('Paris'), 25);

\echo ''
\echo '=== LEXICAL RELATIONS (foundry crawl edge set) involving these words ==='
SELECT (SELECT NULLIF(lp_l.content,lp_l.entity::text) FROM public.laplace_labels(array_remove(ARRAY[(c.subject_id)::bytea],NULL)::public.blake3[]) lp_l) AS subject,
       (SELECT lp_l.content FROM public.laplace_labels(array_remove(ARRAY[(c.type_id)::bytea],NULL)::public.blake3[]) lp_l) AS rel,
       (SELECT NULLIF(lp_l.content,lp_l.entity::text) FROM public.laplace_labels(array_remove(ARRAY[(c.object_id)::bytea],NULL)::public.blake3[]) lp_l) AS object,
       consensus.eff_mu_display(c.rating, c.rd) AS mu
FROM consensus c
WHERE (c.subject_id IN (word_id('capital'), word_id('france'), word_id('Paris'))
    OR c.object_id IN (word_id('capital'), word_id('france'), word_id('Paris')))
  AND c.type_id IN (
    SELECT relation_type_id(n) FROM unnest(ARRAY[
        'IS_A','IS_SYNONYM_OF','IS_COORDINATE_TERM_WITH','IS_ANTONYM_OF',
        'HAS_PART','PART_OF','MEMBER_OF','DERIVATIONALLY_RELATED',
        'FORM_OF','HAS_HYPONYM','IS_HYPERNYM_OF','SIMILAR_TO','PERTAINS_TO',
        'HAS_SENSE','IS_SENSE_OF'
    ]) AS n
  )
  AND NOT consensus.refuted(c.rating, c.rd)
ORDER BY consensus.eff_mu(c.rating, c.rd) DESC
LIMIT 40;

\echo ''
\echo '=== FOUNDRY CRAWL: is france reachable from capital seed alone? ==='
SELECT surface, weight FROM generation.foundry_vocab_crawl(ARRAY['capital'], 1500, 3, 64)
WHERE surface IN ('france','Paris','paris','France','of','city','capital');

\echo ''
\echo '=== FOUNDRY CRAWL: france in full corpus-seed crawl? ==='
SELECT surface, weight FROM generation.foundry_vocab_crawl(
    (SELECT array_agg(surface) FROM (SELECT surface FROM generation.corpus_word_vocab(1000, 400000)) q),
    1500, 3, 64)
WHERE surface IN ('france','Paris','paris','France','capital');

\echo ''
\echo '=== PRECEDES scaffold global rank (why scaffold misses france) ==='
SELECT rank, surface, d FROM (
    SELECT row_number() OVER (ORDER BY d DESC) AS rank,
           (SELECT NULLIF(lp_l.content,lp_l.entity::text) FROM public.laplace_labels(array_remove(ARRAY[(id)::bytea],NULL)::public.blake3[]) lp_l) AS surface, d
    FROM (
        SELECT c.subject_id AS id, count(*) AS d
        FROM consensus c JOIN entities e ON e.id = c.subject_id AND e.tier = 2
        WHERE c.type_id = relation_type_id('PRECEDES') AND c.object_id IS NOT NULL
        GROUP BY c.subject_id
    ) f
) x
WHERE surface IN ('france','Paris','paris','capital','the','of','brain')
   OR rank <= 15
ORDER BY rank;
