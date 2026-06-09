#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// out_vals receives the raw signed bilinear score L[i].R[j]. out_scores, when non-NULL,
// receives the int64 fixed-point Glicko score laplace_score_fp(v, 1.0) computed in-kernel,
// so the caller never round-trips per edge to score. Pass NULL to skip.
int bilinear_edges_tile(
    const double* left,  size_t row_begin, size_t row_end,
    const double* right, size_t n_right,
    size_t r, double theta,
    int* out_rows, int* out_cols, double* out_vals, long long* out_scores,
    size_t cap, size_t* out_count, int* overflow);

int project_embedding(const float* pts, size_t n, size_t d,
                      const float* W, size_t r, double* out);

// Like project_embedding but pts is already double — skips the float→double conversion
// of the (potentially large) embedding matrix.
int project_embedding_d(const double* pts, size_t n, size_t d,
                        const float* W, size_t r, double* out);

#ifdef __cplusplus
}
#endif
