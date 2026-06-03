#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Glicko-2 fixed-point state ( — scale 1e9, int64 throughout).
 *
 * Field semantics (all at scale 1e9):
 *   rating      = Glicko-1 rating r (e.g. 1500.0 -> 1_500_000_000_000)
 *   rd          = Glicko-1 rating deviation RD (e.g. 350.0 -> 350_000_000_000)
 *   volatility  = Glicko-2 volatility sigma (e.g. 0.06 -> 60_000_000)
 *
 * Bookkeeping:
 *   last_observed_at_unix_ns  -- nanoseconds since epoch of last update
 *   observation_count         -- debug count of resolver-produced evidence items applied; never source-count truth evidence
 */
typedef struct {
    int64_t rating;
    int64_t rd;
    int64_t volatility;
    int64_t last_observed_at_unix_ns;
    int64_t observation_count;
} glicko2_state_t;

/* One observation within a rating period. opponent_rating / opponent_rd are
 * Glicko-1 scale (matching glicko2_state_t.rating / .rd). score is at scale
 * 1e9 in [0, 1e9]: 0 = loss, 5e8 = draw, 1e9 = win. */
typedef struct {
    int64_t opponent_rating;
    int64_t opponent_rd;
    int64_t score;
} glicko2_observation_t;

/* Glicko-2 system constant tau (volatility constraint). Reasonable defaults
 * are 0.3 .. 1.2 per Glickman; we use 0.5 by convention. At scale 1e9 that
 * is 500_000_000. */
#define LAPLACE_GLICKO2_DEFAULT_TAU 500000000LL

/* Convergence tolerance for Illinois algorithm on log-volatility. Scale 1e9.
 * 1e-6 absolute is the textbook value (-> 1000 at our scale). */
#define LAPLACE_GLICKO2_ILLINOIS_EPS 1000LL

/* Rating-period duration for time-based RD decay (per Glickman c² choice).
 * One rating period = 30 days. Adjustable per deployment via decay arg. */
#define LAPLACE_GLICKO2_RATING_PERIOD_NS 2592000000000000LL  /* 30 days */

/* Initialize state from a prior. Sets observation_count = 0 and
 * last_observed_at_unix_ns = 0. */
void glicko2_init(glicko2_state_t* st,
                  int64_t r0,
                  int64_t rd0,
                  int64_t vol0);

/* Apply a full rating period of observations (the batched update specified
 * by Glickman 2013, "Example of the Glicko-2 System"). Updates rating, rd,
 * volatility; sets last_observed_at_unix_ns = now_ns; increments
 * observation_count by n. tau is the Glicko-2 system constant at scale 1e9
 * (use LAPLACE_GLICKO2_DEFAULT_TAU if unsure).
 *
 * If n == 0, applies the pre-rating-period decay only:
 *   phi* = sqrt(phi^2 + sigma^2) -- RD grows, rating unchanged. */
void glicko2_update_period(glicko2_state_t* st,
                           const glicko2_observation_t* obs,
                           size_t n,
                           int64_t tau,
                           int64_t now_ns);

/* Convenience: single-observation update treated as a rating period of one.
 * This is test/debug adapter math, not substrate arena semantics. Production
 * attestation ingestion must resolve incoming evidence through typed arena
 * policy, then call glicko2_update_period with the resolver-produced
 * observation batch. */
void glicko2_update(glicko2_state_t* st,
                    int64_t score,
                    int64_t source_credibility,
                    int64_t now_ns);

/* Apply time-decay to RD when no observations occurred this rating period:
 *   phi' = sqrt(phi^2 + sigma^2 * periods_elapsed)
 *   periods_elapsed = (now_ns - last_observed_at_unix_ns) / RATING_PERIOD_NS
 * Rating unchanged. Bounded above by 350.0 in Glicko-1 scale (the
 * convention for an "unrated" entity). */
void glicko2_decay_rd_in_place(glicko2_state_t* st, int64_t now_ns);

/* Effective mu for cascade scoring: rating discounted by RD-driven
 * uncertainty. arena/source-trust semantics: cascade A*
 * weights edges by a high-confidence quantile of the rating distribution,
 * not raw mu. Returns r - 2*RD at scale 1e9 (the ~95% lower bound). */
int64_t glicko2_effective_mu(const glicko2_state_t* st);

/* Test/debug helpers — fixed-point math primitives used internally. Exposed
 * to the test surface so we can verify the building blocks against
 * double-precision references. NOT part of the substrate public API. */
int64_t laplace_fp_mul(int64_t a, int64_t b);
int64_t laplace_fp_div(int64_t a, int64_t b);
int64_t laplace_fp_sqrt(int64_t x);
int64_t laplace_fp_exp(int64_t x);
int64_t laplace_fp_log(int64_t x);

/* Glicko-2 helper functions exposed so tests can pin them to the worked-
 * example values in Glickman 2013 §"Example calculation". All values at
 * scale 1e9. phi, mu, mu_j are Glicko-2 scale (post-/173.7178); rating /
 * rd in glicko2_state_t are Glicko-1 scale. */
int64_t laplace_glicko2_g(int64_t phi);
int64_t laplace_glicko2_E(int64_t mu, int64_t mu_j, int64_t g_j);

/* Trace of the documented intermediates from one rating-period update.
 * Field names match the paper's notation. All values at scale 1e9 unless
 * noted. mu, phi are Glicko-2 scale. v, delta, sigma_new, phi_star,
 * phi_new, mu_new are Glicko-2 scale. r_new, rd_new are Glicko-1 scale. */
typedef struct {
    int64_t mu;            /* mu          (Step 2) */
    int64_t phi;           /* phi         (Step 2) */
    int64_t v;             /* v           (Step 3) */
    int64_t delta;         /* Delta       (Step 4) */
    int64_t a_value;       /* a = ln(sigma^2)   (Step 5) */
    int64_t sigma_new;     /* sigma'      (Step 5) */
    int64_t phi_star;      /* phi*        (Step 6) */
    int64_t phi_new;       /* phi'        (Step 7) */
    int64_t mu_new;        /* mu'         (Step 7) */
    int64_t r_new;         /* r'          (Step 8, Glicko-1 scale) */
    int64_t rd_new;        /* RD'         (Step 8, Glicko-1 scale) */
    int      illinois_iters; /* observed iteration count */
} glicko2_trace_t;

/* Same semantics as glicko2_update_period; additionally fills *trace with
 * documented intermediates when non-NULL. */
void laplace_glicko2_update_period_traced(glicko2_state_t* st,
                                           const glicko2_observation_t* obs,
                                           size_t n,
                                           int64_t tau,
                                           int64_t now_ns,
                                           glicko2_trace_t* trace);

#ifdef __cplusplus
}
#endif
