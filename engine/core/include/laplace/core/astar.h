#pragma once

#include <stdbool.h>
#include <stddef.h>
#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

/* A* cascade traversal primitives — compiled cascade is a substrate-native
 * operator, NOT recursive SQL or app-layer loops. The kernel owns the search
 * (frontier heap, visited set, path reconstruction); the GRAPH is supplied by
 * an expansion callback, so engine/core stays PG-free and the extension layer
 * provides the SPI-backed, Glicko-μ-weighted neighbor provider over consensus.
 *
 * `astar_query_t` is an opaque handle — type-erased per R14 because the
 * internal frontier heap, visited set, and came-from map use C++ containers
 * (priority_queue, unordered_map) that don't cross the C ABI cleanly. */
typedef struct astar_query astar_query_t;

/* A single step in a cascade path. Streamed through astar_next() into the
 * caller's iteration loop (the laplace_astar_path PG SRF return path).
 * Layout: hash (16 B) + g (8 B) + h (8 B) = 32 B, half a cache line. */
typedef struct {
    hash128_t entity;  /* 16 B — the entity at this step */
    double    g;       /*  8 B — accumulated cost from start */
    double    h;       /*  8 B — heuristic estimate to the goal region */
} astar_step_t;

/* One weighted out-edge, emitted by the expansion callback. cost MUST be
 * non-negative (Dijkstra/A* admissibility); the provider derives it from the
 * edge's Glicko μ — a stronger relation (higher μ) is a cheaper hop. */
typedef struct {
    hash128_t target;
    double    cost;
} astar_edge_t;

/* Neighbor expansion callback: write up to `cap` weighted out-edges of `node`
 * into `out`, return the count written (≤ cap), or -1 on error. When a node has
 * more than `cap` neighbors the provider caps by top-μ (cheapest hops first) —
 * the same top-μ bound the SQL reads use; A*'s goal-direction makes the loss on
 * hub nodes immaterial for the paths that matter. */
typedef int (*astar_expand_fn)(void* ctx, const hash128_t* node,
                               astar_edge_t* out, int cap);

/* Open a cascade query: least-cost path from `start` into the goal region
 * (`goal_region[0..goal_count)`), edges supplied by `expand(ctx, …)`, bounded
 * by `max_depth` hops. `k_paths` reserved for k-best; the current kernel
 * resolves the single least-cost path (k_paths is clamped to 1). Returns NULL
 * on bad arguments; otherwise a handle whose path (possibly empty — no route)
 * is streamed by astar_next(). h is currently 0 (uniform-cost / Dijkstra); the
 * struct and ordering are heuristic-ready for a glome-geodesic h. */
astar_query_t* astar_open(const hash128_t* start,
                          const hash128_t* goal_region, size_t goal_count,
                          size_t max_depth, size_t k_paths,
                          astar_expand_fn expand, void* ctx);

/* Stream the next step of the resolved path (start → … → goal), in order.
 * Returns false when the path is exhausted (or empty). */
bool           astar_next(astar_query_t* q, astar_step_t* out_step);
void           astar_close(astar_query_t* q);

#ifdef __cplusplus
}
#endif
