#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

size_t laplace_word_break_next(
    const uint32_t* codepoints,
    size_t          n,
    size_t          from);

#ifdef __cplusplus
}
#endif
