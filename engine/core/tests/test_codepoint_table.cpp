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



TEST(LaplaceWhitespaceLaw, AsciiAndUnicodeSpacesAreWhitespace) {
    
    for (uint32_t cp : {0x09u, 0x0Au, 0x0Bu, 0x0Cu, 0x0Du, 0x20u})
        EXPECT_TRUE(laplace_codepoint_is_whitespace(cp)) << "cp=" << cp;
    
    for (uint32_t cp : {0x00A0u,  
                        0x1680u,  
                        0x2000u, 0x2001u, 0x2002u, 0x2003u, 0x2004u, 0x2005u,
                        0x2006u, 0x2007u, 0x2008u, 0x2009u, 0x200Au,
                        0x2028u,  
                        0x2029u,  
                        0x202Fu,  
                        0x205Fu,  
                        0x3000u}) 
        EXPECT_TRUE(laplace_codepoint_is_whitespace(cp)) << "cp=" << cp;
}

TEST(LaplaceWhitespaceLaw, LettersAndZwspAreNotWhitespace) {
    
    for (uint32_t cp : {0x41u, 0x61u, 0x4E2Du, 0x200Bu, 0xFEFFu, 0x30u})
        EXPECT_FALSE(laplace_codepoint_is_whitespace(cp)) << "cp=" << cp;
}

TEST(LaplaceWhitespaceLaw, AllWhitespaceTextOmniglottal) {
    EXPECT_TRUE(laplace_text_is_all_whitespace((const uint8_t*)"   ", 3));
    EXPECT_TRUE(laplace_text_is_all_whitespace((const uint8_t*)"\t\n ", 3));
    
    const uint8_t ideo[] = {0xE3, 0x80, 0x80};
    EXPECT_TRUE(laplace_text_is_all_whitespace(ideo, sizeof(ideo)));
    
    const uint8_t nbsp_sp[] = {0xC2, 0xA0, 0x20};
    EXPECT_TRUE(laplace_text_is_all_whitespace(nbsp_sp, sizeof(nbsp_sp)));
}

TEST(LaplaceWhitespaceLaw, NonWhitespaceAndEmptyRejected) {
    EXPECT_FALSE(laplace_text_is_all_whitespace((const uint8_t*)"", 0));
    EXPECT_FALSE(laplace_text_is_all_whitespace((const uint8_t*)" a ", 3));
    EXPECT_FALSE(laplace_text_is_all_whitespace((const uint8_t*)"x", 1));
    
    const uint8_t bad[] = {0xFF, 0xFE};
    EXPECT_FALSE(laplace_text_is_all_whitespace(bad, sizeof(bad)));
}
