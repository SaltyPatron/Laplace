#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

int gram_schmidt_orthonormalize(double* vectors,
                                size_t  n_vecs,
                                size_t  dim);

#ifdef __cplusplus
}
#endif
