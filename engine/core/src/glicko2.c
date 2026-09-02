#include "laplace/core/glicko2.h"

#include <math.h>
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

/*
 * The old fold let ordinary signed-int64 arithmetic surround a handful of
 * __int128 intermediates.  Once a rating escaped the normal Glicko range those
 * edges were enough to invoke signed overflow/UB and turn one bad transition
 * into a runaway carrier.  Keep the public fixed-point surface total instead:
 * every carrier operation has a deterministic saturating result, while the
 * stateful Glicko entry points still reject an illegal state rather than
 * publishing saturation as a rating.
 */
static int64_t clamp_i128(__int128 value)
{
    if (value > (__int128)INT64_MAX) return INT64_MAX;
    if (value < (__int128)INT64_MIN) return INT64_MIN;
    return (int64_t)value;
}

static int64_t sat_add_i64(int64_t a, int64_t b)
{
    return clamp_i128((__int128)a + (__int128)b);
}

static int64_t sat_sub_i64(int64_t a, int64_t b)
{
    return clamp_i128((__int128)a - (__int128)b);
}

static int64_t sat_neg_i64(int64_t value)
{
    return value == INT64_MIN ? INT64_MAX : -value;
}

static int64_t sat_abs_i64(int64_t value)
{
    if (value >= 0) return value;
    return value == INT64_MIN ? INT64_MAX : -value;
}

static int64_t sat_scale_i64(int64_t value, int64_t multiplier)
{
    return clamp_i128((__int128)value * (__int128)multiplier);
}

/* Illinois' secant correction is (B-A)*f(A)/(f(A)-f(B)).  All three
 * operands are already Q1e9, so multiplying with laplace_fp_mul first throws
 * away another 1e9 of precision before the division.  Near the root that made
 * a non-zero correction round to zero and the solver bounce between two
 * representable points until its iteration guard fired.  Divide the wide
 * product directly and round only once. */
static int64_t rounded_ratio_i128(__int128 numerator, int64_t denominator)
{
    if (denominator == 0)
        return numerator >= 0 ? INT64_MAX : INT64_MIN;

    __int128 den = (__int128)denominator;
    const int negative = ((numerator < 0) ^ (den < 0));
    if (numerator < 0) numerator = -numerator;
    if (den < 0) den = -den;
    __int128 q = (numerator + den / 2) / den;
    if (negative) q = -q;
    return clamp_i128(q);
}

static int glicko2_state_is_admissible(const glicko2_state_t *st)
{
    return st != NULL && st->rd > 0 && st->rd <= LAPLACE_FP_RD_MAX &&
           st->volatility > 0 && st->observation_count >= 0;
}

int64_t laplace_fp_mul(int64_t a, int64_t b)
{
    __int128 prod = (__int128)a * (__int128)b;
    __int128 rounded;
    if (prod >= 0)
        rounded = (prod + (LAPLACE_FP_ONE / 2)) / LAPLACE_FP_ONE;
    else
        rounded = -(((-prod) + (LAPLACE_FP_ONE / 2)) / LAPLACE_FP_ONE);
    return clamp_i128(rounded);
}

int64_t laplace_fp_div(int64_t a, int64_t b)
{
    if (b == 0)
        return (a >= 0) ? INT64_MAX : INT64_MIN;

    /* Do not form -INT64_MIN in int64.  Both magnitudes live in int128 until
     * the rounded quotient has been range-checked. */
    __int128 aa = (__int128)a;
    __int128 bb = (__int128)b;
    const int negative = ((a < 0) ^ (b < 0));
    if (aa < 0) aa = -aa;
    if (bb < 0) bb = -bb;
    __int128 q = (aa * (__int128)LAPLACE_FP_ONE + bb / 2) / bb;
    if (negative) q = -q;
    return clamp_i128(q);
}

