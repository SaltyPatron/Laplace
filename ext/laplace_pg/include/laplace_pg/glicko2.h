/*
 * glicko2.h — Glicko2Service public API.
 *
 * Phase 2 / Track B / Service B18.
 *
 * Mark E. Glickman, "Example of the Glicko-2 system" (2013). Paper-faithful.
 * Three-layer Glicko-2 in the substrate (Source / Entity / Edge) reuses the
 * same single-rating arithmetic — the layers differ in WHO carries the rating
 * and WHICH events update it, not in the math. This service is the math.
 *
 * Pre-rejected substitutions: ELO. Negative sampling. "Rated based on
 * accuracy". Glicko-2 in the substrate is RATED-SOURCE ATTESTATION:
 * trusted source observes/asserts X → weighted win for X scaled by source's
 * own rating. No loser sampling. Absence = high RD, not low rating.
 */

#ifndef LAPLACE_GLICKO2_H
#define LAPLACE_GLICKO2_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    double mu;       /* internal rating (rating - 1500) / 173.7178 */
    double phi;      /* internal RD (rating_dev / 173.7178)        */
    double sigma;    /* volatility                                  */
    int    games;    /* total games observed                        */
} laplace_glicko2_state_t;

typedef struct {
    double opponent_mu;
    double opponent_phi;
    double score;    /* 1 = win, 0.5 = draw, 0 = loss              */
    double weight;   /* attestation weight (typically opponent rating-derived) */
} laplace_glicko2_observation_t;

/*
 * Apply one rating period (Glickman 2013, §2 + §3).
 *
 * Inputs:
 *   in       : current state
 *   obs      : pointer to n_observations observations (may be NULL if
 *              n_observations == 0; in that case applies the "no games"
 *              decay phi := sqrt(phi^2 + sigma^2))
 *   tau      : system constant (Glickman recommends 0.3-1.2; use 0.5)
 *
 * Output:
 *   out      : new state
 */
void laplace_glicko2_apply(const laplace_glicko2_state_t        *in,
                           const laplace_glicko2_observation_t  *obs,
                           size_t                                n_observations,
                           double                                tau,
                           laplace_glicko2_state_t              *out);

/* Convenience: apply the no-games period decay. */
void laplace_glicko2_period_decay(const laplace_glicko2_state_t *in,
                                  laplace_glicko2_state_t       *out);

/* Conversions to/from public Glicko-2 (rating, rd) scale.
 *  rating      = 173.7178 * mu  + 1500
 *  rating_dev  = 173.7178 * phi
 */
double laplace_glicko2_to_rating(double mu);
double laplace_glicko2_to_rating_dev(double phi);
double laplace_glicko2_from_rating(double rating);
double laplace_glicko2_from_rating_dev(double rating_dev);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_GLICKO2_H */
