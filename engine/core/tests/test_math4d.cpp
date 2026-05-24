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

/* === math4d_frechet ====================================================== */

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
    EXPECT_DOUBLE_EQ(math4d_frechet(p, 1, q, 1), 2.0);  /* sqrt(4) */
}

TEST(LaplaceCoreMath4d, FrechetIdenticalTrajectoriesAreZero) {
    /* Same 3-point trajectory walked at the same speed → leash length 0. */
    const double traj[12] = {
        0.0, 0.0, 0.0, 0.0,
        1.0, 0.0, 0.0, 0.0,
        2.0, 0.0, 0.0, 0.0,
    };
    EXPECT_DOUBLE_EQ(math4d_frechet(traj, 3, traj, 3), 0.0);
}

TEST(LaplaceCoreMath4d, FrechetParallelTrajectoriesEqualOffset) {
    /* Two parallel straight trajectories offset by (0,1,0,0).
     * Each P[i] aligns with Q[i] at distance 1; min leash = 1. */
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
    /* Same geometric line, but P samples only the endpoints while Q
     * samples 5 points along it. DISCRETE Fréchet (unlike continuous)
     * pays the cost of mismatched sampling: at the optimal coupling
     * the middle Q[2] = (2,0,0,0) must pair with either P[0] or P[1],
     * each at distance 2.0. Hand-derived: ca[4,1] = 2.0. */
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
    /* Frechet(P,Q) = Frechet(Q,P). */
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

/* === math4d_hausdorff ==================================================== */

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
    /* By construction symmetric: max(directed(A,B), directed(B,A)). */
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
    /* B is a superset of A. directed(A,B) = 0 (every a∈A is in B exactly).
     * directed(B,A) = max distance from any b∈B to nearest A — = 1.0 here.
     * Symmetric Hausdorff = max(0, 1.0) = 1.0. */
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