static uint64_t isqrt_u128(__int128 n)
{
    if (n <= 0) return 0;
    uint64_t hi = (uint64_t)(n >> 64);
    uint64_t lo = (uint64_t)n;
    int bits = 0;
    if (hi) {
        bits = 64;
        uint64_t t = hi;
        while (t) { bits++; t >>= 1; }
    } else {
        uint64_t t = lo;
        while (t) { bits++; t >>= 1; }
    }
    __int128 x = (__int128)1 << ((bits + 1) / 2);
    for (int i = 0; i < 80; ++i) {
        __int128 y = (x + n / x) >> 1;
        if (y >= x) break;
        x = y;
    }
    return (uint64_t)x;
}

int64_t laplace_fp_sqrt(int64_t x)
{
    if (x <= 0) return 0;
    __int128 scaled = (__int128)x * (__int128)LAPLACE_FP_ONE;
    return (int64_t)isqrt_u128(scaled);
}

int64_t laplace_fp_exp(int64_t x)
{
    /*
     * Q1e9 spends roughly 30 carrier bits on 1.0.  Range reduction therefore
     * cannot treat the reduced mantissa as an unscaled integer: shifting a
     * ~1e9 result left by only ~34 bits already crosses signed int64.
     *
     * Clamp BEFORE range-reduction arithmetic so INT64_MIN/INT64_MAX cannot
     * overflow in -x or x + ln(2)/2.  Positive overflow saturates with one
     * Q1e9 unit of headroom because the logistic computes 1 + exp(x).
     * Negative underflow saturates to the smallest positive Q1e9 value: exp(x)
     * is strictly positive, and a zero here can become an illegal Glicko
     * volatility even though PostgreSQL correctly requires volatility > 0.
     */
    const int64_t saturation = INT64_MAX - LAPLACE_FP_ONE;
    const int64_t min_positive = 1;
    const int64_t range_limit = 60LL * LAPLACE_FP_ONE;
    if (x >= range_limit) return saturation;
    if (x <= -range_limit) return min_positive;

    int64_t k;
    if (x >= 0)
        k = (x + LAPLACE_FP_LN2_HALF) / LAPLACE_FP_LN2;
    else
        k = -(((-x) + LAPLACE_FP_LN2_HALF) / LAPLACE_FP_LN2);
    int64_t r = x - k * LAPLACE_FP_LN2;

    int64_t result = LAPLACE_FP_ONE;
    int64_t term   = LAPLACE_FP_ONE;
    for (int n = 1; n <= 14; ++n) {
        term = laplace_fp_mul(term, r) / n;
        result = sat_add_i64(result, term);
        if (term > -10 && term < 10) break;
    }

    if (result <= 0) return min_positive;

    if (k > 0) {
        if (k >= 63) return saturation;
        if (result > (saturation >> k)) return saturation;
        result <<= k;
    } else if (k < 0) {
        int64_t shift = -k;
        if (shift >= 63) return min_positive;
        int64_t add = INT64_C(1) << (shift - 1);
        result = (result + add) >> shift;
    }
    if (result < min_positive) return min_positive;
    return result > saturation ? saturation : result;
}

int64_t laplace_fp_log(int64_t x)
{
    if (x <= 0) return INT64_MIN / 2;

    int64_t y = x;
    int k = 0;
    while (y >= 2 * LAPLACE_FP_ONE) { y >>= 1; ++k; }
    while (y < LAPLACE_FP_ONE)      { y <<= 1; --k; }

    int64_t num = y - LAPLACE_FP_ONE;
    int64_t den = y + LAPLACE_FP_ONE;
    int64_t u   = laplace_fp_div(num, den);
    int64_t u2  = laplace_fp_mul(u, u);

    int64_t term = u;
    int64_t sum = term;
    for (int i = 3; i <= 31; i += 2) {
        term = laplace_fp_mul(term, u2);
        int64_t inc = term / i;
        sum = sat_add_i64(sum, inc);
        if (inc > -1 && inc < 1) break;
    }
    int64_t ln_m = sat_scale_i64(sum, 2);
    return sat_add_i64(sat_scale_i64((int64_t)k, LAPLACE_FP_LN2), ln_m);
}

