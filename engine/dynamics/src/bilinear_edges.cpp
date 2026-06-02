#include "laplace/dynamics/bilinear_edges.h"

#include <cmath>
#include <cstddef>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <mkl_cblas.h>
#endif

/* Faithful contracted-operator edges: M_tile = Left[row_begin:row_end] · Rightᵀ
 * via one exact f64 dgemm, then a deterministic row-major scan emitting every
 * signed cell above the coherence threshold. No argmax, no top-k, no a-priori
 * floor — the only cut is `theta`. See bilinear_edges.h for the invariants. */

extern "C"
int bilinear_edges_tile(
    const double* left,  std::size_t row_begin, std::size_t row_end,
    const double* right, std::size_t n_right,
    std::size_t r, double theta,
    int* out_rows, int* out_cols, double* out_vals,
    std::size_t cap, std::size_t* out_count, int* overflow)
{
    if (!left || !right || !out_rows || !out_cols || !out_vals || !out_count || !overflow)
        return -1;
    if (row_end <= row_begin || n_right == 0 || r == 0) return -1;

    *out_count = 0;
    *overflow  = 0;
    const std::size_t t = row_end - row_begin;

#ifdef LAPLACE_HAS_MKL
    /* M [t × n_right] = left[row_begin:,:] [t × r] · rightᵀ [r × n_right]. */
    std::vector<double> M(t * n_right);
    cblas_dgemm(
        CblasRowMajor, CblasNoTrans, CblasTrans,
        (MKL_INT)t, (MKL_INT)n_right, (MKL_INT)r,
        1.0,
        left + row_begin * r, (MKL_INT)r,
        right,                (MKL_INT)r,
        0.0,
        M.data(),             (MKL_INT)n_right);

    std::size_t cnt = 0;
    for (std::size_t a = 0; a < t; ++a) {
        const double* Mrow = M.data() + a * n_right;
        const int gi = (int)(row_begin + a);
        for (std::size_t b = 0; b < n_right; ++b) {
            const double v = Mrow[b];
            if (std::fabs(v) > theta) {
                if (cnt >= cap) { *overflow = 1; *out_count = cnt; return 0; }
                out_rows[cnt] = gi;
                out_cols[cnt] = (int)b;
                out_vals[cnt] = v;          /* SIGNED — never |v| */
                ++cnt;
            }
        }
    }
    *out_count = cnt;
    return 0;
#else
    (void)left; (void)right; (void)r; (void)theta;
    (void)out_rows; (void)out_cols; (void)out_vals; (void)cap; (void)t;
    return -2;   /* MKL required for the exact dgemm contraction */
#endif
}
