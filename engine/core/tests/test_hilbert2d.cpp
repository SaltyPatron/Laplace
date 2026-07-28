#include <gtest/gtest.h>

#include <cstdlib>
#include <vector>

extern "C" {
#include "laplace/core/hilbert2d.h"
}

static uint32_t abs_diff(uint32_t a, uint32_t b) { return a > b ? a - b : b - a; }

TEST(Hilbert2d, IsABijectionOverTheSquare) {
    for (uint32_t order = 1; order <= 6; ++order) {
        const uint64_t cells = hilbert2d_cells(order);
        std::vector<char> seen(static_cast<size_t>(cells), 0);
        for (uint32_t y = 0; y < hilbert2d_side(order); ++y) {
            for (uint32_t x = 0; x < hilbert2d_side(order); ++x) {
                uint64_t d = 0;
                ASSERT_EQ(hilbert2d_encode(order, x, y, &d), 0);
                ASSERT_LT(d, cells);
                ASSERT_EQ(seen[static_cast<size_t>(d)], 0)
                    << "order " << order << ": index " << d << " hit twice";
                seen[static_cast<size_t>(d)] = 1;

                uint32_t rx = 0, ry = 0;
                ASSERT_EQ(hilbert2d_decode(order, d, &rx, &ry), 0);
                EXPECT_EQ(rx, x);
                EXPECT_EQ(ry, y);
            }
        }
    }
}

/*
 * The defining property, and the ONE that makes the image trajectory compress:
 * consecutive indices are adjacent cells. Row-major fails this at every row
 * boundary, which is what would shred run_length on a flat region.
 */
TEST(Hilbert2d, ConsecutiveIndicesAreOrthogonallyAdjacent) {
    for (uint32_t order = 1; order <= 7; ++order) {
        uint32_t px = 0, py = 0;
        ASSERT_EQ(hilbert2d_decode(order, 0, &px, &py), 0);
        for (uint64_t d = 1; d < hilbert2d_cells(order); ++d) {
            uint32_t x = 0, y = 0;
            ASSERT_EQ(hilbert2d_decode(order, d, &x, &y), 0);
            const uint32_t step = abs_diff(x, px) + abs_diff(y, py);
            ASSERT_EQ(step, 1u) << "order " << order << ": jump of " << step
                                << " between index " << (d - 1) << " and " << d;
            px = x; py = y;
        }
    }
}

/*
 * Locality is the whole reason the law picks this curve over a scanline. A
 * contiguous block of the curve must stay spatially compact — measured here as
 * the bounding box of a run, against the row-major alternative on the same run
 * length. This is the property that turns a flat image region into few runs
 * rather than one run per raster row.
 */
TEST(Hilbert2d, BeatsRowMajorOnRunCompactness) {
    const uint32_t order = 6;                 // 64x64
    const uint64_t side = hilbert2d_side(order);
    const uint64_t run = 64;                  // one raster row's worth of cells

    uint64_t hilbert_area = 0, rowmajor_area = 0;
    for (uint64_t start = 0; start + run <= hilbert2d_cells(order); start += run) {
        uint32_t hx0 = ~0u, hy0 = ~0u, hx1 = 0, hy1 = 0;
        for (uint64_t d = start; d < start + run; ++d) {
            uint32_t x = 0, y = 0;
            ASSERT_EQ(hilbert2d_decode(order, d, &x, &y), 0);
            if (x < hx0) hx0 = x;
            if (y < hy0) hy0 = y;
            if (x > hx1) hx1 = x;
            if (y > hy1) hy1 = y;
        }
        hilbert_area += (uint64_t)(hx1 - hx0 + 1) * (uint64_t)(hy1 - hy0 + 1);
        // Row-major run of the same length spans a full row: side x 1.
        rowmajor_area += side;
    }
    // Hilbert runs of 64 cells occupy an 8x8 box (area 64); row-major occupies 64x1.
    EXPECT_LE(hilbert_area, rowmajor_area);
    EXPECT_EQ(hilbert_area, (hilbert2d_cells(order) / run) * run);
}

TEST(Hilbert2d, RejectsOutOfRangeInputs) {
    uint64_t d = 0; uint32_t x = 0, y = 0;
    EXPECT_EQ(hilbert2d_encode(0, 0, 0, &d), -1);            // order 0
    EXPECT_EQ(hilbert2d_encode(32, 0, 0, &d), -1);           // past uint64 index
    EXPECT_EQ(hilbert2d_encode(3, 8, 0, &d), -1);            // x outside side 8
    EXPECT_EQ(hilbert2d_encode(3, 0, 8, &d), -1);            // y outside
    EXPECT_EQ(hilbert2d_decode(3, hilbert2d_cells(3), &x, &y), -1);
    EXPECT_EQ(hilbert2d_encode(3, 0, 0, nullptr), -1);
}

TEST(Hilbert2d, MatchesTheKnownOrder1And2Curves) {
    // Order 1: the U — (0,0) (0,1) (1,1) (1,0).
    const uint32_t expect1[4][2] = {{0,0},{0,1},{1,1},{1,0}};
    for (uint64_t i = 0; i < 4; ++i) {
        uint32_t x = 0, y = 0;
        ASSERT_EQ(hilbert2d_decode(1, i, &x, &y), 0);
        EXPECT_EQ(x, expect1[i][0]) << "order1 index " << i;
        EXPECT_EQ(y, expect1[i][1]) << "order1 index " << i;
    }
    // Order 2 endpoints: the curve starts at a corner and ends at the adjacent one.
    uint32_t x0 = 0, y0 = 0, x15 = 0, y15 = 0;
    ASSERT_EQ(hilbert2d_decode(2, 0, &x0, &y0), 0);
    ASSERT_EQ(hilbert2d_decode(2, 15, &x15, &y15), 0);
    EXPECT_EQ(x0, 0u);  EXPECT_EQ(y0, 0u);
    EXPECT_EQ(x15, 3u); EXPECT_EQ(y15, 0u);
}
