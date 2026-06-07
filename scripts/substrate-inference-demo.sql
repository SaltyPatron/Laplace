\set ON_ERROR_STOP on
\timing on

CREATE OR REPLACE FUNCTION pg_temp.cli_id(h text) RETURNS bytea LANGUAGE sql IMMUTABLE AS $$
  SELECT decode(
    (SELECT string_agg(substr(h, 17-2*i, 2), '' ORDER BY i) FROM generate_series(1,8) i) ||
    (SELECT string_agg(substr(h, 33-2*i, 2), '' ORDER BY i) FROM generate_series(1,8) i), 'hex') $$;
CREATE OR REPLACE FUNCTION pg_temp.kind(n text) RETURNS bytea LANGUAGE sql IMMUTABLE AS $$
  SELECT public.laplace_hash128_blake3(convert_to('substrate/kind/'||n||'/v1','UTF8')) $$;

DROP TABLE IF EXISTS cp_map;
CREATE TEMP TABLE cp_map AS
  SELECT public.laplace_hash128_blake3(convert_to(chr(i),'UTF8')) AS id, chr(i) AS ch
  FROM generate_series(1, 1114111) i WHERE i NOT BETWEEN 55296 AND 57343;
CREATE UNIQUE INDEX ON cp_map(id);

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
