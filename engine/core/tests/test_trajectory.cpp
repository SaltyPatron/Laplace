#include <gtest/gtest.h>

#include "laplace/core/trajectory.h"
#include "laplace/core/hash128.h"

/* Real tests land Chunk 2+ — round-trip lossless (N hashes → mantissa-packed
 * XYZM buffer → N hashes back, byte-identical). Stub for now. */

TEST(LaplaceCoreTrajectory, StubBuildReturnsError) {
    hash128_t h;
    hash128_zero(&h);
    double xyzm[4];
    int rc = trajectory_build(&h, 1, xyzm);
    EXPECT_NE(rc, 0);
}
