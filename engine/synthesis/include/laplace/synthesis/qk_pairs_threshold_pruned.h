#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/synthesis/qk_pairs_threshold.h"  /* qk_pair_f64_t */

#ifdef __cplusplus
extern "C" {
#endif

/* Exact, deterministic, sub-quadratic QK token-relation scorer — a drop-in,
 * bit-identical replacement for compute_qk_pairs_above_threshold that scores
 * only a provably-sufficient subset of (query, key) pairs.
 *
 * SEMANTICS — identical to compute_qk_pairs_above_threshold (qk_pairs_threshold.h):
 *
 *   q_t[d] = Σ_{m=0..d_model-1} f64(E_f32[t*d_model + m]) * f64(Wq_head[d*d_model + m])
 *   k_s[d] = Σ_{m=0..d_model-1} f64(E_f32[s*d_model + m]) * f64(Wk_head[d*d_model + m])
 *   score(t,s) = Σ_{d=0..head_dim-1} q_t[d] * k_s[d]
 *
 * A pair (t, s, score) is emitted iff |score| > noise_floor. No scale, no top-k.
 * For any input this returns the SAME set, the SAME order (rows ascending by t,
 * within a row keys ascending by s), and the SAME f64 score bits as the
 * all-pairs kernel. The two functions are interchangeable.
 *
 * WHY IT IS SUB-QUADRATIC (and still EXACT — never drops a real survivor):
 *   By the Cauchy–Schwarz inequality |q_t·k_s| <= ‖q_t‖·‖k_s‖. A pair can only
 *   satisfy |score| > noise_floor if ‖q_t‖·‖k_s‖ > noise_floor, i.e. (for
 *   ‖q_t‖ > 0) ‖k_s‖ > noise_floor/‖q_t‖. We compute every key norm ‖k_s‖ once,
 *   sort keys by norm descending, and for each query only score the keys whose
 *   norm exceeds the per-query cutoff (a prefix of the sorted list, located by
 *   binary search). Keys below the cutoff PROVABLY cannot exceed the floor, so
 *   skipping them changes nothing. On realistic weights the surviving prefix is
 *   tiny, so the work is O(vocab·log vocab + (#candidates)·head_dim·d_model).
 *
 *   Crucially, the candidates that ARE scored are scored with the *identical*
 *   compensated-f64 arithmetic and fixed summation order as the all-pairs
 *   kernel, so every emitted score is bit-for-bit equal. The pruning only
 *   removes pairs the all-pairs kernel would also have rejected.
 *
 * DETERMINISM — same contract as the sibling:
 *   All arithmetic is f64; the only rounding steps (the q/k dot products and the
 *   score) are Neumaier-compensated in a fixed order (m = 0..d_model-1,
 *   d = 0..head_dim-1) — identical to the sibling. Key norms are computed in
 *   fixed key order. Survivors within a query row are emitted in ascending
 *   key-index order (the candidate prefix is re-sorted by key index before the
 *   threshold test), so the emitted order does not depend on the norm-sorted
 *   scan order. Output slots are assigned by a deterministic prefix-sum of
 *   per-row survivor counts (a fixed pass-1, independent of thread count), so
 *   both the emitted set AND its order are bit-identical regardless of thread
 *   count or window boundaries. TBB parallelism is only ACROSS query rows.
 *
 * STREAMING / BOUNDED MEMORY — same contract as the sibling:
 *   The caller drives bounded query-row windows [q0, q1) of ONE head. On
 *   overflow the kernel sets *overflow = 1, writes the deterministic prefix of
 *   pairs that fit (a whole number of leading query rows — never a partial row),
 *   and returns that prefix's count. Beyond the caller buffer the kernel
 *   allocates O(vocab) for the key-norm table (shared, computed once) plus
 *   O(head_dim) per worker and O(q1-q0) row metadata — never O(vocab·vocab) or
 *   O(vocab·k).
 *
 *   Parameters and return value are IDENTICAL to compute_qk_pairs_above_threshold.
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
long compute_qk_pairs_above_threshold_pruned(
    const float* E_f32, size_t vocab, size_t d_model,
    const float* Wq_head, const float* Wk_head, size_t head_dim,
    double noise_floor, size_t q0, size_t q1,
    qk_pair_f64_t* out, size_t out_cap, int* overflow);

#ifdef __cplusplus
}
#endif
