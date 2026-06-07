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

astar_query_t* astar_open(const hash128_t* start,
                          const hash128_t* goal_region, size_t goal_count,
                          size_t max_depth, size_t k_paths,
                          astar_expand_fn expand, void* ctx);

bool           astar_next(astar_query_t* q, astar_step_t* out_step);
void           astar_close(astar_query_t* q);

#ifdef __cplusplus
}
#endif
