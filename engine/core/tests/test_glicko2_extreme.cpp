#include <gtest/gtest.h>

#include <cstdint>
#include <cstdlib>

#include "laplace/core/glicko2.h"

namespace {

constexpr int64_t SCALE = 1000000000LL;

inline int64_t to_fp(double v) {
    return static_cast<int64_t>(v * static_cast<double>(SCALE));
}

} // namespace

TEST(LaplaceCoreGlicko2Extreme, ExpPositiveTailNeverWrapsNegative) {
    int64_t previous = laplace_fp_exp(to_fp(20.0));
    ASSERT_GT(previous, 0);

    for (double x = 20.25; x <= 60.0; x += 0.25) {
        const int64_t current = laplace_fp_exp(to_fp(x));
        EXPECT_GT(current, 0) << "x=" << x;
        EXPECT_GE(current, previous) << "x=" << x;
        previous = current;
    }

    EXPECT_EQ(laplace_fp_exp(to_fp(60.0)), laplace_fp_exp(to_fp(100.0)));
}

TEST(LaplaceCoreGlicko2Extreme, ExpectedScoreIsBoundedMonotoneAndSymmetricAcrossExtremeGap) {
    const int64_t g = SCALE;
    int64_t previous = SCALE;

    for (int opponent_mu = -40; opponent_mu <= 40; ++opponent_mu) {
        const int64_t e = laplace_glicko2_E(0, to_fp(static_cast<double>(opponent_mu)), g);
        const int64_t reverse = laplace_glicko2_E(
            to_fp(static_cast<double>(opponent_mu)), 0, g);

        EXPECT_GE(e, 0) << "opponent_mu=" << opponent_mu;
        EXPECT_LE(e, SCALE) << "opponent_mu=" << opponent_mu;
        EXPECT_LE(e, previous) << "opponent_mu=" << opponent_mu;
        EXPECT_LE(std::llabs((e + reverse) - SCALE), 1)
            << "opponent_mu=" << opponent_mu;

        previous = e;
    }
}

TEST(LaplaceCoreGlicko2Extreme, ExtremeUpsetCannotExplodePastOpponent) {
    glicko2_state_t state;
    glicko2_init(&state, to_fp(1500.0), to_fp(100.0), to_fp(0.06));

    const glicko2_observation_t observation = {
        to_fp(5500.0),
        to_fp(100.0),
        to_fp(1.0),
    };

    glicko2_update_period(
        &state, &observation, 1, LAPLACE_GLICKO2_DEFAULT_TAU, 1000);

    EXPECT_GT(state.rating, to_fp(1500.0));
    EXPECT_LT(state.rating, to_fp(5500.0));
    EXPECT_GE(state.rd, 0);
    EXPECT_LE(state.rd, to_fp(350.0));
}
