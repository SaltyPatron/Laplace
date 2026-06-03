#include <gtest/gtest.h>

#include "laplace/core/astar.h"
#include "laplace/core/hash128.h"

/* Real tests land Chunk 5+ — compiled cascade,
 * Glicko-2-weighted heuristic, arena-aware traversal
 *. Stub for now. */

TEST(LaplaceCoreAstar, StubOpenReturnsNull) {
    hash128_t start, goal;
    hash128_zero(&start);
    hash128_zero(&goal);
    astar_query_t* q = astar_open(&start, &goal, 10, 1);
    EXPECT_EQ(q, nullptr);
    astar_close(q);
}
