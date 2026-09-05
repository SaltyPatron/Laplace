#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/intent_stage.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * One already-realized constituent of an ordered composition.  The caller
 * supplies identity, live placement, and the packed T0 atom declaration
 * needed by exact readback; this operation owns all parent computation.
 * `tier` is altitude only: it is used to derive the parent floor and
 * trajectory flags and never enters the parent identity.
 */
typedef struct {
    hash128_t id;
    double    coord[4];
    uint32_t  atom;
    uint8_t   tier;
    uint8_t   has_atom;
    uint8_t   _pad[2];
} laplace_ordered_component_t;

/* Identity and placement computed by the native composition kernel. */
typedef struct {
    hash128_t    id;
    double       coord[4];
    hilbert128_t hilbert;
    uint8_t      tier;
    uint8_t      _pad[7];
} laplace_ordered_composition_result_t;

/*
 * One independent composition in a bulk call.  `type_id` is an already
 * governed realization declaration, not a category inferred from tier.
 * `source_id` and `observed_at_unix_us` are passed through to the staged rows.
 */
typedef struct {
    const laplace_ordered_component_t* components;
    size_t                             component_count;
    hash128_t                          type_id;
    hash128_t                          source_id;
    int64_t                            observed_at_unix_us;
} laplace_ordered_composition_request_t;

/*
 * Compose independent ordered component sequences in one native crossing.
 * This is the shared identity/placement kernel for resolve and staging. A
 * multi-component parent has floor max(child.tier) + 1 and a centroid/Hilbert
 * key. A singleton is its child and therefore has its id, floor, and
 * placement. `out_results` holds `request_count` results in request order.
 */
int laplace_ordered_composition_compose_batch(
    const laplace_ordered_composition_request_t* requests,
    size_t                                       request_count,
    laplace_ordered_composition_result_t*        out_results);

/*
 * Compose and stage independent ordered component sequences in one native
 * crossing. A multi-component trajectory retains one flagged vertex for every
 * supplied constituent, including repeated adjacent runs: the existing RLE
 * codec cannot retain per-vertex atom flags. A singleton stages no wrapper
 * entity or self physicality.
 *
 * The operation validates every request before mutating `stage`. `out_results`
 * holds `request_count` entries in request order.
 */
int laplace_ordered_composition_stage_batch(
    intent_stage_t*                              stage,
    const laplace_ordered_composition_request_t* requests,
    size_t                                       request_count,
    laplace_ordered_composition_result_t*        out_results);

#ifdef __cplusplus
}
#endif
