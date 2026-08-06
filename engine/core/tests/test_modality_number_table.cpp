#include <gtest/gtest.h>

#include <cstdio>

#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/modality_number_table.h"

#ifndef LAPLACE_MODALITY_NUMBER_PERFCACHE_PATH_FOR_TESTS
#error "LAPLACE_MODALITY_NUMBER_PERFCACHE_PATH_FOR_TESTS must be defined"
#endif

class ModalityNumberTableEnv : public ::testing::Environment {
public:
    void SetUp() override {
        if (modality_number_table_is_loaded()) return;
        const int rc = modality_number_table_load(
            LAPLACE_MODALITY_NUMBER_PERFCACHE_PATH_FOR_TESTS);
        ASSERT_EQ(rc, 0) << "load \"" << LAPLACE_MODALITY_NUMBER_PERFCACHE_PATH_FOR_TESTS
                         << "\" returned " << rc;
    }
};

static ::testing::Environment* const g_mn_env =
    ::testing::AddGlobalTestEnvironment(new ModalityNumberTableEnv);

TEST(ModalityNumberTable, LoadedDense256) {
    (void)g_mn_env;
    ASSERT_TRUE(modality_number_table_is_loaded());
    uint64_t n = 0;
    ASSERT_EQ(modality_number_table_record_count(&n), 0);
    EXPECT_EQ(n, 256u);
}

TEST(ModalityNumberTable, O1LookupMatchesContentRoot) {
    for (uint32_t v : {0u, 5u, 9u, 10u, 255u}) {
        const auto* r = modality_number_table_lookup(v);
        ASSERT_NE(r, nullptr) << "value=" << v;
        EXPECT_EQ(r->value, v);

        char digits[4];
        int len = std::snprintf(digits, sizeof(digits), "%u", v);
        ASSERT_GT(len, 0);
        hash128_t expected{};
        ASSERT_EQ(laplace_content_root_id((const uint8_t*)digits, (size_t)len, &expected), 0);
        EXPECT_TRUE(hash128_equals(&r->id, &expected)) << "value=" << v;
    }
}

TEST(ModalityNumberTable, OutOfRangeIsNull) {
    EXPECT_EQ(modality_number_table_lookup(256u), nullptr);
    EXPECT_EQ(modality_number_table_lookup(0xFFFFFFFFu), nullptr);
}