void glicko2_init(glicko2_state_t* st,
                  int64_t r0,
                  int64_t rd0,
                  int64_t vol0)
{
    if (!st) return;
    st->rating                    = r0;
    st->rd                        = rd0;
    st->volatility                = vol0;
    st->last_observed_at_unix_ns  = 0;
    st->observation_count         = 0;
}

static int64_t g1_to_mu(int64_t r)
{
    return laplace_fp_div(sat_sub_i64(r, LAPLACE_FP_BASE_RATING),
                          LAPLACE_FP_RATING_SCALE);
}

static int64_t g1_to_phi(int64_t rd)
{
    return laplace_fp_div(rd, LAPLACE_FP_RATING_SCALE);
}

static int64_t mu_to_g1(int64_t mu)
{
    return sat_add_i64(laplace_fp_mul(mu, LAPLACE_FP_RATING_SCALE),
                       LAPLACE_FP_BASE_RATING);
}

static int64_t phi_to_g1(int64_t phi)
{
    return laplace_fp_mul(phi, LAPLACE_FP_RATING_SCALE);
}

int64_t laplace_glicko2_g(int64_t phi)
{
    int64_t phi_sq = laplace_fp_mul(phi, phi);
    int64_t three_phi_sq = sat_scale_i64(phi_sq, 3);
    int64_t denom_inside = sat_add_i64(
        LAPLACE_FP_ONE,
        laplace_fp_div(three_phi_sq, LAPLACE_FP_PI_SQ));
    int64_t denom = laplace_fp_sqrt(denom_inside);
    return laplace_fp_div(LAPLACE_FP_ONE, denom);
}

int64_t laplace_glicko2_E(int64_t mu, int64_t mu_j, int64_t g_j)
{
    int64_t difference = sat_sub_i64(mu, mu_j);
    int64_t arg = sat_neg_i64(laplace_fp_mul(g_j, difference));
    int64_t ex = laplace_fp_exp(arg);
    int64_t denom = sat_add_i64(LAPLACE_FP_ONE, ex);
    int64_t result = laplace_fp_div(LAPLACE_FP_ONE, denom);
    if (result < 0) return 0;
    if (result > LAPLACE_FP_ONE) return LAPLACE_FP_ONE;
    return result;
}

static int64_t illinois_f(int64_t x,
                          int64_t delta_sq,
                          int64_t phi_sq,
                          int64_t v,
                          int64_t a,
                          int64_t tau_sq)
{
    int64_t ex = laplace_fp_exp(x);
    int64_t denom_inner = sat_add_i64(sat_add_i64(phi_sq, v), ex);
    int64_t denom_square = laplace_fp_mul(denom_inner, denom_inner);
    int64_t denom = sat_scale_i64(denom_square, 2);
    int64_t inner = sat_sub_i64(
        sat_sub_i64(sat_sub_i64(delta_sq, phi_sq), v), ex);
    int64_t num = laplace_fp_mul(ex, inner);
    int64_t lhs = laplace_fp_div(num, denom);
    int64_t rhs = laplace_fp_div(sat_sub_i64(x, a), tau_sq);
    return sat_sub_i64(lhs, rhs);
}

