#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif






















int ffn_token_pairs_tile(
    const double* emb,   size_t n, size_t d,
    const double* unemb,
    const double* gate, const double* up, const double* down, size_t interm,
    size_t row_begin, size_t row_end,
    double theta,
    int* out_rows, int* out_cols, double* out_vals, long long* out_scores,
    size_t cap, size_t* out_count, int* overflow);

#ifdef __cplusplus
}
#endif
