#pragma once

#include <stddef.h>
#include <stdint.h>
#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

/* One caller owns a working window. Native composition and deduplication stay
 * here; the host sends the returned frontiers and COPY bytes in batches.
 * Coordinates are Native's exact signed 2^53 fixed-point values. Vertex
 * dispositions describe containment, never identity. */
typedef struct {
    hash128_t id;
    int64_t coordinate[4];
    uint8_t tier, said, outcome;
    uint32_t position, spare;
} canonical_stage_ref_t;

typedef struct canonical_stage canonical_stage_t;
typedef enum {
    CANONICAL_STAGE_OK = 0,
    CANONICAL_STAGE_INVALID = -1,
    CANONICAL_STAGE_LIMIT = -2,
    CANONICAL_STAGE_CONFLICT = -3,
    CANONICAL_STAGE_STATE = -4
} canonical_stage_status_t;
typedef enum {
    CANONICAL_STAGE_ENTITY = 1,
    CANONICAL_STAGE_PHYSICALITY = 2
} canonical_stage_table_t;

/* Limits bound nodes, stored vertices and combined COPY payload, respectively.
 * The configured scalar floor must already be loaded. The optional highway is
 * an immutable Native mapping, borrowed for this window; NULL permits zero
 * masks only. No database connection or schema lifecycle is owned here. */
canonical_stage_t* canonical_stage_new(size_t maximum_nodes,
    size_t maximum_vertices, size_t maximum_copy_bytes, const void* highway);
void canonical_stage_free(canonical_stage_t* stage);
void canonical_stage_reset(canonical_stage_t* stage);
canonical_stage_status_t canonical_stage_status(const canonical_stage_t* stage);

/* An authenticated existing-entity read may supply a reference without its
 * subtree. Cache atoms are recognized directly, without host registration. */
canonical_stage_status_t canonical_stage_reference(canonical_stage_t* stage,
    const canonical_stage_ref_t* recorded);
canonical_stage_status_t canonical_stage_compose(canonical_stage_t* stage,
    const canonical_stage_ref_t* children, size_t count, uint8_t tier,
    uint64_t entity_mask, uint64_t path_mask, int structural_spine,
    canonical_stage_ref_t* out);
/* Uses the very same Native UAX29 decomposition as the Engine. */
canonical_stage_status_t canonical_stage_text(canonical_stage_t* stage,
    const uint8_t* utf8, size_t length, canonical_stage_ref_t* out);

/* Begin at logical roots. A present answer amputates the complete known
 * subtree, including shared descendants. Synthetic spine nodes are traversed
 * immediately and are never returned in a database frontier. Each answer byte
 * is exactly 0 or 1 and corresponds to the last frontier's order. */
canonical_stage_status_t canonical_stage_begin(canonical_stage_t* stage,
    const hash128_t* roots, size_t count);
const hash128_t* canonical_stage_frontier(const canonical_stage_t* stage,
    size_t* count);
canonical_stage_status_t canonical_stage_answer(canonical_stage_t* stage,
    const uint8_t* present, size_t count);

/* Finish only after all frontiers are answered. The two complete PostgreSQL
 * binary COPY streams use the canonical five-column layouts and bit(64)
 * carrier representation. Known rows with new carrier bits remain eligible
 * for the server's set merge; no standing is computed on the client.
 * Returned bytes are borrowed until reset/free. */
canonical_stage_status_t canonical_stage_finish(canonical_stage_t* stage);
const uint8_t* canonical_stage_copy(const canonical_stage_t* stage,
    canonical_stage_table_t table, size_t* bytes, size_t* rows);
const char* canonical_stage_columns(canonical_stage_table_t table);

#ifdef __cplusplus
}
#endif
