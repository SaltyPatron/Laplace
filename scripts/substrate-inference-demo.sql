\set ON_ERROR_STOP on
\timing on

-- GH #534: ids resolve through the installed surface only. Never hand-hash
-- relation / content ids (the prior pg_temp.relation_type_id used a different
-- salt than relation_type_id() and silently queried empty bands).
--
-- Default subject = word_id('hot'). Override with native encode() hex:
--   psql -v subj=36fcbb29fdd839df97da13190a9983c2 -f scripts/substrate-inference-demo.sql
SET search_path = laplace, public;

\if :{?subj}
SELECT decode(:'subj', 'hex') AS subject_id \gset
\else
SELECT word_id('hot') AS subject_id \gset
\endif

\echo '== A. CONTENT ROUND-TRIP (entity -> its own text, no model in the path) =='
SELECT encode(:'subject_id'::bytea, 'hex') AS subject_hex,
       render_text(:'subject_id'::bytea) AS recovered_surface,
       realize.realize(:'subject_id'::bytea) AS realized;

\echo '== B. RANKED-μ: top EMBEDS channels of the subject (sorted index scan) =='
SELECT left(encode(object_id,'hex'),16) AS channel,
       round((rating/1e9)::numeric,3) AS mu,
       witness_count
FROM consensus
WHERE subject_id = :'subject_id'::bytea
  AND type_id = relation_type_id('EMBEDS')
ORDER BY rating DESC
LIMIT 5;

\echo '== C. QUERY-TIME BILINEAR READ: subject --EMBEDS--> ch --OUTPUT_PROJECTS--> tokens =='
\echo '   (exact over all channels; signed strength = mu - 1500; surfaced via render_text) =='
WITH emb AS (
  SELECT object_id AS ch, (rating/1e9 - 1500.0) AS m
  FROM consensus
  WHERE subject_id = :'subject_id'::bytea
    AND type_id = relation_type_id('EMBEDS')
),
comp AS (
  SELECT o.object_id AS tok, sum(e.m * (o.rating/1e9 - 1500.0)) AS score
  FROM emb e
  JOIN consensus o
    ON o.subject_id = e.ch
   AND o.type_id = relation_type_id('OUTPUT_PROJECTS')
  GROUP BY o.object_id
)
SELECT rank() OVER (ORDER BY score DESC) AS rnk,
       COALESCE(render_text(tok), left(encode(tok,'hex'),12)) AS token,
       round(score::numeric,0) AS score
FROM comp
ORDER BY score DESC
LIMIT 15;

\echo '== consensus health =='
SELECT * FROM consensus.stats();
