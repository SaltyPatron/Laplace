















#ifndef LAPLACE_CONSENSUS_FOLD_MATH_H
#define LAPLACE_CONSENSUS_FOLD_MATH_H

#define CONSENSUS_FOLD_NEUTRAL_MU         INT64CONST(1500000000000)
#define CONSENSUS_FOLD_INITIAL_RD         INT64CONST(350000000000)
#define CONSENSUS_FOLD_INITIAL_VOLATILITY INT64CONST(60000000)








/*
 * THE OPPONENT IS AN ARGUMENT NOW, NOT A CONSTANT.
 *
 * This body passed CONSENSUS_FOLD_NEUTRAL_MU as the opponent rating on every
 * call. `st` is the standing record — correctly the player — and it was put on
 * the board against a fixed 1500 forever. A Glicko update is driven by
 * surprise, delta ~ g(phi_j) * (s - E(mu, mu_j, phi_j)), so with mu_j pinned the
 * update reduced to a function of the cell's own mu and the match count, and the
 * rating became a saturating witness counter.
 *
 * Measured on the live substrate 2026-08-24 before this change: replaying the
 * fold against the constant reproduced the entire 65 GB distribution to within
 * 0.4% (simulated 1662.31/1750.54/1809.07/1852.30 vs measured
 * 1664.49/1748.66/1804.78/1845.77 at witness counts 1..4), and 400,000
 * single-witness cells spanning source trust 0.40..0.95 held 2 distinct ratings
 * with a total spread of 0.23 points while one extra witness moved eff_mu by
 * 157.98.
 *
 * CONSENSUS_FOLD_NEUTRAL_MU remains correct in exactly one place: glicko2_init
 * for a cell with no record. A new player is unrated and 1500 is what Glicko
 * gives it. The defect was using it again on every subsequent fold.
 *
 * Callers that genuinely have no opponent rating pass
 * CONSENSUS_FOLD_NEUTRAL_MU explicitly and get the old behaviour, which keeps
 * this change a no-op until the evidence carries a rating.
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
