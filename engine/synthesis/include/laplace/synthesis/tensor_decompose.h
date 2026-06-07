#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

int tensor_svd_truncate(
    const float* A, size_t m, size_t n,
    double       rel_err_tol,
    size_t*      out_rank,
    float* U, float* S, float* Vt, size_t kmax);

#ifdef __cplusplus
}
#endif
