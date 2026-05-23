#include <gtest/gtest.h>

#include <cmath>
#include <cstring>

#include "laplace/core/math4d.h"

namespace {
constexpr double kEpsilon = 1e-12;
}

TEST(LaplaceCoreMath4d, DotProductBasicValues) {
    const double a[4] = {1.0, 2.0, 3.0, 4.0};
    const double b[4] = {5.0, 6.0, 7.0, 8.0};
    EXPECT_DOUBLE_EQ(math4d_dot(a, b), 5.0 + 12.0 + 21.0 + 32.0);
}

TEST(LaplaceCoreMath4d, DotProductWithZeroVector) {
    const double a[4] = {1.0, 2.0, 3.0, 4.0};
    const double z[4] = {0.0, 0.0, 0.0, 0.0};
    EXPECT_DOUBLE_EQ(math4d_dot(a, z), 0.0);
}

TEST(LaplaceCoreMath4d, NormOfUnitAxisVectors) {
    const double e0[4] = {1.0, 0.0, 0.0, 0.0};
    const double e1[4] = {0.0, 1.0, 0.0, 0.0};
    const double e2[4] = {0.0, 0.0, 1.0, 0.0};
    const double e3[4] = {0.0, 0.0, 0.0, 1.0};
    EXPECT_DOUBLE_EQ(math4d_norm(e0), 1.0);
    EXPECT_DOUBLE_EQ(math4d_norm(e1), 1.0);
    EXPECT_DOUBLE_EQ(math4d_norm(e2), 1.0);
    EXPECT_DOUBLE_EQ(math4d_norm(e3), 1.0);
}

TEST(LaplaceCoreMath4d, NormOfHalfHalfHalfHalf) {
    const double v[4] = {0.5, 0.5, 0.5, 0.5};
    EXPECT_DOUBLE_EQ(math4d_norm(v), 1.0);
}

TEST(LaplaceCoreMath4d, RadiusFromOriginEqualsNorm) {
    const double v[4] = {0.1, -0.2, 0.3, -0.4};
    EXPECT_DOUBLE_EQ(math4d_radius_from_origin(v), math4d_norm(v));
}

TEST(LaplaceCoreMath4d, DistanceSqAndDistanceConsistent) {
    const double a[4] = {1.0, 2.0, 3.0, 4.0};
    const double b[4] = {5.0, 6.0, 7.0, 8.0};
    const double d2 = math4d_distance_sq(a, b);
    EXPECT_DOUBLE_EQ(d2, 16.0 * 4);
    EXPECT_DOUBLE_EQ(math4d_distance(a, b), std::sqrt(d2));
}

TEST(LaplaceCoreMath4d, DistanceSelfIsZero) {
    const double a[4] = {1.0, -2.0, 3.0, -4.0};
    EXPECT_DOUBLE_EQ(math4d_distance(a, a), 0.0);
    EXPECT_DOUBLE_EQ(math4d_distance_sq(a, a), 0.0);
}

TEST(LaplaceCoreMath4d, DistanceSymmetric) {
    const double a[4] = {0.1, 0.2, 0.3, 0.4};
    const double b[4] = {-0.5, 0.6, -0.7, 0.8};
    EXPECT_DOUBLE_EQ(math4d_distance(a, b), math4d_distance(b, a));
}

TEST(LaplaceCoreMath4d, AngularDistanceIdenticalUnitVectorIsZero) {
    const double a[4] = {0.5, 0.5, 0.5, 0.5};
    EXPECT_NEAR(math4d_angular_distance(a, a), 0.0, kEpsilon);
}

TEST(LaplaceCoreMath4d, AngularDistanceOrthogonalIsPiOverTwo) {
    const double e0[4] = {1.0, 0.0, 0.0, 0.0};
    const double e1[4] = {0.0, 1.0, 0.0, 0.0};
    EXPECT_NEAR(math4d_angular_distance(e0, e1), M_PI / 2.0, kEpsilon);
}

