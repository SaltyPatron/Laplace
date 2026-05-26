#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/synthesis/token_attn_scorer.h"  /* qk_pair_t */

#ifdef __cplusplus
extern "C" {
#endif

/* E·Wᵀ linear-projection scorer — the interior-role math_function per ADR 0056
 * (V_PROJECTS / O_PROJECTS / GATES / UP_PROJECTS / DOWN_PROJECTS).
 *
 * Computes P[t, o] = (E · Wᵀ)[t, o] = Σ_d E[t,d]·W[o,d] — the magnitude of token t
 * projected onto output/feature dimension o under weight W. For each token row,
 * keeps the top-k output dims by |P|. The emitted matchup is
 * (token_entity, feature_dim): how strongly token t drives feature o.
 *
 * Unlike the QK scorer (a token↔token bilinear), this is a single token→feature
 * projection: one SGEMM, then per-row top-k. No SVD, no candidate selection.
 *
 *   E_bf16:       [n_vocab × d_model] BF16 (HF embed_tokens layout).
 *   W_f32:        [n_out × d_model] f32 (HF output×input convention).
 *   topk_per_row: kept feature dims per token.
 *   out_pairs:    caller-allocated, >= n_vocab*topk_per_row; query_idx=token,
 *                 key_idx=feature/out dim, score=projection value.
 *
 * Returns the number of pairs written, -1 on bad args, -2 if MKL/BLAS unavailable. */
int compute_static_projection_scores(
    const uint16_t* E_bf16, size_t n_vocab, size_t d_model,
    const float*    W_f32,  size_t n_out,
    size_t          topk_per_row,
    qk_pair_t*      out_pairs, size_t out_cap);

#ifdef __cplusplus
}
#endif
