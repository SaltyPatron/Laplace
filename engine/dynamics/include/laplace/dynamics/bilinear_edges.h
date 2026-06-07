#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

int bilinear_edges_tile(
    const double* left,  size_t row_begin, size_t row_end,
    const double* right, size_t n_right,
    size_t r, double theta,
    int* out_rows, int* out_cols, double* out_vals,
    size_t cap, size_t* out_count, int* overflow);

int project_embedding(const float* pts, size_t n, size_t d,
                      const float* W, size_t r, double* out);

#ifdef __cplusplus
}
#endif
