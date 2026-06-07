CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION laplace_geom;

SELECT octet_length(laplace_hash128_blake3(''::bytea)) AS empty_len;

SELECT encode(laplace_hash128_blake3('hello'::bytea), 'hex') AS hello_hex;

SELECT laplace_hash128_blake3('foo'::bytea)
     = laplace_hash128_blake3('foo'::bytea) AS deterministic;

SELECT laplace_hash128_blake3('foo'::bytea)
    <> laplace_hash128_blake3('bar'::bytea) AS distinct_inputs_distinct_outputs;

SELECT octet_length(
    laplace_hash128_merkle(
        0::smallint,
        ARRAY[
            laplace_hash128_blake3('a'::bytea),
            laplace_hash128_blake3('b'::bytea)
        ]
    )
) AS merkle_len;

SELECT laplace_hash128_merkle(
        0::smallint,
        ARRAY[laplace_hash128_blake3('x'::bytea)]
    )
    = laplace_hash128_merkle(
        1::smallint,
        ARRAY[laplace_hash128_blake3('x'::bytea)]
    ) AS tier_is_not_identity;

SELECT laplace_hash128_merkle(
        0::smallint,
        ARRAY[
            laplace_hash128_blake3('a'::bytea),
            laplace_hash128_blake3('b'::bytea)
        ]
    )
    <> laplace_hash128_merkle(
        0::smallint,
        ARRAY[
            laplace_hash128_blake3('b'::bytea),
            laplace_hash128_blake3('a'::bytea)
        ]
    ) AS child_order_matters;

DO $$
BEGIN
    PERFORM laplace_hash128_merkle(
        0::smallint,
        ARRAY['short'::bytea]
    );
    RAISE EXCEPTION 'expected error for non-16-byte child';
EXCEPTION WHEN invalid_parameter_value THEN
    NULL;
END;
$$;

SELECT 'merkle_short_child_rejected' AS validation_check;
