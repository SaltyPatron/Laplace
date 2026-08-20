#pragma once

#include <stdbool.h>
#include <stddef.h>
#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct astar_query astar_query_t;

typedef struct {
    hash128_t entity;
    double    g;
    double    h;
} astar_step_t;

typedef struct {
    hash128_t target;
    double    cost;
} astar_edge_t;

/* Return a borrowed, complete adjacency span for one node. The span only has
 * to remain valid until the next expansion call. Exact traversal must not
 * silently truncate high-degree nodes to an engine-owned buffer size. */
typedef bool (*astar_expand_fn)(void* ctx, const hash128_t* node,
                                const astar_edge_t** out, size_t* count);

/*
 * Optional ordering hint. Uniform-cost g remains the primary frontier key, so
 * this value can break equal-cost ties without changing the least-cost answer.
 * No geometric/cost relationship has been proved for substrate edges; treating
 * geometry as an additive lower bound would make the search inexact.
 */
typedef double (*astar_heuristic_fn)(void* ctx, const hash128_t* node,
                                     const hash128_t* goal_region, size_t goal_count);

astar_query_t* astar_open(const hash128_t* start,
                          const hash128_t* goal_region, size_t goal_count,
                          size_t max_depth, size_t k_paths,
                          astar_expand_fn expand, void* ctx,
                          astar_heuristic_fn heuristic, void* heur_ctx);

bool           astar_next(astar_query_t* q, astar_step_t* out_step);
void           astar_close(astar_query_t* q);

#ifdef __cplusplus
}
#endif
