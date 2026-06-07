#include <gtest/gtest.h>

#include <cmath>
#include <cstring>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

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

TEST(LaplaceCoreMath4d, LogExpRoundTripRecoversPoint) {
    const double base[4] = {1.0, 0.0, 0.0, 0.0};
    double p[4] = {0.3, 0.5, -0.2, 0.7};
    const double n = std::sqrt(p[0]*p[0] + p[1]*p[1] + p[2]*p[2] + p[3]*p[3]);
    for (double &c : p) c /= n;

    double tng[4], back[4];
    math4d_log_s3(base, p, tng);
    math4d_exp_s3(base, tng, back);
    EXPECT_NEAR(back[0], p[0], 1e-12);
    EXPECT_NEAR(back[1], p[1], 1e-12);
    EXPECT_NEAR(back[2], p[2], 1e-12);
    EXPECT_NEAR(back[3], p[3], 1e-12);
}

TEST(LaplaceCoreMath4d, LogTangentNormEqualsAngularDistance) {
    const double base[4] = {0.5, 0.5, 0.5, 0.5};
    double p[4] = {0.0, 1.0, 0.0, 0.0};
    double tng[4];
    math4d_log_s3(base, p, tng);
    const double tn = std::sqrt(tng[0]*tng[0] + tng[1]*tng[1] + tng[2]*tng[2] + tng[3]*tng[3]);
    EXPECT_NEAR(tn, math4d_angular_distance(base, p), 1e-12);
}

TEST(LaplaceCoreMath4d, LogTangentIsOrthogonalToBase) {
    const double base[4] = {1.0, 0.0, 0.0, 0.0};
    const double p[4]    = {0.0, 0.0, 1.0, 0.0};
    double tng[4];
    math4d_log_s3(base, p, tng);
    EXPECT_NEAR(math4d_dot(base, tng), 0.0, 1e-12);
}

TEST(LaplaceCoreMath4d, LogOfBaseIsZeroTangent) {
    const double base[4] = {0.0, 1.0, 0.0, 0.0};
    double tng[4];
    math4d_log_s3(base, base, tng);
    EXPECT_NEAR(math4d_norm(tng), 0.0, 1e-15);
}

TEST(LaplaceCoreMath4d, LogOfAntipodeFallsBackToZeroTangent) {
    const double base[4] = {1.0, 0.0, 0.0, 0.0};
    const double anti[4] = {-1.0, 0.0, 0.0, 0.0};
    double tng[4];
    math4d_log_s3(base, anti, tng);
    EXPECT_NEAR(math4d_norm(tng), 0.0, 1e-15);
}

TEST(LaplaceCoreMath4d, ExpZeroTangentReturnsBase) {
    const double base[4] = {0.5, 0.5, 0.5, 0.5};
    const double zero[4] = {0.0, 0.0, 0.0, 0.0};
    double out[4];
    math4d_exp_s3(base, zero, out);
    EXPECT_DOUBLE_EQ(out[0], base[0]);
    EXPECT_DOUBLE_EQ(out[1], base[1]);
    EXPECT_DOUBLE_EQ(out[2], base[2]);
    EXPECT_DOUBLE_EQ(out[3], base[3]);
}

namespace {
constexpr double kKarcherTol = 1e-12;
constexpr int    kKarcherMaxIters = 64;

void ExpectOnSphere(const double v[4], double eps = 1e-12) {
    EXPECT_NEAR(math4d_norm(v), 1.0, eps);
}
}

TEST(LaplaceCoreMath4d, KarcherZeroPointsLeavesOutputZeroed) {
    double out[4] = {99, 99, 99, 99};
    math4d_karcher_mean(nullptr, 0, nullptr, kKarcherTol, kKarcherMaxIters, out);
    EXPECT_DOUBLE_EQ(out[0], 0.0);
    EXPECT_DOUBLE_EQ(out[1], 0.0);
    EXPECT_DOUBLE_EQ(out[2], 0.0);
    EXPECT_DOUBLE_EQ(out[3], 0.0);
}

TEST(LaplaceCoreMath4d, KarcherSinglePointIsThatPointOnSphere) {
    const double p[4] = {0.0, 1.0, 0.0, 0.0};
    double out[4];
    math4d_karcher_mean(p, 1, nullptr, kKarcherTol, kKarcherMaxIters, out);
    EXPECT_DOUBLE_EQ(out[0], 0.0);
    EXPECT_DOUBLE_EQ(out[1], 1.0);
    EXPECT_DOUBLE_EQ(out[2], 0.0);
    EXPECT_DOUBLE_EQ(out[3], 0.0);
    ExpectOnSphere(out);
}

