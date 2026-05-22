#include <gtest/gtest.h>

#include "laplace/core/glicko2.h"

/* Real test cases land Chunk 5 — Glicko-2 update equations (against
 * Glickman's reference example), int64 fixed-point determinism across
 * runs, RD decay over time, arena/source-credibility-aware effective mu.
 * Stub for now. */

TEST(LaplaceCoreGlicko2, InitSetsInitialState) {
    glicko2_state_t st;
    glicko2_init(&st, 1500000000000LL, 350000000000LL, 60000000LL);
    EXPECT_EQ(st.rating, 1500000000000LL);
    EXPECT_EQ(st.rd, 350000000000LL);
    EXPECT_EQ(st.volatility, 60000000LL);
    EXPECT_EQ(st.observation_count, 0LL);
}
