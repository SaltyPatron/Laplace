#include "laplace/dynamics/model_math.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <mkl.h>
#  include "laplace/dynamics/tbb_parallel.h"
#  include <oneapi/tbb/blocked_range.h>
#endif

namespace {

template <typename Body>
void rows_parallel(size_t n, size_t grain, Body body)
{
#ifdef LAPLACE_HAS_MKL
    laplace::tbb_ops::parallel_for_size(
        oneapi::tbb::blocked_range<size_t>(0, n, grain),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t i = rng.begin(); i != rng.end(); ++i) body(i);
        });
#else
    for (size_t i = 0; i < n; ++i) body(i);
#endif
}

// Parallel column-mean: per-block partial sums reduced serially over the
// (small) block count — the reduction is O(blocks*d), the scan O(n*d) runs
// across cores.
template <typename T>
static void column_means(const T* m, size_t n, size_t d, std::vector<double>& mean)
{
#ifdef LAPLACE_HAS_MKL
    const size_t block = 1024;
    const size_t nblocks = (n + block - 1) / block;
    std::vector<double> partial(nblocks * d, 0.0);
    laplace::tbb_ops::parallel_for_size(
        oneapi::tbb::blocked_range<size_t>(0, nblocks, 1),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t bk = rng.begin(); bk != rng.end(); ++bk) {
                double* acc = partial.data() + bk * d;
                const size_t e = std::min(n, (bk + 1) * block);
                for (size_t i = bk * block; i < e; ++i) {
                    const T* row = m + i * d;
                    for (size_t j = 0; j < d; ++j) acc[j] += (double)row[j];
                }
            }
        });
    for (size_t bk = 0; bk < nblocks; ++bk)
        for (size_t j = 0; j < d; ++j) mean[j] += partial[bk * d + j];
#else
    for (size_t i = 0; i < n; ++i) {
        const T* row = m + i * d;
        for (size_t j = 0; j < d; ++j) mean[j] += (double)row[j];
    }
#endif
    const double inv = 1.0 / (double)n;
    for (size_t j = 0; j < d; ++j) mean[j] *= inv;
}

}

extern "C"
int center_columns_d(double* m, size_t n, size_t d)
{
    if (!m || n == 0 || d == 0) return -1;
    std::vector<double> mean(d, 0.0);
    column_means(m, n, d, mean);
    rows_parallel(n, 256, [&](size_t i) {
        double* row = m + i * d;
        for (size_t j = 0; j < d; ++j) row[j] -= mean[j];
    });
    return 0;
}

extern "C"
int center_columns_f(float* m, size_t n, size_t d)
{
    if (!m || n == 0 || d == 0) return -1;
    std::vector<double> mean(d, 0.0);
    column_means(m, n, d, mean);
    rows_parallel(n, 256, [&](size_t i) {
        float* row = m + i * d;
        for (size_t j = 0; j < d; ++j) row[j] = (float)((double)row[j] - mean[j]);
    });
    return 0;
}

extern "C"
int hypot_rows_d(const double* a, const double* b, size_t n, double* out)
{
    if (!a || !b || !out || n == 0) return -1;
#ifdef LAPLACE_HAS_MKL
    vdHypot((MKL_INT)n, a, b, out);
#else
    rows_parallel(n, 1024, [&](size_t i) { out[i] = std::sqrt(a[i]*a[i] + b[i]*b[i]); });
#endif
    return 0;
}

extern "C"
int scale_cols_f(float* m, size_t rows, size_t d, const float* g)
{
    if (!m || !g || rows == 0 || d == 0) return -1;
    rows_parallel(rows, 256, [&](size_t i) {
        float* row = m + i * d;
        for (size_t j = 0; j < d; ++j) row[j] *= g[j];
    });
    return 0;
}

extern "C"
int scale_cols_d(double* m, size_t rows, size_t d, const float* g)
{
    if (!m || !g || rows == 0 || d == 0) return -1;
    rows_parallel(rows, 256, [&](size_t i) {
        double* row = m + i * d;
        for (size_t j = 0; j < d; ++j) row[j] *= (double)g[j];
    });
    return 0;
}

