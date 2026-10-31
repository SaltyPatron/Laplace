\set ON_ERROR_STOP on
SET statement_timeout='30s';
EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON)
WITH nearest AS MATERIALIZED (
 SELECT w.id,w.coord,public.laplace_direction_4d(w.coord) <<->> public.laplace_direction_4d(public.ST_SetSRID(public.ST_MakePoint(1,1,1,1),0)) chord
 FROM laplace.v_word_points w WHERE w.id<>decode(repeat('00',16),'hex') AND w.tier=2
 AND public.laplace_direction_4d(w.coord) IS NOT NULL
 ORDER BY public.laplace_direction_4d(w.coord) <<->> public.laplace_direction_4d(public.ST_SetSRID(public.ST_MakePoint(1,1,1,1),0)) LIMIT 8)
SELECT id,public.laplace_angular_distance_4d(coord,public.ST_SetSRID(public.ST_MakePoint(1,1,1,1),0)) FROM nearest ORDER BY chord,id;
