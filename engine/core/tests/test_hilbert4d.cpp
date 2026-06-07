#include <gtest/gtest.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <random>
#include <vector>

#include "laplace/core/hilbert4d.h"
#include "laplace/core/math4d.h"

namespace {

struct U128 { uint64_t hi, lo; };

U128 to_u128(const hilbert128_t& h) {
    U128 r{0, 0};
    for (int i = 0; i < 8;  ++i) r.hi = (r.hi << 8) | h.bytes[i];
    for (int i = 8; i < 16; ++i) r.lo = (r.lo << 8) | h.bytes[i];
    return r;
}

bool u128_less(const U128& a, const U128& b) {
    if (a.hi != b.hi) return a.hi < b.hi;
    return a.lo < b.lo;
}

U128 u128_abs_diff(const U128& a, const U128& b) {
    const U128& big   = u128_less(a, b) ? b : a;
    const U128& small = u128_less(a, b) ? a : b;
    const uint64_t lo = big.lo - small.lo;
    const uint64_t borrow = (big.lo < small.lo) ? 1 : 0;
    const uint64_t hi = big.hi - small.hi - borrow;
    return {hi, lo};
}

}

TEST(LaplaceCoreHilbert4d, EncodeIsDeterministic) {
    const double p[4] = {0.5, -0.5, 0.25, -0.25};
    hilbert128_t h1, h2;
    hilbert4d_encode(p, &h1);
    hilbert4d_encode(p, &h2);
    EXPECT_EQ(0, std::memcmp(h1.bytes, h2.bytes, sizeof(h1.bytes)));
}

TEST(LaplaceCoreHilbert4d, OriginEncodesIntoLowHalfOfIndexSpace) {
    const double p[4] = {0.0, 0.0, 0.0, 0.0};
    hilbert128_t h;
    hilbert4d_encode(p, &h);
    bool all_zero = std::all_of(std::begin(h.bytes), std::end(h.bytes),
                                [](uint8_t b) { return b == 0; });
    bool all_ones = std::all_of(std::begin(h.bytes), std::end(h.bytes),
                                [](uint8_t b) { return b == 0xFF; });
    EXPECT_FALSE(all_zero);
    EXPECT_FALSE(all_ones);
}

TEST(LaplaceCoreHilbert4d, DecodeIsInverseOfEncodeWithinQuantization) {
    std::mt19937_64 rng(0xC0FFEEULL);
    std::uniform_real_distribution<double> u(-0.999, 0.999);

    const double tolerance = 2.0 / (1ULL << 31);

    for (int trial = 0; trial < 1000; ++trial) {
        const double p[4] = {u(rng), u(rng), u(rng), u(rng)};
        hilbert128_t h;
        hilbert4d_encode(p, &h);
        double q[4];
        hilbert4d_decode(&h, q);
        for (int i = 0; i < 4; ++i) {
            EXPECT_NEAR(p[i], q[i], tolerance)
                << "trial " << trial << " dim " << i;
        }
    }
}

TEST(LaplaceCoreHilbert4d, BoundaryClampsAreStable) {
    const double low[4]  = {-1.0, -1.0, -1.0, -1.0};
    const double high[4] = { 1.0,  1.0,  1.0,  1.0};
    const double over[4] = { 2.0,  2.0,  2.0,  2.0};
    const double under[4] = {-2.0, -2.0, -2.0, -2.0};
    hilbert128_t h_low, h_high, h_over, h_under;
    hilbert4d_encode(low,   &h_low);
    hilbert4d_encode(high,  &h_high);
    hilbert4d_encode(over,  &h_over);
    hilbert4d_encode(under, &h_under);
    EXPECT_EQ(0, std::memcmp(h_high.bytes, h_over.bytes, sizeof(h_high.bytes)));
    EXPECT_EQ(0, std::memcmp(h_low.bytes,  h_under.bytes, sizeof(h_low.bytes)));
}

TEST(LaplaceCoreHilbert4d, CompareMatchesMemcmp) {
    std::mt19937_64 rng(0xBEEFULL);
    std::uniform_real_distribution<double> u(-0.999, 0.999);

    for (int trial = 0; trial < 256; ++trial) {
        const double pa[4] = {u(rng), u(rng), u(rng), u(rng)};
        const double pb[4] = {u(rng), u(rng), u(rng), u(rng)};
        hilbert128_t a, b;
        hilbert4d_encode(pa, &a);
        hilbert4d_encode(pb, &b);
        const int engine = hilbert128_compare(&a, &b);
        const int memcmp_result = std::memcmp(a.bytes, b.bytes, sizeof(a.bytes));
        const int en = (engine > 0) - (engine < 0);
        const int mn = (memcmp_result > 0) - (memcmp_result < 0);
        EXPECT_EQ(en, mn) << "trial " << trial;
    }
}

