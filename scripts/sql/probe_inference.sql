\set probe 'king'
\set phrase 'the king'
\timing on

\echo '================ 1. GENERATION (trajectory walk / FFN over the corpus) ================'
SELECT generation.generate('I was', 24);
SELECT generation.walk_text('the king', 18);

\echo '================ 2. DEFINE (gloss-led sense readout) ================'
SELECT definition, round(eff_mu,1) AS mu, witnesses
FROM lexical.define(public.blake3_send(public.laplace_text_id(:'probe')), 5);

\echo '================ 3. SYNONYMS (same-language synset co-members, dominant sense first) ====='
SELECT synonym, round(eff_mu,1) AS mu, witnesses
FROM converse.synonyms(public.blake3_send(public.laplace_text_id(:'probe')), 10);

\echo '================ 4. TRANSLATIONS (emergent, one row per language, dominant sense first) =='
SELECT translation, language, round(eff_mu,1) AS mu
FROM converse.translations(public.blake3_send(public.laplace_text_id(:'probe')), 14);

\echo '================ 5. TRANSLATE_TO french (scoped; roi/monarch sense must lead) ==========='
SELECT translation, language, round(eff_mu,1) AS mu
FROM converse.translate_to(public.blake3_send(public.laplace_text_id(:'probe')), 'french', 8);

\echo '================ 6. LANGUAGE COVERAGE (omniglottal witness count) ======================='
SELECT reply FROM converse.language_coverage(public.blake3_send(public.laplace_text_id(:'probe')));

\echo '================ 7. RECALL x4 (conversational intent routing) =========================='
SELECT 'define'      AS intent, reply FROM converse.recall(:'probe');
SELECT 'translate'   AS intent, reply FROM converse.recall('how do you say king in french');
SELECT 'languages'   AS intent, reply FROM converse.recall('what languages does king have');
SELECT 'related'     AS intent, reply FROM converse.recall('what is related to king');

\echo '================ 8. ATTENTION (Glicko-2 consensus weight + S3 geodesic) ================='
SELECT neighbor, relation, round(attention::numeric,4) AS attention, round(geodesic::numeric,4) AS geodesic
FROM converse.attention(public.blake3_send(public.laplace_text_id(:'probe')), 12);

\echo '================ 9. S3 SHAPE (nearest stored coordinates; Frechet over realized curves) ========'
WITH anchor AS MATERIALIZED (SELECT public.laplace_text_id(:'probe') AS id,public.laplace_text_coord(:'probe') AS coord),
nearest AS MATERIALIZED (SELECT n.id,n.geodesic FROM anchor a CROSS JOIN LATERAL (
  SELECT e.id,e.coord OPERATOR(public.<=>) a.coord AS geodesic FROM public.entity e WHERE e.id<>a.id
  ORDER BY e.coord OPERATOR(public.<=>) a.coord LIMIT 8) n),
curves AS MATERIALIZED (SELECT c.entity,public.ST_GeomFromEWKB(c.curve_ewkb) AS curve
  FROM public.laplace_curves(ARRAY(SELECT id FROM nearest)||ARRAY(SELECT id FROM anchor)) c)
SELECT l.content AS neighbor, round(n.geodesic::numeric,4) AS geodesic,
       round(public.laplace_frechet4d(c.curve,(SELECT ac.curve FROM curves ac JOIN anchor a ON ac.entity=a.id LIMIT 1))::numeric,4) AS frechet
FROM nearest n JOIN public.laplace_labels(ARRAY(SELECT id FROM nearest)) l ON l.entity=n.id
LEFT JOIN curves c ON c.entity=n.id ORDER BY n.geodesic,n.id;

\timing off
\echo '================ probe complete ================'
