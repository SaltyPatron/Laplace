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

/* Compose one interior node from an ordered sequence of constituents.
 * Identity is content-only (tier is accepted for parity but not hashed):
 *   n == 1 -> out_id = child_ids[0]   (passthrough; strips meaningless unary wrappers)
 *   n  > 1 -> out_id = merkle(child_ids)
 * Placement is the 4D centroid of the child coords; out_hb is its Hilbert key.
 * This is the single composition truth shared by the text law (regular tiers) and
 * the code law (irregular AST sequences); both go through it byte-identically.
 * child_ids has n entries; child_coords has n*4 doubles (XYZM per child). */
void hash_composer_compose_node(
    uint8_t          tier,
    const hash128_t* child_ids,
    const double*    child_coords,
    size_t           n,
    hash128_t*       out_id,
    double           out_coord[4],
    hilbert128_t*    out_hb);

#ifdef __cplusplus
}
#endif
