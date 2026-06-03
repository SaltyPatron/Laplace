#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* UAX#29 word boundary. Same iterator contract as
 * laplace_grapheme_break_next: returns the codepoint index of the next
 * word boundary AFTER `from`, or `n` if no further boundary exists
 * (per WB2 sot ÷ ... eot ÷). Compliance scope: rules WB1-WB15 (covers
 * the full UAX#29 word boundary algorithm; no extended rules). */
size_t laplace_word_break_next(
    const uint32_t* codepoints,
    size_t          n,
    size_t          from);

#ifdef __cplusplus
}
#endif
