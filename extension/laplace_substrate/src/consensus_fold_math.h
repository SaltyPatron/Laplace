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

/* One period's pre-merged partial applied to a Glicko-2 state. All `games`
 * observations are against the neutral line at opponent φ, so the period is
 * the closed-form uniform-opponent update — O(1), no observation array, and
 * int64-identical to the per-observation loop (regress parity pins it). The
 * `obs` parameter is retained for ABI stability and ignored; callers may pass
 * NULL once their scratch allocations are removed. The caller guards games
 * (> 0, <= 1<<27). */
static inline void
consensus_fold_apply_partial(glicko2_state_t *st,
                             int64_t phi,
                             int64_t games,
                             int64_t sum_score,
                             int64_t tau,
                             glicko2_observation_t *obs)
{
    (void) obs;
    glicko2_fold_uniform_period(st, CONSENSUS_FOLD_NEUTRAL_MU, phi,
                                games, sum_score, tau, 0);
}

#endif /* LAPLACE_CONSENSUS_FOLD_MATH_H */
