/* engine/core/src/glicko2.c
 *
 * Glicko-2 update equations (Glickman 2013, "Example of the Glicko-2
 * System") implemented in int64 fixed-point at scale 1e9 per ADR 0004.
 * Cross-machine determinism by construction: no double intermediates, no
 * machine-dependent rounding. All math goes through __int128 to avoid
 * overflow when multiplying scaled values.
 *
 * The fixed-point primitives (laplace_fp_mul / _div / _sqrt / _exp / _log)
 * are exposed for tests via the header; they implement reduction +
 * polynomial / Newton iteration at our precision (~1e-9 relative error).
 *
 * Reference: http://www.glicko.net/glicko/glicko2.pdf */

#include "laplace/core/glicko2.h"

#include <stddef.h>
#include <stdint.h>

/* ===================================================================== */
/* Fixed-point primitives                                                */
/* ===================================================================== */

#define LAPLACE_FP_ONE      1000000000LL          /* 1.0 at scale 1e9 */
#define LAPLACE_FP_HALF      500000000LL
#define LAPLACE_FP_LN2       693147181LL          /* ln(2)  * 1e9 */
#define LAPLACE_FP_LN2_HALF  346573590LL          /* ln(2)/2 */
#define LAPLACE_FP_PI       3141592654LL          /* pi     * 1e9 */
#define LAPLACE_FP_PI_SQ    9869604401LL          /* pi^2   * 1e9 */

/* Glicko rating-scale constants (scale 1e9). */
#define LAPLACE_FP_BASE_RATING   1500000000000LL  /* 1500.0 */
#define LAPLACE_FP_RATING_SCALE   173717800000LL  /* (400 / ln(10)) * 1e9 ≈ 173.7178 */
#define LAPLACE_FP_RD_MAX         350000000000LL  /* 350.0 (unrated cap) */

int64_t laplace_fp_mul(int64_t a, int64_t b) {
    __int128 prod = (__int128)a * (__int128)b;
    /* Round-to-nearest with ties-away-from-zero for determinism. */
    if (prod >= 0) {
        return (int64_t)((prod + (LAPLACE_FP_ONE / 2)) / LAPLACE_FP_ONE);
    } else {
        return (int64_t)(-(((-prod) + (LAPLACE_FP_ONE / 2)) / LAPLACE_FP_ONE));
    }
}

int64_t laplace_fp_div(int64_t a, int64_t b) {
    if (b == 0) {
        return (a >= 0) ? INT64_MAX : INT64_MIN;
    }
    __int128 num = (__int128)a * (__int128)LAPLACE_FP_ONE;
    int64_t  abs_b = b < 0 ? -b : b;
    int      sign  = ((a < 0) ^ (b < 0)) ? -1 : 1;
    if (a < 0) num = -num;
    __int128 q = (num + abs_b / 2) / abs_b;  /* round-to-nearest */
    return sign * (int64_t)q;
}

/* Integer sqrt of a 128-bit non-negative value, returns 64-bit. */
static uint64_t isqrt_u128(__int128 n) {
    if (n < 0) return 0;
    if (n == 0) return 0;
    /* Newton's method, integer. Initial estimate via bit length. */
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
    /* Newton: x = (x + n/x) / 2, iterate until stable. */
    for (int i = 0; i < 80; ++i) {
        __int128 y = (x + n / x) >> 1;
        if (y >= x) break;
        x = y;
    }
    return (uint64_t)x;
}

int64_t laplace_fp_sqrt(int64_t x) {
    if (x <= 0) return 0;
    /* sqrt(x/1e9) at scale 1e9 = isqrt(x * 1e9). */
    __int128 scaled = (__int128)x * (__int128)LAPLACE_FP_ONE;
    return (int64_t)isqrt_u128(scaled);
}

/* fp_exp via range reduction + Taylor.
 *
 *   x = k * ln2 + r,  r in [-ln2/2, ln2/2]
 *   e^x = 2^k * e^r
 *   e^r ≈ sum_{n=0..N} r^n / n!,  with N chosen so residual < 1 ulp at our scale.
 *
 * For |r| <= 0.347, terms shrink as ~r^n/n!; r^12/12! ≈ 4e-13 at r=0.347,
 * so N = 12 is comfortable for 1e-9 precision. */
