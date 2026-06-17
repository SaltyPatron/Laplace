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
