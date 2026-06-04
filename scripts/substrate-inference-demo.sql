-- substrate-inference-demo.sql — SQL inference over an ingested model, no GPU.
--
-- Run against a DB that has a model ingested (consensus populated):
--   psql -d laplace-dev -U laplace_admin -v ON_ERROR_STOP=1 \
--        -v src=<model-source-hex-CLI-order> -v subj=<entity-hex-CLI-order> \
--        -f scripts/substrate-inference-demo.sql
--
-- Proves three claims of the invention, each as plain SQL the substrate ships
-- (consensus is read directly — never a per-query re-aggregation of evidence):
--   A. CONTENT ROUND-TRIP — an entity's text is recovered from its own
--      content trajectory (mantissa-packed constituent codepoint ids → chars),
--      with zero model/source in the path. Identity is content.
--   B. RANKED-μ RELATEDNESS — a sorted index scan over consensus effective μ.
--   C. THE QUERY-TIME BILINEAR READ — token --EMBEDS--> channels
--      --OUTPUT_PROJECTS--> tokens, composed by a μ-ranked join (the embed→
--      unembed map; the layers' knowledge is the multi-hop extension of this).
--
-- :src  = the model's content source id, CLI-printed hex (u64-LE halves).
-- :subj = a subject entity id to inspect, in CLI-printed hex (what
--         `laplace inspect` shows). Default below = TinyLlama "Paris",
--         CLI id 72e7eea2c9fd8c5c84d10b3bc594a4cc (→ DB-order bytea via cli_id).

\set ON_ERROR_STOP on
\timing on

-- CLI hex (two u64 printed big-endian) -> DB bytea (per-u64 little-endian).
CREATE OR REPLACE FUNCTION pg_temp.cli_id(h text) RETURNS bytea LANGUAGE sql IMMUTABLE AS $$
  SELECT decode(
    (SELECT string_agg(substr(h, 17-2*i, 2), '' ORDER BY i) FROM generate_series(1,8) i) ||
    (SELECT string_agg(substr(h, 33-2*i, 2), '' ORDER BY i) FROM generate_series(1,8) i), 'hex') $$;
CREATE OR REPLACE FUNCTION pg_temp.kind(n text) RETURNS bytea LANGUAGE sql IMMUTABLE AS $$
  SELECT public.laplace_hash128_blake3(convert_to('substrate/kind/'||n||'/v1','UTF8')) $$;

-- Derivable codepoint reverse map: entity id = BLAKE3(utf8(char)).
DROP TABLE IF EXISTS cp_map;
CREATE TEMP TABLE cp_map AS
  SELECT public.laplace_hash128_blake3(convert_to(chr(i),'UTF8')) AS id, chr(i) AS ch
  FROM generate_series(1, 1114111) i WHERE i NOT BETWEEN 55296 AND 57343;
CREATE UNIQUE INDEX ON cp_map(id);

-- surface(entity) — recover text from the content trajectory (kind=1).
CREATE OR REPLACE FUNCTION pg_temp.surface(p_id bytea) RETURNS text LANGUAGE sql STABLE AS $$
  SELECT string_agg(repeat(m.ch, GREATEST((u).run_length,1)), '' ORDER BY (u).ordinal)
  FROM laplace.physicalities p,
       LATERAL ST_DumpPoints(p.trajectory) g,
       LATERAL public.laplace_mantissa_unpack(g.geom) u
  JOIN cp_map m ON m.id = (u).entity_id
  WHERE p.entity_id = p_id AND p.kind = 1 $$;

\if :{?subj}
\else
  \set subj '72e7eea2c9fd8c5c84d10b3bc594a4cc'
\endif

\echo '== A. CONTENT ROUND-TRIP (entity -> its own text, no model in the path) =='
SELECT :'subj' AS subject_cli_hex, pg_temp.surface(pg_temp.cli_id(:'subj')) AS recovered_surface;

\echo '== B. RANKED-μ: top EMBEDS channels of the subject (sorted index scan) =='
SELECT left(encode(object_id,'hex'),16) AS channel, round((rating/1e9)::numeric,3) AS mu, witness_count
FROM laplace.consensus
WHERE subject_id = pg_temp.cli_id(:'subj') AND kind_id = pg_temp.kind('EMBEDS')
ORDER BY rating DESC LIMIT 5;

\echo '== C. QUERY-TIME BILINEAR READ: subject --EMBEDS--> ch --OUTPUT_PROJECTS--> tokens =='
\echo '   (exact over all channels; signed strength = mu - 1500; surfaced) =='
WITH emb AS (
  SELECT object_id AS ch, (rating/1e9 - 1500.0) AS m
  FROM laplace.consensus
  WHERE subject_id = pg_temp.cli_id(:'subj') AND kind_id = pg_temp.kind('EMBEDS')
),
comp AS (
  SELECT o.object_id AS tok, sum(e.m * (o.rating/1e9 - 1500.0)) AS score
  FROM emb e
  JOIN laplace.consensus o ON o.subject_id = e.ch AND o.kind_id = pg_temp.kind('OUTPUT_PROJECTS')
  GROUP BY o.object_id
)
SELECT rank() OVER (ORDER BY score DESC) AS rnk,
       COALESCE(pg_temp.surface(tok), left(encode(tok,'hex'),12)) AS token,
       round(score::numeric,0) AS score
FROM comp ORDER BY score DESC LIMIT 15;

\echo '== consensus health =='
SELECT * FROM laplace.consensus_stats();
