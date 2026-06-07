#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

int64_t laplace_score_fp(double v, double m);

double laplace_score_inverse_fp(int64_t score_fp, double m);

void laplace_score_batch_fp(const float* w, size_t n, double m, int64_t* out);

#ifdef __cplusplus
}
#endif
