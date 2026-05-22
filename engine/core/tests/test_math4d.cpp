#include <gtest/gtest.h>

#include "laplace/core/math4d.h"

/* Real test cases land Chunk 1 Story 1.1 — verify distance/dot/norm/centroid
 * against hand-computed values + cross-machine determinism. For now: stub
 * tests proving the C ABI links and returns predictable placeholder values. */

TEST(LaplaceCoreMath4d, StubDistanceReturnsZero) {
    double a[4] = {0, 0, 0, 0};
    double b[4] = {1, 1, 1, 1};
    EXPECT_DOUBLE_EQ(math4d_distance(a, b), 0.0);
}

TEST(LaplaceCoreMath4d, StubCentroidZeroesOutput) {
    double points[8] = {1, 2, 3, 4, 5, 6, 7, 8};
    double out[4] = {99, 99, 99, 99};
    math4d_centroid(points, 2, out);
    EXPECT_DOUBLE_EQ(out[0], 0.0);
}
