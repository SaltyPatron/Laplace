#include <gtest/gtest.h>

#include "laplace/core/codepoint_table.h"

/* GoogleTest global environment: load the T0 perf-cache blob once before
 * any test runs, unload it after the suite. The UAX#29/UAX#15 state
 * machines + codepoint_table accessors resolve every property from this
 * mapping, so without it the conformance suites would see DEFAULT (0) for
 * every codepoint. LAPLACE_PERFCACHE_PATH_FOR_TESTS is the blob the build
 * emits from the pinned UCD/UCA. */

namespace {

class PerfcacheEnvironment : public ::testing::Environment {
public:
    void SetUp() override {
        const int rc = codepoint_table_load_perfcache(LAPLACE_PERFCACHE_PATH_FOR_TESTS);
        ASSERT_EQ(0, rc) << "codepoint_table_load_perfcache(\""
                         << LAPLACE_PERFCACHE_PATH_FOR_TESTS << "\") returned " << rc
                         << " (-1 open/mmap, -2 magic/version, -3 count/size, -4 CRC)";
    }
    void TearDown() override { codepoint_table_unload(); }
};

/* gtest_main calls InitGoogleTest() then RUN_ALL_TESTS(); environments
 * registered from a static initializer are honored. */
::testing::Environment* const kPerfcacheEnv =
    ::testing::AddGlobalTestEnvironment(new PerfcacheEnvironment);

}  // namespace