int64_t laplace_fp_exp(int64_t x) {
    /* Saturate at extremes — Glicko-2 only calls exp on small arguments
     * (log-variance neighborhoods), but be defensive. */
    if (x >  60LL * LAPLACE_FP_ONE) return INT64_MAX / 2;
    if (x < -60LL * LAPLACE_FP_ONE) return 0;

    /* k = round(x / ln2). */
    int64_t k;
    if (x >= 0) {
        k = (x + LAPLACE_FP_LN2_HALF) / LAPLACE_FP_LN2;
    } else {
        k = -(((-x) + LAPLACE_FP_LN2_HALF) / LAPLACE_FP_LN2);
    }
    int64_t r = x - k * LAPLACE_FP_LN2;

    /* Taylor series for e^(r/1e9) at scale 1e9. */
    int64_t result = LAPLACE_FP_ONE;
    int64_t term   = LAPLACE_FP_ONE;
    for (int n = 1; n <= 14; ++n) {
        /* term = term * r / n */
        term = laplace_fp_mul(term, r) / n;
        result += term;
        if (term > -10 && term < 10) break;  /* convergence */
    }

    /* Multiply by 2^k via shift. */
    if (k > 0) {
        if (k >= 62) return INT64_MAX / 2;
        result <<= k;
    } else if (k < 0) {
        if (k <= -62) return 0;
        /* Round-to-nearest right shift. */
        int64_t add = (1LL << ((-k) - 1));
        result = (result + add) >> (-k);
    }
    return result;
}

/* fp_log via range reduction + atanh series.
 *
 *   For x = m * 2^k with m in [1, 2):
 *     ln(x) = k * ln2 + ln(m)
 *     ln(m) = 2 * atanh((m-1)/(m+1))
 *     atanh(u) = sum_{i=1,3,5,...} u^i / i
 *
 * For m in [1, 2), u in [0, 1/3], so u^21 / 21 ≈ 5e-11 — 11 odd terms suffice
 * for our 1e-9 precision. */
int64_t laplace_fp_log(int64_t x) {
    if (x <= 0) return INT64_MIN / 2;

    int64_t y = x;
    int     k = 0;
    while (y >= 2 * LAPLACE_FP_ONE) { y >>= 1; ++k; }
    while (y <  LAPLACE_FP_ONE)     { y <<= 1; --k; }
    /* y in [1e9, 2e9), i.e. m in [1, 2) at scale 1e9. */

    int64_t num = y - LAPLACE_FP_ONE;
    int64_t den = y + LAPLACE_FP_ONE;
    int64_t u   = laplace_fp_div(num, den);          /* u in [0, 1/3] */
    int64_t u2  = laplace_fp_mul(u, u);

    int64_t term = u;
    int64_t sum  = term;
    for (int i = 3; i <= 31; i += 2) {
        term = laplace_fp_mul(term, u2);
        int64_t inc = term / i;
        sum += inc;
        if (inc > -1 && inc < 1) break;
    }
    int64_t ln_m = 2 * sum;
    return (int64_t)k * LAPLACE_FP_LN2 + ln_m;
}

/* ===================================================================== */
/* Glicko-2 update                                                       */
/* ===================================================================== */

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

/* Glicko-1 -> Glicko-2 scale conversions. */
static int64_t g1_to_mu(int64_t r) {
    return laplace_fp_div(r - LAPLACE_FP_BASE_RATING, LAPLACE_FP_RATING_SCALE);
}
static int64_t g1_to_phi(int64_t rd) {
    return laplace_fp_div(rd, LAPLACE_FP_RATING_SCALE);
}
static int64_t mu_to_g1(int64_t mu) {
    return laplace_fp_mul(mu, LAPLACE_FP_RATING_SCALE) + LAPLACE_FP_BASE_RATING;
}
static int64_t phi_to_g1(int64_t phi) {
    return laplace_fp_mul(phi, LAPLACE_FP_RATING_SCALE);
}

/* g(phi) = 1 / sqrt(1 + 3 * phi^2 / pi^2) — Glickman 2013 §"The formulas",
 * Step 3. */
int64_t laplace_glicko2_g(int64_t phi) {
    int64_t phi_sq = laplace_fp_mul(phi, phi);
    int64_t three_phi_sq = 3 * phi_sq;
    int64_t denom_inside = LAPLACE_FP_ONE
                         + laplace_fp_div(three_phi_sq, LAPLACE_FP_PI_SQ);
    int64_t denom = laplace_fp_sqrt(denom_inside);
    return laplace_fp_div(LAPLACE_FP_ONE, denom);
}

/* E(mu, mu_j, phi_j) = 1 / (1 + exp(-g(phi_j) * (mu - mu_j))) — Glickman
 * 2013 §"The formulas", Step 3. The third argument is pre-computed
 * g(phi_j); passing it explicitly lets callers (and tests) avoid recomputing
 * the same value when iterating over opponents. */
int64_t laplace_glicko2_E(int64_t mu, int64_t mu_j, int64_t g_j) {
    int64_t arg = -laplace_fp_mul(g_j, mu - mu_j);
    int64_t ex  = laplace_fp_exp(arg);
    return laplace_fp_div(LAPLACE_FP_ONE, LAPLACE_FP_ONE + ex);
}

