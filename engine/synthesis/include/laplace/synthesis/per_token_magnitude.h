#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Per-row (per-token) L2 magnitude of a BF16 [rows x cols] row-major tensor.
 *
 *   out[r] = sqrt( Σ_c ( f32(tensor_bf16[r*cols + c]) )^2 )
 *
 * This is the exact, deterministic, parallel engine replacement for the scalar
 * C# WeightTensorETL.ReducePerCellMagnitude. Semantics match it bit-for-bit:
 *
 *   - BF16 → f32 decode is exact: (uint32_t)bits << 16, reinterpreted as float
 *     (matches C# (uint)bits << 16 + BitConverter.UInt32BitsToSingle).
 *   - Each product v*v is accumulated in f64.
 *   - The summation is the ONLY rounding step. It uses Neumaier compensated
 *     summation in a FIXED column order (c = 0 .. cols-1), so the result is
 *     identical regardless of thread count: each row's reduction is wholly
 *     independent and order-fixed, parallelism is only across rows.
 *   - out[r] = sqrt(sum).
 *
 * Determinism guarantee: for a given input, out is bit-identical (same IEEE-754
 * bit pattern per element) whether run with 1 thread or many. No GEMM, no
 * approximation, no top-k — every row is written.
 *
 *   tensor_bf16: [rows x cols] BF16, row-major (2 bytes/element).
 *   rows, cols:  tensor dimensions.
 *   out:         caller-allocated, length >= rows. out[r] receives the L2 norm
 *                of row r.
 *
 * Returns 0 on success, -1 on bad arguments (null pointer, or rows/cols == 0). */
int compute_per_token_l2_magnitude(const uint16_t* tensor_bf16,
                                   size_t rows, size_t cols,
                                   double* out /*[rows]*/);

#ifdef __cplusplus
}
#endif
