\set ON_ERROR_STOP on
SELECT public.laplace_angular_distance_4d(p1.coord, p2.coord) AS angular_king_King
FROM laplace.physicalities p1, laplace.physicalities p2
WHERE p1.entity_id = laplace.word_id('King') AND p1.type = 1
  AND p2.entity_id = laplace.word_id('king') AND p2.type = 1
LIMIT 1;
