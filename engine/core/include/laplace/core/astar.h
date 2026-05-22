#pragma once

#include <stdbool.h>
#include <stddef.h>
#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

/* A* cascade traversal primitives (per RULES.md R19 — compiled cascade
 * is a substrate-native operator, NOT recursive SQL or app-layer loops).
 *
 * `astar_query_t` is an opaque handle — type-erased per R14 because the
 * internal frontier heap, visited bitset, and arena/source-trust state
 * uses C++ containers (priority_queue, unordered_set) that don't cross
 * the C ABI cleanly. */
typedef struct astar_query astar_query_t;

/* A single step in a cascade path. Read pattern: streamed through
 * astar_next() into the caller's iteration loop (PG SRF return path
 * per the laplace_astar_path table function). Small POD struct keeps
 * the streaming hot loop cache-friendly.
 *
 * Layout: hash (16 B) + g (8 B) + h (8 B) = 32 B, fits half a cache
 * line; an array of these for top-k results is dense. */
typedef struct {
    hash128_t entity;  /* 16 B — the entity at this step */
    double    g;       /*  8 B — accumulated cost from start */
    double    h;       /*  8 B — heuristic estimate to goal region */
} astar_step_t;

/* Implementations land in Chunk 5+ — full cascade traversal with
 * Glicko-2-weighted edge costs (per ADR 0036 arena semantics). */
astar_query_t* astar_open(const hash128_t* start,
                          const hash128_t* goal_region,
                          size_t max_depth,
                          size_t k_paths);
bool           astar_next(astar_query_t* q, astar_step_t* out_step);
void           astar_close(astar_query_t* q);

#ifdef __cplusplus
}
#endif
