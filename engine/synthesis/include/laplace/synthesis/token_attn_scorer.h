#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Sparse (query, key, score) triplet emitted by the QK scorers. */
typedef struct {
    uint32_t query_idx;
    uint32_t key_idx;
    float    score;
} qk_pair_t;

/* SVD-based per-head QK scorer.
 *
 * E_bf16:  [n_vocab × d_model] in BF16 (2 bytes/element, HF layout)
 * Wq:      [head_dim × d_model] in f32, one head (HF output×input convention)
 * Wk:      [head_dim × d_model] in f32, one head
 *
 * Algorithm (O(n_vocab × head_dim²) instead of the old O(n_vocab²)):
 *   1. Decode BF16 E → f32 E_f32 (once per call; shared with K side)
 *   2. Q = E_f32 × Wq^T  [n_vocab × head_dim]  via SGEMM
 *   3. K = E_f32 × Wk^T  [n_vocab × head_dim]  via SGEMM
 *   4. Thin SVD of K → U_K [n_vocab × head_dim], s_K [head_dim]
 *   5. Q_modes = Q × Vt_K^T  — Q projected into K's principal directions
 *   6. Candidate selection: for the top n_modes singular directions,
 *      take the top-C tokens from each sign of U_K[:,r] as key candidates.
 *   7. SCORES = Q × K_cands^T / sqrt(head_dim)  via SGEMM  [n_vocab × n_cands]
 *   8. TBB parallel: nth_element top-topk_per_row from SCORES per query row.
 *
 * out_pairs: caller-allocated; must hold at least n_vocab*topk_per_row entries.
 * Returns number of pairs written, or -1 on error. */
int compute_static_qk_scores(
    const uint16_t* E_bf16,
    size_t          n_vocab,
    size_t          d_model,
    const float*    Wq,
    const float*    Wk,
    size_t          head_dim,
    size_t          topk_per_row,
    qk_pair_t*      out_pairs,
    size_t          out_cap);

/* Batch variant: all attention heads for one layer, fully TBB-parallel.
 *
 * E_bf16:            [n_vocab × d_model] BF16 — decoded once, shared across heads.
 * Wq_all:            [n_heads × head_dim × d_model] f32 — all query projections.
 * Wk_all:            [n_kv_heads × head_dim × d_model] f32 — all key projections.
 * queries_per_kv:    n_heads / n_kv_heads (GQA grouping factor).
 * out_pairs:         flat buffer [n_heads × out_cap_per_head]; head h writes at
 *                    out_pairs + h * out_cap_per_head.
 * out_counts[h]:     number of pairs written for head h.
 * out_cap_per_head:  must be ≥ n_vocab * topk_per_row.
 *
 * Returns 0 on success, -1 on error. */
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
    size_t          out_cap_per_head);

#ifdef __cplusplus
}
#endif
