#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Substrate-native interior-tensor reconstruction (ADR 0056 corrected codec).
 *
 * Solves for a weight matrix W such that the static-bilinear that produced
 * the substrate's token-pair attestations is recovered up to gauge:
 *
 *     S[i,j] ≈ Σ_k_pair E[i] · M · E[j]ᵀ                     (in general)
 *
 *   For SELF-BILINEAR kinds (V_PROJECTS, O_PROJECTS, GATES, UP_PROJECTS,
 *   DOWN_PROJECTS — substrate aggregates layer/head/expert per ADR 0056
 *   Phase 2): M = Wᵀ·W → S is symmetric → recover ONE W via
 *   `reconstruct_w_from_token_pair_attestations`. The result fills
 *   that kind's tensor at the target out_dim.
 *
 *   For JOINT-BILINEAR kinds (Q_PROJECTS — substrate aggregates the joint
 *   Q·K matchup): M = Wqᵀ·Wk is non-symmetric → recover BOTH Wq and Wk via
 *   `reconstruct_qk_from_token_pair_attestations`. Lets TinyLlama GQA
 *   (Wq=[2048×2048], Wk=[256×2048] — different shapes) reconstruct
 *   correctly instead of collapsing to symmetric Wq=Wk.
 *
 * Inputs:
 *   E             — [vocab × N] row-major double; freshly-synthesized
 *                   embedding from spectral embedding of substrate's
 *                   typed-edge graph.
 *   s_rows/cols   — length s_nnz; substrate-source COO triples for the
 *                   kind-specific subgraph (subject_idx, object_idx).
 *   s_weights     — length s_nnz; Glicko-2 effective-μ aggregated across
 *                   sources (zero-or-negative weights dropped internally).
 *   out_dim       — target W shape [out_dim × N].
 *   lambda        — Tikhonov regularization for the EᵀE + λI inverse;
 *                   typical 1e-3 .. 1e-2 depending on E's condition.
 *
 * Outputs:
 *   W_out         — [out_dim × N] row-major float (cast from double for
 *                   storage; GGUF/safetensors carry the value in f32/bf16).
 *
 * Returns:
 *    0   success.
 *   -1   null input.
 *   -2   invalid arguments (vocab == 0, N == 0, out_dim == 0, etc.).
 *   -3   eigensolver / SVD did not converge.
 *   -4   degenerate (e.g., E is rank-zero — substrate had no signal). */
int reconstruct_w_from_token_pair_attestations(
    const double* E, size_t vocab, size_t N,
    const int*    s_rows, const int* s_cols, const double* s_weights, size_t s_nnz,
    size_t        out_dim, double lambda,
    float*        W_out);

/* Asymmetric (joint-bilinear) factorization: S ≈ E·Wqᵀ·Wk·Eᵀ. Recovers
 * BOTH Wq [out_dim_q × N] AND Wk [out_dim_k × N]. Used for Q_PROJECTS
 * where Wq and Wk have different shapes under GQA. Same error codes. */
int reconstruct_qk_from_token_pair_attestations(
    const double* E, size_t vocab, size_t N,
    const int*    s_rows, const int* s_cols, const double* s_weights, size_t s_nnz,
    size_t        out_dim_q, size_t out_dim_k, double lambda,
    float*        Wq_out, float* Wk_out);

#ifdef __cplusplus
}
#endif
