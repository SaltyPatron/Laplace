#include "laplace/core/astar.h"

#include <cstddef>
#include <cstdint>
#include <limits>
#include <queue>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace {

struct H128Hash {
    size_t operator()(const hash128_t& k) const noexcept {
        uint64_t x = k.hi * 1099511628211ULL ^ k.lo;
        x ^= x >> 33;
        return static_cast<size_t>(x);
    }
};
struct H128Eq {
    bool operator()(const hash128_t& a, const hash128_t& b) const noexcept {
        return a.hi == b.hi && a.lo == b.lo;
    }
};

struct Frontier {
    double    f;
    double    g;
    size_t    depth;
    hash128_t node;
};
struct FrontierGreater {
    bool operator()(const Frontier& a, const Frontier& b) const noexcept {
        return a.f > b.f;
    }
};

constexpr int    kExpandCap = 256;
constexpr double kInf       = std::numeric_limits<double>::infinity();

}

struct astar_query {
    std::vector<astar_step_t> path;
    size_t                    cursor = 0;
};

astar_query_t* astar_open(const hash128_t* start,
                          const hash128_t* goal_region, size_t goal_count,
                          size_t max_depth, size_t k_paths,
                          astar_expand_fn expand, void* ctx) {
    (void)k_paths;
    if (start == nullptr || expand == nullptr || goal_region == nullptr ||
        goal_count == 0) {
        return nullptr;
    }

    std::unordered_set<hash128_t, H128Hash, H128Eq> goals;
    goals.reserve(goal_count);
    for (size_t i = 0; i < goal_count; ++i) goals.insert(goal_region[i]);

    std::unordered_map<hash128_t, double, H128Hash, H128Eq>    best_g;
    std::unordered_map<hash128_t, hash128_t, H128Hash, H128Eq> came_from;
    std::priority_queue<Frontier, std::vector<Frontier>, FrontierGreater> frontier;

    best_g[*start] = 0.0;
    frontier.push(Frontier{0.0, 0.0, 0, *start});

    bool      reached = false;
    hash128_t goal_hit{};

    std::vector<astar_edge_t> buf(kExpandCap);

    while (!frontier.empty()) {
        Frontier cur = frontier.top();
        frontier.pop();

        auto bg = best_g.find(cur.node);
        if (bg != best_g.end() && cur.g > bg->second) continue;

        if (goals.count(cur.node)) { reached = true; goal_hit = cur.node; break; }
        if (cur.depth >= max_depth) continue;

        int n = expand(ctx, &cur.node, buf.data(), kExpandCap);
        if (n < 0) {
            return new astar_query();
        }
        for (int i = 0; i < n; ++i) {
            const astar_edge_t& e = buf[static_cast<size_t>(i)];
            double cost = e.cost < 0.0 ? 0.0 : e.cost;
            double ng   = cur.g + cost;
            auto   it   = best_g.find(e.target);
            double prev = (it == best_g.end()) ? kInf : it->second;
            if (ng < prev) {
                best_g[e.target]    = ng;
                came_from[e.target] = cur.node;
                double h = 0.0;
                frontier.push(Frontier{ng + h, ng, cur.depth + 1, e.target});
            }
        }
    }

    auto* q = new astar_query();
    if (!reached) return q;

    std::vector<hash128_t> rev;
    hash128_t at = goal_hit;
    rev.push_back(at);
    while (!(at.hi == start->hi && at.lo == start->lo)) {
        auto it = came_from.find(at);
        if (it == came_from.end()) break;
        at = it->second;
        rev.push_back(at);
    }
    q->path.reserve(rev.size());
    for (auto it = rev.rbegin(); it != rev.rend(); ++it) {
        astar_step_t step{};
        step.entity = *it;
        step.g      = best_g.count(*it) ? best_g[*it] : 0.0;
        step.h      = 0.0;
        q->path.push_back(step);
    }
    return q;
}

bool astar_next(astar_query_t* q, astar_step_t* out_step) {
    if (q == nullptr || out_step == nullptr) return false;
    if (q->cursor >= q->path.size()) return false;
    *out_step = q->path[q->cursor++];
    return true;
}

void astar_close(astar_query_t* q) {
    delete q;
}
