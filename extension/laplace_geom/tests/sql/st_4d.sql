-- Smoke test for the ST_*_4d wrapper family + hilbert + mantissa.
-- Verifies the engine kernels are reachable from SQL and produce the
-- correct numerical answers for hand-checkable inputs.

-- Suppress NOTICE chatter so the expected output is stable whether this
-- test runs first or after another that already created the extensions.
SET client_min_messages = warning;
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
RESET client_min_messages;

-- ===========================================================================
-- laplace_distance_4d
-- ===========================================================================

-- Identical POINT4Ds: distance is 0.
SELECT laplace_distance_4d(
    ST_MakePoint(1.0, 2.0, 3.0, 4.0),
    ST_MakePoint(1.0, 2.0, 3.0, 4.0)
) AS distance_same;

-- Unit step along the X axis.
SELECT laplace_distance_4d(
    ST_MakePoint(0.0, 0.0, 0.0, 0.0),
    ST_MakePoint(1.0, 0.0, 0.0, 0.0)
) AS distance_unit_x;

-- (0,0,0,0) -> (1,1,1,1) = sqrt(4) = 2.0.
SELECT laplace_distance_4d(
    ST_MakePoint(0.0, 0.0, 0.0, 0.0),
    ST_MakePoint(1.0, 1.0, 1.0, 1.0)
) AS distance_unit_diag;

-- Missing Z/M default to 0: 2D POINT(3,4) vs origin → 5.0 (3-4-5 triangle).
SELECT laplace_distance_4d(
    ST_MakePoint(0.0, 0.0),
    ST_MakePoint(3.0, 4.0)
) AS distance_2d_lifted;

-- Type error: non-POINT raises a clear message.
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

-- ===========================================================================
-- laplace_dwithin_4d
-- ===========================================================================

-- Within: distance 1.0 ≤ eps 2.0.
SELECT laplace_dwithin_4d(
    ST_MakePoint(0.0, 0.0, 0.0, 0.0),
    ST_MakePoint(1.0, 0.0, 0.0, 0.0),
    2.0
) AS dwithin_inside;

-- Outside: distance 2.0 > eps 1.0.
SELECT laplace_dwithin_4d(
    ST_MakePoint(0.0, 0.0, 0.0, 0.0),
    ST_MakePoint(2.0, 0.0, 0.0, 0.0),
    1.0
) AS dwithin_outside;

-- Exactly at eps: inclusive.
SELECT laplace_dwithin_4d(
    ST_MakePoint(0.0, 0.0, 0.0, 0.0),
    ST_MakePoint(1.0, 0.0, 0.0, 0.0),
    1.0
) AS dwithin_at_boundary;

-- Negative eps is rejected.
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

-- ===========================================================================
-- laplace_centroid_4d
-- ===========================================================================

-- Centroid of a single POINT is that point.
SELECT ST_AsText(laplace_centroid_4d(ST_MakePoint(1.0, 2.0, 3.0, 4.0))) AS centroid_single;

-- Centroid of a 2-vertex LINESTRING in 4D = midpoint.
SELECT ST_AsText(laplace_centroid_4d(
    ST_MakeLine(
        ST_MakePoint(0.0, 0.0, 0.0, 0.0),
        ST_MakePoint(2.0, 4.0, 6.0, 8.0)
    )
)) AS centroid_line_midpoint;

-- Centroid of a MULTIPOINT.
SELECT ST_AsText(laplace_centroid_4d(
    ST_Collect(ARRAY[
        ST_MakePoint(1.0, 1.0, 1.0, 1.0),
        ST_MakePoint(3.0, 3.0, 3.0, 3.0)
    ])
)) AS centroid_multipoint;

-- ===========================================================================
-- laplace_radius_origin
-- ===========================================================================

-- Origin has radius 0.
SELECT laplace_radius_origin(ST_MakePoint(0.0, 0.0, 0.0, 0.0)) AS radius_origin_zero;

-- Unit XYZM vector has radius 2.0 (sqrt(4)).
SELECT laplace_radius_origin(ST_MakePoint(1.0, 1.0, 1.0, 1.0)) AS radius_unit_diag;

-- 2D POINT(3,4) lifts to 4D → radius 5.
SELECT laplace_radius_origin(ST_MakePoint(3.0, 4.0)) AS radius_3_4_5;

-- ===========================================================================
-- laplace_frechet_4d
-- ===========================================================================

-- Identical 3-point LINESTRINGs: Fréchet = 0.
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

-- Parallel offset trajectories: Fréchet = offset = 1.0.
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

-- Symmetric.
SELECT laplace_frechet_4d(
    ST_MakeLine(ST_MakePoint(0,0,0,0), ST_MakePoint(1,0,0,0)),
    ST_MakeLine(ST_MakePoint(0,1,0,0), ST_MakePoint(1,1,0,0))
) = laplace_frechet_4d(
    ST_MakeLine(ST_MakePoint(0,1,0,0), ST_MakePoint(1,1,0,0)),
    ST_MakeLine(ST_MakePoint(0,0,0,0), ST_MakePoint(1,0,0,0))
) AS frechet_symmetric;

