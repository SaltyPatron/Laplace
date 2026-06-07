#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

int laplace_bf16_decode(const void* raw_bytes, size_t n_elements, double* out);

#ifdef __cplusplus
}
#endif
