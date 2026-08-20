CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION laplace_geom;
CREATE EXTENSION laplace_substrate;

SELECT count(*) = 7 AS structural_functions_present
FROM pg_proc p
JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname = 'structural'
  AND p.proname IN (
      'word_curve', 'word_shape_distance', 'anagrams_of',
      'collocates', 'cluster', 'cluster_batch', 'entity_curve');

SELECT count(*) AS collocates_empty_for_unknown_word
FROM structural.collocates('zzznosuchword', 5);

SELECT structural.word_curve(laplace.word_id('dog')) IS NULL AS no_curve_without_physicality;

SELECT count(*) = 0 AS cluster_empty_without_physicality
FROM structural.cluster(laplace.word_id('dog'), 0.05, 10);

-- Non-empty contract and batch parity. The previous test stopped at the early-return
-- path, so the scalar's ambiguous output variables and the batch's nonexistent
-- sc.recurrence column both survived into production.
\set ECHO none
BEGIN;
DO $cluster_fixture$
DECLARE
    src       bytea := public.laplace_hash128_blake3('test/cluster/source');
    type_t    bytea := public.laplace_hash128_blake3('Type');
    type_word bytea := public.laplace_hash128_blake3('Word');
    seed      bytea := public.laplace_hash128_blake3('test/cluster/seed');
    near_word bytea := public.laplace_hash128_blake3('test/cluster/near');
    far_word  bytea := public.laplace_hash128_blake3('test/cluster/far');
    unresolved bytea := public.laplace_hash128_blake3('test/cluster/unresolved');
    l0 bytea := public.laplace_hash128_blake3('test/cluster/leaf-0');
    l1 bytea := public.laplace_hash128_blake3('test/cluster/leaf-1');
    l2 bytea := public.laplace_hash128_blake3('test/cluster/leaf-2');
    l3 bytea := public.laplace_hash128_blake3('test/cluster/leaf-3');
    scalar_ids bytea[];
    batch_ids  bytea[];
    n bigint;
BEGIN
    INSERT INTO laplace.entities (id, tier, type_id, first_observed_by) VALUES
        (src, 0, type_t, NULL),
        (l0, 0, type_word, src), (l1, 0, type_word, src),
        (l2, 0, type_word, src), (l3, 0, type_word, src),
        (seed, 2, type_word, src), (near_word, 2, type_word, src),
        (far_word, 2, type_word, src);

    INSERT INTO laplace.physicalities
        (id, entity_id, type, coord, hilbert_index, trajectory, n_constituents, observed_at)
    VALUES
        (public.laplace_hash128_blake3('test/cluster/phys-l0'), l0, 1,
         public.ST_SetSRID(public.ST_MakePoint(0.0,0.0,0.0,0.0),0), decode(repeat('00',16),'hex'), NULL,0,now()),
        (public.laplace_hash128_blake3('test/cluster/phys-l1'), l1, 1,
         public.ST_SetSRID(public.ST_MakePoint(0.1,0.1,0.1,0.1),0), decode(repeat('01',16),'hex'), NULL,0,now()),
        (public.laplace_hash128_blake3('test/cluster/phys-l2'), l2, 1,
         public.ST_SetSRID(public.ST_MakePoint(0.8,0.8,0.8,0.8),0), decode(repeat('02',16),'hex'), NULL,0,now()),
        (public.laplace_hash128_blake3('test/cluster/phys-l3'), l3, 1,
         public.ST_SetSRID(public.ST_MakePoint(0.9,0.9,0.9,0.9),0), decode(repeat('03',16),'hex'), NULL,0,now()),
        (public.laplace_hash128_blake3('test/cluster/phys-seed'), seed, 1,
         public.ST_SetSRID(public.ST_MakePoint(0.40,0.40,0.40,0.40),0), decode(repeat('10',16),'hex'),
         public.ST_MakeLine(ARRAY[
             public.laplace_mantissa_pack(l0,1,1,0),
             public.laplace_mantissa_pack(l1,2,1,0)]),2,now()),
        (public.laplace_hash128_blake3('test/cluster/phys-near'), near_word, 1,
         public.ST_SetSRID(public.ST_MakePoint(0.41,0.41,0.41,0.41),0), decode(repeat('11',16),'hex'),
         public.ST_MakeLine(ARRAY[
             public.laplace_mantissa_pack(l0,1,1,0),
             public.laplace_mantissa_pack(l1,2,1,0)]),2,now()),
        (public.laplace_hash128_blake3('test/cluster/phys-far'), far_word, 1,
         public.ST_SetSRID(public.ST_MakePoint(0.95,0.95,0.95,0.95),0), decode(repeat('12',16),'hex'),
         public.ST_MakeLine(ARRAY[
             public.laplace_mantissa_pack(l2,1,1,0),
             public.laplace_mantissa_pack(l3,2,1,0)]),2,now());

    SELECT count(*) INTO n
    FROM structural.cluster(seed, 0.001, 1) c
    WHERE c.entity_id = near_word AND c.recurrence = 1;
    IF n <> 1 THEN
        RAISE EXCEPTION 'FAIL: non-empty scalar cluster did not return its bounded survivor';
    END IF;

    SELECT array_agg(c.entity_id ORDER BY c.frechet, c.entity_id)
      INTO scalar_ids
    FROM structural.cluster(seed, 0.001, 1) c;
    SELECT array_agg(c.entity_id ORDER BY c.frechet, c.entity_id)
      INTO batch_ids
    FROM structural.cluster_batch(ARRAY[seed], 0.001, 1) c
    WHERE c.input_ordinal = 1;
    IF scalar_ids IS DISTINCT FROM batch_ids THEN
        RAISE EXCEPTION 'FAIL: scalar/batch fingerprints differ';
    END IF;

    SELECT count(*) INTO n
    FROM structural.cluster_batch(ARRAY[seed, seed, unresolved], 0.001, 1) c;
    IF n <> 2 OR EXISTS (
        SELECT 1
        FROM structural.cluster_batch(ARRAY[seed, seed, unresolved], 0.001, 1) c
        WHERE c.input_ordinal NOT IN (1,2)) THEN
        RAISE EXCEPTION 'FAIL: duplicate/unresolved batch ordinality was not preserved';
    END IF;

    IF EXISTS (SELECT 1 FROM structural.cluster_batch(NULL, 0.001, 1))
       OR EXISTS (SELECT 1 FROM structural.cluster_batch(ARRAY[]::bytea[], 0.001, 1)) THEN
        RAISE EXCEPTION 'FAIL: NULL/empty cluster batch must be empty';
    END IF;

    RAISE NOTICE 'structural cluster: bounded non-empty scalar/batch parity and ordinality pass';
