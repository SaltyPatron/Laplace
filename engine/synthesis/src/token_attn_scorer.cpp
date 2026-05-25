#include "laplace/synthesis/token_attn_scorer.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <mkl_cblas.h>
#  include <oneapi/tbb/parallel_for.h>
#  include <oneapi/tbb/blocked_range.h>
#  include <oneapi/tbb/enumerable_thread_specific.h>
#endif

/* Static token-to-token QK attention scorer.
 *
 * Computes token-to-token attention scores for one attention head from
 * the vocabulary embedding matrix and the head's Q/K projection weights.
 * The score for (query_token i, key_token j) is:
 *
 *   score[i,j] = (E[i] · Wq) · (E[j] · Wk)^T / sqrt(head_dim)
 *
 * Processing in query-row blocks avoids materializing the full n_vocab×n_vocab
 * score matrix (which would be ~8 GB for a 32K vocab). Each row block is
 * processed with DGEMM, top-k applied immediately, and only survivors emitted.
 *
 * MKL path: two bulk DGEMMs for Q/K, then per-block DGEMM for scores.
 * Scalar path: naive loops with the same semantics. */

namespace {

constexpr size_t kQueryBlockSize = 256;

/* nth_element-based per-row top-k. scratch must be at least row_size floats.
 * Fills out_k_indices[0..k-1] with the column indices of the top-k values. */
void topk_row(const float* row, size_t row_size, size_t k,
              size_t* out_k_indices, float* scratch) {
    if (k == 0 || row_size == 0) return;
    if (k >= row_size) {
        for (size_t j = 0; j < row_size; ++j) out_k_indices[j] = j;
        return;
    }

    /* Copy absolute values to scratch alongside original indices via an
     * index array sorted by value magnitude. Using nth_element on a
     * float[row_size] copy is simplest — O(row_size) expected time. */
    for (size_t j = 0; j < row_size; ++j) scratch[j] = std::fabs(row[j]);

    /* Partial sort: scratch after this has the k-th largest at index k-1. */
    std::vector<size_t> idx(row_size);
    for (size_t j = 0; j < row_size; ++j) idx[j] = j;
    std::nth_element(idx.begin(), idx.begin() + (k - 1), idx.end(),
                     [&](size_t a, size_t b) {
                         return scratch[a] > scratch[b];
                     });
    for (size_t j = 0; j < k; ++j) out_k_indices[j] = idx[j];
}

} /* namespace */

extern "C"
int compute_static_qk_scores(
    const double* E,
    size_t        n_vocab,
    size_t        d_model,
    const double* Wq,
    const double* Wk,
    size_t        head_dim,
    size_t        topk_per_row,
    qk_pair_t*    out_pairs,
    size_t        out_cap) {

    if (!E || !Wq || !Wk || !out_pairs) return -1;
    if (n_vocab == 0 || d_model == 0 || head_dim == 0 || topk_per_row == 0) return -1;

    const size_t max_pairs = n_vocab * topk_per_row;
    if (out_cap < max_pairs) return -1;

    const double inv_sqrt_hd = 1.0 / std::sqrt((double)head_dim);

    /* Allocate Q = E × Wq and K = E × Wk, each [n_vocab × head_dim]. */
    std::vector<double> Q(n_vocab * head_dim, 0.0);
    std::vector<double> K(n_vocab * head_dim, 0.0);

#ifdef LAPLACE_HAS_MKL
    /* Wq/Wk stored [head_dim × d_model] (HuggingFace output×input convention).
     * Q = E × Wq^T: (n_vocab×d_model) × (d_model×head_dim) → (n_vocab×head_dim).
     * CblasTrans on Wq so DGEMM reads it column-major (= row-major transposed). */
    cblas_dgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
                (MKL_INT)n_vocab, (MKL_INT)head_dim, (MKL_INT)d_model,
                1.0, E, (MKL_INT)d_model,
                Wq, (MKL_INT)d_model,
                0.0, Q.data(), (MKL_INT)head_dim);

    /* K = E × Wk^T */
    cblas_dgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
                (MKL_INT)n_vocab, (MKL_INT)head_dim, (MKL_INT)d_model,
                1.0, E, (MKL_INT)d_model,
                Wk, (MKL_INT)d_model,
                0.0, K.data(), (MKL_INT)head_dim);
