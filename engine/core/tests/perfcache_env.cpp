#include <gtest/gtest.h>

#include "laplace/core/codepoint_table.h"

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

::testing::Environment* const kPerfcacheEnv =
    ::testing::AddGlobalTestEnvironment(new PerfcacheEnvironment);

}
