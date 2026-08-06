#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Decimal ASCII digit codepoints (U+0030..U+0039) + optional U+002D for signed. */
#define LAPLACE_DECIMAL_MAX_CPS 12u

/*
 * Render integer as decimal codepoints — same law as ModelCoordinates.ScalarId /
 * modality-ladder-law.md. No leading zeros except zero itself. Returns count.
 */
uint32_t laplace_decimal_codepoints_u32(uint32_t value, uint32_t out_cps[LAPLACE_DECIMAL_MAX_CPS]);
uint32_t laplace_decimal_codepoints_i32(int32_t value, uint32_t out_cps[LAPLACE_DECIMAL_MAX_CPS]);

#ifdef __cplusplus
}
#endif
