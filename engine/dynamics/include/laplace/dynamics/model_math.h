#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Model-lane matrix transforms (doc 06 Rule #1: native does the heavy lifting;
 * C# orchestrates). All row-major. TBB-parallel over rows where the work is
 * not already inside one MKL call. */

/* Subtract each column's mean in place. */
int center_columns_d(double* m, size_t n, size_t d);
int center_columns_f(float* m, size_t n, size_t d);

/* True per-row LayerNorm in place: x = (x-mean)/sqrt(var+eps)*gamma + beta.
 * beta may be NULL (gain-only). Data-dependent per row — never foldable into
 * weight columns. */
int layer_norm_rows_d(double* m, size_t n, size_t d,
                      const float* gamma, const float* beta, double eps);

/* Broadcast add of a float row-vector to every row of m, in place
 * (projection biases; additive embedding terms). */
int add_row_vector_d(double* m, size_t n, size_t d, const float* v);

/* out[i] = sqrt(a[i]^2 + b[i]^2). */
int hypot_rows_d(const double* a, const double* b, size_t n, double* out);

/* Multiply each column j by g[j], in place. */
int scale_cols_f(float* m, size_t rows, size_t d, const float* g);
int scale_cols_d(double* m, size_t rows, size_t d, const float* g);

/* Copy head h's contiguous [hd] slice out of each row of an [n x fullDim]
 * matrix into an [n x hd] matrix. */
int slice_head_d(const double* full, double* head,
                 size_t n, size_t full_dim, size_t h, size_t hd);

/* L2 norm of each row into out[n] (does not modify m). */
int row_norms_out_d(const double* m, size_t n, size_t d, double* out);

/* Widen float32 to float64. */
int f32_to_f64(const float* src, size_t count, double* dst);

/* Per-token gated-FFN activation norms: out[i] = || silu(gate·x_i) ⊙ (up·x_i) ||
 * (gate NULL => ungated: || up·x_i ||). x is [n x d], up/gate are [interm x d]
 * float32. GEMM tiles inside (MKL), row-parallel combine. */
int ffn_activation_norms(const double* x, size_t n, size_t d,
                         const float* up, const float* gate, size_t interm,
                         double* out_norms);

#ifdef __cplusplus
}
#endif
