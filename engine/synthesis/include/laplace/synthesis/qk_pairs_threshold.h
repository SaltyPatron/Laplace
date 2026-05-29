#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Sparse (query, key, score) triplet with an f64 score, emitted by the exact
 * threshold-based QK scorer. The sibling qk_pair_t (token_attn_scorer.h) carries
 * an f32 score, which would discard the compensated-summation precision this
 * kernel guarantees — so this kernel uses its own f64-score struct. */
typedef struct {
    uint32_t query_idx;
    uint32_t key_idx;
    double   score;
} qk_pair_f64_t;

/* Exact, deterministic, streaming threshold-based QK token-relation scorer.
 *
 * Replaces the lossy top-k / candidate-SVD path of compute_static_qk_scores
 * (token_attn_scorer.cpp): instead of selecting the top-k keys per query (which
 * also needs a vocab×k staging buffer and OOMs), this emits EVERY (query, key)
 * pair whose score magnitude strictly exceeds noise_floor, and never allocates
 * a vocab×vocab or vocab×k buffer.
 *
 * Semantics (one attention head; per ADR 0056:157, q_proj·k_proj):
 *
 *   q_t[d] = Σ_{m=0..d_model-1} f64(E_f32[t*d_model + m]) * f64(Wq_head[d*d_model + m])
 *   k_s[d] = Σ_{m=0..d_model-1} f64(E_f32[s*d_model + m]) * f64(Wk_head[d*d_model + m])
 *   score(t,s) = Σ_{d=0..head_dim-1} q_t[d] * k_s[d]
 *
 * A pair (t, s, score) is emitted iff |score| > noise_floor. There is NO scale
 * factor and NO top-k: the full above-threshold relation set is exact.
 *
 * Streaming / bounded memory:
 *   The caller drives bounded query-row windows [q0, q1) of ONE head. The kernel
 *   fills the caller-provided out buffer (capacity out_cap pairs). If the window
 *   would emit more than out_cap pairs it sets *overflow = 1, writes the
 *   deterministic prefix of pairs that fit (a whole number of leading query rows
 *   — never a partial row), and returns that prefix's count; the caller then
 *   retries with a smaller [q0, q1). The kernel allocates only O(head_dim) per
 *   worker plus O(q1-q0) row metadata — never O(vocab·vocab) or O(vocab·k).
 *
 * Exactness + determinism:
 *   - Inputs E_f32 / Wq_head / Wk_head are exact f32 (BF16->f32 is exact and is
 *     performed once upstream by the caller). ALL arithmetic is f64.
 *   - Every summation (the only rounding step) is Neumaier-compensated in a
 *     FIXED order: q/k dot products over m = 0..d_model-1, the score over
 *     d = 0..head_dim-1.
 *   - Each query row's scores + threshold test are wholly independent and
 *     order-fixed, so TBB parallelism is only ACROSS query rows. Output slots
 *     are assigned by a deterministic prefix-sum of per-row above-threshold
 *     counts (computed in a fixed pass-1, independent of thread count), so both
 *     the emitted set AND its order (rows ascending by t, keys ascending by s)
 *     are bit-identical regardless of thread count or window boundaries.
 *
 *   E_f32:    [vocab x d_model] f32, row-major. Pre-decoded once by the caller.
 *   vocab:    number of tokens (rows of E, both query and key axes).
 *   d_model:  embedding width.
 *   Wq_head:  [head_dim x d_model] f32 for this head (HF output×input layout).
 *   Wk_head:  [head_dim x d_model] f32 for this head.
 *   head_dim: projected dimension of the head.
 *   noise_floor: pairs with |score| <= noise_floor are dropped (must be >= 0).
 *   q0, q1:   process query rows [q0, q1); requires q0 <= q1 <= vocab.
 *   out:      caller-allocated, capacity out_cap pairs.
 *   out_cap:  capacity of out, in pairs.
 *   overflow: set to 1 if out_cap was hit (else 0); must be non-null.
 *
 * Returns the number of pairs written (>= 0) for [q0, q1), or -1 on bad args
 * (null pointer, q0 > q1, q1 > vocab, any of vocab/d_model/head_dim == 0, or
 * noise_floor < 0 / NaN). On overflow the return value is the count of the
 * deterministic leading prefix that fit. */
long compute_qk_pairs_above_threshold(
    const float* E_f32, size_t vocab, size_t d_model,
    const float* Wq_head, const float* Wk_head, size_t head_dim,
    double noise_floor, size_t q0, size_t q1,
    qk_pair_f64_t* out, size_t out_cap, int* overflow);

#ifdef __cplusplus
}
#endif
