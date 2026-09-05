SET client_min_messages = warning;
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
RESET client_min_messages;

-- The stored LINESTRING has two vertices, but its logical sequence is A,A,A,B.
-- This proves that the native expanded operation derives true ordinals from the
-- run-length prefix sum and does not use ST_NPoints or the stored vertex index.
WITH ids AS (
    SELECT laplace_hash128_blake3('rle-a'::bytea) AS a,
           laplace_hash128_blake3('rle-b'::bytea) AS b
), packed AS (
    SELECT a, b, ST_MakeLine(ARRAY[
        -- atom 97, tier 0, repeated three times
        laplace_mantissa_pack(a, 1, 3, 1::bigint | (97::bigint << 31)),
        -- ordinary tier 47: low five bits plus the canonical extension payload
        laplace_mantissa_pack(b, 4, 1,
            ((47::bigint & 31) << 1) | (1::bigint << 46) | (1::bigint << 43))
    ]) AS trajectory
    FROM ids
), expanded AS (
    SELECT p.a, p.b, e.ordinal, e.entity_id, e.flags
    FROM packed p
    CROSS JOIN LATERAL laplace_trajectory_expanded_constituents(p.trajectory) e
)
SELECT (SELECT ST_NPoints(trajectory) = 2 FROM packed) AS stored_vertices_are_rle,
       array_agg(ordinal ORDER BY ordinal) = ARRAY[1,2,3,4] AS true_ordinals,
       array_agg(entity_id ORDER BY ordinal) = ARRAY[min(a),min(a),min(a),min(b)]
           AS repeated_then_following_id,
       array_agg(laplace_vertex_atom(flags) ORDER BY ordinal) = ARRAY[97,97,97,NULL::integer]
           AS atom_flags_preserved,
       array_agg(laplace_vertex_tier(flags) ORDER BY ordinal) = ARRAY[0,0,0,47]::smallint[]
           AS tier_flags_preserved
FROM expanded;

-- Logical equality compares the expanded identity/order/full-flags stream
-- without materializing it. Plain and RLE storage are equal; changing any
-- semantic part is not. STRICT supplies SQL NULL propagation.
WITH ids AS (
    SELECT laplace_hash128_blake3('eq-a'::bytea) AS a,
           laplace_hash128_blake3('eq-b'::bytea) AS b
), forms AS (
    SELECT
      ST_MakeLine(ARRAY[
        laplace_mantissa_pack(a, 1, 1, 7),
        laplace_mantissa_pack(a, 2, 1, 7),
        laplace_mantissa_pack(a, 3, 1, 7),
        laplace_mantissa_pack(b, 4, 1, 9)]) AS plain,
      ST_MakeLine(ARRAY[
        laplace_mantissa_pack(a, 1, 3, 7),
        laplace_mantissa_pack(b, 4, 1, 9)]) AS compressed,
      ST_MakeLine(ARRAY[
        laplace_mantissa_pack(b, 1, 1, 9),
        laplace_mantissa_pack(a, 2, 3, 7)]) AS reordered,
      ST_MakeLine(ARRAY[
        laplace_mantissa_pack(a, 1, 3, 8),
        laplace_mantissa_pack(b, 4, 1, 9)]) AS flags_changed,
      ST_MakeLine(ARRAY[
        laplace_mantissa_pack(a, 1, 2, 7),
        laplace_mantissa_pack(b, 3, 1, 9)]) AS shortened
    FROM ids
)
SELECT laplace_trajectory_equivalent(plain, compressed) AS encoding_independent,
       NOT laplace_trajectory_equivalent(plain, reordered) AS order_matters,
       NOT laplace_trajectory_equivalent(plain, flags_changed) AS flags_matter,
       NOT laplace_trajectory_equivalent(plain, shortened) AS count_matters,
       laplace_trajectory_equivalent(NULL, compressed) IS NULL
         AND laplace_trajectory_equivalent(plain, NULL) IS NULL AS null_propagates
FROM forms;
