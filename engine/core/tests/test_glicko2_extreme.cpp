#include <gtest/gtest.h>

#include <cstdint>
#include <limits>

#include "laplace/core/glicko2.h"

namespace {

constexpr int64_t SCALE = 1000000000LL;

inline int64_t to_fp(double v) {
    return static_cast<int64_t>(v * static_cast<double>(SCALE));
}

void ExpectSameState(const glicko2_state_t& actual, const glicko2_state_t& expected) {
    EXPECT_EQ(actual.rating, expected.rating);
    EXPECT_EQ(actual.rd, expected.rd);
    EXPECT_EQ(actual.volatility, expected.volatility);
    EXPECT_EQ(actual.last_observed_at_unix_ns, expected.last_observed_at_unix_ns);
    EXPECT_EQ(actual.observation_count, expected.observation_count);
}

} // namespace

TEST(LaplaceCoreGlicko2Extreme, FixedPointMulAndDivAreTotalAtCarrierEdges) {
    const auto lo = std::numeric_limits<int64_t>::min();
    const auto hi = std::numeric_limits<int64_t>::max();

    EXPECT_EQ(laplace_fp_mul(hi, hi), hi);
    EXPECT_EQ(laplace_fp_mul(lo, hi), lo);
    EXPECT_EQ(laplace_fp_mul(hi, SCALE), hi);
    EXPECT_EQ(laplace_fp_mul(lo, SCALE), lo);

    EXPECT_EQ(laplace_fp_div(lo, -1), hi);
    EXPECT_EQ(laplace_fp_div(lo, 1), lo);
    EXPECT_EQ(laplace_fp_div(hi, 1), hi);
    EXPECT_EQ(laplace_fp_div(hi, 0), hi);
    EXPECT_EQ(laplace_fp_div(lo, 0), lo);
}

TEST(LaplaceCoreGlicko2Extreme, ExpIsTotalAcrossFullInt64Carrier) {
    const int64_t low = laplace_fp_exp(std::numeric_limits<int64_t>::min());
    const int64_t high = laplace_fp_exp(std::numeric_limits<int64_t>::max());

    EXPECT_GT(low, 0);
    EXPECT_GT(high, 0);
    EXPECT_LE(high, std::numeric_limits<int64_t>::max() - SCALE);
}

TEST(LaplaceCoreGlicko2Extreme, ExpPositiveTailNeverWrapsAndNegativeTailNeverBecomesZero) {
    int64_t previous = laplace_fp_exp(to_fp(20.0));
    ASSERT_GT(previous, 0);

    for (double x = 20.25; x <= 60.0; x += 0.25) {
        const int64_t current = laplace_fp_exp(to_fp(x));
        EXPECT_GT(current, 0) << "x=" << x;
        EXPECT_GE(current, previous) << "x=" << x;
        previous = current;
    }

    EXPECT_EQ(laplace_fp_exp(to_fp(60.0)), laplace_fp_exp(to_fp(100.0)));
    EXPECT_GT(laplace_fp_exp(to_fp(-60.0)), 0);
    EXPECT_GT(laplace_fp_exp(to_fp(-100.0)), 0);
}

TEST(LaplaceCoreGlicko2Extreme, ExpectedScoreStaysInsideProbabilityDomainAcrossExtremeGap) {
    const int64_t g = SCALE;
    int64_t previous = SCALE;

    for (int opponent_mu = -40; opponent_mu <= 40; ++opponent_mu) {
        const int64_t e = laplace_glicko2_E(0, to_fp(static_cast<double>(opponent_mu)), g);
        const int64_t reverse = laplace_glicko2_E(
            to_fp(static_cast<double>(opponent_mu)), 0, g);

        EXPECT_GE(e, 0) << "opponent_mu=" << opponent_mu;
        EXPECT_LE(e, SCALE) << "opponent_mu=" << opponent_mu;
        EXPECT_LE(e, previous) << "opponent_mu=" << opponent_mu;
        EXPECT_LE(e + reverse, SCALE + 1) << "opponent_mu=" << opponent_mu;
        EXPECT_GE(e + reverse, SCALE - 1) << "opponent_mu=" << opponent_mu;
        previous = e;
    }
}

