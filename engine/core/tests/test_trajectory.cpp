#include <gtest/gtest.h>

#include <cmath>
#include <vector>

#include "laplace/core/trajectory.h"
#include "laplace/core/hash128.h"

TEST(LaplaceCoreTrajectory, BuildThenConstituentsRoundTrips) {
    std::vector<hash128_t> in(5);
    for (size_t i = 0; i < in.size(); ++i) {
        in[i].hi = 0x1122334455667788ull ^ (i * 0x9E3779B97F4A7C15ull);
        in[i].lo = 0xA5A5A5A5DEADBEEFull + i;
    }
    std::vector<double> xyzm(in.size() * 4);
    ASSERT_EQ(0, trajectory_build(in.data(), in.size(), xyzm.data()));

    std::vector<hash128_t> out(in.size());
    int n = trajectory_constituents(xyzm.data(), in.size(), out.data(), out.size());
    ASSERT_EQ((int)in.size(), n);
    for (size_t i = 0; i < in.size(); ++i) {
        EXPECT_EQ(in[i].hi, out[i].hi) << "vertex " << i;
        EXPECT_EQ(in[i].lo, out[i].lo) << "vertex " << i;
    }
}

TEST(LaplaceCoreTrajectory, EveryVertexIsGeometryValidDouble) {
    std::vector<hash128_t> in(3);
    for (auto& h : in) { h.hi = ~0ull; h.lo = ~0ull; }
    std::vector<double> xyzm(in.size() * 4);
    ASSERT_EQ(0, trajectory_build(in.data(), in.size(), xyzm.data()));
    for (double d : xyzm) {
        EXPECT_TRUE(std::isfinite(d));
        EXPECT_GE(std::abs(d), 1.0);
        EXPECT_LT(std::abs(d), 2.0);
    }
}

TEST(LaplaceCoreTrajectory, RejectsOverwideTrajectory) {
    EXPECT_NE(0, trajectory_build(nullptr, 70000, nullptr));
}

TEST(LaplaceCoreTrajectory, EmptyTrajectoryIsValid) {
    double in_dummy = 1.0;
    hash128_t out_dummy;
    EXPECT_EQ(0, trajectory_build(nullptr, 0, &in_dummy));
    EXPECT_EQ(0, trajectory_constituents(nullptr, 0, &out_dummy, 1));
}

TEST(LaplaceCoreTrajectoryRle, AllDistinctMatchesVertexCount) {
    std::vector<hash128_t> in(5);
    for (size_t i = 0; i < in.size(); ++i) {
        in[i].hi = i + 1;
        in[i].lo = i + 100;
    }
    std::vector<double> xyzm(in.size() * 4);
    size_t vc = 99;
    ASSERT_EQ(0, trajectory_build_rle(in.data(), in.size(), xyzm.data(), &vc));
    ASSERT_EQ(in.size(), vc);

    std::vector<hash128_t> out(vc);
    int n = trajectory_constituents(xyzm.data(), vc, out.data(), vc);
    ASSERT_EQ((int)vc, n);
    for (size_t i = 0; i < in.size(); ++i) {
        EXPECT_EQ(in[i].hi, out[i].hi) << "vertex " << i;
        EXPECT_EQ(in[i].lo, out[i].lo) << "vertex " << i;
    }
}

TEST(LaplaceCoreTrajectoryRle, ConsecutiveDuplicatesCollapse) {
    hash128_t A = {}; A.hi = 1; A.lo = 1;
    hash128_t B = {}; B.hi = 2; B.lo = 2;
    hash128_t C = {}; C.hi = 3; C.lo = 3;
    hash128_t in[] = { A, A, B, C, C };
    std::vector<double> xyzm(5 * 4);
    size_t vc = 99;
    ASSERT_EQ(0, trajectory_build_rle(in, 5, xyzm.data(), &vc));
    ASSERT_EQ(3u, vc);

    std::vector<hash128_t> verts(3);
    int n = trajectory_constituents(xyzm.data(), 3, verts.data(), 3);
    ASSERT_EQ(3, n);
    EXPECT_EQ(A.hi, verts[0].hi); EXPECT_EQ(A.lo, verts[0].lo);
    EXPECT_EQ(B.hi, verts[1].hi); EXPECT_EQ(B.lo, verts[1].lo);
    EXPECT_EQ(C.hi, verts[2].hi); EXPECT_EQ(C.lo, verts[2].lo);
}

TEST(LaplaceCoreTrajectoryRle, AllSameCollapseToOneVertex) {
    hash128_t A = {}; A.hi = 0xDEAD; A.lo = 0xBEEF;
    hash128_t in[4] = { A, A, A, A };
    std::vector<double> xyzm(4 * 4);
    size_t vc = 99;
    ASSERT_EQ(0, trajectory_build_rle(in, 4, xyzm.data(), &vc));
    ASSERT_EQ(1u, vc);

    hash128_t out;
    ASSERT_EQ(1, trajectory_constituents(xyzm.data(), 1, &out, 1));
    EXPECT_EQ(A.hi, out.hi);
    EXPECT_EQ(A.lo, out.lo);
}

TEST(LaplaceCoreTrajectoryRle, OutputIsGeometryValidDouble) {
    hash128_t A = {}; A.hi = ~0ull; A.lo = ~0ull;
    hash128_t in[3] = { A, A, A };
    std::vector<double> xyzm(3 * 4);
    size_t vc = 99;
    ASSERT_EQ(0, trajectory_build_rle(in, 3, xyzm.data(), &vc));
    ASSERT_EQ(1u, vc);
    for (int i = 0; i < 4; ++i) {
        EXPECT_TRUE(std::isfinite(xyzm[i]));
        EXPECT_GE(std::abs(xyzm[i]), 1.0);
        EXPECT_LT(std::abs(xyzm[i]), 2.0);
    }
}

TEST(LaplaceCoreTrajectoryRle, EmptyInputIsValid) {
    double dummy;
    size_t vc = 99;
    EXPECT_EQ(0, trajectory_build_rle(nullptr, 0, &dummy, &vc));
    EXPECT_EQ(0u, vc);
}

TEST(LaplaceCoreTrajectoryRle, NullOutArgsFail) {
    hash128_t h = {}; h.hi = 1; h.lo = 1;
    double xyzm[4];
    size_t vc;
    EXPECT_NE(0, trajectory_build_rle(&h, 1, nullptr, &vc));
    EXPECT_NE(0, trajectory_build_rle(&h, 1, xyzm, nullptr));
}
