#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Thin F32 SVD with an explicit relative Frobenius truncation budget. A zero
 * budget drops only exactly zero singular values; SVD arithmetic still rounds.
 * U has row stride kmax, S has kmax entries, Vt has row stride n.
 * kmax is buffer capacity, never permission to exceed rel_err_tol.
 * Returns 0 on success, -1 for invalid arguments/nonfinite input, -2 without
 * MKL, -3 when the required rank exceeds kmax, or positive LAPACK info.
 * On -3, *out_rank is the required rank and U/S/Vt remain untouched. */
int tensor_svd_truncate(
    const float* A, size_t m, size_t n,
    double       rel_err_tol,
    size_t*      out_rank,
    float* U, float* S, float* Vt, size_t kmax);

#ifdef __cplusplus
}
#endif
