\set probe 'king'
\set phrase 'the king'
\timing on

\echo '================ 1. GENERATION (trajectory walk / FFN over the corpus) ================'
SELECT laplace.generate('I was', 24);
SELECT laplace.walk_text('the king', 18);

\echo '================ 2. DEFINE (gloss-led sense readout) ================'
SELECT definition, round(eff_mu,1) AS mu, witnesses
FROM laplace.define(laplace.word_id(:'probe'), 5);

\echo '================ 3. SYNONYMS (same-language synset co-members, dominant sense first) ====='
SELECT synonym, round(eff_mu,1) AS mu, witnesses
FROM laplace.synonyms(laplace.word_id(:'probe'), 10);

\echo '================ 4. TRANSLATIONS (emergent, one row per language, dominant sense first) =='
SELECT translation, language, round(eff_mu,1) AS mu
FROM laplace.translations(laplace.word_id(:'probe'), 14);

\echo '================ 5. TRANSLATE_TO french (scoped; roi/monarch sense must lead) ==========='
SELECT translation, language, round(eff_mu,1) AS mu
FROM laplace.translate_to(laplace.word_id(:'probe'), 'french', 8);

\echo '================ 6. LANGUAGE COVERAGE (omniglottal witness count) ======================='
SELECT reply FROM laplace.language_coverage(laplace.word_id(:'probe'));

\echo '================ 7. RECALL x4 (conversational intent routing) =========================='
SELECT 'define'      AS intent, reply FROM laplace.recall(:'probe');
SELECT 'translate'   AS intent, reply FROM laplace.recall('how do you say king in french');
SELECT 'languages'   AS intent, reply FROM laplace.recall('what languages does king have');
SELECT 'related'     AS intent, reply FROM laplace.recall('what is related to king');

\echo '================ 8. ATTENTION (Glicko-2 consensus weight + S3 geodesic) ================='
SELECT neighbor, relation, round(attention::numeric,4) AS attention, round(geodesic::numeric,4) AS geodesic
FROM laplace.attention(laplace.word_id(:'probe'), 12);

\echo '================ 9. S3 GEOMETRY (nearest neighbors on the glome) ========================'
SELECT neighbor, round(geodesic::numeric,4) AS geodesic, round(frechet::numeric,4) AS frechet
FROM laplace.nearest_neighbors_4d(:'probe', 8);

\timing off
\echo '================ probe complete ================'
