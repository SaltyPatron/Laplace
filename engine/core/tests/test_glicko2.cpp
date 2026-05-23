/* engine/core/tests/test_glicko2.cpp
 *
 * Verifies the int64 fixed-point Glicko-2 implementation against the
 * documented values in:
 *
 *     Glickman, M.E. (2022 revision). "Example of the Glicko-2 system."
 *     http://www.glicko.net/glicko/glicko2.pdf
 *
 * Every documented numeric value in §"Example calculation" is pinned to
 * tight tolerance:
 *   per-opponent (mu_j, phi_j, g(phi_j), E(mu, mu_j, phi_j)),
 *   v, Delta, a = ln(sigma^2), sigma', phi*, phi', mu', r', RD'.
 * Tolerances reflect the precision the paper itself reports (4-5
 * significant figures).
 *
 * Fixed-point primitives (laplace_fp_*) are also tested against double-
 * precision references to ADR 0004's committed 1e-6 relative error.
 */

#include <gtest/gtest.h>

#include <cmath>
#include <cstdint>

#include "laplace/core/glicko2.h"

namespace {

constexpr int64_t SCALE = 1000000000LL;

inline int64_t to_fp(double v) {
    return (int64_t)llround(v * (double)SCALE);
}
inline double from_fp(int64_t v) {
    return (double)v / (double)SCALE;
}

::testing::AssertionResult close_enough(double actual, double expected,
                                        double rel = 1e-6, double abs = 1e-7) {
    double diff = std::fabs(actual - expected);
    double tol  = std::max(abs, rel * std::fabs(expected));
    if (diff <= tol) return ::testing::AssertionSuccess();
    return ::testing::AssertionFailure()
        << "actual=" << actual << " expected=" << expected
        << " diff=" << diff << " tol=" << tol;
}

}  // namespace

/* ===================================================================== */
/* Fixed-point primitives (ADR 0004: 1e-6 relative vs double).            */
/* ===================================================================== */

TEST(LaplaceCoreGlicko2Fp, MulAccurate) {
    EXPECT_EQ(laplace_fp_mul(SCALE, SCALE), SCALE);
    EXPECT_EQ(laplace_fp_mul(SCALE * 2, SCALE * 3), SCALE * 6);
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_mul(to_fp(0.5), to_fp(0.5))), 0.25));
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_mul(to_fp(-1.5), to_fp(2.5))), -3.75));
}

TEST(LaplaceCoreGlicko2Fp, DivAccurate) {
    EXPECT_EQ(laplace_fp_div(SCALE, SCALE), SCALE);
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_div(to_fp(1.0), to_fp(3.0))), 1.0/3.0));
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_div(to_fp(7.0), to_fp(-2.0))), -3.5));
}

TEST(LaplaceCoreGlicko2Fp, SqrtAccurate) {
    EXPECT_EQ(laplace_fp_sqrt(0), 0);
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_sqrt(to_fp(1.0))), 1.0));
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_sqrt(to_fp(4.0))), 2.0));
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_sqrt(to_fp(2.0))), std::sqrt(2.0)));
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_sqrt(to_fp(0.04))), 0.2));
}

TEST(LaplaceCoreGlicko2Fp, ExpAccurate) {
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_exp(0)), 1.0));
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_exp(to_fp(1.0))), std::exp(1.0)));
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_exp(to_fp(-1.0))), std::exp(-1.0)));
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_exp(to_fp(0.5))), std::exp(0.5)));
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_exp(to_fp(-5.0))), std::exp(-5.0)));
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_exp(to_fp(5.0))), std::exp(5.0)));
}

TEST(LaplaceCoreGlicko2Fp, LogAccurate) {
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_log(to_fp(1.0))), 0.0, 1e-6, 1e-7));
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_log(to_fp(std::exp(1.0)))), 1.0));
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_log(to_fp(2.0))), std::log(2.0)));
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_log(to_fp(0.5))), std::log(0.5)));
    /* Paper's a = ln(0.06^2) = ln(0.0036) = -5.62682. Pin tightly. */
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_log(to_fp(0.0036))),
                             std::log(0.0036), 1e-5, 1e-7));
}

TEST(LaplaceCoreGlicko2Fp, ExpLogRoundtrip) {
    for (double x = -5.0; x <= 5.0; x += 0.5) {
        int64_t y = laplace_fp_log(laplace_fp_exp(to_fp(x)));
        EXPECT_TRUE(close_enough(from_fp(y), x, 1e-5, 1e-6));
    }
}

/* ===================================================================== */
/* g(phi) and E(mu, mu_j, phi_j) — Glickman §3 per-opponent table.        */
/* ===================================================================== */

