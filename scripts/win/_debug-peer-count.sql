\set ON_ERROR_STOP on
SELECT array_length(laplace.lexical_peers(laplace.word_id('King')), 1) AS king_cap_peers;
SELECT array_length(laplace.lexical_peers(laplace.word_id('king')), 1) AS king_low_peers;
SELECT laplace.word_id('King') = ANY(laplace.lexical_peers(laplace.word_id('king'))) AS cap_in_low;
SELECT laplace.word_id('king') = ANY(laplace.lexical_peers(laplace.word_id('King'))) AS low_in_cap;
SELECT public.laplace_angular_distance_4d(p1.coord, p2.coord) AS ang
FROM laplace.physicalities p1, laplace.physicalities p2
WHERE p1.entity_id=laplace.word_id('king') AND p1.type=1
  AND p2.entity_id=laplace.word_id('King') AND p2.type=1;
