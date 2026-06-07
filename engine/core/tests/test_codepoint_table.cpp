#include <gtest/gtest.h>

#include <cstring>

#include "laplace/core/codepoint_table.h"

TEST(LaplaceCoreCodepointTable, Loaded) {
    EXPECT_TRUE(codepoint_table_is_loaded());
}

TEST(LaplaceCoreCodepointTable, DirectIndexIsCodepoint) {
    for (uint32_t cp : {0x41u, 0x61u, 0x20u, 0x4E2Du, 0x10FFFFu}) {
        const codepoint_entry_t* e = codepoint_table_lookup(cp);
        ASSERT_NE(e, nullptr) << "cp=" << cp;
        EXPECT_EQ(e->codepoint, cp);
    }
}

TEST(LaplaceCoreCodepointTable, OutOfRangeIsNull) {
    EXPECT_EQ(codepoint_table_lookup(0x110000u), nullptr);
    EXPECT_EQ(codepoint_table_lookup(0xFFFFFFFFu), nullptr);
}

TEST(LaplaceCoreCodepointTable, CoordOnUnitGlome) {
    const codepoint_entry_t* e = codepoint_table_lookup(0x41u);
    ASSERT_NE(e, nullptr);
    double r2 = 0.0;
    for (int k = 0; k < 4; ++k) r2 += e->coord[k] * e->coord[k];
    EXPECT_NEAR(r2, 1.0, 1e-9);
}

TEST(LaplaceCoreCodepointTable, PropertyAccessorsKnownValues) {
    EXPECT_EQ(codepoint_table_gb(0x0Du), LAPLACE_GB_CR);
    EXPECT_EQ(codepoint_table_wb(0x0Du), LAPLACE_WB_CR);
    EXPECT_EQ(codepoint_table_sb(0x0Du), LAPLACE_SB_CR);
    EXPECT_EQ(codepoint_table_gb(0x41u), LAPLACE_GB_OTHER);
    EXPECT_EQ(codepoint_table_wb(0x41u), LAPLACE_WB_ALETTER);
    EXPECT_EQ(codepoint_table_sb(0x41u), LAPLACE_SB_UPPER);
    EXPECT_EQ(codepoint_table_ccc(0x41u), 0u);
    EXPECT_EQ(codepoint_table_ccc(0x0301u), 230u);
    EXPECT_EQ(codepoint_table_gb(0x0301u), LAPLACE_GB_EXTEND);
}

TEST(LaplaceCoreCodepointTable, CanonicalDecomposition) {
    const uint32_t* seq = nullptr;
    uint32_t len = 0;
    ASSERT_EQ(codepoint_table_decompose(0x00C0u, &seq, &len), 1);
    ASSERT_EQ(len, 2u);
    EXPECT_EQ(seq[0], 0x41u);
    EXPECT_EQ(seq[1], 0x0300u);
    EXPECT_EQ(codepoint_table_decompose(0x41u, &seq, &len), 0);
}

TEST(LaplaceCoreCodepointTable, CanonicalComposition) {
    uint32_t composed = 0;
    ASSERT_EQ(codepoint_table_compose(0x41u, 0x0300u, &composed), 1);
    EXPECT_EQ(composed, 0x00C0u);
    EXPECT_EQ(codepoint_table_compose(0x41u, 0x42u, &composed), 0);
}

TEST(LaplaceCoreCodepointTable, BulkRecordsExposesWholeArray) {
    const codepoint_entry_t* recs = nullptr;
    uint64_t count = 0;
    ASSERT_EQ(codepoint_table_records(&recs, &count), 0);
    ASSERT_NE(recs, nullptr);
    EXPECT_EQ(count, 1114112u);
    EXPECT_EQ(recs[0x41].codepoint, 0x41u);
    EXPECT_EQ(&recs[0x41], codepoint_table_lookup(0x41u));
    EXPECT_EQ(recs[count - 1].codepoint, 0x10FFFFu);
}

TEST(LaplaceCoreCodepointTable, RejectsBadPath) {
    EXPECT_EQ(codepoint_table_load_perfcache("/nonexistent/perfcache.bin"), -1);
    EXPECT_TRUE(codepoint_table_is_loaded());
    EXPECT_NE(codepoint_table_lookup(0x41u), nullptr);
}
