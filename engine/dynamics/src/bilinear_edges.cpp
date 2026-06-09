#include "laplace/dynamics/bilinear_edges.h"
#include "laplace/core/score.h"

#include <cmath>
#include <cstddef>
#include <cstring>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <mkl_cblas.h>
#endif

extern "C"
int bilinear_edges_tile(
    const double* left,  std::size_t row_begin, std::size_t row_end,
    const double* right, std::size_t n_right,
    std::size_t r, double theta,
    int* out_rows, int* out_cols, double* out_vals, long long* out_scores,
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
                if (out_scores) out_scores[cnt] = (long long)laplace_score_fp(v, 1.0);
                ++cnt;
            }
        }
    }
    *out_count = cnt;
    return 0;
#else
    (void)left; (void)right; (void)r; (void)theta;
    (void)out_rows; (void)out_cols; (void)out_vals; (void)out_scores; (void)cap; (void)t;
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

extern "C"
int project_embedding_d(const double* pts, std::size_t n, std::size_t d,
                         const float* W, std::size_t r, double* out)
{
    if (!pts || !W || !out || n == 0 || d == 0 || r == 0) return -1;
#ifdef LAPLACE_HAS_MKL
    std::vector<double> Wd((std::size_t)r * d);
    for (std::size_t i = 0; i < (std::size_t)r * d; ++i) Wd[i] = (double)W[i];

    cblas_dgemm(
        CblasRowMajor, CblasNoTrans, CblasTrans,
        (MKL_INT)n, (MKL_INT)r, (MKL_INT)d,
        1.0, pts, (MKL_INT)d, Wd.data(), (MKL_INT)d,
        0.0, out, (MKL_INT)r);
    return 0;
#else
    (void)pts; (void)W; (void)out;
    return -2;
#endif
}

extern "C"
int norm_rows_d(double* data, std::size_t n, std::size_t dim)
{
    if (!data || n == 0 || dim == 0) return -1;
#ifdef LAPLACE_HAS_MKL
    for (std::size_t i = 0; i < n; ++i) {
        double* row = data + i * dim;
        double ss = cblas_ddot((MKL_INT)dim, row, 1, row, 1);
        if (ss > 0.0) {
            double inv = 1.0 / std::sqrt(ss);
            cblas_dscal((MKL_INT)dim, inv, row, 1);
        }
    }
    return 0;
#else
    for (std::size_t i = 0; i < n; ++i) {
        double* row = data + i * dim;
        double ss = 0.0;
        for (std::size_t c = 0; c < dim; ++c) ss += row[c] * row[c];
        double inv = ss > 0.0 ? 1.0 / std::sqrt(ss) : 0.0;
        for (std::size_t c = 0; c < dim; ++c) row[c] *= inv;
    }
    return 0;
#endif
}

extern "C"
int expand_kv_heads_d(const double* kv, std::size_t n, std::size_t n_heads,
                      std::size_t n_kv, std::size_t head_dim, double* out)
{
    if (!kv || !out || n == 0 || n_heads == 0 || n_kv == 0 || head_dim == 0) return -1;
    const std::size_t kv_dim = n_kv * head_dim;
    const std::size_t attn_dim = n_heads * head_dim;
    if (kv_dim == attn_dim) {
        std::memcpy(out, kv, n * attn_dim * sizeof(double));
        return 0;
    }
    for (std::size_t i = 0; i < n; ++i) {
        const double* src = kv + i * kv_dim;
        double* dst = out + i * attn_dim;
        for (std::size_t h = 0; h < n_heads; ++h) {
            std::size_t kh = std::min(n_kv - 1, h * n_kv / std::max<std::size_t>(1, n_heads));
            std::memcpy(dst + h * head_dim, src + kh * head_dim, head_dim * sizeof(double));
        }
    }
    return 0;
}
