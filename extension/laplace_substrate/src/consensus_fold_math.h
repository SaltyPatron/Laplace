















#ifndef LAPLACE_CONSENSUS_FOLD_MATH_H
#define LAPLACE_CONSENSUS_FOLD_MATH_H

#define CONSENSUS_FOLD_NEUTRAL_MU         INT64CONST(1500000000000)
#define CONSENSUS_FOLD_INITIAL_RD         INT64CONST(350000000000)
#define CONSENSUS_FOLD_INITIAL_VOLATILITY INT64CONST(60000000)








/*
 * Fold one uniform rating period into a consensus cell. `st` is the cell's own
 * standing; the opponent rating and phi are the evidence's, so the update is
 * driven by surprise against that opponent. CONSENSUS_FOLD_NEUTRAL_MU is the
 * unrated prior for a cell with no record, and the opponent a caller passes
 * when its evidence carries no rating.
 */
static inline int
consensus_fold_apply_partial(glicko2_state_t *st,
                             int64_t opponent_rating,
                             int64_t phi,
                             int64_t games,
                             int64_t sum_score,
                             int64_t tau)
{
    return glicko2_fold_uniform_period(st, opponent_rating, phi,
                                       games, sum_score, tau, 0);
}

#endif 