static int glicko2_finish_period(glicko2_state_t* st,
                                 int64_t mu, int64_t phi, int64_t phi_sq,
                                 int64_t sigma, int64_t v_inv,
                                 int64_t delta_inner, size_t n,
                                 int64_t tau, int64_t now_ns,
                                 glicko2_trace_t* trace)
{
    if (!st || sigma <= 0 || tau <= 0 ||
        n > (size_t)INT64_MAX || st->observation_count < 0 ||
        st->observation_count > INT64_MAX - (int64_t)n)
        return -1;

    if (v_inv <= 0) v_inv = 1;
    int64_t v = laplace_fp_div(LAPLACE_FP_ONE, v_inv);
    if (v <= 0) v = 1;
    int64_t delta = laplace_fp_mul(v, delta_inner);
    int64_t delta_sq = laplace_fp_mul(delta, delta);

    int64_t sigma_sq = laplace_fp_mul(sigma, sigma);
    int64_t tau_sq = laplace_fp_mul(tau, tau);
    if (sigma_sq <= 0 || tau_sq <= 0) return -1;
    int64_t a = laplace_fp_log(sigma_sq);

    int64_t A = a;
    int64_t B;
    int64_t phi_v = sat_add_i64(phi_sq, v);
    if (delta_sq > phi_v) {
        int64_t distance = sat_sub_i64(delta_sq, phi_v);
        if (distance <= 0) return -1;
        B = laplace_fp_log(distance);
    } else {
        int k = 1;
        for (;;) {
            int64_t step = clamp_i128((__int128)k * (__int128)tau);
            B = sat_sub_i64(a, step);
            if (illinois_f(B, delta_sq, phi_sq, v, a, tau_sq) >= 0)
                break;
            if (++k > 100) return -1;
        }
    }

    int64_t fA = illinois_f(A, delta_sq, phi_sq, v, a, tau_sq);
    int64_t fB = illinois_f(B, delta_sq, phi_sq, v, a, tau_sq);

    int iter_count = 0;
    for (; iter_count < 100; ++iter_count) {
        int64_t diff = sat_sub_i64(B, A);
        if (sat_abs_i64(diff) <= LAPLACE_GLICKO2_ILLINOIS_EPS) break;
        int64_t f_span = sat_sub_i64(fA, fB);
        if (f_span == 0) return -1;

        /* Keep the secant numerator wide until the division.  The previous
         * laplace_fp_mul(diff, fA) rounded a Q1e18 numerator back to Q1e9
         * before dividing by f_span; close to the root that became zero and
         * manufactured a non-convergence failure for valid periods. */
        int64_t correction = rounded_ratio_i128(
            (__int128)diff * (__int128)fA, f_span);
        int64_t C = sat_add_i64(A, correction);
        int64_t fC = illinois_f(C, delta_sq, phi_sq, v, a, tau_sq);

        /* A fixed-point product is not a sign test.  Small same-sign values
         * can multiply to less than half a Q1e9 unit and round to zero, which
         * used to be misread as a bracket crossing. */
        const int crosses_zero =
            fC == 0 || fB == 0 || ((fC < 0) != (fB < 0));
        if (crosses_zero) {
            A = B;
            fA = fB;
        } else {
            fA /= 2;
        }
        B = C;
        fB = fC;
    }
    if (iter_count == 100) return -1;

    int64_t sigma_new = laplace_fp_exp(A / 2);
    if (sigma_new <= 0) return -1;

    int64_t sigma_new_sq = laplace_fp_mul(sigma_new, sigma_new);
    int64_t phi_star_sq = sat_add_i64(phi_sq, sigma_new_sq);
    if (sigma_new_sq <= 0 || phi_star_sq <= 0) return -1;
    int64_t phi_star = laplace_fp_sqrt(phi_star_sq);
    if (phi_star <= 0) return -1;

    int64_t inv_phi_star_sq = laplace_fp_div(LAPLACE_FP_ONE, phi_star_sq);
    int64_t inv_v = laplace_fp_div(LAPLACE_FP_ONE, v);
    int64_t precision = sat_add_i64(inv_phi_star_sq, inv_v);
    if (precision <= 0) return -1;
    int64_t phi_new_sq = laplace_fp_div(LAPLACE_FP_ONE, precision);
    if (phi_new_sq <= 0) return -1;
    int64_t phi_new = laplace_fp_sqrt(phi_new_sq);
    if (phi_new <= 0) return -1;

    int64_t mu_new = sat_add_i64(
        mu, laplace_fp_mul(phi_new_sq, delta_inner));
    int64_t r_new = mu_to_g1(mu_new);
    int64_t rd_new = phi_to_g1(phi_new);
    if (rd_new > LAPLACE_FP_RD_MAX) rd_new = LAPLACE_FP_RD_MAX;
    if (rd_new <= 0) return -1;

    if (trace) {
        trace->mu             = mu;
        trace->phi            = phi;
        trace->v              = v;
        trace->delta          = delta;
        trace->a_value        = a;
        trace->sigma_new      = sigma_new;
        trace->phi_star       = phi_star;
        trace->phi_new        = phi_new;
        trace->mu_new         = mu_new;
        trace->r_new          = r_new;
        trace->rd_new         = rd_new;
        trace->illinois_iters = iter_count;
    }

    st->rating                   = r_new;
    st->rd                       = rd_new;
    st->volatility               = sigma_new;
    st->last_observed_at_unix_ns = now_ns;
    st->observation_count        = sat_add_i64(st->observation_count, (int64_t)n);
    return glicko2_state_is_admissible(st) ? 0 : -1;
}

