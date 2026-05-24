#pragma once

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Lottery-ticket-aware sparsity filter (per RULES.md R3 + ADR 0007).
 *
 * Three-pass filter for AI-model tensor ingestion:
 *   1. Per-tensor relative top-k% (respects tensor's own magnitude regime)
 *   2. Per-row top-k for attention/MLP (preserves load-bearing IO connectivity)
 *   3. Probe-validated retention test (synthesize candidate sparse subgraph;
 *      verify behavior preserved on probe set)
 *
 * Flat numeric thresholds are FORBIDDEN per R3.
 *
 * Linguistic resources do NOT use this filter — every entry at full
 * fidelity per RULES.md R3. */

typedef struct {
    double per_tensor_topk_pct;  /* e.g., 0.05 = top 5% by importance */
    size_t per_row_topk;          /* e.g., 8 per row for attention/MLP */
    /* Probe validation params land Chunk 6 Story 6.12. */
} sparsity_params_t;

/* Apply pass 1: per-tensor relative top-k%. Marks the top fraction of
 * |weights| as retained (out_mask[i] = 1) per the params. */
int sparsity_per_tensor_topk(const double*            weights,
                             size_t                   n,
                             const sparsity_params_t* params,
                             uint8_t*                 out_mask);

/* Apply pass 2: per-row top-k for a 2D tensor (rows × cols, row-major).
 * Refines the mask from pass 1: a position survives only if it's in
 * both the per-tensor top-k% AND its row's top-k. */
int sparsity_per_row_topk(const double*            weights,
                          size_t                   rows,
                          size_t                   cols,
                          const sparsity_params_t* params,
                          uint8_t*                 inout_mask);

/* Apply pass 3: probe-validated retention. Lands Chunk 6 Story 6.12 —
 * runs a probe forward pass against the candidate sparse subgraph and
 * keeps weights whose ablation affects probe behavior. */
int sparsity_probe_validate(const double*            weights,
                            size_t                   n,
                            const sparsity_params_t* params,
                            uint8_t*                 inout_mask);

/* === Streaming variants (per Story B.1 + B.2 / Framework Epic #232) ===
 *
 * These are SINGLE-PASS streaming versions of pass-1 and pass-2 that the
 * future WeightTensorETL (ADR 0056 / #231) calls during model ingest.
 * They differ from the multi-pass functions above:
 *   - No `sparsity_params_t` (caller passes the single relevant scalar)
 *   - Out-of-place mask (no inout dependence on a prior pass)
 *   - TBB-parallel inner loops (deterministic per MKL_CBWR set at init)
 *   - Independent of probe-validate (which never lands per ADR 0056)
 *
 * Both functions are deterministic: same input + same MKL_CBWR mode →
 * byte-identical mask across thread counts (RULES R7). */

/* Per-tensor relative top-k% over a flat double buffer.
 *
 * Selects the indices i such that |values[i]| is in the top fraction
 * (topk_pct in (0, 1]) by absolute magnitude. Output mask[i] = 1 for
 * retained, 0 for pruned. The retained count is ceil(n * topk_pct);
 * ties at the threshold are all retained (so actual count may slightly
 * exceed the target — acceptable for top-k% semantics).
 *
 * Algorithm:
 *   1. Materialize |values| into a temp buffer (single MKL VML vdAbs pass
 *      when LAPLACE_HAS_MKL; std::fabs loop otherwise; auto-vectorizable).
 *   2. Find threshold via std::nth_element (deterministic; O(n) average).
 *   3. Mask pass: mask[i] = (|values[i]| >= threshold) (TBB parallel_for
 *      across chunks for n >= 65536; serial otherwise — partitioning is
 *      order-independent so determinism holds across thread counts).
 *
 * Returns 0 on success; non-zero on invalid args (null, n=0,
 * topk_pct out of (0, 1]). */
int sparsity_per_tensor_topk_streaming(
    const double* values,
    size_t        n,
    double        topk_pct,
    uint8_t*      out_mask);

/* Per-row top-k over a row-major 2D tensor.
 *
 * For each of `row_count` rows of length `row_size`, selects the k indices
 * with the largest |value|. Output: out_masks is row_count * row_size
 * bytes; out_masks[r*row_size + c] = 1 iff column c is in row r's top-k.
 *
 * TBB parallel_for across rows when LAPLACE_HAS_MKL; serial otherwise.
 * Determinism: per-row nth_element is deterministic; the cross-row
 * parallelism is order-independent (each row's mask depends only on that
 * row's data). MKL_CBWR mode is irrelevant here (no MKL reductions);
 * the streaming variant is deterministic by structure.
 *
 * If k >= row_size, every column is retained for that row (entire row
 * mask = 1). If k == 0, every column is pruned.
 *
 * Returns 0 on success; non-zero on invalid args. */
int sparsity_per_row_topk_streaming(
    const double* rows,
    size_t        row_count,
    size_t        row_size,
    size_t        k,
    uint8_t*      out_masks);

#ifdef __cplusplus
}
#endif