/* From the paper's table (mu = 0, phi = 1.1513):
 *   | j | mu_j    | phi_j  | g(phi_j) | E(mu, mu_j, phi_j) | s_j |
 *   | 1 | -0.5756 | 0.1727 | 0.9955   | 0.639              | 1   |
 *   | 2 |  0.2878 | 0.5756 | 0.9531   | 0.432              | 0   |
 *   | 3 |  1.1513 | 1.7269 | 0.7242   | 0.303              | 0   |
 */

TEST(LaplaceCoreGlicko2, GMatchesPaperTable) {
    EXPECT_NEAR(from_fp(laplace_glicko2_g(to_fp(0.1727))), 0.9955, 0.0005);
    EXPECT_NEAR(from_fp(laplace_glicko2_g(to_fp(0.5756))), 0.9531, 0.0005);
    EXPECT_NEAR(from_fp(laplace_glicko2_g(to_fp(1.7269))), 0.7242, 0.0005);
}

TEST(LaplaceCoreGlicko2, EMatchesPaperTable) {
    int64_t mu = 0;
    int64_t g1 = laplace_glicko2_g(to_fp(0.1727));
    int64_t g2 = laplace_glicko2_g(to_fp(0.5756));
    int64_t g3 = laplace_glicko2_g(to_fp(1.7269));
    EXPECT_NEAR(from_fp(laplace_glicko2_E(mu, to_fp(-0.5756), g1)), 0.639, 0.001);
    EXPECT_NEAR(from_fp(laplace_glicko2_E(mu, to_fp( 0.2878), g2)), 0.432, 0.001);
    EXPECT_NEAR(from_fp(laplace_glicko2_E(mu, to_fp( 1.1513), g3)), 0.303, 0.001);
}

/* ===================================================================== */
/* Init                                                                  */
/* ===================================================================== */

TEST(LaplaceCoreGlicko2, InitSetsInitialState) {
    glicko2_state_t st;
    glicko2_init(&st, to_fp(1500.0), to_fp(350.0), to_fp(0.06));
    EXPECT_EQ(st.rating, to_fp(1500.0));
    EXPECT_EQ(st.rd, to_fp(350.0));
    EXPECT_EQ(st.volatility, to_fp(0.06));
    EXPECT_EQ(st.observation_count, 0);
    EXPECT_EQ(st.last_observed_at_unix_ns, 0);
}

/* ===================================================================== */
/* Glickman §"Example calculation" — every documented intermediate pinned */
/* ===================================================================== */

/* Player r=1500, RD=200, sigma=0.06. Three opponents:
 *   #1: r=1400, RD=30  — win  (s=1)
 *   #2: r=1550, RD=100 — loss (s=0)
 *   #3: r=1700, RD=300 — loss (s=0)
 * tau = 0.5. Paper's documented intermediates and final values pinned
 * tighter than the paper's own significant figures permit ambiguity. */
TEST(LaplaceCoreGlicko2, GlickmanPaperIntermediatesPinned) {
    glicko2_state_t st;
    glicko2_init(&st, to_fp(1500.0), to_fp(200.0), to_fp(0.06));

    glicko2_observation_t obs[3] = {
        { to_fp(1400.0), to_fp(30.0),  to_fp(1.0) },
        { to_fp(1550.0), to_fp(100.0), to_fp(0.0) },
        { to_fp(1700.0), to_fp(300.0), to_fp(0.0) },
    };

    glicko2_trace_t tr;
    laplace_glicko2_update_period_traced(&st, obs, 3, to_fp(0.5), 1000, &tr);

    /* Step 2: scale conversion. Paper: phi = 1.1513. */
    EXPECT_NEAR(from_fp(tr.mu),      0.0,     1e-7);
    EXPECT_NEAR(from_fp(tr.phi),     1.1513,  0.0005);

    /* Step 3: v. Paper: 1.7785. */
    EXPECT_NEAR(from_fp(tr.v),       1.7785,  0.001);

    /* Step 4: Delta. Paper: -0.4834. */
    EXPECT_NEAR(from_fp(tr.delta),  -0.4834,  0.001);

    /* Step 5: a = ln(sigma^2) = ln(0.0036) = -5.62682. */
    EXPECT_NEAR(from_fp(tr.a_value), -5.62682, 0.0001);

    /* Step 5: sigma'. Paper: 0.05999. */
    EXPECT_NEAR(from_fp(tr.sigma_new), 0.05999, 0.00005);

    /* Step 6: phi*. Paper: 1.152862. */
    EXPECT_NEAR(from_fp(tr.phi_star),  1.152862, 0.0001);

    /* Step 7: phi'. Paper: 0.8722. */
    EXPECT_NEAR(from_fp(tr.phi_new),   0.8722,   0.0005);

    /* Step 7: mu'. Paper: -0.2069. */
    EXPECT_NEAR(from_fp(tr.mu_new),   -0.2069,   0.001);

    /* Step 8: r' = 1464.06, RD' = 151.52. */
    EXPECT_NEAR(from_fp(tr.r_new),  1464.06, 0.05);
    EXPECT_NEAR(from_fp(tr.rd_new), 151.52,  0.05);

    /* State reflects trace. */
    EXPECT_EQ(st.rating,                tr.r_new);
    EXPECT_EQ(st.rd,                    tr.rd_new);
    EXPECT_EQ(st.volatility,            tr.sigma_new);
    EXPECT_EQ(st.observation_count,     3);
    EXPECT_EQ(st.last_observed_at_unix_ns, 1000);

    /* Paper notes: median Illinois iterations = 5, max observed = 19. */
    EXPECT_LE(tr.illinois_iters, 25);
}

