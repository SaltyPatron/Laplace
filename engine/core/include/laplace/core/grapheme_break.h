#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* UAX#29 grapheme cluster break. Operates on a buffer of
 * Unicode codepoints (NOT UTF-8 bytes — caller decodes UTF-8 first via
 * laplace_utf8_decode_next or equivalent). Returns the codepoint index
 * of the next grapheme cluster boundary AFTER position `from`, or `n`
 * if no further boundary exists (i.e. end of input is the final
 * boundary per GB2).
 *
 * Convention: a "boundary at index i" means "boundary between
 * codepoints[i-1] and codepoints[i]". `from == 0` always yields 0 in
 * spirit (per GB1 sot ÷); callers iterate by starting at 0 and stepping
 * via the returned next-boundary until the function returns n.
 *
 * Pure: zero allocation, zero global state, deterministic per the
 * compiled-in UCD tables.
 *
 * Compliance scope: rules GB1, GB2, GB3, GB4, GB5, GB6, GB7, GB8, GB9,
 * GB9a, GB9b, GB9c (Indic conjunct break, via the InCB property tables),
 * GB11 (extended-pictographic ZWJ sequence), GB12, GB13 (regional
 * indicator pairing), GB999. */

/* Returns the codepoint index of the next grapheme cluster boundary
 * AFTER `from`. If `from >= n`, returns n. */
size_t laplace_grapheme_break_next(
    const uint32_t* codepoints,
    size_t          n,
    size_t          from);

#ifdef __cplusplus
}
#endif