static void glicko2_update_period_impl(glicko2_state_t* st,
                                       const glicko2_observation_t* obs,
                                       size_t n,
                                       int64_t tau,
                                       int64_t now_ns,
                                       glicko2_trace_t* trace)
{
    if (!glicko2_state_is_admissible(st) || tau <= 0) return;

    if (n == 0 || !obs) {
        int64_t phi = g1_to_phi(st->rd);
        int64_t sigma = st->volatility;
        int64_t phi_sq = laplace_fp_mul(phi, phi);
        int64_t sig_sq = laplace_fp_mul(sigma, sigma);
        int64_t phi_new_sq = sat_add_i64(phi_sq, sig_sq);
        int64_t phi_new = laplace_fp_sqrt(phi_new_sq);
        int64_t rd_new = phi_to_g1(phi_new);
        if (rd_new > LAPLACE_FP_RD_MAX) rd_new = LAPLACE_FP_RD_MAX;
        if (rd_new <= 0) return;
        if (trace) {
            trace->mu             = g1_to_mu(st->rating);
            trace->phi            = phi;
            trace->v              = 0;
            trace->delta          = 0;
            trace->a_value        = laplace_fp_log(laplace_fp_mul(sigma, sigma));
            trace->sigma_new      = sigma;
            trace->phi_star       = phi_new;
            trace->phi_new        = phi_new;
            trace->mu_new         = trace->mu;
            trace->r_new          = st->rating;
            trace->rd_new         = rd_new;
            trace->illinois_iters = 0;
        }
        st->rd = rd_new;
        st->last_observed_at_unix_ns = now_ns;
        return;
    }

    if (n > (size_t)INT64_MAX ||
        st->observation_count > INT64_MAX - (int64_t)n)
        return;

    int64_t mu = g1_to_mu(st->rating);
    int64_t phi = g1_to_phi(st->rd);
    int64_t sigma = st->volatility;
    int64_t phi_sq = laplace_fp_mul(phi, phi);

    int64_t v_inv = 0;
    int64_t delta_inner = 0;
    for (size_t i = 0; i < n; ++i) {
        if (obs[i].opponent_rd < 0 || obs[i].score < 0 ||
            obs[i].score > LAPLACE_FP_ONE)
            return;
        int64_t mu_j = g1_to_mu(obs[i].opponent_rating);
        int64_t phi_j = g1_to_phi(obs[i].opponent_rd);
        int64_t g_j = laplace_glicko2_g(phi_j);
        int64_t E_j = laplace_glicko2_E(mu, mu_j, g_j);
        int64_t g_sq = laplace_fp_mul(g_j, g_j);
        int64_t E_1mE = laplace_fp_mul(E_j, sat_sub_i64(LAPLACE_FP_ONE, E_j));
        v_inv = sat_add_i64(v_inv, laplace_fp_mul(g_sq, E_1mE));
        delta_inner = sat_add_i64(
            delta_inner,
            laplace_fp_mul(g_j, sat_sub_i64(obs[i].score, E_j)));
    }

    glicko2_state_t next = *st;
    if (glicko2_finish_period(&next, mu, phi, phi_sq, sigma,
                              v_inv, delta_inner, n, tau, now_ns,
                              trace) == 0)
        *st = next;
}

