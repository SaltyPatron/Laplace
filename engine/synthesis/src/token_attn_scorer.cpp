#include "laplace/synthesis/token_attn_scorer.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <numeric>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <mkl_cblas.h>
#  include <mkl_lapacke.h>
#  include <mkl.h>
#  include <oneapi/tbb/parallel_for.h>
#  include <oneapi/tbb/blocked_range.h>
#endif

/* SVD-based static token-to-token QK attention scorer.
 *
 * Key insight: score[i,j] = Q[i]·K[j]/sqrt(d) where Q=E·Wq^T, K=E·Wk^T.
 * The score matrix is rank ≤ head_dim.  Instead of materialising the full
 * [n_vocab × n_vocab] score matrix (O(n_vocab²)), thin-SVD K to find the
 * principal key directions, project queries into those directions, pre-select
 * candidate key tokens from the dominant singular modes, then evaluate exact
 * scores via SGEMM only against the candidate set.
 *
 * Complexity: O(n_vocab × d_model × head_dim) for Q/K DGEMMs +
 *             O(n_vocab × head_dim²)            for SVD of K +
 *             O(n_vocab × head_dim × n_cands)   for candidate score SGEMM
 * vs. the old O(n_vocab² × head_dim) block-DGEMM approach. */

namespace {

/* ---------- BF16 decode -------------------------------------------------- */

static void bf16_to_f32(const uint16_t* src, float* dst, size_t n) {
    /* BF16 = top 16 bits of IEEE-754 float32.  Shift into the upper half of
     * a uint32, then bit-reinterpret as float. */
    for (size_t i = 0; i < n; i++) {
        const uint32_t bits = (uint32_t)src[i] << 16;
        memcpy(&dst[i], &bits, sizeof(float));
    }
}

/* ---------- Thin SVD helper ---------------------------------------------- */

/* Thin SVD of A [m × n] (m ≥ n) using LAPACK sgesdd (divide-and-conquer,
 * fastest path). A is overwritten on return.
 * Outputs: U [m × n], s [n], Vt [n × n].  Row-major throughout.
 * Returns 0 on success. */
static int thin_svd(float* A, int m, int n,
                    float* U, float* s, float* Vt) {
    if (m < n) return -1;
#ifdef LAPLACE_HAS_MKL
    return LAPACKE_sgesdd(LAPACK_ROW_MAJOR, 'S',
                          m, n, A, n, s, U, n, Vt, n);
#else
    (void)A; (void)m; (void)n; (void)U; (void)s; (void)Vt;
    return -1;
#endif
}

/* ---------- Per-head scorer ---------------------------------------------- */

/* Tuning knobs. */
constexpr size_t kModesUsed       = 8;   /* top singular modes to sample */
constexpr size_t kCandsPerSign    = 256; /* tokens per sign bucket per mode */

/* Score one attention head.  E_f32 is shared read-only (decoded once outside).
 * out_pairs must hold at least n_vocab*topk_per_row entries.
 * Returns the number of pairs written, or -1 on error. */
static int score_head(
    const float* E_f32,     /* [n_vocab × d_model] */
    const float* Wq,        /* [head_dim × d_model] */
    const float* Wk,        /* [head_dim × d_model] */
    size_t n_vocab, size_t d_model, size_t head_dim,
    size_t topk_per_row,
    qk_pair_t* out_pairs)
{
    if (!E_f32 || !Wq || !Wk || !out_pairs) return -1;
    if (n_vocab == 0 || d_model == 0 || head_dim == 0) return -1;
    if ((int)n_vocab < (int)head_dim) return -1;  /* thin_svd requires m ≥ n */

    const float scale = 1.0f / std::sqrt((float)head_dim);

    /* --- 1. Q = E × Wq^T,  K = E × Wk^T  [n_vocab × head_dim] ----------- */
    std::vector<float> Q(n_vocab * head_dim, 0.0f);
    std::vector<float> K(n_vocab * head_dim, 0.0f);

#ifdef LAPLACE_HAS_MKL
    /* Tell MKL to use 1 thread so TBB-level parallelism across heads is clean. */
    mkl_set_num_threads_local(1);

    cblas_sgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
                (MKL_INT)n_vocab, (MKL_INT)head_dim, (MKL_INT)d_model,
                1.0f, E_f32, (MKL_INT)d_model,
                Wq, (MKL_INT)d_model,
                0.0f, Q.data(), (MKL_INT)head_dim);