TEST(LaplaceCoreMath4d, KarcherSinglePointNonUnitIsNormalized) {
    const double p[4] = {0.0, 3.0, 0.0, 0.0};
    double out[4];
    math4d_karcher_mean(p, 1, nullptr, kKarcherTol, kKarcherMaxIters, out);
    EXPECT_DOUBLE_EQ(out[1], 1.0);
    ExpectOnSphere(out);
}

TEST(LaplaceCoreMath4d, KarcherAllEqualPointsIsThatPoint) {
    const double pts[12] = {
        0.5, 0.5, 0.5, 0.5,
        0.5, 0.5, 0.5, 0.5,
        0.5, 0.5, 0.5, 0.5,
    };
    double out[4];
    math4d_karcher_mean(pts, 3, nullptr, kKarcherTol, kKarcherMaxIters, out);
    EXPECT_NEAR(out[0], 0.5, 1e-12);
    EXPECT_NEAR(out[1], 0.5, 1e-12);
    EXPECT_NEAR(out[2], 0.5, 1e-12);
    EXPECT_NEAR(out[3], 0.5, 1e-12);
    ExpectOnSphere(out);
}

TEST(LaplaceCoreMath4d, KarcherSymmetricPairIsGeodesicMidpoint) {
    const double a[4] = {1.0, 0.0, 0.0, 0.0};
    const double b[4] = {0.0, 1.0, 0.0, 0.0};
    const double pts[8] = {
        a[0], a[1], a[2], a[3],
        b[0], b[1], b[2], b[3],
    };
    double out[4];
    math4d_karcher_mean(pts, 2, nullptr, kKarcherTol, kKarcherMaxIters, out);

    const double inv_sqrt2 = 1.0 / std::sqrt(2.0);
    EXPECT_NEAR(out[0], inv_sqrt2, 1e-10);
    EXPECT_NEAR(out[1], inv_sqrt2, 1e-10);
    EXPECT_NEAR(out[2], 0.0, 1e-10);
    EXPECT_NEAR(out[3], 0.0, 1e-10);
    ExpectOnSphere(out);
    EXPECT_NEAR(math4d_angular_distance(out, a),
                math4d_angular_distance(out, b), 1e-10);
    EXPECT_NEAR(math4d_angular_distance(out, a), M_PI / 4.0, 1e-10);
}

TEST(LaplaceCoreMath4d, KarcherClusterMeanLiesNearClusterAndOnSphere) {
    double d[4] = {0.2, 0.4, 0.5, 0.7};
    const double dn = std::sqrt(d[0]*d[0] + d[1]*d[1] + d[2]*d[2] + d[3]*d[3]);
    for (double &c : d) c /= dn;

    const double pert[5][4] = {
        { 0.01,  0.00,  0.00,  0.00},
        {-0.01,  0.02,  0.00,  0.00},
        { 0.00, -0.01,  0.015, 0.00},
        { 0.005, 0.00, -0.01,  0.01},
        {-0.005, 0.01,  0.00, -0.012},
    };
    double pts[20];
    for (int i = 0; i < 5; ++i) {
        double q[4] = {d[0] + pert[i][0], d[1] + pert[i][1],
                       d[2] + pert[i][2], d[3] + pert[i][3]};
        const double qn = std::sqrt(q[0]*q[0] + q[1]*q[1] + q[2]*q[2] + q[3]*q[3]);
        for (int k = 0; k < 4; ++k) pts[i * 4 + k] = q[k] / qn;
    }

    double out[4];
    math4d_karcher_mean(pts, 5, nullptr, kKarcherTol, kKarcherMaxIters, out);
    ExpectOnSphere(out);
    EXPECT_LT(math4d_angular_distance(out, d), 0.02);
}

TEST(LaplaceCoreMath4d, KarcherWeightsPullTowardHeavyPoint) {
    const double pts[8] = {
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
    };
    const double a[4] = {1.0, 0.0, 0.0, 0.0};
    const double b[4] = {0.0, 1.0, 0.0, 0.0};
    const double weights[2] = {9.0, 1.0};
    double out[4];
    math4d_karcher_mean(pts, 2, weights, kKarcherTol, kKarcherMaxIters, out);
    ExpectOnSphere(out);
    EXPECT_LT(math4d_angular_distance(out, a), math4d_angular_distance(out, b));
    EXPECT_NEAR(math4d_angular_distance(out, a), 0.1 * (M_PI / 2.0), 1e-9);
}

