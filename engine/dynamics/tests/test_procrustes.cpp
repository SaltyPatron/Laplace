#include <gtest/gtest.h>

#include "laplace/dynamics/procrustes.h"

/* Real tests land Chunk 6 Story 6.8 — fit + apply round-trip on synthetic
 * data with known rotation; residual approaches zero for noise-free input.
 * Stub for now. */

TEST(LaplaceDynamicsProcrustes, StubFitReturnsNull) {
    double src[12] = {1,0,0, 0,1,0, 0,0,1, 1,1,1};
    double tgt[16] = {1,0,0,0, 0,1,0,0, 0,0,1,0, 1,1,1,0};
    procrustes_transform_t* t = procrustes_fit(src, 4, 3, tgt);
    EXPECT_EQ(t, nullptr);
    procrustes_free(t);
}