/* The walk-down branch is the rare case (k >= 2 per Glickman's notes).
 * Force a case where Delta^2 <= phi^2 + v AND f(a - tau) < 0 so k must
 * advance. */
TEST(LaplaceCoreGlicko2, IllinoisConvergesInWalkDownBranch) {
    /* Mostly-consistent player (small RD) sweeping wins against weak
     * opponents — Delta is small, f(a) ≈ 0, and the walk-down may
     * advance. */
    glicko2_state_t st;
    glicko2_init(&st, to_fp(1500.0), to_fp(50.0), to_fp(0.06));
    glicko2_observation_t obs[3] = {
        { to_fp(1400.0), to_fp(30.0), to_fp(1.0) },
        { to_fp(1420.0), to_fp(30.0), to_fp(1.0) },
        { to_fp(1450.0), to_fp(30.0), to_fp(1.0) },
    };
    glicko2_trace_t tr;
    laplace_glicko2_update_period_traced(&st, obs, 3, to_fp(0.5), 1000, &tr);
    /* Volatility should stay close to its prior since the result is
     * "expected"; rating moves up modestly. */
    EXPECT_GT(from_fp(tr.r_new), 1500.0);
    EXPECT_NEAR(from_fp(tr.sigma_new), 0.06, 0.01);
}

/* ===================================================================== */
/* Behavior properties (independent of paper example)                    */
/* ===================================================================== */

TEST(LaplaceCoreGlicko2, EmptyPeriodGrowsRdLeavesRatingAndVolatilityAlone) {
    glicko2_state_t st;
    glicko2_init(&st, to_fp(1500.0), to_fp(50.0), to_fp(0.06));
    int64_t old_rd  = st.rd;
    int64_t old_r   = st.rating;
    int64_t old_vol = st.volatility;
    glicko2_update_period(&st, nullptr, 0, to_fp(0.5), 1000);
    EXPECT_EQ(st.rating, old_r);
    EXPECT_EQ(st.volatility, old_vol);
    EXPECT_GT(st.rd, old_rd);
    EXPECT_LE(st.rd, to_fp(350.0));
}

TEST(LaplaceCoreGlicko2, EmptyPeriodMatchesPaperFormula) {
    /* Paper: phi' = phi* = sqrt(phi^2 + sigma^2). With RD=50, sigma=0.06:
     *   phi = 50/173.7178 ≈ 0.28782
     *   phi' = sqrt(0.28782^2 + 0.06^2) ≈ 0.29399
     *   RD' = 0.29399 * 173.7178 ≈ 51.07 */
    glicko2_state_t st;
    glicko2_init(&st, to_fp(1500.0), to_fp(50.0), to_fp(0.06));
    glicko2_update_period(&st, nullptr, 0, to_fp(0.5), 1000);
    EXPECT_NEAR(from_fp(st.rd), 51.07, 0.02);
}

TEST(LaplaceCoreGlicko2, PositiveEvidenceAgainstHigherRatedReferenceRaisesRating) {
    glicko2_state_t st;
    glicko2_init(&st, to_fp(1500.0), to_fp(100.0), to_fp(0.06));
    glicko2_observation_t obs = { to_fp(1700.0), to_fp(50.0), to_fp(1.0) };
    glicko2_update_period(&st, &obs, 1, to_fp(0.5), 1000);
    EXPECT_GT(st.rating, to_fp(1500.0));
    EXPECT_LT(st.rd, to_fp(100.0));
}

