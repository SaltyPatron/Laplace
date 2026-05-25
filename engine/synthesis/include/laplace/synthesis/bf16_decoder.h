#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Convert n_elements packed BF16 values (2 bytes each) to double-precision.
 *
 * BF16 is the upper 16 bits of IEEE 754 float32. Conversion:
 *   (uint16 << 16) → reinterpret as float32 → cast to double.
 *
 * AVX2-vectorized on x86-64; scalar fallback otherwise.
 * Returns 0 on success, -1 on null pointer. */
int laplace_bf16_decode(const void* raw_bytes, size_t n_elements, double* out);

#ifdef __cplusplus
}
#endif