void glicko2_update_period(glicko2_state_t* st,
                           const glicko2_observation_t* obs,
                           size_t n,
                           int64_t tau,
                           int64_t now_ns)
{
    glicko2_update_period_impl(st, obs, n, tau, now_ns, NULL);
}

int glicko2_fold_grouped_period(glicko2_state_t* st,
                                const int64_t* opponent_ratings,
                                const int64_t* opponent_phis,
                                const int64_t* games,
                                const int64_t* score_sums,
                                size_t group_count,
                                int64_t tau,
                                int64_t now_ns)
{
    if (!glicko2_state_is_admissible(st) || !opponent_ratings ||
        !opponent_phis || !games || !score_sums || group_count == 0 ||
        tau <= 0)
        return -1;

    int64_t mu = g1_to_mu(st->rating);
    int64_t phi = g1_to_phi(st->rd);
    int64_t sigma = st->volatility;
    int64_t phi_sq = laplace_fp_mul(phi, phi);

    __int128 v_inv_wide = 0;
    __int128 delta_wide = 0;
    __int128 total_games_wide = 0;

    for (size_t i = 0; i < group_count; ++i) {
        int64_t n = games[i];
        if (n <= 0 || opponent_phis[i] < 0) return -1;
        __int128 maximum_score = (__int128)n * (__int128)LAPLACE_FP_ONE;
        if (score_sums[i] < 0 || (__int128)score_sums[i] > maximum_score)
            return -1;

        int64_t mu_j = g1_to_mu(opponent_ratings[i]);
        int64_t phi_j = g1_to_phi(opponent_phis[i]);
        int64_t g_j = laplace_glicko2_g(phi_j);
        int64_t E_j = laplace_glicko2_E(mu, mu_j, g_j);
        int64_t g_sq = laplace_fp_mul(g_j, g_j);
        int64_t E_1mE = laplace_fp_mul(
            E_j, sat_sub_i64(LAPLACE_FP_ONE, E_j));

        v_inv_wide += (__int128)n * laplace_fp_mul(g_sq, E_1mE);

        /* Score enters linearly, but fixed-point multiplication rounds per
         * observation. q/rem reproduces exactly the observation sequence
         * represented by an aggregate without allocating O(games) records. */
        int64_t q = score_sums[i] / n;
        __int128 rem_wide = (__int128)score_sums[i] - (__int128)q * (n - 1);
        if (rem_wide > INT64_MAX || rem_wide < INT64_MIN)
            return -1;
        int64_t rem = (int64_t)rem_wide;
        delta_wide += (__int128)(n - 1) *
                          laplace_fp_mul(g_j, sat_sub_i64(q, E_j))
                    + laplace_fp_mul(g_j, sat_sub_i64(rem, E_j));
        total_games_wide += n;
    }

    if (v_inv_wide > INT64_MAX || v_inv_wide < INT64_MIN ||
        delta_wide > INT64_MAX || delta_wide < INT64_MIN ||
        total_games_wide > INT64_MAX || total_games_wide <= 0 ||
        st->observation_count > INT64_MAX - (int64_t)total_games_wide)
        return -1;

    glicko2_state_t next = *st;
    if (glicko2_finish_period(
            &next, mu, phi, phi_sq, sigma,
            (int64_t)v_inv_wide, (int64_t)delta_wide,
            (size_t)total_games_wide, tau, now_ns, NULL) != 0)
        return -1;
    if (!glicko2_state_is_admissible(&next)) return -1;
    *st = next;
    return 0;
}

