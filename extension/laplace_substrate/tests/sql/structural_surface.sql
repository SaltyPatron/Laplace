CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION laplace_geom;
CREATE EXTENSION laplace_substrate;

SET search_path TO laplace, public;

SELECT count(*) = 6 AS structural_functions_present
FROM pg_proc p
JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname = 'laplace'
  AND p.proname IN (
      'word_curve', 'word_shape_distance', 'anagrams_of',
      'collocates', 'structural_cluster', 'entity_curve');

SELECT count(*) AS collocates_empty_for_unknown_word
FROM collocates('zzznosuchword', 5);

SELECT word_curve(word_id('dog')) IS NULL AS no_curve_without_physicality;

SELECT count(*) = 0 AS cluster_empty_without_physicality
FROM structural_cluster(word_id('dog'), 0.05, 10);