#else
    /* Scalar fallback: naive matrix multiply.
     * Wq stored [head_dim × d_model]: Wq[h, d] at Wq[h*d_model + d]. */
    for (size_t i = 0; i < n_vocab; ++i) {
        for (size_t h = 0; h < head_dim; ++h) {
            double qval = 0.0, kval = 0.0;
            for (size_t d = 0; d < d_model; ++d) {
                qval += E[i * d_model + d] * Wq[h * d_model + d];
                kval += E[i * d_model + d] * Wk[h * d_model + d];
            }
            Q[i * head_dim + h] = qval;
            K[i * head_dim + h] = kval;
        }
    }
#endif

    /* Process query tokens in blocks to avoid n_vocab×n_vocab materialization. */
    const size_t block_size = (n_vocab < kQueryBlockSize) ? n_vocab : kQueryBlockSize;
    const size_t n_blocks   = (n_vocab + block_size - 1) / block_size;

    size_t pair_count = 0;

    /* Thread-local scratch buffers: score_row[n_vocab] and topk_indices[topk_per_row]. */
    struct ThreadLocal {
        std::vector<double> score_block;
        std::vector<float>  score_row_f;
        std::vector<float>  scratch;
        std::vector<size_t> top_indices;
    };

#ifdef LAPLACE_HAS_MKL
    oneapi::tbb::enumerable_thread_specific<ThreadLocal> tls;
    /* Accumulate results from each block into a per-thread portion of out_pairs.
     * To keep output order deterministic we process blocks serially here and
     * use TBB only for the DGEMM within each block (MKL already parallelises
     * large DGEMMs via its own thread pool). */
    (void)tls;
#endif

    std::vector<double> score_block(block_size * n_vocab);
    std::vector<float>  score_row_f(n_vocab);
    std::vector<float>  scratch(n_vocab);
    std::vector<size_t> top_indices(topk_per_row);

    for (size_t blk = 0; blk < n_blocks; ++blk) {
        const size_t q_start = blk * block_size;
        const size_t q_end   = std::min(q_start + block_size, n_vocab);
        const size_t rows    = q_end - q_start;

        /* score_block = Q_block × K^T: [rows × n_vocab]
         * Q_block: [rows × head_dim] starting at Q[q_start*head_dim]
         * K:       [n_vocab × head_dim] (treat as transposed) */
#ifdef LAPLACE_HAS_MKL
        cblas_dgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
                    (MKL_INT)rows, (MKL_INT)n_vocab, (MKL_INT)head_dim,
                    inv_sqrt_hd,
                    Q.data() + q_start * head_dim, (MKL_INT)head_dim,
                    K.data(), (MKL_INT)head_dim,
                    0.0,
                    score_block.data(), (MKL_INT)n_vocab);
#else
        for (size_t qi = 0; qi < rows; ++qi) {
            const double* q_row = Q.data() + (q_start + qi) * head_dim;
            for (size_t kj = 0; kj < n_vocab; ++kj) {
                const double* k_row = K.data() + kj * head_dim;
                double s = 0.0;
                for (size_t h = 0; h < head_dim; ++h)
                    s += q_row[h] * k_row[h];
                score_block[qi * n_vocab + kj] = s * inv_sqrt_hd;
            }
        }
#endif

        /* Apply per-row top-k and emit survivors. */
        for (size_t qi = 0; qi < rows; ++qi) {
            const size_t query_idx = q_start + qi;
            const double* score_row = score_block.data() + qi * n_vocab;

            /* Downcast to float for the topk kernel (sufficient for ranking). */
            for (size_t j = 0; j < n_vocab; ++j)
                score_row_f[j] = (float)score_row[j];

            const size_t k = std::min(topk_per_row, n_vocab);
            topk_row(score_row_f.data(), n_vocab, k,
                     top_indices.data(), scratch.data());

            for (size_t ki = 0; ki < k; ++ki) {
                const size_t key_idx = top_indices[ki];
                out_pairs[pair_count++] = {
                    (uint32_t)query_idx,
                    (uint32_t)key_idx,
                    score_row_f[key_idx]
                };
            }
        }
    }

    return (int)pair_count;
}
