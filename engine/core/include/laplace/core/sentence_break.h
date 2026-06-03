#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* UAX#29 sentence boundary. Same iterator contract as
 * laplace_grapheme_break_next + laplace_word_break_next. */
size_t laplace_sentence_break_next(
    const uint32_t* codepoints,
    size_t          n,
    size_t          from);

#ifdef __cplusplus
}
#endif
