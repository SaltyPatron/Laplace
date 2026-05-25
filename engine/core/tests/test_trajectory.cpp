#include <gtest/gtest.h>

#include <cmath>
#include <vector>

#include "laplace/core/trajectory.h"
#include "laplace/core/hash128.h"

/* Round-trip lossless: N constituent hashes → mantissa-packed XYZM buffer →
 * N hashes back, byte-identical, in order. This is the substrate's
 * content-storage codec (ADR 0012); it must be exactly reversible or
 * reconstruction-from-DB cannot be bit-perfect. */

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
    // ADR 0012: each packed component must be a finite normal double in
    // [1,2)∪(-2,-1] so PG geometry accepts it (no NaN/inf).
    std::vector<hash128_t> in(3);
    for (auto& h : in) { h.hi = ~0ull; h.lo = ~0ull; }   // all-ones: worst case
    std::vector<double> xyzm(in.size() * 4);
    ASSERT_EQ(0, trajectory_build(in.data(), in.size(), xyzm.data()));
    for (double d : xyzm) {
        EXPECT_TRUE(std::isfinite(d));
        EXPECT_GE(std::abs(d), 1.0);
        EXPECT_LT(std::abs(d), 2.0);
    }
}

TEST(LaplaceCoreTrajectory, RejectsOverwideTrajectory) {
    // ordinal is uint16 — a single trajectory caps at 65535 direct
    // constituents (tier deeper instead of widening).
    EXPECT_NE(0, trajectory_build(nullptr, 70000, nullptr));
}

TEST(LaplaceCoreTrajectory, EmptyTrajectoryIsValid) {
    double in_dummy = 1.0;
    hash128_t out_dummy;
    EXPECT_EQ(0, trajectory_build(nullptr, 0, &in_dummy));
    EXPECT_EQ(0, trajectory_constituents(nullptr, 0, &out_dummy, 1));
}
