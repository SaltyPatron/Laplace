#include <gtest/gtest.h>

#include "laplace/dynamics/init.h"

TEST(LaplaceDynamicsInit, ReturnsZeroOnSuccess) {
    EXPECT_EQ(laplace_dynamics_init(), 0);
}

TEST(LaplaceDynamicsInit, ReportsVersion) {
    const char* v = laplace_dynamics_version();
    ASSERT_NE(v, nullptr);
    EXPECT_STREQ(v, "0.1.0");
}
