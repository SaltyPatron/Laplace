#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Architecture template — per-architecture knowledge of how to
 * materialize tensors from substrate state (per ADR 0011 plugin
 * interface IArchitectureTemplate + DESIGN.md VI).
 *
 * Substrate-specific invention (no upstream provides this) — opaque
 * handle, internal C++ implementation per template. Initial templates:
 *   - LlamaTemplate (Chunk 7 Story 7.2; handles Llama / Qwen family)
 *
 * Per RULES.md R10: adding a new architecture = one new template
 * plugin; never touches schema/query/synthesis core. */
typedef struct arch_template arch_template_t;

/* Load an architecture template by name (e.g., "llama", "mamba",
 * "diffusion"). Returns NULL if no template registered with that name. */
arch_template_t* arch_template_load(const char* template_name);

/* Tensor specification (shape + dtype + role) returned by the template
 * when asked what tensors a recipe requires. Substrate-specific
 * concept — no upstream equivalent. */
typedef struct {
    const char* name;        /* canonical tensor name in the architecture */
    size_t      rank;        /* number of dimensions */
    size_t      shape[8];    /* up to 8 dims supported initially */
    int         dtype;       /* enum: 0=fp32, 1=fp16, 2=bf16, 3=q8, 4=q4, ... */
} tensor_spec_t;

/* Get the list of tensors this template needs to materialize for a
 * given recipe. Caller allocates `out_specs` array; returns count
 * written, or -1 if `cap` too small. */
int arch_template_required_tensors(const arch_template_t* tmpl,
                                   const void*            recipe,  /* recipe_t* */
                                   tensor_spec_t*         out_specs,
                                   size_t                 cap);

void arch_template_free(arch_template_t* t);

/* Substrate consensus value bundle the template uses to materialize a tensor.
 * Per ADR 0056:183 + DESIGN.md:660: the architecture template distributes
 * consensus values across the recipe's per-(layer, head, dim) layout — NOT
 * a pseudoinverse recovery problem.
 *
 * Fields:
 *   per_token_consensus  — [vocab] doubles, one per token. For unary kinds
 *                          (EMBEDS / V_PROJECTS / O_PROJECTS / GATES /
 *                          UP_PROJECTS / DOWN_PROJECTS / OUTPUT_PROJECTS):
 *                          the per-token aggregated Glicko-2 effective-mu
 *                          consensus for that kind.
 *   per_pair_rows/cols/vals/nnz — sparse COO for binary kinds (Q_PROJECTS).
 *                                  Each (row, col) = (token_i, token_j) and
 *                                  val = consensus for (token_i, kind, token_j).
 *   vocab                — vocab size (= per_token_consensus length).
 *   norm_aggregate       — single scalar for NORMALIZES (unary on model
 *                          recipe entity).
 *   token_basis          — [vocab × basis_dim] doubles row-major; the
 *                          substrate's spectral embedding of tokens per
 *                          ADR 0056:50 (legitimate substrate-native
 *                          behavioral-robustness mechanism). Used by the
 *                          template to project per-token consensus into
 *                          the recipe's [hidden_dim, hidden_dim] shape.
 *                          May be NULL if not yet computed.
 *   basis_dim            — width of token_basis (= hidden_dim per recipe).
 */
typedef struct {
    const double* per_token_consensus;
    size_t        vocab;
    const int*    per_pair_rows;
    const int*    per_pair_cols;
    const double* per_pair_vals;
    size_t        per_pair_nnz;
    double        norm_aggregate;
    const double* token_basis;
    size_t        basis_dim;
    /* Precomputed gram matrices for interior tensor materialization.
     * unary_gram[basis_dim × basis_dim]  = E^T · diag(per_token_consensus) · E
     * binary_gram[basis_dim × basis_dim] = E^T · S_qk · E  (S_qk = sparse Q_PROJECTS adj)
     * NULL when not yet computed; materialize falls back to constant fill. */
    const double* unary_gram;
    const double* binary_gram;
} substrate_view_t;

/* Compute gram matrices for efficient interior tensor materialization.
 * unary_gram[basis_dim × basis_dim]  = E^T · diag(per_token) · E
 * binary_gram[basis_dim × basis_dim] = E^T · S_qk · E
 * Both outputs are caller-allocated (basis_dim * basis_dim doubles each).
 * Requires MKL (LAPLACE_HAS_MKL). Returns 0 ok, -1 null input, -2 no MKL. */
int compute_substrate_gram(
    const double* token_basis,
    const double* per_token,
    size_t        vocab,
    size_t        basis_dim,
    const int*    qk_rows,
    const int*    qk_cols,
    const double* qk_vals,
    size_t        nnz,
    double*       unary_gram,
    double*       binary_gram);

/* Materialize one tensor's values from substrate consensus per the
 * architecture template's per-tensor distribution policy (per ADR 0056:183
 * + DESIGN.md:660). The template knows for each tensor: which kind's
 * consensus feeds it, whether it's a token-axis tensor (embed_tokens,
 * lm_head, norms) or a [hidden_dim, hidden_dim]-shape interior tensor,
 * and how the recipe layout distributes consensus across the slot.
 *
 * For Llama-family:
 *   token_embd.weight [vocab × hidden]   ← outer(per_token_consensus, token_basis_row)
 *   output.weight     [vocab × hidden]   ← per_token_consensus broadcast through basis
 *   blk.L.attn_q.weight [h*hd × hidden]  ← E^T @ (Q_PROJECTS sparse adj) @ E projected
 *   blk.L.attn_v.weight [kv*hd × hidden] ← per-token_consensus[V_PROJECTS] broadcast
 *   blk.L.ffn_gate.weight [interm × hidden] ← per-token consensus[GATES] broadcast
 *   ... etc.
 *   *_norm.weight [hidden]               ← norm_aggregate normalized per dim
 *
 * The exact broadcast is the recipe-layout distribution per ADR 0056:183
 * ("the inverse of this aggregation" = broadcast per recipe shape, not SVD
 * pseudoinverse — see Memory `project_model_decomposer_attestation_insight`).
 *
 * out_values is caller-allocated; size = product(spec.shape) * dtype_size(spec.dtype).
 * Returns 0 on success, -1 on null input, -2 on shape/template mismatch,
 * -3 on substrate view incompatibility (e.g. basis_dim != hidden_dim).
 */
int arch_template_materialize_tensor(const arch_template_t*  tmpl,
                                     const tensor_spec_t*    spec,
                                     const substrate_view_t* view,
                                     void*                   out_values);

#ifdef __cplusplus
}
#endif