    cblas_sgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
                (MKL_INT)n_vocab, (MKL_INT)head_dim, (MKL_INT)d_model,
                1.0f, E_f32, (MKL_INT)d_model,
                Wk, (MKL_INT)d_model,
                0.0f, K.data(), (MKL_INT)head_dim);
#else
    for (size_t i = 0; i < n_vocab; i++)
        for (size_t h = 0; h < head_dim; h++) {
            float qv = 0.0f, kv = 0.0f;
            for (size_t d = 0; d < d_model; d++) {
                const float e = E_f32[i * d_model + d];
                qv += e * Wq[h * d_model + d];
                kv += e * Wk[h * d_model + d];
            }
            Q[i * head_dim + h] = qv;
            K[i * head_dim + h] = kv;
        }
#endif

    /* --- 2. Thin SVD of K: K = U_K × diag(s_K) × Vt_K  ------------------- */
    std::vector<float> K_svd(K);  /* sgesdd overwrites; keep K for exact scores */
    std::vector<float> U_K(n_vocab * head_dim);
    std::vector<float> s_K(head_dim);
    std::vector<float> Vt_K(head_dim * head_dim);

    if (thin_svd(K_svd.data(), (int)n_vocab, (int)head_dim,
                 U_K.data(), s_K.data(), Vt_K.data()) != 0)
        return -1;

    /* --- 3. Q_modes = Q × Vt_K^T  [n_vocab × head_dim]  ------------------- */
    /* Q_modes[i,r] = Q[i] projected onto K's r-th principal direction (Vt_K[r,:]). */
    std::vector<float> Q_modes(n_vocab * head_dim, 0.0f);
#ifdef LAPLACE_HAS_MKL
    cblas_sgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
                (MKL_INT)n_vocab, (MKL_INT)head_dim, (MKL_INT)head_dim,
                1.0f, Q.data(), (MKL_INT)head_dim,
                Vt_K.data(), (MKL_INT)head_dim,
                0.0f, Q_modes.data(), (MKL_INT)head_dim);
#else
    for (size_t i = 0; i < n_vocab; i++)
        for (size_t r = 0; r < head_dim; r++) {
            float v = 0.0f;
            for (size_t c = 0; c < head_dim; c++)
                v += Q[i * head_dim + c] * Vt_K[r * head_dim + c];
            Q_modes[i * head_dim + r] = v;
        }
