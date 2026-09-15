\set ECHO none
\set QUIET 1
\pset format unaligned
\pset tuples_only on
SET client_min_messages=warning;
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;

DO $$
DECLARE
    actual geometry;
    reversed geometry;
    samples geometry;
    invalid geometry;
    diagonal double precision := 1.0/sqrt(2.0);
BEGIN
    -- The native singleton path normalizes, unlike Euclidean centroid.
    actual := laplace_karcher_mean_4d(ST_MakePoint(2,0,0,0));
    IF laplace_distance_4d(actual,ST_MakePoint(1,0,0,0)) > 1e-12
       OR laplace_distance_4d(actual,laplace_centroid_4d(ST_MakePoint(2,0,0,0))) < 0.9 THEN
        RAISE EXCEPTION 'singleton must use the canonical Karcher kernel';
    END IF;

    -- Both Z and M participate; the result is on S3 rather than the chord.
    samples := ST_MakeLine(ST_MakePoint(0,0,1,0),ST_MakePoint(0,0,0,1));
    actual := laplace_karcher_mean_4d(samples);
    IF laplace_distance_4d(actual,ST_MakePoint(0,0,diagonal,diagonal)) > 1e-12
       OR abs(laplace_radius_origin(actual)-1.0) > 1e-12
       OR laplace_distance_4d(actual,laplace_centroid_4d(samples)) < 0.1 THEN
        RAISE EXCEPTION 'Karcher mean lost Z/M or used the Euclidean centroid';
    END IF;

    -- Repeated occurrences contribute separately: two X turns and one Y turn
    -- have intrinsic mean at pi/6, not the pi/4 mean of the unique point set.
    samples := ST_Collect(ARRAY[ST_MakePoint(1,0,0,0),
        ST_MakePoint(0,1,0,0),ST_MakePoint(1,0,0,0)]);
    actual := laplace_karcher_mean_4d(samples);
    reversed := laplace_karcher_mean_4d(ST_Collect(ARRAY[
        ST_MakePoint(0,1,0,0),ST_MakePoint(1,0,0,0),ST_MakePoint(1,0,0,0)]));
    IF laplace_distance_4d(actual,ST_MakePoint(sqrt(3.0)/2.0,0.5,0,0)) > 1e-12
       OR ST_AsBinary(actual) IS DISTINCT FROM ST_AsBinary(reversed) THEN
        RAISE EXCEPTION 'occurrence weight or canonical ordering was lost';
    END IF;

    -- Exact repeated placements and the zero vector retain the core's defined
    -- behavior; the SQL boundary adds no replacement normalization policy.
    actual := laplace_karcher_mean_4d(ST_Collect(ARRAY[
        ST_MakePoint(0.5,0.5,0.5,0.5),ST_MakePoint(0.5,0.5,0.5,0.5)]));
    IF laplace_distance_4d(actual,ST_MakePoint(0.5,0.5,0.5,0.5)) > 1e-12
       OR laplace_distance_4d(laplace_karcher_mean_4d(ST_MakePoint(0,0,0,0)),
                             ST_MakePoint(0,0,0,0)) <> 0
       OR laplace_karcher_mean_4d(NULL::geometry) IS NOT NULL THEN
        RAISE EXCEPTION 'degenerate or strict-null contract changed';
    END IF;

    FOREACH invalid IN ARRAY ARRAY[
        ST_MakePoint(1,0), ST_MakePoint(1,0,0),
        'POINT M (1 0 0)'::geometry,
        'POINT ZM EMPTY'::geometry,
        'LINESTRING ZM EMPTY'::geometry,
        'MULTIPOINT ZM EMPTY'::geometry,
        'MULTIPOINT ZM (EMPTY,(1 0 0 0))'::geometry,
        'GEOMETRYCOLLECTION ZM (POINT ZM (1 0 0 0))'::geometry,
        ST_MakePoint('Infinity'::double precision,0,0,0),
        ST_MakePoint(1,'NaN'::double precision,0,0),
        ST_MakePoint(1,0,'-Infinity'::double precision,0),
        ST_MakePoint(1,0,0,'NaN'::double precision)
    ] LOOP
        BEGIN
            PERFORM laplace_karcher_mean_4d(invalid);
            RAISE EXCEPTION 'invalid constituent geometry was accepted';
        EXCEPTION WHEN invalid_parameter_value THEN
            NULL;
        END;
    END LOOP;
END
$$;
SELECT 'KARCHER_MEAN_4D_OK';
