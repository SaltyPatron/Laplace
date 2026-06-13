#include <gtest/gtest.h>

#include <cmath>
#include <cstdint>
#include <vector>

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

}

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
    EXPECT_TRUE(close_enough(from_fp(laplace_fp_log(to_fp(0.0036))),
                             std::log(0.0036), 1e-5, 1e-7));
}

TEST(LaplaceCoreGlicko2Fp, ExpLogRoundtrip) {
    for (double x = -5.0; x <= 5.0; x += 0.5) {
        int64_t y = laplace_fp_log(laplace_fp_exp(to_fp(x)));
        EXPECT_TRUE(close_enough(from_fp(y), x, 1e-5, 1e-6));
    }
}

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

TEST(LaplaceCoreGlicko2, InitSetsInitialState) {
    glicko2_state_t st;
    glicko2_init(&st, to_fp(1500.0), to_fp(350.0), to_fp(0.06));
    EXPECT_EQ(st.rating, to_fp(1500.0));
    EXPECT_EQ(st.rd, to_fp(350.0));
    EXPECT_EQ(st.volatility, to_fp(0.06));
    EXPECT_EQ(st.observation_count, 0);
    EXPECT_EQ(st.last_observed_at_unix_ns, 0);
}

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

    EXPECT_NEAR(from_fp(tr.mu),      0.0,     1e-7);
    EXPECT_NEAR(from_fp(tr.phi),     1.1513,  0.0005);

    EXPECT_NEAR(from_fp(tr.v),       1.7785,  0.001);

    EXPECT_NEAR(from_fp(tr.delta),  -0.4834,  0.001);

    EXPECT_NEAR(from_fp(tr.a_value), -5.62682, 0.0001);

    EXPECT_NEAR(from_fp(tr.sigma_new), 0.05999, 0.00005);

    EXPECT_NEAR(from_fp(tr.phi_star),  1.152862, 0.0001);

    EXPECT_NEAR(from_fp(tr.phi_new),   0.8722,   0.0005);

    EXPECT_NEAR(from_fp(tr.mu_new),   -0.2069,   0.001);

    EXPECT_NEAR(from_fp(tr.r_new),  1464.06, 0.05);
    EXPECT_NEAR(from_fp(tr.rd_new), 151.52,  0.05);

    EXPECT_EQ(st.rating,                tr.r_new);
    EXPECT_EQ(st.rd,                    tr.rd_new);
    EXPECT_EQ(st.volatility,            tr.sigma_new);
    EXPECT_EQ(st.observation_count,     3);
    EXPECT_EQ(st.last_observed_at_unix_ns, 1000);

    EXPECT_LE(tr.illinois_iters, 25);
}

TEST(LaplaceCoreGlicko2, IllinoisConvergesInWalkDownBranch) {
    glicko2_state_t st;
    glicko2_init(&st, to_fp(1500.0), to_fp(50.0), to_fp(0.06));
    glicko2_observation_t obs[3] = {
        { to_fp(1400.0), to_fp(30.0), to_fp(1.0) },
        { to_fp(1420.0), to_fp(30.0), to_fp(1.0) },
        { to_fp(1450.0), to_fp(30.0), to_fp(1.0) },
    };
    glicko2_trace_t tr;
    laplace_glicko2_update_period_traced(&st, obs, 3, to_fp(0.5), 1000, &tr);
    EXPECT_GT(from_fp(tr.r_new), 1500.0);
    EXPECT_NEAR(from_fp(tr.sigma_new), 0.06, 0.01);
}

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

/* The closed-form consensus-fold period (glicko2_fold_uniform_period) must be
 * int64-IDENTICAL to building the games-long q/rem observation array and
 * folding it through glicko2_update_period — that identity is what lets the
 * fold drop the per-relation observation loop. */
TEST(LaplaceCoreGlicko2, FoldUniformMatchesObservationLoop) {
    const int64_t neutral = 1500000000000LL;
    const int64_t tau     = 500000000LL;

    struct Case { int64_t r, rd, vol, phi, games, sum_score; };
    const Case cases[] = {
        { 1500000000000LL, 350000000000LL, 60000000LL,  30000000000LL,  1,  500000000LL },
        { 1500000000000LL, 350000000000LL, 60000000LL,  30000000000LL,  3, 1500000000LL },
        { 1600000000000LL, 120000000000LL, 60000000LL, 200000000000LL, 22, 13000000000LL },
        { 1480000000000LL,  90000000000LL, 61000000LL,  45000000000LL,  7,  2100000000LL },
        { 1500000000000LL, 350000000000LL, 60000000LL, 350000000000LL, 80, 40000000000LL },
        { 1720000000000LL,  40000000000LL, 59000000LL,  12000000000LL,  2,   900000000LL },
    };

    for (const auto& c : cases) {
        glicko2_state_t closed; glicko2_init(&closed, c.r, c.rd, c.vol);
        glicko2_fold_uniform_period(&closed, neutral, c.phi, c.games, c.sum_score, tau, 0);

        glicko2_state_t loop; glicko2_init(&loop, c.r, c.rd, c.vol);
        std::vector<glicko2_observation_t> obs((size_t)c.games);
        int64_t q   = c.sum_score / c.games;
        int64_t rem = c.sum_score - q * (c.games - 1);
        for (int64_t i = 0; i < c.games - 1; ++i)
            obs[(size_t)i] = { neutral, c.phi, q };
        obs[(size_t)(c.games - 1)] = { neutral, c.phi, rem };
        glicko2_update_period(&loop, obs.data(), (size_t)c.games, tau, 0);

        EXPECT_EQ(closed.rating,     loop.rating)     << "games=" << c.games;
        EXPECT_EQ(closed.rd,         loop.rd)         << "games=" << c.games;
        EXPECT_EQ(closed.volatility, loop.volatility) << "games=" << c.games;
        EXPECT_EQ(closed.observation_count, loop.observation_count) << "games=" << c.games;
    }
}
