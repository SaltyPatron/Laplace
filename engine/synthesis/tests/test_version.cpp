#include <gtest/gtest.h>

#include "laplace/synthesis/version.h"

TEST(LaplaceSynthesisVersion, ReportsExpectedString) {
    const char* v = laplace_synthesis_version();
    ASSERT_NE(v, nullptr);
    EXPECT_STREQ(v, "0.1.0");
}
