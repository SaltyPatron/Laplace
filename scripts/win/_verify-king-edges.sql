\set ON_ERROR_STOP on
\echo '=== consensus edge counts King vs king ==='
SELECT 'King' AS form, laplace.type_label(c.type_id) AS rel, count(*) AS n
FROM laplace.consensus c
WHERE c.subject_id = laplace.word_id('King')
GROUP BY 1, 2
UNION ALL
SELECT 'king', laplace.type_label(c.type_id), count(*)
FROM laplace.consensus c
WHERE c.subject_id = laplace.word_id('king')
GROUP BY 1, 2
ORDER BY 1, 3 DESC;

\echo '=== senses() ==='
SELECT 'King' AS form, count(*) FROM laplace.senses(laplace.word_id('King'))
UNION ALL SELECT 'king', count(*) FROM laplace.senses(laplace.word_id('king'));

\echo '=== IS_LEMMA_OF / FORM_OF linking ==='
SELECT laplace.type_label(c.type_id), count(*)
FROM laplace.consensus c
WHERE c.subject_id = laplace.word_id('King')
   OR c.object_id = laplace.word_id('King')
   OR c.subject_id = laplace.word_id('king')
   OR c.object_id = laplace.word_id('king')
GROUP BY 1
ORDER BY 2 DESC
LIMIT 15;