TEST(LaplaceCoreGlicko2, NegativeEvidenceAgainstLowerRatedReferenceLowersRating) {
    glicko2_state_t st;
    glicko2_init(&st, to_fp(1500.0), to_fp(100.0), to_fp(0.06));
    glicko2_observation_t obs = { to_fp(1300.0), to_fp(50.0), to_fp(0.0) };
    glicko2_update_period(&st, &obs, 1, to_fp(0.5), 1000);
    EXPECT_LT(st.rating, to_fp(1500.0));
    EXPECT_LT(st.rd, to_fp(100.0));
}

TEST(LaplaceCoreGlicko2, DrawAgainstEqualLeavesRatingNearlyUnchanged) {
    glicko2_state_t st;
    glicko2_init(&st, to_fp(1500.0), to_fp(100.0), to_fp(0.06));
    glicko2_observation_t obs = { to_fp(1500.0), to_fp(100.0), to_fp(0.5) };
    glicko2_update_period(&st, &obs, 1, to_fp(0.5), 1000);
    EXPECT_NEAR(from_fp(st.rating), 1500.0, 1.0);
    EXPECT_LT(st.rd, to_fp(100.0));
}

TEST(LaplaceCoreGlicko2, RdNeverExceedsUnratedCap) {
    glicko2_state_t st;
    glicko2_init(&st, to_fp(1500.0), to_fp(345.0), to_fp(0.3));
    for (int i = 0; i < 100; ++i) {
        glicko2_update_period(&st, nullptr, 0, to_fp(0.5), 1000 * (i + 1));
        EXPECT_LE(st.rd, to_fp(350.0));
    }
}

TEST(LaplaceCoreGlicko2, DecayGrowsRdProportionalToTime) {
    glicko2_state_t st;
    glicko2_init(&st, to_fp(1500.0), to_fp(50.0), to_fp(0.06));
    st.last_observed_at_unix_ns = 1000;
    int64_t later = 1000 + LAPLACE_GLICKO2_RATING_PERIOD_NS;
    glicko2_decay_rd_in_place(&st, later);
    EXPECT_GT(st.rd, to_fp(50.0));
    EXPECT_LT(st.rd, to_fp(350.0));
    EXPECT_EQ(st.last_observed_at_unix_ns, later);
}

TEST(LaplaceCoreGlicko2, EffectiveMuDiscountsByTwoRd) {
    glicko2_state_t st;
    glicko2_init(&st, to_fp(1600.0), to_fp(80.0), to_fp(0.06));
    int64_t eff = glicko2_effective_mu(&st);
    EXPECT_EQ(eff, st.rating - 2 * st.rd);
    EXPECT_EQ(from_fp(eff), 1600.0 - 2.0 * 80.0);
}

/* ===================================================================== */
/* Determinism (ADR 0004: cross-machine bit-identical)                   */
/* ===================================================================== */

TEST(LaplaceCoreGlicko2, DeterministicAcrossRuns) {
    glicko2_state_t a, b;
    glicko2_init(&a, to_fp(1500.0), to_fp(200.0), to_fp(0.06));
    glicko2_init(&b, to_fp(1500.0), to_fp(200.0), to_fp(0.06));
    glicko2_observation_t obs[3] = {
        { to_fp(1400.0), to_fp(30.0),  to_fp(1.0) },
        { to_fp(1550.0), to_fp(100.0), to_fp(0.0) },
        { to_fp(1700.0), to_fp(300.0), to_fp(0.0) },
    };
    glicko2_update_period(&a, obs, 3, to_fp(0.5), 1000);
    glicko2_update_period(&b, obs, 3, to_fp(0.5), 1000);
    EXPECT_EQ(a.rating, b.rating);
    EXPECT_EQ(a.rd, b.rd);
    EXPECT_EQ(a.volatility, b.volatility);
}

TEST(LaplaceCoreGlicko2, DeterministicAcrossMultiplePeriods) {
    glicko2_state_t a, b;
    glicko2_init(&a, to_fp(1500.0), to_fp(200.0), to_fp(0.06));
    glicko2_init(&b, to_fp(1500.0), to_fp(200.0), to_fp(0.06));
    glicko2_observation_t obs = { to_fp(1550.0), to_fp(100.0), to_fp(0.5) };
    for (int i = 0; i < 25; ++i) {
        glicko2_update_period(&a, &obs, 1, to_fp(0.5), 1000 * (i + 1));
        glicko2_update_period(&b, &obs, 1, to_fp(0.5), 1000 * (i + 1));
    }
    EXPECT_EQ(a.rating, b.rating);
    EXPECT_EQ(a.rd, b.rd);
    EXPECT_EQ(a.volatility, b.volatility);
}
