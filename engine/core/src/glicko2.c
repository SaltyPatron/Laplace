#include "glicko2_internal.h"

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
    if (laplace_glicko2_finish_period(&next, mu, phi, phi_sq, sigma,
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

    int64_t last_opponent_rating = 0;
    int64_t last_opponent_phi = 0;
    int64_t g_j = 0;
    int64_t E_j = 0;
    int64_t information_per_game = 0;

    for (size_t i = 0; i < group_count; ++i) {
        int64_t n = games[i];
        if (n <= 0 || opponent_phis[i] < 0) return -1;
        __int128 maximum_score = (__int128)n * (__int128)LAPLACE_FP_ONE;
        if (score_sums[i] < 0 || (__int128)score_sums[i] > maximum_score)
            return -1;

        if (i == 0 || opponent_ratings[i] != last_opponent_rating ||
            opponent_phis[i] != last_opponent_phi) {
            int64_t mu_j = g1_to_mu(opponent_ratings[i]);
            int64_t phi_j = g1_to_phi(opponent_phis[i]);
            g_j = laplace_glicko2_g(phi_j);
            E_j = laplace_glicko2_E(mu, mu_j, g_j);
            int64_t g_sq = laplace_fp_mul(g_j, g_j);
            int64_t E_1mE = laplace_fp_mul(
                E_j, sat_sub_i64(LAPLACE_FP_ONE, E_j));
            information_per_game = laplace_fp_mul(g_sq, E_1mE);
            last_opponent_rating = opponent_ratings[i];
            last_opponent_phi = opponent_phis[i];
        }

        v_inv_wide += (__int128)n * information_per_game;

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
    if (laplace_glicko2_finish_period(
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
    return clamp_i128((__int128)rating - 2 * (__int128)rd);
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

double laplace_glicko2_expected_score(int64_t rating, int64_t rd)
{
    return (double)laplace_glicko2_expected_score_fp(rating, rd)
           / LAPLACE_GLICKO2_FP_SCALE_D;
}

double laplace_walk_edge_weight(int64_t rating, int64_t rd)
{
    return 2.0 * laplace_glicko2_expected_score(rating, rd) - 1.0;
}