#endif

    /* --- 4. Candidate selection from K's principal directions -------------- */
    /* Sort modes by singular value magnitude (s_K already non-increasing from
     * sgesdd, but be defensive). */
    const size_t n_modes = std::min(kModesUsed, head_dim);
    const size_t n_cands_per_sign = std::min(kCandsPerSign, n_vocab / 2);

    std::vector<bool> is_cand(n_vocab, false);
    std::vector<size_t> idx(n_vocab);
    std::iota(idx.begin(), idx.end(), 0U);

    for (size_t mi = 0; mi < n_modes; mi++) {
        /* Top-n_cands_per_sign tokens where U_K[:,mi] is most positive. */
        std::nth_element(idx.begin(),
                         idx.begin() + (ptrdiff_t)(n_cands_per_sign - 1),
                         idx.end(),
                         [&](size_t a, size_t b) {
                             return U_K[a * head_dim + mi] > U_K[b * head_dim + mi];
                         });
        for (size_t ci = 0; ci < n_cands_per_sign; ci++)
            is_cand[idx[ci]] = true;

        /* Top-n_cands_per_sign tokens where U_K[:,mi] is most negative. */
        std::nth_element(idx.begin(),
                         idx.begin() + (ptrdiff_t)(n_cands_per_sign - 1),
                         idx.end(),
                         [&](size_t a, size_t b) {
                             return U_K[a * head_dim + mi] < U_K[b * head_dim + mi];
                         });
        for (size_t ci = 0; ci < n_cands_per_sign; ci++)
            is_cand[idx[ci]] = true;

        /* Re-seed idx for next mode. */
        std::iota(idx.begin(), idx.end(), 0U);
    }

    /* Compact candidate list and build K_cands [n_cands × head_dim]. */
    std::vector<size_t> cands;
    cands.reserve(n_modes * n_cands_per_sign * 2);
    for (size_t j = 0; j < n_vocab; j++)
        if (is_cand[j]) cands.push_back(j);
    const size_t n_cands = cands.size();
    if (n_cands == 0) return 0;

    std::vector<float> K_cands(n_cands * head_dim);
    for (size_t ci = 0; ci < n_cands; ci++)
        std::copy(K.data() + cands[ci] * head_dim,
                  K.data() + cands[ci] * head_dim + head_dim,
                  K_cands.data() + ci * head_dim);

    /* --- 5. SCORES = Q × K_cands^T / sqrt(head_dim)  [n_vocab × n_cands] -- */
    std::vector<float> SCORES(n_vocab * n_cands);
#ifdef LAPLACE_HAS_MKL
    cblas_sgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
                (MKL_INT)n_vocab, (MKL_INT)n_cands, (MKL_INT)head_dim,
                scale,
                Q.data(), (MKL_INT)head_dim,
                K_cands.data(), (MKL_INT)head_dim,
                0.0f, SCORES.data(), (MKL_INT)n_cands);
#else
    for (size_t i = 0; i < n_vocab; i++)
        for (size_t ci = 0; ci < n_cands; ci++) {
            float s = 0.0f;
            for (size_t h = 0; h < head_dim; h++)
                s += Q[i * head_dim + h] * K_cands[ci * head_dim + h];
            SCORES[i * n_cands + ci] = s * scale;
        }
#endif

    /* --- 6. Top-topk_per_row per query row (TBB-parallel) ----------------- */
    const size_t k = std::min(topk_per_row, n_cands);
    std::atomic<size_t> pair_count{0};

#ifdef LAPLACE_HAS_MKL
    oneapi::tbb::parallel_for(
        oneapi::tbb::blocked_range<size_t>(0, n_vocab, 256),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            std::vector<size_t> local_idx(n_cands);
            for (size_t i = rng.begin(); i != rng.end(); i++) {
                const float* row = SCORES.data() + i * n_cands;
                std::iota(local_idx.begin(), local_idx.end(), 0U);
                std::nth_element(local_idx.begin(),
                                 local_idx.begin() + (ptrdiff_t)(k - 1),
                                 local_idx.begin() + (ptrdiff_t)n_cands,
                                 [&](size_t a, size_t b) {
                                     return std::fabs(row[a]) > std::fabs(row[b]);
                                 });
                const size_t base = pair_count.fetch_add(k, std::memory_order_relaxed);
                for (size_t ki = 0; ki < k; ki++) {
                    const size_t ci = local_idx[ki];
                    out_pairs[base + ki] = {
                        (uint32_t)i,
                        (uint32_t)cands[ci],
                        row[ci]
                    };
                }
            }
        });
