#ifndef LAPLACE_CORE_GLICKO2_INTERNAL_H
#define LAPLACE_CORE_GLICKO2_INTERNAL_H

#include "laplace/core/glicko2.h"

#include <stddef.h>
#include <stdint.h>

#define LAPLACE_FP_ONE      1000000000LL
#define LAPLACE_FP_HALF      500000000LL
#define LAPLACE_FP_LN2       693147181LL
#define LAPLACE_FP_LN2_HALF  346573590LL
#define LAPLACE_FP_PI       3141592654LL
#define LAPLACE_FP_PI_SQ    9869604401LL

#define LAPLACE_FP_BASE_RATING   LAPLACE_GLICKO2_NEUTRAL_MU_FP
#define LAPLACE_FP_RATING_SCALE   173717800000LL
#define LAPLACE_FP_RD_MAX         350000000000LL

static inline int64_t clamp_i128(__int128 value)
{
    if (value > (__int128)INT64_MAX) return INT64_MAX;
    if (value < (__int128)INT64_MIN) return INT64_MIN;
    return (int64_t)value;
}

static inline int64_t sat_add_i64(int64_t a, int64_t b)
{
    return clamp_i128((__int128)a + (__int128)b);
}

static inline int64_t sat_sub_i64(int64_t a, int64_t b)
{
    return clamp_i128((__int128)a - (__int128)b);
}

static inline int64_t sat_neg_i64(int64_t value)
{
    return value == INT64_MIN ? INT64_MAX : -value;
}

static inline int64_t sat_scale_i64(int64_t value, int64_t multiplier)
{
    return clamp_i128((__int128)value * (__int128)multiplier);
}

static inline int glicko2_state_is_admissible(const glicko2_state_t *st)
{
    return st != NULL &&
           (__int128)st->rating - 2 * (__int128)st->rd > INT64_MIN &&
           (__int128)st->rating + 2 * (__int128)st->rd < INT64_MAX &&
           st->rd > 0 && st->rd <= LAPLACE_FP_RD_MAX &&
           st->volatility > 0 && st->observation_count >= 0;
}

static inline int64_t g1_to_mu(int64_t r)
{
    return laplace_fp_div(sat_sub_i64(r, LAPLACE_FP_BASE_RATING),
                          LAPLACE_FP_RATING_SCALE);
}

static inline int64_t g1_to_phi(int64_t rd)
{
    return laplace_fp_div(rd, LAPLACE_FP_RATING_SCALE);
}

static inline int64_t phi_to_g1(int64_t phi)
{
    return laplace_fp_mul(phi, LAPLACE_FP_RATING_SCALE);
}

int laplace_glicko2_finish_period(glicko2_state_t* st,
                                  int64_t mu, int64_t phi, int64_t phi_sq,
                                  int64_t sigma, int64_t v_inv,
                                  int64_t delta_inner, size_t n,
                                  int64_t tau, int64_t now_ns,
                                  glicko2_trace_t* trace);

#endif
