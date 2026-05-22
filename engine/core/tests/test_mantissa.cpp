#include <gtest/gtest.h>

#include "laplace/core/mantissa.h"

/* Real test cases land Chunk 1 Story 1.7 — round-trip lossless on
 * payload bits (every (tier, position, hash_partial) maps to a unique
 * coord that unpacks back to identical payload). Stub for now. */

TEST(LaplaceCoreMantissa, StubUnpackZeroesPayload) {
    double vertex[4] = {0.5, 0.5, 0.5, 0.5};
    mantissa_payload_t out = {99, 99, 99};
    mantissa_unpack(vertex, &out);
    EXPECT_EQ(out.tier, 0u);
    EXPECT_EQ(out.position, 0u);
    EXPECT_EQ(out.hash_partial, 0u);
}
