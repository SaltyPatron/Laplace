#include "laplace/synthesis/tensor_decompose.h"

#include <cstring>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <mkl_lapacke.h>
#endif

/* Energy-truncated thin SVD. The reconstruction-error tolerance is converted
 * to a Frobenius energy budget: keep the minimal leading rank r whose dropped
 * tail energy (sum of squared discarded singular values) stays within
 * rel_err_tol^2 * total_energy. Singular values from sgesdd are non-increasing,
 * so the discardable tail is contiguous from the smallest end. */

extern "C"
int tensor_svd_truncate(
    const float* A, size_t m, size_t n,
    double       rel_err_tol,
    size_t*      out_rank,
    float* U, float* S, float* Vt, size_t kmax)
{
    if (!A || !out_rank || !U || !S || !Vt) return -1;
    if (m == 0 || n == 0)                   return -1;
    if (rel_err_tol < 0.0 || rel_err_tol >= 1.0) return -1;

    const size_t k = (m < n) ? m : n;   /* min(m, n) = full thin-SVD rank */
    if (kmax < k) return -1;

#ifdef LAPLACE_HAS_MKL
    /* sgesdd overwrites its input matrix — decompose a copy. Row-major. */
    std::vector<float> Acopy(A, A + m * n);
    std::vector<float> Ufull(m * k);
    std::vector<float> Sfull(k);
    std::vector<float> Vtfull(k * n);

    const lapack_int info = LAPACKE_sgesdd(
        LAPACK_ROW_MAJOR, 'S',
        (lapack_int)m, (lapack_int)n,
        Acopy.data(), (lapack_int)n,
        Sfull.data(),
        Ufull.data(),  (lapack_int)k,
        Vtfull.data(), (lapack_int)n);
    if (info != 0) return (info > 0) ? (int)info : -1;

    double total = 0.0;
    for (size_t i = 0; i < k; ++i) total += (double)Sfull[i] * (double)Sfull[i];

    if (total == 0.0) { *out_rank = 0; return 0; }   /* all-zero tensor */

    /* Drop smallest modes while cumulative dropped energy stays in budget. */
    const double budget = rel_err_tol * rel_err_tol * total;
    double dropped = 0.0;
    size_t r = k;
    for (size_t i = k; i >= 1; --i) {
        const double e = (double)Sfull[i - 1] * (double)Sfull[i - 1];
        if (dropped + e > budget) break;   /* keeping mode i-1 is required */
        dropped += e;
        r = i - 1;
    }
    if (r == 0) r = 1;   /* a non-zero tensor always keeps its dominant mode */

    /* Pack the leading r modes into the caller's [m x kmax] / [kmax] / [kmax x n]
     * buffers. U columns must be strided from the k-wide work buffer; S and the
     * leading r rows of Vt are contiguous. */
    for (size_t row = 0; row < m; ++row)
        std::memcpy(U + row * kmax, Ufull.data() + row * k, r * sizeof(float));
    std::memcpy(S,  Sfull.data(),  r * sizeof(float));
    std::memcpy(Vt, Vtfull.data(), r * n * sizeof(float));

    *out_rank = r;
    return 0;
#else
    (void)A; (void)rel_err_tol;
    (void)U; (void)S; (void)Vt;
    *out_rank = 0;
    return -2;   /* LAPACK/MKL required for SVD */
#endif
}