TEST(LaplaceCoreMath4d, KarcherDeterministicAcrossRuns) {
    double pts[16];
    const double raw[16] = {
        0.1, 0.2, 0.3, 0.4,
        -0.5, 0.6, -0.7, 0.8,
        0.9, -0.1, 0.2, -0.3,
        -0.4, 0.5, -0.6, 0.7,
    };
    for (int i = 0; i < 4; ++i) {
        double n = 0.0;
        for (int k = 0; k < 4; ++k) n += raw[i*4+k] * raw[i*4+k];
        n = std::sqrt(n);
        for (int k = 0; k < 4; ++k) pts[i*4+k] = raw[i*4+k] / n;
    }
    double a[4], b[4];
    math4d_karcher_mean(pts, 4, nullptr, kKarcherTol, kKarcherMaxIters, a);
    math4d_karcher_mean(pts, 4, nullptr, kKarcherTol, kKarcherMaxIters, b);
    EXPECT_EQ(0, std::memcmp(a, b, sizeof(a)));
}

TEST(LaplaceCoreMath4d, KarcherDiffersFromEuclideanCentroid) {
    const double pts[12] = {
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
    };
    double karcher[4];
    math4d_karcher_mean(pts, 3, nullptr, kKarcherTol, kKarcherMaxIters, karcher);

    double centroid[4];
    math4d_centroid(pts, 3, centroid);
    const double cn = math4d_norm(centroid);
    for (double &c : centroid) c /= cn;

    ExpectOnSphere(karcher);
    const double pts2[12] = {
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
    };
    double k2[4], c2[4];
    math4d_karcher_mean(pts2, 3, nullptr, kKarcherTol, kKarcherMaxIters, k2);
    math4d_centroid(pts2, 3, c2);
    const double c2n = math4d_norm(c2);
    for (double &c : c2) c /= c2n;
    EXPECT_GT(math4d_angular_distance(k2, c2), 1e-6);
}

TEST(LaplaceCoreMath4d, FrechetEmptyTrajectoryReturnsNaN) {
    const double p[4] = {0, 0, 0, 0};
    EXPECT_TRUE(std::isnan(math4d_frechet(p, 1, nullptr, 0)));
    EXPECT_TRUE(std::isnan(math4d_frechet(nullptr, 0, p, 1)));
    EXPECT_TRUE(std::isnan(math4d_frechet(nullptr, 0, nullptr, 0)));
}

TEST(LaplaceCoreMath4d, FrechetIdenticalSinglePointIsZero) {
    const double p[4] = {1.0, 2.0, 3.0, 4.0};
    EXPECT_DOUBLE_EQ(math4d_frechet(p, 1, p, 1), 0.0);
}

TEST(LaplaceCoreMath4d, FrechetSinglePointEqualsEuclidean) {
    const double p[4] = {0.0, 0.0, 0.0, 0.0};
    const double q[4] = {1.0, 1.0, 1.0, 1.0};
    EXPECT_DOUBLE_EQ(math4d_frechet(p, 1, q, 1), 2.0);
}

TEST(LaplaceCoreMath4d, FrechetIdenticalTrajectoriesAreZero) {
    const double traj[12] = {
        0.0, 0.0, 0.0, 0.0,
        1.0, 0.0, 0.0, 0.0,
        2.0, 0.0, 0.0, 0.0,
    };
    EXPECT_DOUBLE_EQ(math4d_frechet(traj, 3, traj, 3), 0.0);
}

TEST(LaplaceCoreMath4d, FrechetParallelTrajectoriesEqualOffset) {
    const double p[12] = {
        0.0, 0.0, 0.0, 0.0,
        1.0, 0.0, 0.0, 0.0,
        2.0, 0.0, 0.0, 0.0,
    };
    const double q[12] = {
        0.0, 1.0, 0.0, 0.0,
        1.0, 1.0, 0.0, 0.0,
        2.0, 1.0, 0.0, 0.0,
    };
    EXPECT_DOUBLE_EQ(math4d_frechet(p, 3, q, 3), 1.0);
}

TEST(LaplaceCoreMath4d, FrechetCoarseSamplingPaysGapCost) {
    const double p[8] = {
        0.0, 0.0, 0.0, 0.0,
        4.0, 0.0, 0.0, 0.0,
    };
    const double q[20] = {
        0.0, 0.0, 0.0, 0.0,
        1.0, 0.0, 0.0, 0.0,
        2.0, 0.0, 0.0, 0.0,
        3.0, 0.0, 0.0, 0.0,
        4.0, 0.0, 0.0, 0.0,
    };
    EXPECT_DOUBLE_EQ(math4d_frechet(p, 2, q, 5), 2.0);
}

