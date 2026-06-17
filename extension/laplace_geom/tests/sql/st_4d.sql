SET client_min_messages = warning;
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
RESET client_min_messages;

SELECT laplace_distance_4d(
    ST_MakePoint(1.0, 2.0, 3.0, 4.0),
    ST_MakePoint(1.0, 2.0, 3.0, 4.0)
) AS distance_same;

SELECT laplace_distance_4d(
    ST_MakePoint(0.0, 0.0, 0.0, 0.0),
    ST_MakePoint(1.0, 0.0, 0.0, 0.0)
) AS distance_unit_x;

SELECT laplace_distance_4d(
    ST_MakePoint(0.0, 0.0, 0.0, 0.0),
    ST_MakePoint(1.0, 1.0, 1.0, 1.0)
) AS distance_unit_diag;

SELECT laplace_distance_4d(
    ST_MakePoint(0.0, 0.0),
    ST_MakePoint(3.0, 4.0)
) AS distance_2d_lifted;

DO $$
BEGIN
    PERFORM laplace_distance_4d(
        ST_MakeLine(ST_MakePoint(0,0), ST_MakePoint(1,1)),
        ST_MakePoint(0,0)
    );
    RAISE EXCEPTION 'expected non-POINT to be rejected';
EXCEPTION WHEN invalid_parameter_value THEN
    NULL;
END;
$$;
SELECT 'distance_4d_rejects_linestring' AS validation_check;

SELECT laplace_dwithin_4d(
    ST_MakePoint(0.0, 0.0, 0.0, 0.0),
    ST_MakePoint(1.0, 0.0, 0.0, 0.0),
    2.0
) AS dwithin_inside;

SELECT laplace_dwithin_4d(
    ST_MakePoint(0.0, 0.0, 0.0, 0.0),
    ST_MakePoint(2.0, 0.0, 0.0, 0.0),
    1.0
) AS dwithin_outside;

SELECT laplace_dwithin_4d(
    ST_MakePoint(0.0, 0.0, 0.0, 0.0),
    ST_MakePoint(1.0, 0.0, 0.0, 0.0),
    1.0
) AS dwithin_at_boundary;

DO $$
BEGIN
    PERFORM laplace_dwithin_4d(
        ST_MakePoint(0,0,0,0),
        ST_MakePoint(1,0,0,0),
        -0.1
    );
    RAISE EXCEPTION 'expected negative eps to be rejected';
EXCEPTION WHEN invalid_parameter_value THEN
    NULL;
END;
$$;
SELECT 'dwithin_4d_rejects_negative_eps' AS validation_check;

SELECT ST_AsText(laplace_centroid_4d(ST_MakePoint(1.0, 2.0, 3.0, 4.0))) AS centroid_single;

SELECT ST_AsText(laplace_centroid_4d(
    ST_MakeLine(
        ST_MakePoint(0.0, 0.0, 0.0, 0.0),
        ST_MakePoint(2.0, 4.0, 6.0, 8.0)
    )
)) AS centroid_line_midpoint;

SELECT ST_AsText(laplace_centroid_4d(
    ST_Collect(ARRAY[
        ST_MakePoint(1.0, 1.0, 1.0, 1.0),
        ST_MakePoint(3.0, 3.0, 3.0, 3.0)
    ])
)) AS centroid_multipoint;

SELECT laplace_radius_origin(ST_MakePoint(0.0, 0.0, 0.0, 0.0)) AS radius_origin_zero;

SELECT laplace_radius_origin(ST_MakePoint(1.0, 1.0, 1.0, 1.0)) AS radius_unit_diag;

SELECT laplace_radius_origin(ST_MakePoint(3.0, 4.0)) AS radius_3_4_5;

SELECT laplace_frechet_4d(
    ST_MakeLine(ARRAY[
        ST_MakePoint(0.0, 0.0, 0.0, 0.0),
        ST_MakePoint(1.0, 0.0, 0.0, 0.0),
        ST_MakePoint(2.0, 0.0, 0.0, 0.0)
    ]),
    ST_MakeLine(ARRAY[
        ST_MakePoint(0.0, 0.0, 0.0, 0.0),
        ST_MakePoint(1.0, 0.0, 0.0, 0.0),
        ST_MakePoint(2.0, 0.0, 0.0, 0.0)
    ])
) AS frechet_identical;

SELECT laplace_frechet_4d(
    ST_MakeLine(ARRAY[
        ST_MakePoint(0.0, 0.0, 0.0, 0.0),
        ST_MakePoint(1.0, 0.0, 0.0, 0.0),
        ST_MakePoint(2.0, 0.0, 0.0, 0.0)
    ]),
    ST_MakeLine(ARRAY[
        ST_MakePoint(0.0, 1.0, 0.0, 0.0),
        ST_MakePoint(1.0, 1.0, 0.0, 0.0),
        ST_MakePoint(2.0, 1.0, 0.0, 0.0)
    ])
) AS frechet_parallel_offset_1;

