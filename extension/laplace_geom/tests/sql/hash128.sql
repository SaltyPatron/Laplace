CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace;

-- laplace_hash128_blake3 is a raw BLAKE3-128 digest of bytes (fixtures,
-- receipts), never an entity's ID. Identity is Laplace-Native's alone: a
-- composition's ID is public.laplace_compose (lp_id_compose) and text's is
-- public.laplace_text_id (lp_text_id).
SELECT octet_length(laplace_hash128_blake3(''::bytea)) AS empty_len,
       encode(laplace_hash128_blake3('hello'::bytea), 'hex') AS hello_hex;

SELECT to_regprocedure('laplace_hash128_merkle(smallint,bytea[])') IS NULL AS merkle_duplicate_retired;

-- A codepoint's digest is its Native ID (BLAKE3-128 of its UTF-8 bytes).
SELECT laplace_hash128_blake3(convert_to('é', 'UTF8')) = public.blake3_send(public.laplace_text_id('é')) AS codepoint_digest_is_native_id;

-- The Native composition law the retired hasher duplicated: one child is that
-- child; order and repeats are identity.
SELECT public.laplace_compose(ARRAY[public.laplace_text_id('x')]) = public.laplace_text_id('x') AS singleton_collapses,
       public.laplace_compose(ARRAY[public.laplace_text_id('a'), public.laplace_text_id('b')])
    <> public.laplace_compose(ARRAY[public.laplace_text_id('b'), public.laplace_text_id('a')]) AS child_order_matters,
       public.laplace_compose(ARRAY[public.laplace_text_id('a'), public.laplace_text_id('a')])
    <> public.laplace_text_id('a') AS repeats_are_identity;
