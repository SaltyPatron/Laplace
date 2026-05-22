#include <gtest/gtest.h>
#include <cstring>

#include "laplace/core/version.h"

TEST(LaplaceCoreVersion, ReportsExpectedString) {
    const char* v = laplace_core_version();
    ASSERT_NE(v, nullptr);
    EXPECT_STREQ(v, "0.1.0");
}