TEST(LaplaceCoreHilbert4d, ConsecutiveIndicesProduceAdjacentCells) {
    auto index_to_cell = [](uint64_t i, uint32_t cells[4]) {
        hilbert128_t h;
        std::memset(h.bytes, 0, 16);
        for (int k = 0; k < 8; ++k) {
            h.bytes[15 - k] = (uint8_t)(i >> (k * 8));
        }
        double q[4];
        hilbert4d_decode(&h, q);
        for (int d = 0; d < 4; ++d) {
            double u = (q[d] + 1.0) * 2147483648.0 - 0.5;
            cells[d] = (uint32_t)(u + 0.5);
        }
    };

    constexpr uint64_t MAX_I = 1ULL << 16;
    uint32_t prev[4];
    index_to_cell(0, prev);
    int total = 0;
    int adjacent = 0;
    for (uint64_t i = 1; i < MAX_I; ++i) {
        uint32_t curr[4];
        index_to_cell(i, curr);
        int diff_dims = 0;
        int delta_in_diff_dim = 0;
        for (int d = 0; d < 4; ++d) {
            if (curr[d] != prev[d]) {
                ++diff_dims;
                int64_t delta = (int64_t)curr[d] - (int64_t)prev[d];
                if (delta < 0) delta = -delta;
                if (delta == 1) delta_in_diff_dim = 1;
            }
        }
        ++total;
        if (diff_dims == 1 && delta_in_diff_dim == 1) ++adjacent;
        std::memcpy(prev, curr, sizeof(prev));
    }
    const double frac = (double)adjacent / (double)total;
    EXPECT_GT(frac, 0.999)
        << "Decoded consecutive Hilbert indices must yield adjacent cells "
        << "in exactly one dim by exactly 1 cell. Got " << adjacent << "/"
        << total << " = " << frac;
}

TEST(LaplaceCoreHilbert4d, AdjacentCellsAreHilbertNearby) {
    std::mt19937_64 rng(0xABCDEFULL);
    std::uniform_real_distribution<double> u(-0.95, 0.95);

    constexpr int N_ANCHORS = 200;
    constexpr double CELL_STEP = 1.0 / (1ULL << 31);

    const U128 SMALL_JUMP_THRESHOLD{0, 256};

    int small_jumps = 0;
    int total = 0;

    for (int trial = 0; trial < N_ANCHORS; ++trial) {
        double anchor[4] = {u(rng), u(rng), u(rng), u(rng)};
        hilbert128_t h_anchor;
        hilbert4d_encode(anchor, &h_anchor);
        U128 ai = to_u128(h_anchor);

        for (int d = 0; d < 4; ++d) {
            double neighbor[4] = {anchor[0], anchor[1], anchor[2], anchor[3]};
            neighbor[d] += CELL_STEP;
            if (neighbor[d] >= 1.0) continue;
            hilbert128_t h_neighbor;
            hilbert4d_encode(neighbor, &h_neighbor);
            U128 ni = to_u128(h_neighbor);
            U128 diff = u128_abs_diff(ai, ni);
            if (u128_less(diff, SMALL_JUMP_THRESHOLD) ||
                (diff.hi == SMALL_JUMP_THRESHOLD.hi && diff.lo == SMALL_JUMP_THRESHOLD.lo)) {
                ++small_jumps;
            }
            ++total;
        }
    }

    const double frac = (double)small_jumps / (double)total;
    EXPECT_GT(frac, 0.70)
        << "Single-cell-neighbor Hilbert locality degraded (frac=" << frac
        << ", small=" << small_jumps << "/" << total << ")";
}

TEST(LaplaceCoreHilbert4d, LocalityNearestNeighborRecall) {
    std::mt19937_64 rng(0x12345ULL);
    std::uniform_real_distribution<double> u(-0.999, 0.999);

    constexpr int N = 256;
    constexpr int K = 5;
    constexpr int RECALL_WINDOW = 2 * K;

    std::vector<std::array<double, 4>> pts(N);
    std::vector<U128> idxs(N);
    for (int i = 0; i < N; ++i) {
        for (int d = 0; d < 4; ++d) pts[i][d] = u(rng);
        hilbert128_t h;
        hilbert4d_encode(pts[i].data(), &h);
        idxs[i] = to_u128(h);
    }

    int total_hits = 0;
    int total_lookups = 0;

    for (int q = 0; q < N; ++q) {
        std::vector<std::pair<double, int>> by_coord;
        std::vector<std::pair<U128,  int>> by_hilbert;
        by_coord.reserve(N - 1);
        by_hilbert.reserve(N - 1);
        for (int i = 0; i < N; ++i) {
            if (i == q) continue;
            by_coord.emplace_back(math4d_distance(pts[q].data(), pts[i].data()), i);
            by_hilbert.emplace_back(u128_abs_diff(idxs[q], idxs[i]), i);
        }
        std::partial_sort(by_coord.begin(), by_coord.begin() + K, by_coord.end());
        std::partial_sort(by_hilbert.begin(),
                          by_hilbert.begin() + RECALL_WINDOW,
                          by_hilbert.end(),
                          [](const auto& a, const auto& b) { return u128_less(a.first, b.first); });

        std::vector<int> top_hilbert;
        for (int k = 0; k < RECALL_WINDOW; ++k) top_hilbert.push_back(by_hilbert[k].second);

        for (int k = 0; k < K; ++k) {
            ++total_lookups;
            if (std::find(top_hilbert.begin(), top_hilbert.end(), by_coord[k].second)
                != top_hilbert.end()) {
                ++total_hits;
            }
        }
    }

    const double recall = (double)total_hits / (double)total_lookups;
    EXPECT_GT(recall, 0.45)
        << "Hilbert NN-recall degraded (recall=" << recall
        << " across " << total_lookups << " lookups)";
}
