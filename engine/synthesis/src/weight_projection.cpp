#include "laplace/synthesis/weight_projection.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <numeric>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <mkl_cblas.h>
#  include <oneapi/tbb/parallel_for.h>
#  include <oneapi/tbb/blocked_range.h>
#endif

/* E·Wᵀ token→feature projection + per-token top-k by |magnitude|.
 * One SGEMM (P = E · Wᵀ, [n_vocab × n_out]) then a TBB-parallel per-row nth_element.
 * P is materialized in full (n_vocab × n_out × 4 bytes — ~720 MB for a 32K-vocab ×
 * 5632-intermediate tensor); block-by-block streaming for larger models is future
 * work per ADR 0056 / tracking #222. */

namespace {
void bf16_to_f32(const uint16_t* src, float* dst, size_t n) {
    for (size_t i = 0; i < n; i++) {
        const uint32_t bits = (uint32_t)src[i] << 16;
        std::memcpy(&dst[i], &bits, sizeof(float));
    }
}
} /* namespace */

extern "C"
int compute_static_projection_scores(
    const uint16_t* E_bf16, size_t n_vocab, size_t d_model,
    const float*    W_f32,  size_t n_out,
    size_t          topk_per_row,
    qk_pair_t*      out_pairs, size_t out_cap)
{
    if (!E_bf16 || !W_f32 || !out_pairs)                       return -1;
    if (n_vocab == 0 || d_model == 0 || n_out == 0 || topk_per_row == 0) return -1;
    if (out_cap < n_vocab * topk_per_row)                      return -1;

#ifdef LAPLACE_HAS_MKL
    std::vector<float> E(n_vocab * d_model);
    bf16_to_f32(E_bf16, E.data(), n_vocab * d_model);

    /* P = E [n_vocab × d_model] · Wᵀ [d_model × n_out]  →  [n_vocab × n_out] */
    std::vector<float> P(n_vocab * n_out);
    cblas_sgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
                (MKL_INT)n_vocab, (MKL_INT)n_out, (MKL_INT)d_model,
                1.0f, E.data(), (MKL_INT)d_model,
                W_f32, (MKL_INT)d_model,
                0.0f, P.data(), (MKL_INT)n_out);

    const size_t k = std::min(topk_per_row, n_out);
    std::atomic<size_t> count{0};

    oneapi::tbb::parallel_for(
        oneapi::tbb::blocked_range<size_t>(0, n_vocab, 256),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            std::vector<size_t> idx(n_out);
            for (size_t t = rng.begin(); t != rng.end(); ++t) {
                const float* row = P.data() + t * n_out;
                std::iota(idx.begin(), idx.end(), 0U);
                std::nth_element(idx.begin(), idx.begin() + (ptrdiff_t)(k - 1), idx.end(),
                                 [&](size_t a, size_t b) {
                                     return std::fabs(row[a]) > std::fabs(row[b]);
                                 });
                const size_t base = count.fetch_add(k, std::memory_order_relaxed);
                for (size_t ki = 0; ki < k; ++ki) {
                    const size_t o = idx[ki];
                    out_pairs[base + ki] = { (uint32_t)t, (uint32_t)o, row[o] };
                }
            }
        });

    return (int)count.load();
#else
    (void)d_model; (void)W_f32; (void)n_out; (void)topk_per_row;
    return -2;   /* MKL/BLAS required */
#endif
}
