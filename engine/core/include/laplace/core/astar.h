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

typedef int (*astar_expand_fn)(void* ctx, const hash128_t* node,
                               astar_edge_t* out, int cap);

/*
 * Optional admissible-lower-bound estimator: given the current node and the
 * goal region, return a cost estimate that never overestimates the true
 * remaining cost to any goal. NULL (the default at every existing call site)
 * preserves today's exact behavior: h is always 0.0, i.e. uniform-cost search
 * (Dijkstra), not A*. Passing a heuristic is opt-in per call -- this must
 * never change default behavior for a caller that doesn't pass one, since
 * astar_open is shared with the foundry synthesis path (Issue 05).
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
