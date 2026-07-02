#include <gtest/gtest.h>

#include <cstdlib>

#include "laplace/dynamics/init.h"

TEST(LaplaceDynamicsToolchain, MklRequiredAndLinked) {
    ASSERT_EQ(laplace_dynamics_has_mkl(), 1)
        << "LAPLACE_HAS_MKL=0 — configure with setvars + -DLAPLACE_REQUIRE_MKL=ON";
    ASSERT_EQ(laplace_runtime_init(LAPLACE_RUNTIME_HOST_CLI, -1), 0);
    EXPECT_EQ(laplace_runtime_host(), LAPLACE_RUNTIME_HOST_CLI);
    EXPECT_EQ(laplace_dynamics_cbwr_branch(), 10);
    // Env vars are overrides; with none set the runtime self-detects from
    // CPU topology and must still land on a positive thread count. Mirror
    // the runtime's parse_positive_env: non-positive values mean unset.
    const char* expect = std::getenv("MKL_NUM_THREADS");
    if (expect != nullptr && std::atoi(expect) > 0) {
        EXPECT_EQ(laplace_dynamics_mkl_num_threads(), std::atoi(expect));
    } else {
        EXPECT_GE(laplace_dynamics_mkl_num_threads(), 1);
    }
}
