#include <gtest/gtest.h>

#include <algorithm>
#include <cstdint>
#include <vector>
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
    EXPECT_EQ(laplace_effective_mu_fp(hi, hi), -hi);
    EXPECT_EQ(laplace_effective_mu_fp(lo, lo), hi);
}

TEST(LaplaceCoreGlicko2Extreme, RefutationUsesWideOptimisticBound) {
    const auto lo = std::numeric_limits<int64_t>::min();
    const auto hi = std::numeric_limits<int64_t>::max();
    EXPECT_FALSE(laplace_glicko2_refuted(hi, to_fp(350)));
    EXPECT_TRUE(laplace_glicko2_refuted(lo, to_fp(350)));
    EXPECT_FALSE(laplace_glicko2_refuted(hi, hi));
    EXPECT_TRUE(laplace_glicko2_refuted(lo, lo));
    EXPECT_FALSE(laplace_glicko2_refuted(to_fp(800), to_fp(350)));
    EXPECT_TRUE(laplace_glicko2_refuted(to_fp(799), to_fp(350)));
}

TEST(LaplaceCoreGlicko2Extreme, SaturatedDatabasePriorCannotBePublishedAgain) {
    for (const int64_t rating : {std::numeric_limits<int64_t>::max(),
                                 std::numeric_limits<int64_t>::min()}) {
        glicko2_state_t state;
        glicko2_init(&state, rating, to_fp(350), 90682809647104LL);
        const auto before = state;
        EXPECT_NE(0, glicko2_fold_uniform_period(&state, to_fp(1500), to_fp(62),
            1, SCALE, LAPLACE_GLICKO2_DEFAULT_TAU, 1000));
        ExpectSameState(state, before);
    }
}


TEST(LaplaceCoreGlicko2Extreme, WideIllinoisIntermediateKeepsARepresentableUpset) {
    glicko2_state_t state;
    glicko2_init(&state, 1500000000000LL, 100000000000LL, 60000000LL);
    ASSERT_EQ(0, glicko2_fold_uniform_period(
        &state, 5500000000000LL, 100000000000LL, 1, SCALE,
        LAPLACE_GLICKO2_DEFAULT_TAU, 1000));
    // Independent arbitrary-integer Q1e9 equations, preserving each rounding
    // step. delta^2 and the Illinois denominator exceed the public int64 range.
    EXPECT_EQ(state.rating, 1555463991368LL);
    EXPECT_EQ(state.rd, 100541955192LL);
    EXPECT_EQ(state.volatility, 60012266LL);
    EXPECT_EQ(state.observation_count, 1);
}

TEST(LaplaceCoreGlicko2Extreme, WideDeltaDoesNotRejectARepresentableSmallTauPeriod) {
    glicko2_state_t state;
    glicko2_init(&state, 1500000000000LL, 100000000000LL, 60000000LL);
    ASSERT_EQ(0, glicko2_fold_uniform_period(
        &state, 5500000000000LL, 100000000000LL, 458, 458000000000LL,
        1000000LL, 1000));
    // delta itself exceeds int64 here, while all published fields fit. A guard
    // which rejects a saturated delta before doing wide math would be wrong.
    EXPECT_EQ(state.rating, 26902490117592LL);
    EXPECT_EQ(state.rd, 100541919754LL);
    EXPECT_EQ(state.volatility, 60010295LL);
    EXPECT_EQ(state.observation_count, 458);
}

TEST(LaplaceCoreGlicko2Extreme, AlternatingPeriodsCannotPublishSaturatedIntermediateState) {
    glicko2_state_t state;
    glicko2_init(&state, 1500000000000LL, 350000000000LL, 60000000LL);
    ASSERT_EQ(0, glicko2_fold_uniform_period(
        &state, 1756000000000LL, 62000000000LL, 458, 458000000000LL,
        LAPLACE_GLICKO2_DEFAULT_TAU, 1000));
    EXPECT_EQ(state.rating, 2425413480906LL);
    EXPECT_EQ(state.rd, 21023642064LL);
    EXPECT_EQ(state.volatility, 60019914LL);
    ASSERT_EQ(0, glicko2_fold_uniform_period(
        &state, 1756000000000LL, 62000000000LL, 458, 0,
        LAPLACE_GLICKO2_DEFAULT_TAU, 2000));
    EXPECT_EQ(state.rating, -5487708174176LL);
    EXPECT_EQ(state.rd, 55934962386LL);
    EXPECT_EQ(state.volatility, 5336093260LL);
    EXPECT_EQ(state.observation_count, 916);
    const auto before = state;

    // The wide Q1e9 reference gives sigma=23800720923308326912 (>INT64_MAX).
    // The former saturated nonlinear solve instead accepted a spurious finite
    // rating near 2.86 million. Neither output clipping nor prior reset is valid.
    EXPECT_NE(0, glicko2_fold_uniform_period(
        &state, 1756000000000LL, 62000000000LL, 458, 458000000000LL,
        LAPLACE_GLICKO2_DEFAULT_TAU, 3000));
    ExpectSameState(state, before);

    std::vector<glicko2_observation_t> observations(
        458, {1756000000000LL, 62000000000LL, SCALE});
    glicko2_update_period(&state, observations.data(), observations.size(),
                          LAPLACE_GLICKO2_DEFAULT_TAU, 3000);
    ExpectSameState(state, before);
}