extern "C"
int slice_head_d(const double* full, double* head,
                 size_t n, size_t full_dim, size_t h, size_t hd)
{
    if (!full || !head || n == 0 || full_dim == 0 || hd == 0) return -1;
    if ((h + 1) * hd > full_dim) return -1;
    rows_parallel(n, 512, [&](size_t i) {
        std::memcpy(head + i * hd, full + i * full_dim + h * hd, hd * sizeof(double));
    });
    return 0;
}

extern "C"
int row_norms_out_d(const double* m, size_t n, size_t d, double* out)
{
    if (!m || !out || n == 0 || d == 0) return -1;
#ifdef LAPLACE_HAS_MKL
    rows_parallel(n, 256, [&](size_t i) {
        const double* row = m + i * d;
        const double ss = cblas_ddot((MKL_INT)d, row, 1, row, 1);
        out[i] = ss > 0.0 ? std::sqrt(ss) : 0.0;
    });
#else
    rows_parallel(n, 256, [&](size_t i) {
        const double* row = m + i * d;
        double ss = 0.0;
        for (size_t j = 0; j < d; ++j) ss += row[j] * row[j];
        out[i] = ss > 0.0 ? std::sqrt(ss) : 0.0;
    });
#endif
    return 0;
}

extern "C"
int f32_to_f64(const float* src, size_t count, double* dst)
{
    if (!src || !dst || count == 0) return -1;
    rows_parallel(count / 4096 + 1, 1, [&](size_t blk) {
        const size_t b = blk * 4096;
        const size_t e = b + 4096 < count ? b + 4096 : count;
        for (size_t i = b; i < e; ++i) dst[i] = (double)src[i];
    });
    return 0;
}

extern "C"
int ffn_activation_norms(const double* x, size_t n, size_t d,
                         const float* up, const float* gate, size_t interm,
                         double* out_norms)
{
    if (!x || !up || !out_norms || n == 0 || d == 0 || interm == 0) return -1;
#ifdef LAPLACE_HAS_MKL
    std::vector<double> upd((size_t)interm * d);
    rows_parallel(interm, 16, [&](size_t r) {
        for (size_t k = 0; k < d; ++k) upd[r * d + k] = (double)up[r * d + k];
    });
    std::vector<double> gated;
    if (gate) {
        gated.resize((size_t)interm * d);
        rows_parallel(interm, 16, [&](size_t r) {
            for (size_t k = 0; k < d; ++k) gated[r * d + k] = (double)gate[r * d + k];
        });
    }

    const size_t tile = 1024;
    std::vector<double> U(tile * interm), G(gate ? tile * interm : 0),
                        E(gate ? tile * interm : 0);
    for (size_t rb = 0; rb < n; rb += tile) {
        const size_t t = (rb + tile < n ? tile : n - rb);
        cblas_dgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
                    (MKL_INT)t, (MKL_INT)interm, (MKL_INT)d,
                    1.0, x + rb * d, (MKL_INT)d, upd.data(), (MKL_INT)d,
                    0.0, U.data(), (MKL_INT)interm);
        if (gate) {
            cblas_dgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
                        (MKL_INT)t, (MKL_INT)interm, (MKL_INT)d,
                        1.0, x + rb * d, (MKL_INT)d, gated.data(), (MKL_INT)d,
                        0.0, G.data(), (MKL_INT)interm);
            /* silu via VML: E = exp(-G) vectorized, then the fused combine. */
            rows_parallel(t, 32, [&](size_t i) {
                const double* g = G.data() + i * interm;
                double* e = E.data() + i * interm;
                for (size_t c = 0; c < interm; ++c) e[c] = -g[c];
            });
            vdExp((MKL_INT)(t * interm), E.data(), E.data());
        }
        rows_parallel(t, 32, [&](size_t i) {
            const double* u = U.data() + i * interm;
            const double* g = gate ? G.data() + i * interm : nullptr;
            const double* e = gate ? E.data() + i * interm : nullptr;
            double ss = 0.0;
            for (size_t c = 0; c < interm; ++c) {
                double a = u[c];
                if (g) a *= g[c] / (1.0 + e[c]);   /* silu(gate)·up */
                ss += a * a;
            }
            out_norms[rb + i] = ss > 0.0 ? std::sqrt(ss) : 0.0;
        });
    }
    return 0;
#else
    (void)up; (void)gate; (void)interm;
    return -2;
#endif
}
