#include <gtest/gtest.h>

#include <cmath>
#include <vector>

#include "laplace/core/trajectory.h"
#include "laplace/core/hash128.h"
#include "laplace/core/mantissa.h"

namespace {
struct ExpandedTrajectoryItem {
    size_t ordinal;
    hash128_t id;
    uint64_t flags;
};

int collect_expanded_trajectory(void* context, size_t ordinal,
                                const hash128_t* id, uint64_t flags) {
    auto* output = static_cast<std::vector<ExpandedTrajectoryItem>*>(context);
    output->push_back({ordinal, *id, flags});
    return 0;
}
}

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

    size_t expanded = 0;
    ASSERT_EQ(0, trajectory_constituent_count(xyzm.data(), vc, &expanded));
    ASSERT_EQ(5u, expanded);
    std::vector<hash128_t> verts(expanded);
    int n = trajectory_constituents(xyzm.data(), vc, verts.data(), verts.size());
    ASSERT_EQ(5, n);
    EXPECT_EQ(A.hi, verts[0].hi); EXPECT_EQ(A.lo, verts[0].lo);
    EXPECT_EQ(A.hi, verts[1].hi); EXPECT_EQ(A.lo, verts[1].lo);
    EXPECT_EQ(B.hi, verts[2].hi); EXPECT_EQ(B.lo, verts[2].lo);
    EXPECT_EQ(C.hi, verts[3].hi); EXPECT_EQ(C.lo, verts[3].lo);
    EXPECT_EQ(C.hi, verts[4].hi); EXPECT_EQ(C.lo, verts[4].lo);
}

TEST(LaplaceCoreTrajectoryRle, AllSameCollapseToOneVertex) {
    hash128_t A = {}; A.hi = 0xDEAD; A.lo = 0xBEEF;
    hash128_t in[4] = { A, A, A, A };
    std::vector<double> xyzm(4 * 4);
    size_t vc = 99;
    ASSERT_EQ(0, trajectory_build_rle(in, 4, xyzm.data(), &vc));
    ASSERT_EQ(1u, vc);

    std::vector<hash128_t> out(4);
    ASSERT_EQ(4, trajectory_constituents(xyzm.data(), 1, out.data(), out.size()));
    for (const hash128_t& h : out) {
        EXPECT_EQ(A.hi, h.hi);
        EXPECT_EQ(A.lo, h.lo);
    }
}

TEST(LaplaceCoreTrajectoryRle, PreservesCompleteFlagPayloadAndSourceOrdinals) {
    hash128_t space = {}; space.hi = 0x11; space.lo = 0x22;
    hash128_t letter = {}; letter.hi = 0x33; letter.lo = 0x44;
    const hash128_t in[] = { space, space, space, letter, space, space };
    const uint64_t atom = laplace_vertex_flags(0, 1, 0x20);
    const uint64_t deep = laplace_vertex_flags(47, 0, 0);
    const uint64_t flags[] = { atom, atom, atom, deep, atom, atom };
    double xyzm[6 * 4];
    size_t vc = 0;

    ASSERT_EQ(0, trajectory_build_flagged_rle(in, flags, 6, xyzm, &vc));
    ASSERT_EQ(3u, vc) << "three repeated source spaces must occupy one stored vertex per run";

    const uint16_t expected_ord[] = { 1, 4, 5 };
    const uint16_t expected_run[] = { 3, 1, 2 };
    const uint64_t expected_flags[] = { atom, deep, atom };
    for (size_t i = 0; i < vc; ++i) {
        mantissa_payload_t p;
        mantissa_unpack(&xyzm[i * 4], &p);
        EXPECT_EQ(expected_ord[i], p.ordinal);
        EXPECT_EQ(expected_run[i], p.run_length);
        EXPECT_EQ(expected_flags[i], p.flags);
        EXPECT_EQ(i == 1 ? letter.hi : space.hi, p.entity_id.hi);
        EXPECT_EQ(i == 1 ? letter.lo : space.lo, p.entity_id.lo);
    }
    size_t expanded = 0;
    ASSERT_EQ(0, trajectory_constituent_count(xyzm, vc, &expanded));
    ASSERT_EQ(6u, expanded);
    hash128_t out[6];
    ASSERT_EQ(6, trajectory_constituents(xyzm, vc, out, 6));
    for (size_t i = 0; i < 6; ++i) {
        EXPECT_EQ(in[i].hi, out[i].hi);
        EXPECT_EQ(in[i].lo, out[i].lo);
    }

    std::vector<ExpandedTrajectoryItem> visited;
    ASSERT_EQ(0, trajectory_visit_constituents(
        xyzm, vc, collect_expanded_trajectory, &visited));
    ASSERT_EQ(6u, visited.size());
    for (size_t i = 0; i < visited.size(); ++i) {
        EXPECT_EQ(i + 1, visited[i].ordinal);
        EXPECT_EQ(in[i].hi, visited[i].id.hi);
        EXPECT_EQ(in[i].lo, visited[i].id.lo);
        EXPECT_EQ(flags[i], visited[i].flags);
    }
}

