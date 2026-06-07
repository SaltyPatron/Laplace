#include "laplace/dynamics/bilinear_edges.h"

#include <cmath>
#include <cstddef>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <mkl_cblas.h>
#endif

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
                out_vals[cnt] = v;
                ++cnt;
            }
        }
    }
    *out_count = cnt;
    return 0;
#else
    (void)left; (void)right; (void)r; (void)theta;
    (void)out_rows; (void)out_cols; (void)out_vals; (void)cap; (void)t;
    return -2;
#endif
}

extern "C"
int project_embedding(const float* pts, std::size_t n, std::size_t d,
                      const float* W, std::size_t r, double* out)
{
    if (!pts || !W || !out || n == 0 || d == 0 || r == 0) return -1;
#ifdef LAPLACE_HAS_MKL
    std::vector<double> P((std::size_t)n * d), Wd((std::size_t)r * d);
    for (std::size_t i = 0; i < (std::size_t)n * d; ++i) P[i]  = (double)pts[i];
    for (std::size_t i = 0; i < (std::size_t)r * d; ++i) Wd[i] = (double)W[i];

    cblas_dgemm(
        CblasRowMajor, CblasNoTrans, CblasTrans,
        (MKL_INT)n, (MKL_INT)r, (MKL_INT)d,
        1.0, P.data(), (MKL_INT)d, Wd.data(), (MKL_INT)d,
        0.0, out, (MKL_INT)r);
    return 0;
#else
    (void)pts; (void)W; (void)out;
    return -2;
#endif
}
