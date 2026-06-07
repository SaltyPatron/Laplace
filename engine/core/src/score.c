#include "laplace/core/score.h"

#include <math.h>
#include <stddef.h>
#include <stdint.h>

int64_t laplace_score_fp(double v, double m) {
    double s = 0.5 * (1.0 + v / (m + fabs(v)));
    return (int64_t)llround(s * 1e9);
}

double laplace_score_inverse_fp(int64_t score_fp, double m) {
    double u = (double)score_fp / 1e9 * 2.0 - 1.0;
    if (u > 1.0 - 1e-12) u = 1.0 - 1e-12;
    if (u < -(1.0 - 1e-12)) u = -(1.0 - 1e-12);
    return m * u / (1.0 - fabs(u));
}

void laplace_score_batch_fp(const float* w, size_t n, double m, int64_t* out) {
    for (size_t i = 0; i < n; ++i) {
        double v = (double)w[i];
        double s = 0.5 * (1.0 + v / (m + fabs(v)));
        out[i] = (int64_t)llround(s * 1e9);
    }
}