TEST(LaplaceCoreTrajectoryRle, SameIdWithDifferentFlagsDoesNotCoalesce) {
    hash128_t id = {}; id.hi = 7; id.lo = 8;
    const hash128_t in[] = { id, id };
    const uint64_t flags[] = {
        laplace_vertex_flags(0, 1, 3),
        laplace_vertex_flags(0, 1, 4)
    };
    double xyzm[8];
    size_t vc = 0;
    ASSERT_EQ(0, trajectory_build_flagged_rle(in, flags, 2, xyzm, &vc));
    ASSERT_EQ(2u, vc);
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

TEST(LaplaceCoreTrajectoryRle, LogicalEquivalenceIgnoresOnlyRleEncoding) {
    hash128_t a = {1, 11};
    hash128_t b = {2, 22};
    const hash128_t ids[] = {a, a, a, b, b};
    const uint64_t flags[] = {7, 7, 7, 9, 9};
    double plain[5 * 4];
    double compressed[5 * 4];
    size_t compressed_points = 0;
    ASSERT_EQ(0, trajectory_build_flagged(ids, flags, 5, plain));
    ASSERT_EQ(0, trajectory_build_flagged_rle(
        ids, flags, 5, compressed, &compressed_points));
    ASSERT_EQ(2u, compressed_points);

    EXPECT_EQ(1, trajectory_equivalent(plain, 5, compressed, compressed_points));
    EXPECT_EQ(1, trajectory_equivalent(compressed, compressed_points, plain, 5));
    EXPECT_EQ(1, trajectory_equivalent(nullptr, 0, nullptr, 0));
}

TEST(LaplaceCoreTrajectoryRle, LogicalEquivalencePreservesOrderFlagsAndCount) {
    hash128_t a = {1, 11};
    hash128_t b = {2, 22};
    const hash128_t baseline_ids[] = {a, a, b};
    const uint64_t baseline_flags[] = {7, 7, 9};
    double baseline[3 * 4];
    ASSERT_EQ(0, trajectory_build_flagged(
        baseline_ids, baseline_flags, 3, baseline));

    const hash128_t reordered_ids[] = {a, b, a};
    double reordered[3 * 4];
    ASSERT_EQ(0, trajectory_build_flagged(
        reordered_ids, baseline_flags, 3, reordered));
    EXPECT_EQ(0, trajectory_equivalent(baseline, 3, reordered, 3));

    const uint64_t changed_flags[] = {7, 8, 9};
    double flag_changed[3 * 4];
    ASSERT_EQ(0, trajectory_build_flagged(
        baseline_ids, changed_flags, 3, flag_changed));
    EXPECT_EQ(0, trajectory_equivalent(baseline, 3, flag_changed, 3));

    double shortened[2 * 4];
    ASSERT_EQ(0, trajectory_build_flagged(
        baseline_ids, baseline_flags, 2, shortened));
    EXPECT_EQ(0, trajectory_equivalent(baseline, 3, shortened, 2));
    EXPECT_EQ(-1, trajectory_equivalent(nullptr, 1, baseline, 3));
}
