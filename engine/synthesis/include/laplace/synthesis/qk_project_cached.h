#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/synthesis/qk_pairs_threshold.h"

#ifdef __cplusplus
extern "C" {
#endif

int project_qk_layer(
    const float* E_f32, size_t vocab, size_t d_model,
    const float* Wq, size_t n_heads,
    const float* Wk, size_t n_kv,
    size_t head_dim,
    double* q_cache_out, double* k_cache_out);

long score_qk_head_cached(
    const double* q_cache, size_t n_heads,
    const double* k_cache, size_t n_kv,
    size_t vocab, size_t head_dim,
    size_t head, size_t kv_head,
    double floor, size_t q0, size_t q1,
    qk_pair_f64_t* out, size_t out_cap, int* overflow);

#ifdef __cplusplus
}
#endif
