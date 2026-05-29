#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/synthesis/qk_pairs_threshold.h"  /* qk_pair_f64_t */

#ifdef __cplusplus
extern "C" {
#endif

/* Project-once + score-from-cache decomposition of the QK relation kernel.
 *
 * MOTIVATION (performance only — bit-identical math):
 *   compute_qk_pairs_above_threshold_pruned re-streams the full [vocab x d_model]
 *   embedding matrix E once PER HEAD (it projects K and Q inside the call). For an
 *   n-layer, n_heads model that re-reads E ~ (n_heads + n_kv) times per layer, so
 *   the kernel is memory-bandwidth bound. These two entry points split the work:
 *
 *     1. project_qk_layer  — stream E ONCE and project ALL heads' Q and K into
 *                            caches (q_cache, k_cache).
 *     2. score_qk_head_cached — score one head purely from the caches (no E touch),
 *                            using the SAME Cauchy-Schwarz norm-pruned scoring as
 *                            compute_qk_pairs_above_threshold_pruned.
 *
 *   The projection arithmetic (per-element compensated Neumaier sum, fixed order
 *   m = 0..d_model-1) and the scoring arithmetic (compensated dot product, fixed
 *   order d = 0..head_dim-1, key-norm-sort / binary-search cutoff / ascending-key
 *   emit / whole-row overflow prefix) are byte-for-byte the same code paths the
 *   pruned kernel uses. Therefore, for any (head, kv_head, floor, window), the
 *   emitted pairs, their order, the f64 score bits, the returned count, and the
 *   overflow flag are BIT-IDENTICAL to compute_qk_pairs_above_threshold_pruned
 *   (and hence to compute_qk_pairs_above_threshold) for that head. */

/* ── 1. Project every head's Q and K into caches in a single pass over E. ─────
 *
 *   q_cache_out: [vocab][n_heads][head_dim] f64, row-major. The projected query
 *                vector for (token t, head h) is at
 *                q_cache_out + ((t*n_heads + h)*head_dim).
 *   k_cache_out: [vocab][n_kv][head_dim]    f64, row-major. The projected key
 *                vector for (token t, kv_head kh) is at
 *                k_cache_out + ((t*n_kv + kh)*head_dim).
 *
 *   q_cache_out MUST hold vocab*n_heads*head_dim doubles; k_cache_out MUST hold
 *   vocab*n_kv*head_dim doubles (both caller-allocated).
 *
 *   Wq: [n_heads*head_dim x d_model] f32, row-major (HF output×input layout); head
 *       h's slice begins at Wq + h*head_dim*d_model.
 *   Wk: [n_kv*head_dim    x d_model] f32, row-major; kv_head kh's slice begins at
 *       Wk + kh*head_dim*d_model.
 *
 *   Each projected element is
 *     q_cache[t,h,d]  = Σ_{m=0..d_model-1} f64(E[t*d_model+m]) * f64(Wq[(h*head_dim+d)*d_model+m])
 *     k_cache[t,kh,d] = Σ_{m=0..d_model-1} f64(E[t*d_model+m]) * f64(Wk[(kh*head_dim+d)*d_model+m])
 *   with the IDENTICAL Neumaier-compensated summation in the IDENTICAL fixed order
 *   as compute_qk_pairs_above_threshold_pruned's projection — so a head scored from
 *   this cache is bit-identical to the same head scored by the pruned kernel.
 *
 *   TBB-parallel across tokens (E is streamed exactly once). Determinism is
 *   per-element (each output element is an independent fixed-order compensated sum),
 *   so the cache contents are thread-count independent.
 *
 *   Returns 0 on success, -1 on bad args (null pointer or any of
 *   vocab/d_model/n_heads/n_kv/head_dim == 0). */
int project_qk_layer(
    const float* E_f32, size_t vocab, size_t d_model,
    const float* Wq, size_t n_heads,
    const float* Wk, size_t n_kv,
    size_t head_dim,
    double* q_cache_out, double* k_cache_out);

/* ── 2. Score one head from the pre-projected caches. ─────────────────────────
 *
 *   Reads q_cache[token][head] and k_cache[token][kv_head] (layouts above) instead
 *   of projecting E. Runs the IDENTICAL Cauchy-Schwarz norm-pruned scoring as
 *   compute_qk_pairs_above_threshold_pruned: build the key-norm table for this
 *   kv_head, sort by norm descending, per query find the candidate prefix by binary
 *   search, score candidates with the compensated dot product, two passes with a
 *   fixed-order prefix-sum of per-row survivor counts for stable offsets, emit each
 *   row's survivors in ascending key index, and the same whole-row overflow prefix.
 *
 *   q_cache:  [vocab][n_heads][head_dim] f64 (from project_qk_layer).
 *   k_cache:  [vocab][n_kv][head_dim]    f64 (from project_qk_layer).
 *   head:     which query head (0..n_heads-1).
 *   kv_head:  which kv head feeds this query head (0..n_kv-1).
 *   floor:    pairs with |score| <= floor are dropped (>= 0).
 *   q0, q1:   process query rows [q0, q1); requires q0 <= q1 <= vocab.
 *   out:      caller-allocated, capacity out_cap pairs.
 *   overflow: set to 1 if out_cap was hit (else 0); must be non-null. On overflow
 *             the kept prefix is a whole number of leading query rows.
 *
 *   For a given (head, kv_head, floor, q0, q1) the emitted pairs, order, f64 score
 *   bits, returned count, and overflow flag are BIT-IDENTICAL to calling
 *   compute_qk_pairs_above_threshold_pruned with Wq's head slice + Wk's kv_head slice.
 *
 *   Returns pairs written (>= 0), or -1 on bad args (null pointer, q0 > q1,
 *   q1 > vocab, any of vocab/head_dim/n_heads/n_kv == 0, head >= n_heads,
 *   kv_head >= n_kv, floor < 0 / NaN). */
long score_qk_head_cached(
    const double* q_cache, size_t n_heads,
    const double* k_cache, size_t n_kv,
    size_t vocab, size_t head_dim,
    size_t head, size_t kv_head,
    double floor, size_t q0, size_t q1,
    qk_pair_f64_t* out, size_t out_cap, int* overflow);

#ifdef __cplusplus
}
#endif