SELECT laplace_frechet_4d(
    ST_MakeLine(ST_MakePoint(0,0,0,0), ST_MakePoint(1,0,0,0)),
    ST_MakeLine(ST_MakePoint(0,1,0,0), ST_MakePoint(1,1,0,0))
) = laplace_frechet_4d(
    ST_MakeLine(ST_MakePoint(0,1,0,0), ST_MakePoint(1,1,0,0)),
    ST_MakeLine(ST_MakePoint(0,0,0,0), ST_MakePoint(1,0,0,0))
) AS frechet_symmetric;

SELECT laplace_hausdorff_4d(
    ST_Collect(ARRAY[ST_MakePoint(0.0,0.0,0.0,0.0), ST_MakePoint(1.0,0.0,0.0,0.0)]),
    ST_Collect(ARRAY[ST_MakePoint(0.0,0.0,0.0,0.0), ST_MakePoint(1.0,0.0,0.0,0.0)])
) AS hausdorff_identical;

SELECT laplace_hausdorff_4d(
    ST_MakePoint(0.0, 0.0, 0.0, 0.0),
    ST_MakePoint(3.0, 0.0, 0.0, 0.0)
) AS hausdorff_single_points;

SELECT laplace_hausdorff_4d(
    ST_Collect(ARRAY[ST_MakePoint(0.0,0.0,0.0,0.0), ST_MakePoint(2.0,0.0,0.0,0.0)]),
    ST_Collect(ARRAY[ST_MakePoint(0.0,0.0,0.0,0.0), ST_MakePoint(1.0,0.0,0.0,0.0), ST_MakePoint(2.0,0.0,0.0,0.0)])
) AS hausdorff_subset_superset;

SELECT octet_length(laplace_hilbert_encode(ST_MakePoint(0.0, 0.0, 0.0, 0.0))) AS hilbert_len;

SELECT laplace_hilbert_encode(ST_MakePoint(0.1, 0.2, 0.3, 0.4))
     = laplace_hilbert_encode(ST_MakePoint(0.1, 0.2, 0.3, 0.4)) AS hilbert_deterministic;

SELECT laplace_hilbert_encode(ST_MakePoint(0.1, 0.2, 0.3, 0.4))
    <> laplace_hilbert_encode(ST_MakePoint(0.5, 0.6, 0.7, 0.8)) AS hilbert_distinct;

SELECT laplace_distance_4d(
    ST_MakePoint(0.1, 0.2, 0.3, 0.4),
    laplace_hilbert_decode(laplace_hilbert_encode(ST_MakePoint(0.1, 0.2, 0.3, 0.4)))
) < 1e-8 AS hilbert_roundtrip_close;

WITH packed AS (
    SELECT laplace_mantissa_pack(
        laplace_hash128_blake3('test_entity'::bytea),
        42,
        7,
        12345::bigint
    ) AS vertex
)
SELECT (laplace_mantissa_unpack(vertex)).entity_id  = laplace_hash128_blake3('test_entity'::bytea) AS eid_roundtrip,
       (laplace_mantissa_unpack(vertex)).ordinal    = 42 AS ord_roundtrip,
       (laplace_mantissa_unpack(vertex)).run_length = 7  AS run_roundtrip,
       (laplace_mantissa_unpack(vertex)).flags      = 12345::bigint AS flags_roundtrip
FROM packed;

DO $$
BEGIN
    PERFORM laplace_mantissa_pack(
        laplace_hash128_blake3('x'::bytea),
        0, 0,
        (1::bigint << 52)
    );
    RAISE EXCEPTION 'expected high-bit flags to be rejected';
EXCEPTION WHEN invalid_parameter_value THEN
    NULL;
END;
$$;
SELECT 'mantissa_pack_rejects_high_flag_bits' AS validation_check;

DO $$
BEGIN
    PERFORM laplace_mantissa_pack(
        laplace_hash128_blake3('x'::bytea),
        70000,
        0,
        0::bigint
    );
    RAISE EXCEPTION 'expected ordinal overflow to be rejected';
EXCEPTION WHEN numeric_value_out_of_range THEN
    NULL;
END;
$$;
SELECT 'mantissa_pack_rejects_ordinal_overflow' AS validation_check;



SELECT laplace_vertex_tier((5::bigint << 1)) = 5             AS tier_decode,
       laplace_vertex_atom(1 + (97::bigint << 31)) = 97      AS atom_decode_with_flag,
       laplace_vertex_atom((97::bigint << 31)) IS NULL       AS atom_null_without_flag,
       laplace_vertex_tier(1 + (3::bigint << 1) + (65::bigint << 31)) = 3
                                                             AS tier_ignores_other_fields;