TEST(LaplaceCoreMath4d, AngularDistanceAntipodalIsPi) {
    const double a[4] = {1.0, 0.0, 0.0, 0.0};
    const double b[4] = {-1.0, 0.0, 0.0, 0.0};
    EXPECT_NEAR(math4d_angular_distance(a, b), M_PI, kEpsilon);
}

TEST(LaplaceCoreMath4d, AngularDistanceHandlesFpRoundoffPastUnity) {
    // Two near-identical unit vectors — dot product can land just above 1.0 from
    // FP roundoff; the clamp inside math4d_angular_distance must keep acos finite.
    const double a[4] = {1.0, 0.0, 0.0, 0.0};
    const double b[4] = {1.0, 0.0, 0.0, 0.0};
    const double d = math4d_angular_distance(a, b);
    EXPECT_FALSE(std::isnan(d));
    EXPECT_NEAR(d, 0.0, kEpsilon);
}

TEST(LaplaceCoreMath4d, AddSubScaleRoundTrip) {
    const double a[4] = {1.0, 2.0, 3.0, 4.0};
    const double b[4] = {0.5, -1.5, 2.5, -3.5};
    double sum[4], back[4], scaled[4];
    math4d_add(a, b, sum);
    math4d_sub(sum, b, back);
    EXPECT_DOUBLE_EQ(back[0], a[0]);
    EXPECT_DOUBLE_EQ(back[1], a[1]);
    EXPECT_DOUBLE_EQ(back[2], a[2]);
    EXPECT_DOUBLE_EQ(back[3], a[3]);
    math4d_scale(a, 2.0, scaled);
    EXPECT_DOUBLE_EQ(scaled[0], 2.0);
    EXPECT_DOUBLE_EQ(scaled[1], 4.0);
    EXPECT_DOUBLE_EQ(scaled[2], 6.0);
    EXPECT_DOUBLE_EQ(scaled[3], 8.0);
}

TEST(LaplaceCoreMath4d, CentroidSinglePointIsThatPoint) {
    const double p[4] = {0.1, 0.2, 0.3, 0.4};
    double out[4] = {99, 99, 99, 99};
    math4d_centroid(p, 1, out);
    EXPECT_DOUBLE_EQ(out[0], p[0]);
    EXPECT_DOUBLE_EQ(out[1], p[1]);
    EXPECT_DOUBLE_EQ(out[2], p[2]);
    EXPECT_DOUBLE_EQ(out[3], p[3]);
}

TEST(LaplaceCoreMath4d, CentroidTwoPointsMidpoint) {
    const double points[8] = {
        1.0, 2.0, 3.0, 4.0,
        5.0, 6.0, 7.0, 8.0,
    };
    double out[4];
    math4d_centroid(points, 2, out);
    EXPECT_DOUBLE_EQ(out[0], 3.0);
    EXPECT_DOUBLE_EQ(out[1], 4.0);
    EXPECT_DOUBLE_EQ(out[2], 5.0);
    EXPECT_DOUBLE_EQ(out[3], 6.0);
}

TEST(LaplaceCoreMath4d, CentroidZeroPointsLeavesOutputZeroed) {
    double out[4] = {99, 99, 99, 99};
    math4d_centroid(nullptr, 0, out);
    EXPECT_DOUBLE_EQ(out[0], 0.0);
    EXPECT_DOUBLE_EQ(out[1], 0.0);
    EXPECT_DOUBLE_EQ(out[2], 0.0);
    EXPECT_DOUBLE_EQ(out[3], 0.0);
}

TEST(LaplaceCoreMath4d, CentroidDeterministicAcrossRuns) {
    const double points[16] = {
        0.1, 0.2, 0.3, 0.4,
        -0.5, 0.6, -0.7, 0.8,
        0.9, -0.1, 0.2, -0.3,
        -0.4, 0.5, -0.6, 0.7,
    };
    double a[4], b[4];
    math4d_centroid(points, 4, a);
    math4d_centroid(points, 4, b);
    EXPECT_EQ(0, std::memcmp(a, b, sizeof(a)));
}
