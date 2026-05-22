#include <gtest/gtest.h>

#include "laplace/core/hash128.h"

/* Real test cases land Chunk 1 Story 1.3 — BLAKE3 known-answer tests
 * (verify against blake3-team test vectors), truncation correctness,
 * Merkle composition determinism. For now: stub tests proving the
 * C ABI links. */

TEST(LaplaceCoreHash128, StubZeroProducesAllZero) {
    hash128_t h;
    hash128_zero(&h);
    EXPECT_EQ(h.hi, 0u);
    EXPECT_EQ(h.lo, 0u);
}

TEST(LaplaceCoreHash128, CompareSelfIsEqual) {
    hash128_t a, b;
    hash128_zero(&a);
    hash128_zero(&b);
    EXPECT_EQ(hash128_compare(&a, &b), 0);
    EXPECT_TRUE(hash128_equals(&a, &b));
}
