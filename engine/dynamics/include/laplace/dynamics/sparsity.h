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

#ifdef __cplusplus
}
#endif
