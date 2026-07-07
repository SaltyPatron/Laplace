#include <gtest/gtest.h>

#include <cmath>
#include <cstring>

#include "laplace/synthesis/feature_extractor.h"

TEST(LaplaceSynthesisFeatureExtractor, CanonicalCoordLoadAndExtract) {
    feature_extractor_t* fe = feature_extractor_load("canonical_coord");
    ASSERT_NE(fe, nullptr);
    EXPECT_EQ(feature_extractor_output_dim(fe), 4u);

    unsigned char hash[16] = {0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef,
                              0xfe, 0xdc, 0xba, 0x98, 0x76, 0x54, 0x32, 0x10};
    double out[4] = {};
    EXPECT_EQ(feature_extractor_extract(fe, hash, out, 4), 0);

    double n2 = 0.0;
    for (int i = 0; i < 4; ++i) n2 += out[i] * out[i];
    EXPECT_NEAR(n2, 1.0, 1e-9);

    /* Dense: non-degenerate, not a single-axis identity spike. */
    int nonzero = 0;
    for (int i = 0; i < 4; ++i)
        if (std::fabs(out[i]) > 1e-6) ++nonzero;
    EXPECT_GE(nonzero, 2);

    feature_extractor_free(fe);
}

TEST(LaplaceSynthesisFeatureExtractor, UnknownExtractorReturnsNull) {
    EXPECT_EQ(feature_extractor_load("unknown"), nullptr);
}
