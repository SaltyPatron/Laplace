#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Energy-truncated thin SVD of a row-major f32 matrix A [m x n].
 *
 * Computes A = U * diag(S) * Vt and keeps the minimal rank r such that the
 * dropped spectral energy satisfies
 *     ||A - A_r||_F  <=  rel_err_tol * ||A||_F
 * (Eckart-Young optimal low-rank approximation).
 *
 * This is the substrate's significance selector: NOT a flat top-k%. The
 * retained rank adapts per tensor to its own spectrum, bounded by a single
 * reconstruction-error tolerance. rel_err_tol == 0 keeps full rank (lossless
 * up to fp round-off); a small tol drops only provably-negligible directions,
 * never good signal.
 *
 *   A:           [m x n] row-major f32, read-only (copied internally).
 *   m, n:        dims, both > 0.
 *   rel_err_tol: Frobenius relative-error budget in [0, 1). 0 => full rank.
 *   out_rank:    receives retained rank r in [0, min(m,n)]; 0 iff A is all-zero.
 *   U:           caller-allocated [m x kmax] row-major; first r columns written.
 *   S:           caller-allocated [kmax];          first r values (descending).
 *   Vt:          caller-allocated [kmax x n] row-major; first r rows written.
 *   kmax:        mode-axis capacity; must be >= min(m, n).
 *
 * Returns 0 on success; -1 on bad args; -2 if MKL/LAPACK is unavailable;
 *         a positive LAPACK `info` code on decomposition failure. */
int tensor_svd_truncate(
    const float* A, size_t m, size_t n,
    double       rel_err_tol,
    size_t*      out_rank,
    float* U, float* S, float* Vt, size_t kmax);

#ifdef __cplusplus
}
#endif
