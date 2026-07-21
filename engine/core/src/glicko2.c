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

int64_t laplace_fp_mul(int64_t a, int64_t b) {
    __int128 prod = (__int128)a * (__int128)b;
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
    __int128 q = (num + abs_b / 2) / abs_b;
    return sign * (int64_t)q;
}

static uint64_t isqrt_u128(__int128 n) {
    if (n < 0) return 0;
    if (n == 0) return 0;
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

int64_t laplace_fp_sqrt(int64_t x) {
    if (x <= 0) return 0;
    __int128 scaled = (__int128)x * (__int128)LAPLACE_FP_ONE;
    return (int64_t)isqrt_u128(scaled);
}

int64_t laplace_fp_exp(int64_t x) {
    if (x >  60LL * LAPLACE_FP_ONE) return INT64_MAX / 2;
    if (x < -60LL * LAPLACE_FP_ONE) return 0;

    int64_t k;
    if (x >= 0) {
        k = (x + LAPLACE_FP_LN2_HALF) / LAPLACE_FP_LN2;
    } else {
        k = -(((-x) + LAPLACE_FP_LN2_HALF) / LAPLACE_FP_LN2);
    }
    int64_t r = x - k * LAPLACE_FP_LN2;

    int64_t result = LAPLACE_FP_ONE;
    int64_t term   = LAPLACE_FP_ONE;
    for (int n = 1; n <= 14; ++n) {
        term = laplace_fp_mul(term, r) / n;
        result += term;
        if (term > -10 && term < 10) break;
    }

    if (k > 0) {
        if (k >= 62) return INT64_MAX / 2;
        result <<= k;
    } else if (k < 0) {
        if (k <= -62) return 0;
        int64_t add = (1LL << ((-k) - 1));
        result = (result + add) >> (-k);
    }
    return result;
}

int64_t laplace_fp_log(int64_t x) {
    if (x <= 0) return INT64_MIN / 2;

    int64_t y = x;
    int     k = 0;
    while (y >= 2 * LAPLACE_FP_ONE) { y >>= 1; ++k; }
    while (y <  LAPLACE_FP_ONE)     { y <<= 1; --k; }

    int64_t num = y - LAPLACE_FP_ONE;
    int64_t den = y + LAPLACE_FP_ONE;
    int64_t u   = laplace_fp_div(num, den);
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

int64_t laplace_glicko2_g(int64_t phi) {
    int64_t phi_sq = laplace_fp_mul(phi, phi);
    int64_t three_phi_sq = 3 * phi_sq;
    int64_t denom_inside = LAPLACE_FP_ONE
                         + laplace_fp_div(three_phi_sq, LAPLACE_FP_PI_SQ);
    int64_t denom = laplace_fp_sqrt(denom_inside);
    return laplace_fp_div(LAPLACE_FP_ONE, denom);
}

int64_t laplace_glicko2_E(int64_t mu, int64_t mu_j, int64_t g_j) {
    int64_t arg = -laplace_fp_mul(g_j, mu - mu_j);
    int64_t ex  = laplace_fp_exp(arg);
    return laplace_fp_div(LAPLACE_FP_ONE, LAPLACE_FP_ONE + ex);
}

static int64_t illinois_f(int64_t x,
                          int64_t delta_sq,
                          int64_t phi_sq,
                          int64_t v,
                          int64_t a,
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




static void glicko2_finish_period(glicko2_state_t* st,
                                  int64_t mu, int64_t phi, int64_t phi_sq,
                                  int64_t sigma, int64_t v_inv, int64_t delta_inner,
                                  size_t n, int64_t tau, int64_t now_ns,
                                  glicko2_trace_t* trace)
{
    if (v_inv <= 0) v_inv = 1;
    int64_t v        = laplace_fp_div(LAPLACE_FP_ONE, v_inv);
    int64_t delta    = laplace_fp_mul(v, delta_inner);
    int64_t delta_sq = laplace_fp_mul(delta, delta);

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
            if (k > 100) break;
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
        int64_t C  = A + laplace_fp_div(laplace_fp_mul(diff, fA), fA - fB);
        int64_t fC = illinois_f(C, delta_sq, phi_sq, v, a, tau_sq);

        if (laplace_fp_mul(fC, fB) <= 0) {
            A  = B;
            fA = fB;
        } else {
            fA = fA / 2;
        }
        B  = C;
        fB = fC;
    }
    int64_t sigma_new = laplace_fp_exp(A / 2);

    int64_t sigma_new_sq = laplace_fp_mul(sigma_new, sigma_new);
    int64_t phi_star_sq  = phi_sq + sigma_new_sq;
    int64_t phi_star     = laplace_fp_sqrt(phi_star_sq);

    int64_t inv_phi_star_sq = laplace_fp_div(LAPLACE_FP_ONE, phi_star_sq);
    int64_t inv_v           = laplace_fp_div(LAPLACE_FP_ONE, v);
    int64_t phi_new_sq      = laplace_fp_div(LAPLACE_FP_ONE,
                                              inv_phi_star_sq + inv_v);
    int64_t phi_new         = laplace_fp_sqrt(phi_new_sq);

    int64_t mu_new = mu + laplace_fp_mul(phi_new_sq, delta_inner);

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

static void glicko2_update_period_impl(glicko2_state_t* st,
                                       const glicko2_observation_t* obs,
                                       size_t n,
                                       int64_t tau,
                                       int64_t now_ns,
                                       glicko2_trace_t* trace)
{
    if (!st) return;

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
    glicko2_finish_period(st, mu, phi, phi_sq, sigma, v_inv, delta_inner,
                          n, tau, now_ns, trace);
}

void glicko2_update_period(glicko2_state_t* st,
                           const glicko2_observation_t* obs,
                           size_t n,
                           int64_t tau,
                           int64_t now_ns)
{
    glicko2_update_period_impl(st, obs, n, tau, now_ns, NULL);
}









void glicko2_fold_uniform_period(glicko2_state_t* st,
                                 int64_t opponent_rating,
                                 int64_t opponent_phi,
                                 int64_t games,
                                 int64_t sum_score,
                                 int64_t tau,
                                 int64_t now_ns)
{
    if (!st || games <= 0) return;

    int64_t mu     = g1_to_mu(st->rating);
    int64_t phi    = g1_to_phi(st->rd);
    int64_t sigma  = st->volatility;
    int64_t phi_sq = laplace_fp_mul(phi, phi);

    int64_t mu_j  = g1_to_mu(opponent_rating);
    int64_t phi_j = g1_to_phi(opponent_phi);
    int64_t g_j   = laplace_glicko2_g(phi_j);
    int64_t E_j   = laplace_glicko2_E(mu, mu_j, g_j);
    int64_t g_sq  = laplace_fp_mul(g_j, g_j);
    int64_t E_1mE = laplace_fp_mul(E_j, LAPLACE_FP_ONE - E_j);

    
    int64_t v_inv = games * laplace_fp_mul(g_sq, E_1mE);

    

    int64_t q   = sum_score / games;
    int64_t rem = sum_score - q * (games - 1);
    int64_t delta_inner = (games - 1) * laplace_fp_mul(g_j, q - E_j)
                        + laplace_fp_mul(g_j, rem - E_j);

    glicko2_finish_period(st, mu, phi, phi_sq, sigma, v_inv, delta_inner,
                          (size_t) games, tau, now_ns, NULL);
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

void glicko2_decay_rd_in_place(glicko2_state_t* st, int64_t now_ns) {
    if (!st) return;
    if (st->last_observed_at_unix_ns <= 0) {
        st->last_observed_at_unix_ns = now_ns;
        return;
    }
    int64_t elapsed_ns = now_ns - st->last_observed_at_unix_ns;
    if (elapsed_ns <= 0) return;

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

int64_t laplace_glicko2_neutral_mu_fp(void) {
    return LAPLACE_GLICKO2_NEUTRAL_MU_FP;
}

int64_t laplace_effective_mu_fp(int64_t rating, int64_t rd) {
    return rating - 2 * rd;
}

int64_t glicko2_effective_mu(const glicko2_state_t* st) {
    if (!st) return 0;
    return laplace_effective_mu_fp(st->rating, st->rd);
}

/* Michaelis-Menten witness saturation, half-max at 4 -- mirrors
 * foundry_witness_sat.sql.in exactly (same constant, same rationale). */
#define LAPLACE_WITNESS_SAT_HALFMAX 4.0

/*
 * SIGN comes from the RATING, magnitude from RD and witness count.
 *
 * Glicko-2 adjudicates win/draw/loss against neutral: rating > neutral means the
 * claim won its matches, rating < neutral means it lost (was refuted). That
 * verdict belongs to the rating alone. RD is a CONFIDENCE interval, not a
 * verdict, and it is already applied below as exp(-kappa*rd).
 *
 * This previously signed on laplace_effective_mu_fp (rating - 2*rd), the
 * conservative lower bound. That double-counted RD -- once in the bound, again
 * in the decay -- and, because a single-witness cell carries a wide RD, pushed
 * the bound below neutral for claims that had WON. Measured over 300k consensus
 * rows: 100% had rating > neutral (all won), yet 99.04% scored negative here.
 * generate_walk.c stops placing at the first non-positive score, so the walk
 * could reach 0.96% of the graph -- an accidental floor at eff_mu >= neutral,
 * precisely the operator-invented floor the substrate invariants forbid.
 *
 * "Uncertain" must not be conflated with "refuted": a wide-RD win ranks LOW
 * (decay and saturation shrink it toward zero) but stays walkable, while a
 * genuine refutation goes negative and still dead-ends. This is consistent with
 * refuted() (mu/refuted.sql.in), which tests the OPTIMISTIC bound
 * rating + 2*rd < neutral -- lost even at its best.
 *
 * eff_mu remains the correct conservative RANKING key everywhere it is used for
 * ordering; only its use as a sign/gate is removed.
 */
double laplace_walk_edge_weight(int64_t rating, int64_t rd, int64_t witness_count,
                                double kappa) {
    int64_t signed_mu_fp = rating - LAPLACE_GLICKO2_NEUTRAL_MU_FP;
    double  signed_mu    = (double) signed_mu_fp / LAPLACE_GLICKO2_FP_SCALE_D;
    double  rd_real      = (double) rd / LAPLACE_GLICKO2_FP_SCALE_D;
    double  decay        = exp(-kappa * rd_real);
    double  wc           = (double) witness_count;
    double  sat          = wc / (wc + LAPLACE_WITNESS_SAT_HALFMAX);
    return signed_mu * decay * sat;
}