#else
    {
        std::vector<size_t> local_idx(n_cands);
        for (size_t i = 0; i < n_vocab; i++) {
            const float* row = SCORES.data() + i * n_cands;
            std::iota(local_idx.begin(), local_idx.end(), 0U);
            std::nth_element(local_idx.begin(),
                             local_idx.begin() + (ptrdiff_t)(k - 1),
                             local_idx.begin() + (ptrdiff_t)n_cands,
                             [&](size_t a, size_t b) {
                                 return std::fabs(row[a]) > std::fabs(row[b]);
                             });
            const size_t base = pair_count.fetch_add(k, std::memory_order_relaxed);
            for (size_t ki = 0; ki < k; ki++) {
                const size_t ci = local_idx[ki];
                out_pairs[base + ki] = {
                    (uint32_t)i,
                    (uint32_t)cands[ci],
                    row[ci]
                };
            }
        }
    }
#endif

    return (int)pair_count.load();
}

} /* namespace */

/* =========================================================================
 * Public C API
 * ========================================================================= */

extern "C"
int compute_static_qk_scores(
    const uint16_t* E_bf16,
    size_t          n_vocab,
    size_t          d_model,
    const float*    Wq,
    const float*    Wk,
    size_t          head_dim,
    size_t          topk_per_row,
    qk_pair_t*      out_pairs,
    size_t          out_cap)
{
    if (!E_bf16 || !Wq || !Wk || !out_pairs) return -1;
    if (out_cap < n_vocab * topk_per_row) return -1;

    /* Decode BF16 → f32 once for this call. */
    std::vector<float> E_f32(n_vocab * d_model);
    bf16_to_f32(E_bf16, E_f32.data(), n_vocab * d_model);

    return score_head(E_f32.data(), Wq, Wk,
                      n_vocab, d_model, head_dim,
                      topk_per_row, out_pairs);
}

extern "C"
int compute_static_qk_scores_batch(
    const uint16_t* E_bf16,
    size_t          n_vocab,
    size_t          d_model,
    const float*    Wq_all,
    const float*    Wk_all,
    size_t          n_heads,
    size_t          n_kv_heads,
    size_t          head_dim,
    size_t          queries_per_kv,
    size_t          topk_per_row,
    qk_pair_t*      out_pairs,
    int*            out_counts,
    size_t          out_cap_per_head)
{
    if (!E_bf16 || !Wq_all || !Wk_all || !out_pairs || !out_counts) return -1;
    if (out_cap_per_head < n_vocab * topk_per_row) return -1;
    if (queries_per_kv == 0 || n_kv_heads == 0) return -1;

    /* Decode E once; all TBB tasks share it read-only. */
    std::vector<float> E_f32(n_vocab * d_model);
    bf16_to_f32(E_bf16, E_f32.data(), n_vocab * d_model);

    const size_t wq_stride = head_dim * d_model;   /* bytes per query head   */
    const size_t wk_stride = head_dim * d_model;   /* bytes per KV head      */

#ifdef LAPLACE_HAS_MKL
    /* Process all heads in parallel via TBB.  MKL is set to 1 thread inside
     * score_head so TBB can drive all cores without over-subscription. */
    oneapi::tbb::parallel_for(
        oneapi::tbb::blocked_range<size_t>(0, n_heads, 1),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t h = rng.begin(); h != rng.end(); h++) {
                const size_t kv_head = h / queries_per_kv;
                const float* Wq_h   = Wq_all + h * wq_stride;
                const float* Wk_h   = Wk_all + kv_head * wk_stride;
                qk_pair_t*   out_h  = out_pairs + h * out_cap_per_head;

                out_counts[h] = score_head(E_f32.data(), Wq_h, Wk_h,
                                           n_vocab, d_model, head_dim,
                                           topk_per_row, out_h);
            }
        });
#else
    for (size_t h = 0; h < n_heads; h++) {
        const size_t kv_head = h / queries_per_kv;
        const float* Wq_h   = Wq_all + h * wq_stride;
        const float* Wk_h   = Wk_all + kv_head * wk_stride;
        qk_pair_t*   out_h  = out_pairs + h * out_cap_per_head;

        out_counts[h] = score_head(E_f32.data(), Wq_h, Wk_h,
                                   n_vocab, d_model, head_dim,
                                   topk_per_row, out_h);
    }
#endif

    return 0;
}