int glicko2_fold_uniform_period(glicko2_state_t* st,
                                int64_t opponent_rating,
                                int64_t opponent_phi,
                                int64_t games,
                                int64_t sum_score,
                                int64_t tau,
                                int64_t now_ns)
{
    return glicko2_fold_grouped_period(st, &opponent_rating, &opponent_phi,
                                       &games, &sum_score, 1, tau, now_ns);
}

void laplace_glicko2_update_period_traced(glicko2_state_t* st,
                                          const glicko2_observation_t* obs,
                                          size_t n,
                                          int64_t tau,
                                          int64_t now_ns,
                                          glicko2_trace_t* trace)
{
    glicko2_update_period_impl(st, obs, n, tau, now_ns, trace);
}

void glicko2_update(glicko2_state_t* st,
                    int64_t score,
                    int64_t source_credibility,
                    int64_t now_ns)
{
    if (!st) return;
    glicko2_observation_t obs = {
        .opponent_rating = source_credibility,
        .opponent_rd     = 30LL * LAPLACE_FP_ONE,
        .score           = score,
    };
    glicko2_update_period(st, &obs, 1, LAPLACE_GLICKO2_DEFAULT_TAU, now_ns);
}

void glicko2_decay_rd_in_place(glicko2_state_t* st, int64_t now_ns)
{
    if (!glicko2_state_is_admissible(st)) return;
    if (st->last_observed_at_unix_ns <= 0) {
        st->last_observed_at_unix_ns = now_ns;
        return;
    }
    if (now_ns <= st->last_observed_at_unix_ns) return;
    int64_t elapsed_ns = clamp_i128(
        (__int128)now_ns - (__int128)st->last_observed_at_unix_ns);

    int64_t periods_fp = laplace_fp_div(
        elapsed_ns, LAPLACE_GLICKO2_RATING_PERIOD_NS);
    int64_t phi = g1_to_phi(st->rd);
    int64_t sigma = st->volatility;
    int64_t phi_sq = laplace_fp_mul(phi, phi);
    int64_t sig_sq = laplace_fp_mul(sigma, sigma);
    int64_t bump = laplace_fp_mul(sig_sq, periods_fp);
    int64_t phi_new_sq = sat_add_i64(phi_sq, bump);
    int64_t phi_new = laplace_fp_sqrt(phi_new_sq);
    int64_t rd_new = phi_to_g1(phi_new);
    if (rd_new > LAPLACE_FP_RD_MAX) rd_new = LAPLACE_FP_RD_MAX;
    if (rd_new <= 0) return;
    st->rd = rd_new;
    st->last_observed_at_unix_ns = now_ns;
}

int64_t laplace_glicko2_neutral_mu_fp(void)
{
    return LAPLACE_GLICKO2_NEUTRAL_MU_FP;
}

int64_t laplace_effective_mu_fp(int64_t rating, int64_t rd)
{
    return sat_sub_i64(rating, sat_scale_i64(rd, 2));
}

int64_t glicko2_effective_mu(const glicko2_state_t* st)
{
    if (!st) return 0;
    return laplace_effective_mu_fp(st->rating, st->rd);
}

int64_t laplace_glicko2_expected_score_fp(int64_t rating, int64_t rd)
{
    int64_t mu = g1_to_mu(rating);
    int64_t neutral_mu = g1_to_mu(LAPLACE_GLICKO2_NEUTRAL_MU_FP);
    int64_t phi = g1_to_phi(rd);
    int64_t confidence = laplace_glicko2_g(phi);
    return laplace_glicko2_E(mu, neutral_mu, confidence);
}

double laplace_walk_edge_weight(int64_t rating, int64_t rd)
{
    double expected = (double)laplace_glicko2_expected_score_fp(rating, rd)
                      / LAPLACE_GLICKO2_FP_SCALE_D;
    return 2.0 * expected - 1.0;
}
