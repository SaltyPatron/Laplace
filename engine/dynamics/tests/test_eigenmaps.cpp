#include <gtest/gtest.h>

#include "laplace/dynamics/eigenmaps.h"

/* Real tests land Chunk 6 Story 6.6 — eigenmaps on a synthetic high-dim
 * cluster (multi-mode Gaussian); verify the target-dim embedding preserves
 * cluster structure. Stub for now. */

TEST(LaplaceDynamicsEigenmaps, StubReturnsError) {
    double pts[12] = {1,0,0, 0,1,0, 0,0,1, 1,1,1};
    double out[16];
    int rc = laplacian_eigenmaps(pts, 4, 3, 2, 4, out);
    EXPECT_NE(rc, 0);
}
