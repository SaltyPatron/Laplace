#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif




int bilinear_edges_tile(
    const double* left,  size_t row_begin, size_t row_end,
    const double* right, size_t n_right,
    size_t r, double theta,
    int* out_rows, int* out_cols, double* out_vals, long long* out_scores,
    size_t cap, size_t* out_count, int* overflow);

int project_embedding(const float* pts, size_t n, size_t d,
                      const float* W, size_t r, double* out);



int project_embedding_d(const double* pts, size_t n, size_t d,
                        const float* W, size_t r, double* out);

int norm_rows_d(double* data, size_t n, size_t dim);

int expand_kv_heads_d(const double* kv, size_t n, size_t n_heads, size_t n_kv,
                      size_t head_dim, double* out);

#ifdef __cplusplus
}
#endif
