#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Sparse (query, key, score) triplet emitted by compute_static_qk_scores. */
typedef struct {
    uint32_t query_idx;
    uint32_t key_idx;
    float    score;
} qk_pair_t;

/* Compute per-row top-k token-to-token static QK attention scores for one
 * attention head, given the full vocabulary embedding matrix.
 *
 * E:   [n_vocab × d_model] row-major — embed_tokens rows
 * Wq:  [head_dim × d_model] row-major — one head's Q projection weight
 *       (HuggingFace output×input convention: rows are output neurons)
 * Wk:  [head_dim × d_model] row-major — one head's K projection weight
 *
 * Algorithm:
 *   Q = E × Wq                    [n_vocab × head_dim]  via DGEMM
 *   K = E × Wk                    [n_vocab × head_dim]  via DGEMM
 *   score[i,j] = Q[i]·K[j]^T / sqrt(head_dim)          block DGEMM per row slice
 *   Keep top topk_per_row j values per query token i.
 *
 * out_pairs: caller-allocated array; must hold at least n_vocab*topk_per_row entries.
 * Returns number of pairs written (≤ n_vocab*topk_per_row), or -1 on error. */
int compute_static_qk_scores(
    const double* E,
    size_t        n_vocab,
    size_t        d_model,
    const double* Wq,
    const double* Wk,
    size_t        head_dim,
    size_t        topk_per_row,
    qk_pair_t*    out_pairs,
    size_t        out_cap);

#ifdef __cplusplus
}
#endif
