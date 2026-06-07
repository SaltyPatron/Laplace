#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint32_t query_idx;
    uint32_t key_idx;
    double   score;
} qk_pair_f64_t;

long compute_qk_pairs_above_threshold(
    const float* E_f32, size_t vocab, size_t d_model,
    const float* Wq_head, const float* Wk_head, size_t head_dim,
    double noise_floor, size_t q0, size_t q1,
    qk_pair_f64_t* out, size_t out_cap, int* overflow);

#ifdef __cplusplus
}
#endif
