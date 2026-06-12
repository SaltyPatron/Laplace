/*
 * consensus_fold_math.h — the fold step shared by both terminal-fold lanes.
 *
 * consensus_fold_step.c (the SQL-lane ordered aggregate) and
 * consensus_fold_engine.c (the engine lane) must produce int64-identical
 * consensus rows from the same staged input — the regress parity pin
 * (tests/sql/consensus_fold.sql) enforces it. Everything that could drift
 * between the lanes lives here and ONLY here: the neutral prior and the
 * partial-application math (the q/rem observation split of
 * pg_laplace_glicko2_accumulate_games).
 *
 * The constants MUST match 13_mu_law.sql.in (glicko2_neutral_mu /
 * glicko2_initial_rd / glicko2_initial_volatility).
 *
 * Include after postgres.h (INT64CONST) and laplace/core/glicko2.h.
 */
#ifndef LAPLACE_CONSENSUS_FOLD_MATH_H
#define LAPLACE_CONSENSUS_FOLD_MATH_H

#define CONSENSUS_FOLD_NEUTRAL_MU         INT64CONST(1500000000000)
#define CONSENSUS_FOLD_INITIAL_RD         INT64CONST(350000000000)
#define CONSENSUS_FOLD_INITIAL_VOLATILITY INT64CONST(60000000)

/* One period's pre-merged partial applied to a Glicko-2 state. The caller
 * guards games (> 0, <= 1<<27) and supplies a scratch buffer with capacity
 * >= games observations. */
static inline void
consensus_fold_apply_partial(glicko2_state_t *st,
                             int64_t phi,
                             int64_t games,
                             int64_t sum_score,
                             int64_t tau,
                             glicko2_observation_t *obs)
{
    int64_t q   = sum_score / games;
    int64_t rem = sum_score - q * (games - 1);
    int64_t i;

    for (i = 0; i < games - 1; i++)
    {
        obs[i].opponent_rating = CONSENSUS_FOLD_NEUTRAL_MU;
        obs[i].opponent_rd     = phi;
        obs[i].score           = q;
    }
    obs[games - 1].opponent_rating = CONSENSUS_FOLD_NEUTRAL_MU;
    obs[games - 1].opponent_rd     = phi;
    obs[games - 1].score           = rem;

    glicko2_update_period(st, obs, (size_t) games, tau, 0);
}

#endif /* LAPLACE_CONSENSUS_FOLD_MATH_H */
