#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/grammar_decomposer.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    hash128_t id;
    uint8_t   tier;
    /* PACKAGING: this node exists so the caller can NAVIGATE the record (spans,
     * containment, id convergence) and must never become a substrate row. Set for
     * every non-root node of a data container -- see grammar_compose.cpp's Rule #8
     * note. Occupies a byte that was already padding, so the struct layout and the
     * C# marshalling (NativeInterop.ComposeEntityNative) are unchanged. */
    uint8_t   packaging;
    uint8_t   _pad[2];
    hash128_t type_id;
} laplace_compose_entity_t;

typedef struct {
    hash128_t    id;
    hash128_t    entity_id;
    double       coord[4];
    hilbert128_t hilbert;
    double*      trajectory_xyzm;
    size_t       trajectory_n;
    size_t       n_constituents;
} laplace_compose_physicality_t;

typedef struct {
    hash128_t subject_id;
    hash128_t object_id;
    int64_t   games;
} laplace_compose_precedes_t;

typedef struct {
    uint32_t  start_byte;
    uint32_t  end_byte;
    hash128_t entity_id;
} laplace_compose_span_t;

typedef struct {
    laplace_compose_entity_t*        entities;
    size_t                           entity_count;
    laplace_compose_physicality_t*   physicalities;
    size_t                           phys_count;
    laplace_compose_precedes_t*      precedes;
    size_t                           precedes_count;
    laplace_compose_span_t*          spans;
    size_t                           span_count;
    /* Open-addressing index over spans[], keyed by (start_byte,end_byte),
     * built once during compose (GH #595) so laplace_compose_span_lookup is
     * O(1) amortized instead of an O(span_count) linear scan called once per
     * AST node from the C# entity-compose loop — O(n) lookups x O(n) scan
     * each was O(n^2), measured pinning a single ingest for 40+ minutes on a
     * file with tens of thousands of nodes. UINT32_MAX is the empty sentinel;
     * NULL/0 (the calloc default) falls back to the old linear scan. */
    uint32_t*                        span_index;
    size_t                           span_index_cap;
    hash128_t                        root_id;
    tier_tree_t*                     tree;
} laplace_compose_result_t;

int laplace_grammar_compose(
    const uint8_t*              utf8,
    size_t                      len,
    laplace_ast_t*              ast,
    const char*                 modality_id,
    hash128_t                   source_id,
    hash128_t                   type_meta_id,
    laplace_compose_result_t**  out);

int laplace_grammar_compose_probe(
    const uint8_t*              utf8,
    size_t                      len,
    laplace_ast_t*              ast,
    const char*                 modality_id,
    hash128_t                   source_id,
    hash128_t                   type_meta_id,
    laplace_compose_result_t**  out);

int laplace_grammar_compose_materialize_phys(
    laplace_compose_result_t*   r,
    const uint8_t*              utf8,
    size_t                      len,
    laplace_ast_t*              ast,
    const char*                 modality_id);

int laplace_grammar_compose_node_id(
    const uint8_t*     utf8,
    size_t             len,
    laplace_ast_t*     ast,
    const char*        modality_id,
    size_t             ast_node_index,
    hash128_t*         out_id,
    uint8_t*           out_tier);

int laplace_grammar_compose_row_root(
    const uint8_t*     utf8,
    size_t             len,
    laplace_ast_t*     ast,
    const char*        modality_id,
    hash128_t*         out_id,
    uint8_t*           out_tier);

int laplace_compose_span_lookup(
    const laplace_compose_result_t* r,
    uint32_t start_byte,
    uint32_t end_byte,
    hash128_t* out_id);

void laplace_compose_result_free(laplace_compose_result_t* r);

size_t laplace_compose_entity_count(const laplace_compose_result_t* r);
size_t laplace_compose_physicality_count(const laplace_compose_result_t* r);
size_t laplace_compose_precedes_count(const laplace_compose_result_t* r);
hash128_t laplace_compose_root_id(const laplace_compose_result_t* r);

tier_tree_t* laplace_compose_get_tier_tree(const laplace_compose_result_t* r);

int laplace_compose_get_entity(const laplace_compose_result_t* r, size_t i,
                               laplace_compose_entity_t* out);
int laplace_compose_get_physicality(const laplace_compose_result_t* r, size_t i,
                                    laplace_compose_physicality_t* out);
int laplace_compose_get_precedes(const laplace_compose_result_t* r, size_t i,
                                 laplace_compose_precedes_t* out);

int laplace_compose_drain_into_stage(
    const laplace_compose_result_t* r,
    intent_stage_t*                 stage,
    const hash128_t*                source_id,
    int64_t                         now_unix_us,
    double                          witness_weight,
    const uint8_t*                  existing_bitmap,
    size_t                          bitmap_bits);

#ifdef __cplusplus
}
#endif
