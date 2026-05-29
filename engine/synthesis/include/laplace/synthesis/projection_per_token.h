#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Per-token E·|W| projection: one weight tensor's per-token contribution.
 *
 * Exact, deterministic, parallel engine replacement for the scalar C#
 * WeightTensorETL.AggregateLayerThroughEmbed. Semantics match it bit-for-bit
 * up to the (more precise) compensated summation order documented below.
 *
 * Math (replicating AggregateLayerThroughEmbed exactly):
 *
 *   Step 1 — per-input-dim magnitude of |W|, summed over the output axis:
 *     perInDim[i] = Σ_{o=0..out_dim-1} | f64( f32(W_bf16[o*in_dim + i]) ) |
 *
 *   Step 2 — per token t (0 .. vocab-1):
 *     if (in_dim == d_model):   project E through perInDim
 *         out[t] = | Σ_{i=0..in_dim-1} f64( f32(E_bf16[t*d_model + i]) ) * perInDim[i] |
 *     else:                     uniform-distribution fallback (down_proj etc.,
 *                               where in_dim = intermediate_dim != d_model)
 *         out[t] = ( Σ_{i=0..in_dim-1} perInDim[i] ) / vocab
 *
 * This computes ONE tensor's contribution into out[vocab]; it does NOT
 * accumulate across layers (the caller does that).
 *
 * Exactness + determinism:
 *   - BF16 -> f32 decode is exact: (uint32_t)bits << 16 reinterpreted as float
 *     (matches C# (uint)bits << 16 + BitConverter.UInt32BitsToSingle).
 *   - All squares/abs/products are computed in f64.
 *   - Every summation (the only rounding step) uses Neumaier compensated
 *     summation in a FIXED order: step 1 over o = 0..out_dim-1, step 2 over
 *     i = 0..in_dim-1. Each perInDim[i] and each out[t] is a wholly
 *     independent, order-fixed reduction, so parallelism is only across the
 *     independent axis (i for step 1, t for step 2) and the result is
 *     bit-identical regardless of thread count.
 *   - No GEMM, no approximation, no top-k — every element of out is written.
 *
 *   E_bf16:  [vocab x d_model] BF16, row-major (2 bytes/element).
 *   vocab, d_model: embedding dimensions.
 *   W_bf16:  [out_dim x in_dim] BF16, row-major (2 bytes/element).
 *   out_dim, in_dim: weight dimensions.
 *   out:     caller-allocated, length >= vocab. out[t] receives token t's
 *            contribution from this tensor.
 *
 * Returns 0 on success, -1 on bad arguments (null pointer, or any of vocab,
 * d_model, out_dim, in_dim == 0). */
int compute_projection_per_token(const uint16_t* E_bf16, size_t vocab, size_t d_model,
                                 const uint16_t* W_bf16, size_t out_dim, size_t in_dim,
                                 double* out /*[vocab]*/);

#ifdef __cplusplus
}
#endif