END
$cluster_fixture$;
ROLLBACK;
\set ECHO all

-- Exact angular KNN must ignore radius. Raw coord chord ranks `raw-close` first,
-- while its angle is worse; unit-direction KNN must return `angular-close`.
BEGIN;
INSERT INTO laplace.entities (id, tier, type_id, first_observed_by) VALUES
    (public.laplace_hash128_blake3('test/angular/source'), 0,
     public.laplace_hash128_blake3('Type'), NULL),
    (public.laplace_hash128_blake3('test/angular/raw-close'), 42,
     public.laplace_hash128_blake3('Word'),
     public.laplace_hash128_blake3('test/angular/source')),
    (public.laplace_hash128_blake3('test/angular/angular-close'), 42,
     public.laplace_hash128_blake3('Word'),
     public.laplace_hash128_blake3('test/angular/source'));

INSERT INTO laplace.physicalities
    (id, entity_id, type, coord, hilbert_index, trajectory, n_constituents)
VALUES
    (public.laplace_hash128_blake3('test/angular/raw-close/physicality'),
     public.laplace_hash128_blake3('test/angular/raw-close'), 1,
     public.ST_SetSRID(public.ST_MakePoint(0.9, 0.1, 0, 0), 0),
     decode(repeat('21', 16), 'hex'), NULL, 0),
    (public.laplace_hash128_blake3('test/angular/angular-close/physicality'),
     public.laplace_hash128_blake3('test/angular/angular-close'), 1,
     public.ST_SetSRID(public.ST_MakePoint(100, 1, 0, 0), 0),
     decode(repeat('22', 16), 'hex'), NULL, 0);

SELECT bool_and(entity_id = public.laplace_hash128_blake3('test/angular/angular-close'))
       AS angular_knn_ignores_radius
FROM generation.nearest_entity(1, 0, 0, 0, 1, NULL, ARRAY[42::smallint]);

SELECT count(*) = 0 AS angular_knn_zero_is_empty
FROM generation.nearest_entity(1, 0, 0, 0, 0, NULL, ARRAY[42::smallint]);

SELECT count(*) = 0 AS zero_anchor_has_no_angular_neighbors
FROM generation.nearest_entity(0, 0, 0, 0, 1, NULL, ARRAY[42::smallint]);

SELECT pg_get_indexdef(i.indexrelid) LIKE '%laplace_direction_4d(coord)%'
       AND pg_get_expr(i.indpred, i.indrelid) LIKE '%type = 1%'
       AND pg_get_expr(i.indpred, i.indrelid) LIKE '%laplace_direction_4d(coord) IS NOT NULL%'
       AS angular_knn_index_is_partial
FROM pg_index i
WHERE i.indexrelid = 'laplace.physicalities_direction_gist'::regclass;
ROLLBACK;

-- Rule #3 gate: production Frechet helpers must realize via entity_curve /
-- word_curve. Packed physicalities.trajectory must never be a Frechet argument.
SELECT count(*) = 0 AS no_packed_trajectory_frechet
FROM pg_proc p
JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE (
      (n.nspname = 'structural' AND p.proname IN (
          'cluster',
          'cluster_batch',
          'neighbors',
          'neighbors_of',
          'explore_anchor_neighbors'))
      OR (n.nspname = 'generation' AND p.proname IN (
          'consensus_peer',
          'metric_edges'))
  )
  AND pg_get_functiondef(p.oid) ~* 'laplace_frechet_4d\([^)]*trajectory';

SELECT count(*) = 6 AS shape_helpers_use_entity_curve
FROM pg_proc p
JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE (
      (n.nspname = 'structural' AND p.proname IN (
          'cluster',
          'cluster_batch',
          'neighbors',
          'neighbors_of',
          'explore_anchor_neighbors'))
      OR (n.nspname = 'generation' AND p.proname IN (
          'consensus_peer',
          'metric_edges'))
  )
  AND pg_get_functiondef(p.oid) LIKE '%entity_curve%';

-- structural_surface complete
