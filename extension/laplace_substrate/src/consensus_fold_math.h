















#ifndef LAPLACE_CONSENSUS_FOLD_MATH_H
#define LAPLACE_CONSENSUS_FOLD_MATH_H

#define CONSENSUS_FOLD_NEUTRAL_MU         INT64CONST(1500000000000)
#define CONSENSUS_FOLD_INITIAL_RD         INT64CONST(350000000000)
#define CONSENSUS_FOLD_INITIAL_VOLATILITY INT64CONST(60000000)








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

#endif 
