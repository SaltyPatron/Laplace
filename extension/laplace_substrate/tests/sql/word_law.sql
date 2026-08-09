BEGIN;

SELECT laplace.word_id('a') = public.laplace_hash128_blake3(convert_to('a', 'UTF8'))
    AS single_grapheme_collapses;


SELECT laplace.word_id('dog') = public.laplace_hash128_blake3(
           ('\x01'::bytea
            || public.laplace_hash128_blake3(convert_to('d', 'UTF8'))
            || public.laplace_hash128_blake3(convert_to('o', 'UTF8'))
            || public.laplace_hash128_blake3(convert_to('g', 'UTF8'))))
    AS ascii_word_law_holds;

SELECT laplace.word_id('') IS NULL AS empty_is_null;




WITH ids AS (
    SELECT public.laplace_hash128_blake3(convert_to('q', 'UTF8'))           AS q_id,
           public.laplace_hash128_blake3(convert_to(U&'\0301', 'UTF8'))     AS acc_id,
           public.laplace_hash128_blake3(convert_to('x', 'UTF8'))           AS x_id
)
SELECT laplace.word_id('q' || U&'\0301' || 'x')
           = public.laplace_hash128_blake3('\x01'::bytea
                 || public.laplace_hash128_blake3('\x01'::bytea || q_id || acc_id)
                 || x_id)                                            AS grapheme_law_nested,
       laplace.word_id('q' || U&'\0301' || 'x')
           <> public.laplace_hash128_blake3('\x01'::bytea || q_id || acc_id || x_id)
                                                                     AS grapheme_law_not_flat
FROM ids;


SELECT realize.codepoint_for_id(public.laplace_hash128_blake3(convert_to('A', 'UTF8'))) = 65
    AS reverse_lookup_works;
SELECT realize.codepoint_for_id('\x00000000000000000000000000000000'::bytea) IS NULL
    AS unknown_id_is_null;
SELECT realize.codepoint_for_id(public.laplace_hash128_blake3(convert_to('字', 'UTF8'))) = 23383
    AS reverse_lookup_cjk;


SELECT realize.render(public.laplace_hash128_blake3(convert_to('A', 'UTF8'))) = 'A'
    AS render_t0_via_perfcache;



SELECT realize.is_all_whitespace('   ')        AS ascii_spaces_ws,
       realize.is_all_whitespace(U&'\3000')    AS ideographic_space_ws,
       realize.is_all_whitespace(U&'\00A0')    AS nbsp_ws,
       realize.is_all_whitespace(U&'\2003')    AS em_space_ws,
       NOT realize.is_all_whitespace(U&'\200B') AS zwsp_is_not_ws,
       NOT realize.is_all_whitespace('a b')    AS mixed_is_not_ws,
       NOT realize.is_all_whitespace('')       AS empty_is_not_ws;



SELECT bool_and(consensus.eff_mu(r, d) = consensus.effective_mu(r, d)) AS eff_mu_matches_engine
FROM (VALUES
    (1500000000000::bigint, 350000000000::bigint),
    (1600000000000::bigint,  80000000000::bigint),
    (2010500000000::bigint,  12000000000::bigint),
    (0::bigint, 0::bigint),
    (-5::bigint, 7::bigint)
) v(r, d);

COMMIT;