TEST(LaplaceCoreGlicko2Extreme, ActualRejectedStoredPriorRemainsUnchanged) {
    glicko2_state_t state;
    glicko2_init(&state, -1947199190011015233LL, 350000000000LL, 482453157376LL);
    state.observation_count = 2474;
    state.last_observed_at_unix_ns = 2000;
    const auto before = state;
    EXPECT_NE(0, glicko2_fold_uniform_period(
        &state, 1756000000000LL, 62000000000LL, 458, 259000000000LL,
        LAPLACE_GLICKO2_DEFAULT_TAU, 3000));
    ExpectSameState(state, before);
}

TEST(LaplaceCoreGlicko2Extreme, CanonicalRetainedEvidenceKeepsExactSixtyGroupInputs) {
    // Authenticated retained testimony of the rejected cell: 60 distinct A
    // groups, 2474 observations, total score1115e9. Transport chunks are not
    // additional Glicko periods. Keep every group's original rounding boundary.
    struct Evidence { int64_t games, score; };
    const Evidence evidence[] = {
        {89, INT64_C(89000000000)},
        {20, INT64_C(0)},
        {49, INT64_C(49000000000)},
        {57, INT64_C(57000000000)},
        {36, INT64_C(0)},
        {70, INT64_C(35000000000)},
        {18, INT64_C(0)},
        {59, INT64_C(59000000000)},
        {59, INT64_C(59000000000)},
        {6, INT64_C(0)},
        {45, INT64_C(22500000000)},
        {41, INT64_C(41000000000)},
        {34, INT64_C(0)},
        {51, INT64_C(0)},
        {61, INT64_C(61000000000)},
        {54, INT64_C(0)},
        {20, INT64_C(20000000000)},
        {16, INT64_C(0)},
        {29, INT64_C(0)},
        {15, INT64_C(15000000000)},
        {34, INT64_C(34000000000)},
        {10, INT64_C(10000000000)},
        {42, INT64_C(0)},
        {47, INT64_C(0)},
        {59, INT64_C(0)},
        {73, INT64_C(0)},
        {44, INT64_C(0)},
        {2, INT64_C(0)},
        {53, INT64_C(53000000000)},
        {67, INT64_C(67000000000)},
        {18, INT64_C(18000000000)},
        {10, INT64_C(5000000000)},
        {48, INT64_C(0)},
        {39, INT64_C(39000000000)},
        {13, INT64_C(6500000000)},
        {41, INT64_C(0)},
        {12, INT64_C(12000000000)},
        {69, INT64_C(0)},
        {44, INT64_C(0)},
        {6, INT64_C(6000000000)},
        {57, INT64_C(57000000000)},
        {57, INT64_C(0)},
        {77, INT64_C(77000000000)},
        {77, INT64_C(38500000000)},
        {55, INT64_C(0)},
        {35, INT64_C(35000000000)},
        {42, INT64_C(0)},
        {19, INT64_C(19000000000)},
        {28, INT64_C(0)},
        {28, INT64_C(14000000000)},
        {46, INT64_C(23000000000)},
        {76, INT64_C(0)},
        {40, INT64_C(20000000000)},
        {58, INT64_C(58000000000)},
        {28, INT64_C(0)},
        {32, INT64_C(0)},
        {40, INT64_C(0)},
        {31, INT64_C(15500000000)},
        {10, INT64_C(0)},
        {78, INT64_C(0)}
    };
    std::vector<int64_t> ratings, rds, games, scores;
    std::vector<glicko2_observation_t> observations;
    for (const auto& e : evidence) {
        ratings.push_back(1756000000000LL);
        rds.push_back(62000000000LL);
        games.push_back(e.games);
        scores.push_back(e.score);
        const int64_t q = e.score / e.games;
        for (int64_t i = 0; i < e.games; ++i)
            observations.push_back({ratings.back(), rds.back(),
                i + 1 == e.games ? e.score - q * (e.games - 1) : q});
    }
    ASSERT_EQ(games.size(), 60u);
    ASSERT_EQ(observations.size(), 2474u);
    glicko2_state_t actual, materialized, reversed;
    glicko2_init(&actual, 1500000000000LL, 350000000000LL, 60000000LL);
    materialized = reversed = actual;
    ASSERT_EQ(0, glicko2_fold_grouped_period(
        &actual, ratings.data(), rds.data(), games.data(), scores.data(), games.size(),
        LAPLACE_GLICKO2_DEFAULT_TAU, 1000));
    EXPECT_EQ(actual.rating, 1798219672794LL);
    EXPECT_EQ(actual.rd, 9058987541LL);
    EXPECT_EQ(actual.volatility, 59999089LL);
    EXPECT_EQ(actual.observation_count, 2474);
    glicko2_update_period(&materialized, observations.data(), observations.size(),
                          LAPLACE_GLICKO2_DEFAULT_TAU, 1000);
    ExpectSameState(materialized, actual);
    std::reverse(games.begin(), games.end());
    std::reverse(scores.begin(), scores.end());
    ASSERT_EQ(0, glicko2_fold_grouped_period(
        &reversed, ratings.data(), rds.data(), games.data(), scores.data(), games.size(),
        LAPLACE_GLICKO2_DEFAULT_TAU, 1000));
    ExpectSameState(reversed, actual);
}
