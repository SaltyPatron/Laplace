#include <gtest/gtest.h>

#include <cmath>
#include <vector>

#include "laplace/core/trajectory.h"
#include "laplace/core/hash128.h"
#include "laplace/core/mantissa.h"

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

/* Was named RejectsOverwideTrajectory and asserted nothing about width: both
 * pointers are null, so it returned on the null-output check and never reached
 * the width guard it claimed to cover. */
TEST(LaplaceCoreTrajectory, RejectsNullOutput) {
    EXPECT_NE(0, trajectory_build(nullptr, 70000, nullptr));
}

/* A composition wider than the 16-bit ordinal field builds and round-trips.
 * The width of a spare field is not a bound on how many constituents a thing
 * may have; vertex position carries the sequence, so the packed ordinal going
 * un-representable past 65,535 costs a duplicate copy and nothing else. */
TEST(LaplaceCoreTrajectory, WiderThanTheOrdinalFieldRoundTrips) {
    const size_t n = 70000;
    std::vector<hash128_t> in(n);
    for (size_t i = 0; i < n; ++i) { in[i].hi = i + 1; in[i].lo = ~(uint64_t)i; }

    std::vector<double> xyzm(n * 4);
    ASSERT_EQ(0, trajectory_build(in.data(), n, xyzm.data()));

    std::vector<hash128_t> back(n);
    ASSERT_EQ((int)n, trajectory_constituents(xyzm.data(), n, back.data(), n));
    for (size_t i = 0; i < n; ++i) {
        EXPECT_EQ(in[i].hi, back[i].hi) << "constituent " << i;
        EXPECT_EQ(in[i].lo, back[i].lo) << "constituent " << i;
    }
}

/* A run longer than the 16-bit run_length splits across vertices instead of
 * clamping: run_length is what readback expands, so a clamped value would
 * reconstruct fewer constituents than went in. */
TEST(LaplaceCoreTrajectoryRle, RunLongerThanTheFieldSplitsRatherThanClamps) {
    const size_t n = 70000;
    std::vector<hash128_t> in(n);
    for (size_t i = 0; i < n; ++i) { in[i].hi = 7; in[i].lo = 7; }

    std::vector<double> xyzm(n * 4);
    size_t vc = 0;
    ASSERT_EQ(0, trajectory_build_rle(in.data(), n, xyzm.data(), &vc));
    EXPECT_EQ(2u, vc) << "70,000 identical constituents need two vertices at 65,535 each";

    size_t total = 0;
    for (size_t v = 0; v < vc; ++v) {
        mantissa_payload_t p;
        mantissa_unpack(&xyzm[v * 4], &p);
        EXPECT_EQ(7u, p.entity_id.hi);
        total += p.run_length;
    }
    EXPECT_EQ(n, total) << "expanding the runs must return every constituent";
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
