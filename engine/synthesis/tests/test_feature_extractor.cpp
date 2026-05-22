#include <gtest/gtest.h>

#include "laplace/synthesis/feature_extractor.h"

TEST(LaplaceSynthesisFeatureExtractor, StubLoadReturnsNull) {
    feature_extractor_t* fe = feature_extractor_load("canonical_coord");
    EXPECT_EQ(fe, nullptr);
    feature_extractor_free(fe);
}
