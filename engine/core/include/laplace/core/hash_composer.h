#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef int (*hash_composer_atom_resolver_fn)(
    uint32_t      atom,
    void*         user_data,
    hash128_t*    out_id,
    double        out_coord[4],
    hilbert128_t* out_hilbert);

int hash_composer_run(
    tier_tree_t*                   tree,
    hash_composer_atom_resolver_fn resolver,
    void*                          resolver_user_data);

/* Dependency-frontier composition for one semantic DAG. worker_count is a
 * caller-owned physical resource grant and never participates in identity.
 * Every parent starts only after its complete child frontier has joined.
 *
 * The resolver supplied here must be safe for concurrent calls. The scalar
 * hash_composer_run entry remains the compatibility/oracle path for arbitrary
 * resolvers and is also the exact fallback when native parallel execution is
 * unavailable. */
int hash_composer_run_workers(
    tier_tree_t*                   tree,
    hash_composer_atom_resolver_fn resolver,
    void*                          resolver_user_data,
    size_t                         worker_count);

void hash_composer_compose_node(
    uint8_t          tier,
    const hash128_t* child_ids,
    const double*    child_coords,
    size_t           n,
    hash128_t*       out_id,
    double           out_coord[4],
    hilbert128_t*    out_hb);

/* Same identity, canonical centroid and Hilbert owner with caller-owned scratch
 * sized by math4d_centroid_workspace_size. Returns 0 on success, -1 on invalid
 * buffers or insufficient workspace; all outputs remain untouched on failure.
 * Workspace must not overlap the input arrays or outputs. */
int hash_composer_compose_node_with_workspace(
    uint8_t tier, const hash128_t* child_ids, const double* child_coords, size_t n,
    void* workspace, size_t workspace_bytes,
    hash128_t* out_id, double out_coord[4], hilbert128_t* out_hb);

#ifdef __cplusplus
}
#endif