-- ===========================================================================
-- laplace_hausdorff_4d
-- ===========================================================================

-- Identical point sets: Hausdorff = 0.
SELECT laplace_hausdorff_4d(
    ST_Collect(ARRAY[ST_MakePoint(0.0,0.0,0.0,0.0), ST_MakePoint(1.0,0.0,0.0,0.0)]),
    ST_Collect(ARRAY[ST_MakePoint(0.0,0.0,0.0,0.0), ST_MakePoint(1.0,0.0,0.0,0.0)])
) AS hausdorff_identical;

-- Single POINTs: equals Euclidean.
SELECT laplace_hausdorff_4d(
    ST_MakePoint(0.0, 0.0, 0.0, 0.0),
    ST_MakePoint(3.0, 0.0, 0.0, 0.0)
) AS hausdorff_single_points;

-- Superset/subset: directed(A,B)=0, directed(B,A)=1.0 → symmetric = 1.0.
SELECT laplace_hausdorff_4d(
    ST_Collect(ARRAY[ST_MakePoint(0.0,0.0,0.0,0.0), ST_MakePoint(2.0,0.0,0.0,0.0)]),
    ST_Collect(ARRAY[ST_MakePoint(0.0,0.0,0.0,0.0), ST_MakePoint(1.0,0.0,0.0,0.0), ST_MakePoint(2.0,0.0,0.0,0.0)])
) AS hausdorff_subset_superset;

-- ===========================================================================
-- laplace_hilbert_encode + laplace_hilbert_decode
-- ===========================================================================

-- Encode produces a 16-byte key.
SELECT octet_length(laplace_hilbert_encode(ST_MakePoint(0.0, 0.0, 0.0, 0.0))) AS hilbert_len;

-- Deterministic: same input twice → same key.
SELECT laplace_hilbert_encode(ST_MakePoint(0.1, 0.2, 0.3, 0.4))
     = laplace_hilbert_encode(ST_MakePoint(0.1, 0.2, 0.3, 0.4)) AS hilbert_deterministic;

-- Different inputs → different keys (with overwhelmingly high probability).
SELECT laplace_hilbert_encode(ST_MakePoint(0.1, 0.2, 0.3, 0.4))
    <> laplace_hilbert_encode(ST_MakePoint(0.5, 0.6, 0.7, 0.8)) AS hilbert_distinct;

-- Round-trip: encode → decode → distance from original ≤ Hilbert quantization.
-- (Skilling 2004 with 32-bit-per-axis precision quantizes ≈ 2^-31 ~ 5e-10
-- per dimension; round-trip distance under 1e-8 is the relevant bound.)
SELECT laplace_distance_4d(
    ST_MakePoint(0.1, 0.2, 0.3, 0.4),
    laplace_hilbert_decode(laplace_hilbert_encode(ST_MakePoint(0.1, 0.2, 0.3, 0.4)))
) < 1e-8 AS hilbert_roundtrip_close;

-- ===========================================================================
-- laplace_mantissa_pack + laplace_mantissa_unpack
-- ===========================================================================

-- Round-trip: pack → unpack returns the original payload.
WITH packed AS (
    SELECT laplace_mantissa_pack(
        laplace_hash128_blake3('test_entity'::bytea),
        42,        -- ordinal
        7,         -- run_length
        12345::bigint  -- flags
    ) AS vertex
)
SELECT (laplace_mantissa_unpack(vertex)).entity_id  = laplace_hash128_blake3('test_entity'::bytea) AS eid_roundtrip,
       (laplace_mantissa_unpack(vertex)).ordinal    = 42 AS ord_roundtrip,
       (laplace_mantissa_unpack(vertex)).run_length = 7  AS run_roundtrip,
       (laplace_mantissa_unpack(vertex)).flags      = 12345::bigint AS flags_roundtrip
FROM packed;

-- Validation: flags must fit in low 52 bits.
DO $$
BEGIN
    PERFORM laplace_mantissa_pack(
        laplace_hash128_blake3('x'::bytea),
        0, 0,
        (1::bigint << 52) -- bit 52 set → high 12 bits non-zero
    );
    RAISE EXCEPTION 'expected high-bit flags to be rejected';
EXCEPTION WHEN invalid_parameter_value THEN
    NULL;
END;
$$;
SELECT 'mantissa_pack_rejects_high_flag_bits' AS validation_check;

-- Validation: ordinal must fit in uint16.
DO $$
BEGIN
    PERFORM laplace_mantissa_pack(
        laplace_hash128_blake3('x'::bytea),
        70000, -- > UINT16_MAX
        0,
        0::bigint
    );
    RAISE EXCEPTION 'expected ordinal overflow to be rejected';
EXCEPTION WHEN numeric_value_out_of_range THEN
    NULL;
END;
$$;
SELECT 'mantissa_pack_rejects_ordinal_overflow' AS validation_check;