TEST(LaplaceCoreGlicko2Extreme, ExpectedScoreIsTotalAcrossFullCarrierInputs) {
    constexpr int64_t carriers[] = {
        std::numeric_limits<int64_t>::min(),
        -1,
        0,
        1,
        std::numeric_limits<int64_t>::max()
    };

    for (const int64_t left : carriers) {
        for (const int64_t right : carriers) {
            for (const int64_t g : carriers) {
                const int64_t e = laplace_glicko2_E(left, right, g);
                EXPECT_GE(e, 0) << "left=" << left << " right=" << right << " g=" << g;
                EXPECT_LE(e, SCALE) << "left=" << left << " right=" << right << " g=" << g;
            }
        }
    }
}

TEST(LaplaceCoreGlicko2Extreme, ExtremeUpsetCannotProduceZeroVolatilityOrRunawayPastOpponent) {
    glicko2_state_t state;
    glicko2_init(&state, to_fp(1500.0), to_fp(100.0), to_fp(0.06));

    const int64_t opponent = to_fp(5500.0);
    const int64_t rd = to_fp(100.0);
    const int64_t games = 1;
    const int64_t score = to_fp(1.0);

    ASSERT_EQ(0, glicko2_fold_grouped_period(
        &state, &opponent, &rd, &games, &score, 1,
        LAPLACE_GLICKO2_DEFAULT_TAU, 1000));

    EXPECT_GT(state.rating, to_fp(1500.0));
    EXPECT_LT(state.rating, opponent);
    EXPECT_GT(state.rd, 0);
    EXPECT_LE(state.rd, to_fp(350.0));
    EXPECT_GT(state.volatility, 0);
}

TEST(LaplaceCoreGlicko2Extreme, ThirtySixObservationsCannotEmitIllegalState) {
    glicko2_state_t state;
    glicko2_init(&state, to_fp(1500.0), to_fp(350.0), to_fp(0.06));

    const int64_t opponent = to_fp(1500.0);
    const int64_t rd = to_fp(350.0);
    const int64_t games = 36;
    const int64_t score = 18 * SCALE;

    ASSERT_EQ(0, glicko2_fold_grouped_period(
        &state, &opponent, &rd, &games, &score, 1,
        LAPLACE_GLICKO2_DEFAULT_TAU, 1000));

    EXPECT_GT(state.rd, 0);
    EXPECT_LE(state.rd, to_fp(350.0));
    EXPECT_GT(state.volatility, 0);
    EXPECT_GT(state.rating, to_fp(-10000.0));
    EXPECT_LT(state.rating, to_fp(10000.0));
}

TEST(LaplaceCoreGlicko2Extreme, InvalidScoreAggregateFailsWithoutMutatingPrior) {
    glicko2_state_t state;
    glicko2_init(&state, to_fp(1500.0), to_fp(350.0), to_fp(0.06));
    state.last_observed_at_unix_ns = 1234;
    state.observation_count = 7;
    const glicko2_state_t before = state;

    const int64_t opponent = to_fp(1500.0);
    const int64_t rd = to_fp(350.0);
    const int64_t games = 1;
    const int64_t impossible_score = SCALE + 1;

    EXPECT_NE(0, glicko2_fold_grouped_period(
        &state, &opponent, &rd, &games, &impossible_score, 1,
        LAPLACE_GLICKO2_DEFAULT_TAU, 2000));
    ExpectSameState(state, before);
}

TEST(LaplaceCoreGlicko2Extreme, IllegalPriorFailsWithoutManufacturingAReplacementState) {
    glicko2_state_t state;
    glicko2_init(&state, to_fp(1500.0), to_fp(350.0), 0);
    state.last_observed_at_unix_ns = 1234;
    state.observation_count = 36;
    const glicko2_state_t before = state;

    const int64_t opponent = to_fp(1500.0);
    const int64_t rd = to_fp(350.0);
    const int64_t games = 1;
    const int64_t score = SCALE;

    EXPECT_NE(0, glicko2_fold_grouped_period(
        &state, &opponent, &rd, &games, &score, 1,
        LAPLACE_GLICKO2_DEFAULT_TAU, 2000));
    ExpectSameState(state, before);
}

TEST(LaplaceCoreGlicko2Extreme, EffectiveMuCannotOverflowCarrier) {
    const auto lo = std::numeric_limits<int64_t>::min();
    const auto hi = std::numeric_limits<int64_t>::max();
    EXPECT_EQ(laplace_effective_mu_fp(hi, lo), hi);
    EXPECT_EQ(laplace_effective_mu_fp(lo, hi), lo);
}