/* f(x) for Illinois iteration on log-volatility. */
static int64_t illinois_f(int64_t x,
                          int64_t delta_sq,
                          int64_t phi_sq,
                          int64_t v,
                          int64_t a,           /* ln(sigma^2) */
                          int64_t tau_sq)
{
    int64_t ex = laplace_fp_exp(x);
    int64_t denom_inner = phi_sq + v + ex;
    int64_t denom = 2 * laplace_fp_mul(denom_inner, denom_inner);
    int64_t num = laplace_fp_mul(ex, delta_sq - phi_sq - v - ex);
    int64_t lhs = laplace_fp_div(num, denom);
    int64_t rhs = laplace_fp_div(x - a, tau_sq);
    return lhs - rhs;
}

/* Core update loop; called from both glicko2_update_period (trace = NULL)
 * and laplace_glicko2_update_period_traced (trace != NULL). */
static void glicko2_update_period_impl(glicko2_state_t* st,
                                       const glicko2_observation_t* obs,
                                       size_t n,
                                       int64_t tau,
                                       int64_t now_ns,
                                       glicko2_trace_t* trace)
{
    if (!st) return;

    /* Pre-rating-period decay only — Glickman 2013, end of §"The formulas":
     *   "if a player does not compete during the rating period, then only
     *    Step 6 applies. ... RD increases according to phi' = phi* =
     *    sqrt(phi^2 + sigma^2)." */
    if (n == 0 || !obs) {
        int64_t phi    = g1_to_phi(st->rd);
        int64_t sigma  = st->volatility;
        int64_t phi_sq = laplace_fp_mul(phi, phi);
        int64_t sig_sq = laplace_fp_mul(sigma, sigma);
        int64_t phi_new = laplace_fp_sqrt(phi_sq + sig_sq);
        int64_t rd_new = phi_to_g1(phi_new);
        if (rd_new > LAPLACE_FP_RD_MAX) rd_new = LAPLACE_FP_RD_MAX;
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

    int64_t mu     = g1_to_mu(st->rating);
    int64_t phi    = g1_to_phi(st->rd);
    int64_t sigma  = st->volatility;
    int64_t phi_sq = laplace_fp_mul(phi, phi);

    /* Step 3 + 4: accumulate v_inv = sum g^2 * E * (1-E) and
     *             delta_inner = sum g_j * (s_j - E_j). */
    int64_t v_inv       = 0;
    int64_t delta_inner = 0;
    for (size_t i = 0; i < n; ++i) {
        int64_t mu_j  = g1_to_mu(obs[i].opponent_rating);
        int64_t phi_j = g1_to_phi(obs[i].opponent_rd);
        int64_t g_j   = laplace_glicko2_g(phi_j);
        int64_t E_j   = laplace_glicko2_E(mu, mu_j, g_j);
        int64_t g_sq  = laplace_fp_mul(g_j, g_j);
        int64_t E_1mE = laplace_fp_mul(E_j, LAPLACE_FP_ONE - E_j);
        v_inv       += laplace_fp_mul(g_sq, E_1mE);
        delta_inner += laplace_fp_mul(g_j, obs[i].score - E_j);
    }
    if (v_inv <= 0) v_inv = 1;  /* defensive — empty period handled above */
    int64_t v        = laplace_fp_div(LAPLACE_FP_ONE, v_inv);
    int64_t delta    = laplace_fp_mul(v, delta_inner);
    int64_t delta_sq = laplace_fp_mul(delta, delta);

    /* Step 5: Illinois iteration on log-volatility. Per Glickman §"Step 5",
     * sub-step 2:  A = a;  if Delta^2 > phi^2 + v then B = ln(Delta^2 -
     * phi^2 - v); else walk down by tau increments until f(a - k*tau) >= 0. */
    int64_t sigma_sq = laplace_fp_mul(sigma, sigma);
    int64_t a        = laplace_fp_log(sigma_sq);
    int64_t tau_sq   = laplace_fp_mul(tau, tau);

    int64_t A = a;
    int64_t B;
    if (delta_sq > phi_sq + v) {
        B = laplace_fp_log(delta_sq - phi_sq - v);
    } else {
        int k = 1;
        while (illinois_f(a - (int64_t)k * tau, delta_sq, phi_sq, v, a, tau_sq) < 0) {
            ++k;
            if (k > 100) break;     /* defensive bound */
        }
        B = a - (int64_t)k * tau;
    }

    int64_t fA = illinois_f(A, delta_sq, phi_sq, v, a, tau_sq);
    int64_t fB = illinois_f(B, delta_sq, phi_sq, v, a, tau_sq);

    int iter_count = 0;
    for (; iter_count < 100; ++iter_count) {
        int64_t diff = B - A;
        int64_t abs_diff = diff < 0 ? -diff : diff;
        if (abs_diff <= LAPLACE_GLICKO2_ILLINOIS_EPS) break;
        /* Illinois sub-step 4(a): C = A + (A-B) * fA / (fB - fA). */
        int64_t C  = A + laplace_fp_div(laplace_fp_mul(diff, fA), fA - fB);
        int64_t fC = illinois_f(C, delta_sq, phi_sq, v, a, tau_sq);

        /* Sub-step 4(b): if fC*fB <= 0 then A <- B, fA <- fB else fA <- fA/2. */
        if (laplace_fp_mul(fC, fB) <= 0) {
            A  = B;
            fA = fB;
        } else {
            fA = fA / 2;
        }
        /* Sub-step 4(c): B <- C, fB <- fC. */
        B  = C;
        fB = fC;
    }
    /* Glickman §Step 5 sub-step 5: sigma' = exp(A/2). */
    int64_t sigma_new = laplace_fp_exp(A / 2);

    /* Step 6: phi* = sqrt(phi^2 + sigma'^2). */
    int64_t sigma_new_sq = laplace_fp_mul(sigma_new, sigma_new);
    int64_t phi_star_sq  = phi_sq + sigma_new_sq;
    int64_t phi_star     = laplace_fp_sqrt(phi_star_sq);

    /* Step 7: phi' = 1 / sqrt(1/phi*^2 + 1/v). */
    int64_t inv_phi_star_sq = laplace_fp_div(LAPLACE_FP_ONE, phi_star_sq);
    int64_t inv_v           = laplace_fp_div(LAPLACE_FP_ONE, v);
    int64_t phi_new_sq      = laplace_fp_div(LAPLACE_FP_ONE,
                                              inv_phi_star_sq + inv_v);
    int64_t phi_new         = laplace_fp_sqrt(phi_new_sq);

    /* Step 7 (mu update): mu' = mu + phi'^2 * sum g_j * (s_j - E_j). */
    int64_t mu_new = mu + laplace_fp_mul(phi_new_sq, delta_inner);

    /* Step 8: convert back to Glicko-1 scale. */
    int64_t r_new  = mu_to_g1(mu_new);
    int64_t rd_new = phi_to_g1(phi_new);
    if (rd_new > LAPLACE_FP_RD_MAX) rd_new = LAPLACE_FP_RD_MAX;
    if (rd_new < 0)                 rd_new = 0;

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
    st->observation_count       += (int64_t)n;
}

void glicko2_update_period(glicko2_state_t* st,
                           const glicko2_observation_t* obs,
                           size_t n,
                           int64_t tau,
                           int64_t now_ns)
{
    glicko2_update_period_impl(st, obs, n, tau, now_ns, NULL);
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
    /* Treat the source as a Glicko-1-rated opponent with RD = 30 (high
     * confidence in the source's stated value). For arena/multi-source
     * aggregation, callers should use glicko2_update_period directly. */
    glicko2_observation_t obs = {
        .opponent_rating = source_credibility,
        .opponent_rd     = 30LL * LAPLACE_FP_ONE,
        .score           = score,
    };
    glicko2_update_period(st, &obs, 1, LAPLACE_GLICKO2_DEFAULT_TAU, now_ns);
}

void glicko2_decay_rd_in_place(glicko2_state_t* st, int64_t now_ns) {
    if (!st) return;
    if (st->last_observed_at_unix_ns <= 0) {
        /* Never observed — RD already at prior. */
        st->last_observed_at_unix_ns = now_ns;
        return;
    }
    int64_t elapsed_ns = now_ns - st->last_observed_at_unix_ns;
    if (elapsed_ns <= 0) return;

    /* periods_elapsed at scale 1e9 = elapsed / RATING_PERIOD_NS. We allow
     * fractional periods. */
    int64_t periods_fp = laplace_fp_div(elapsed_ns,
                                        LAPLACE_GLICKO2_RATING_PERIOD_NS);
    int64_t phi    = g1_to_phi(st->rd);
    int64_t sigma  = st->volatility;
    int64_t phi_sq = laplace_fp_mul(phi, phi);
    int64_t sig_sq = laplace_fp_mul(sigma, sigma);
    int64_t bump   = laplace_fp_mul(sig_sq, periods_fp);
    int64_t phi_new = laplace_fp_sqrt(phi_sq + bump);
    int64_t rd_new  = phi_to_g1(phi_new);
    if (rd_new > LAPLACE_FP_RD_MAX) rd_new = LAPLACE_FP_RD_MAX;
    st->rd = rd_new;
    st->last_observed_at_unix_ns = now_ns;
}

int64_t glicko2_effective_mu(const glicko2_state_t* st) {
    if (!st) return 0;
    /* ~95% lower bound: r - 2*RD. Cascade A* uses this so edges with
     * high uncertainty (large RD) don't outrank edges with confirmed
     * support, even if their raw rating is similar. */
    return st->rating - 2 * st->rd;
}