TEST(LaplaceCoreMath4d, FrechetSymmetric) {
    const double p[8] = {
        0.0, 0.0, 0.0, 0.0,
        1.0, 1.0, 0.0, 0.0,
    };
    const double q[12] = {
        0.0, 0.5, 0.0, 0.0,
        0.5, 0.5, 0.0, 0.0,
        1.0, 0.5, 0.0, 0.0,
    };
    EXPECT_DOUBLE_EQ(math4d_frechet(p, 2, q, 3),
                     math4d_frechet(q, 3, p, 2));
}

TEST(LaplaceCoreMath4d, FrechetDeterministicAcrossRuns) {
    const double p[12] = {0.1, 0.2, 0.3, 0.4,
                          -0.5, 0.6, -0.7, 0.8,
                          0.9, -0.1, 0.2, -0.3};
    const double q[16] = {0.0, 0.0, 0.0, 0.0,
                          0.3, 0.3, 0.3, 0.3,
                          0.6, 0.6, 0.6, 0.6,
                          0.9, 0.9, 0.9, 0.9};
    const double a = math4d_frechet(p, 3, q, 4);
    const double b = math4d_frechet(p, 3, q, 4);
    EXPECT_DOUBLE_EQ(a, b);
}

TEST(LaplaceCoreMath4d, HausdorffEmptySetReturnsNaN) {
    const double p[4] = {0, 0, 0, 0};
    EXPECT_TRUE(std::isnan(math4d_hausdorff(p, 1, nullptr, 0)));
    EXPECT_TRUE(std::isnan(math4d_hausdorff(nullptr, 0, p, 1)));
    EXPECT_TRUE(std::isnan(math4d_hausdorff(nullptr, 0, nullptr, 0)));
}

TEST(LaplaceCoreMath4d, HausdorffIdenticalSetsAreZero) {
    const double pts[12] = {
        0.0, 0.0, 0.0, 0.0,
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
    };
    EXPECT_DOUBLE_EQ(math4d_hausdorff(pts, 3, pts, 3), 0.0);
}

TEST(LaplaceCoreMath4d, HausdorffSinglePointEqualsEuclidean) {
    const double p[4] = {0.0, 0.0, 0.0, 0.0};
    const double q[4] = {3.0, 0.0, 0.0, 0.0};
    EXPECT_DOUBLE_EQ(math4d_hausdorff(p, 1, q, 1), 3.0);
}

TEST(LaplaceCoreMath4d, HausdorffSymmetric) {
    const double a[8] = {
        0.0, 0.0, 0.0, 0.0,
        1.0, 0.0, 0.0, 0.0,
    };
    const double b[12] = {
        0.5, 0.0, 0.0, 0.0,
        1.0, 0.5, 0.0, 0.0,
        2.0, 0.0, 0.0, 0.0,
    };
    EXPECT_DOUBLE_EQ(math4d_hausdorff(a, 2, b, 3),
                     math4d_hausdorff(b, 3, a, 2));
}

TEST(LaplaceCoreMath4d, HausdorffSupersetAndSubset) {
    const double a[8] = {
        0.0, 0.0, 0.0, 0.0,
        2.0, 0.0, 0.0, 0.0,
    };
    const double b[12] = {
        0.0, 0.0, 0.0, 0.0,
        1.0, 0.0, 0.0, 0.0,
        2.0, 0.0, 0.0, 0.0,
    };
    EXPECT_DOUBLE_EQ(math4d_hausdorff(a, 2, b, 3), 1.0);
}

TEST(LaplaceCoreMath4d, HausdorffDeterministicAcrossRuns) {
    const double p[16] = {0.1, 0.2, 0.3, 0.4,
                          -0.5, 0.6, -0.7, 0.8,
                          0.9, -0.1, 0.2, -0.3,
                          -0.4, 0.5, -0.6, 0.7};
    const double q[12] = {0.0, 0.0, 0.0, 0.0,
                          0.5, 0.5, 0.5, 0.5,
                          -0.5, -0.5, -0.5, -0.5};
    const double a = math4d_hausdorff(p, 4, q, 3);
    const double b = math4d_hausdorff(p, 4, q, 3);
    EXPECT_DOUBLE_EQ(a, b);
}
