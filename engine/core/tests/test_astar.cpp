#include <gtest/gtest.h>

#include <cstdint>
#include <map>
#include <vector>

#include "laplace/core/astar.h"
#include "laplace/core/hash128.h"

namespace {

hash128_t H(uint64_t n) { return hash128_t{0, n}; }

struct Graph {
    std::map<uint64_t, std::vector<astar_edge_t>> adj;
};

int expand(void* ctx, const hash128_t* node, astar_edge_t* out, int cap) {
    auto* g  = static_cast<Graph*>(ctx);
    auto  it = g->adj.find(node->lo);
    if (it == g->adj.end()) return 0;
    int n = 0;
    for (const auto& e : it->second) {
        if (n >= cap) break;
        out[n++] = e;
    }
    return n;
}

std::vector<uint64_t> run(Graph& g, uint64_t start, std::vector<uint64_t> goals,
                          size_t max_depth = 16) {
    hash128_t s = H(start);
    std::vector<hash128_t> gr;
    for (auto x : goals) gr.push_back(H(x));
    astar_query_t* q =
        astar_open(&s, gr.data(), gr.size(), max_depth, 1, expand, &g);
    std::vector<uint64_t> path;
    if (q == nullptr) return path;
    astar_step_t step;
    while (astar_next(q, &step)) path.push_back(step.entity.lo);
    astar_close(q);
    return path;
}

}

TEST(LaplaceCoreAstar, PicksLeastCostPath) {
    Graph g;
    g.adj[1] = {{H(2), 1.0}, {H(3), 3.0}};
    g.adj[2] = {{H(4), 1.0}};
    g.adj[3] = {{H(4), 1.0}};
    auto p = run(g, 1, {4});
    ASSERT_EQ(p.size(), 3u);
    EXPECT_EQ(p[0], 1u);
    EXPECT_EQ(p[1], 2u);
    EXPECT_EQ(p[2], 4u);
}

TEST(LaplaceCoreAstar, MultiHopBeatsExpensiveDirect) {
    Graph g;
    g.adj[1] = {{H(9), 10.0}, {H(2), 1.0}};
    g.adj[2] = {{H(9), 1.0}};
    auto p = run(g, 1, {9});
    ASSERT_EQ(p.size(), 3u);
    EXPECT_EQ(p[1], 2u);
}

TEST(LaplaceCoreAstar, ReachesNearestGoalInRegion) {
    Graph g;
    g.adj[1] = {{H(2), 1.0}, {H(3), 5.0}};
    auto p = run(g, 1, {2, 3});
    ASSERT_EQ(p.size(), 2u);
    EXPECT_EQ(p[1], 2u);
}

TEST(LaplaceCoreAstar, NoPathYieldsEmpty) {
    Graph g;
    g.adj[1] = {{H(2), 1.0}};
    auto p = run(g, 1, {99});
    EXPECT_TRUE(p.empty());
}

TEST(LaplaceCoreAstar, StartIsGoal) {
    Graph g;
    auto p = run(g, 7, {7});
    ASSERT_EQ(p.size(), 1u);
    EXPECT_EQ(p[0], 7u);
}

TEST(LaplaceCoreAstar, RespectsMaxDepth) {
    Graph g;
    g.adj[1] = {{H(2), 1.0}};
    g.adj[2] = {{H(3), 1.0}};
    g.adj[3] = {{H(4), 1.0}};
    EXPECT_TRUE(run(g, 1, {4}, 2).empty());
    EXPECT_EQ(run(g, 1, {4}, 3).size(), 4u);
}

TEST(LaplaceCoreAstar, RejectsBadArgs) {
    Graph g;
    hash128_t s = H(1), goal = H(2);
    EXPECT_EQ(astar_open(nullptr, &goal, 1, 8, 1, expand, &g), nullptr);
    EXPECT_EQ(astar_open(&s, &goal, 0, 8, 1, expand, &g), nullptr);
    EXPECT_EQ(astar_open(&s, &goal, 1, 8, 1, nullptr, &g), nullptr);
}
