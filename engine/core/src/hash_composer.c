#include "laplace/core/hash_composer.h"

#include <stddef.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/math4d.h"
#include "laplace/core/tier_tree.h"

int hash_composer_run(
    tier_tree_t*                   tree,
    hash_composer_atom_resolver_fn resolver,
    void*                          resolver_user_data) {
    if (!tree || !resolver) return -1;
    const size_t count = tier_tree_node_count(tree);
    if (count == 0) return 0;

    const uint8_t*  tiers  = tier_tree_tier_array(tree);
    const uint32_t* fci    = tier_tree_first_child_idx_array(tree);
    const uint32_t* cc     = tier_tree_child_count_array(tree);
    const uint32_t* atoms  = tier_tree_atom_array(tree);
    hash128_t*      ids    = tier_tree_id_array_mut(tree);
    double*         coords = tier_tree_coord_array_mut(tree);   /* count*4 doubles */
    hilbert128_t*   hbs    = tier_tree_hilbert_array_mut(tree);
    if (!tiers || !fci || !cc || !atoms || !ids || !coords || !hbs) return -1;

    /* Single bottom-up pass. Each iteration touches only fields of node i
     * (and its children's already-populated id/coord arrays). Cache-
     * friendly: ids array is sequential, coord array is sequential,
     * children of node i are contiguous at [fci[i] .. fci[i]+cc[i]-1]. */
    for (size_t i = 0; i < count; ++i) {
        const uint32_t first = fci[i];
        const uint32_t cnt   = cc[i];
        const int is_leaf = (first == TIER_TREE_INVALID) || (cnt == 0);

        if (is_leaf) {
            /* T0 atom: caller's resolver fills (id, coord, hilbert). */
            double leaf_coord[4] = {0.0, 0.0, 0.0, 0.0};
            hash128_t leaf_id;
            hash128_zero(&leaf_id);
            hilbert128_t leaf_hb;
            for (int b = 0; b < 16; ++b) leaf_hb.bytes[b] = 0;

            const int rc = resolver(atoms[i], resolver_user_data,
                                    &leaf_id, leaf_coord, &leaf_hb);
            if (rc != 0) return rc;

            ids[i] = leaf_id;
            coords[i * 4 + 0] = leaf_coord[0];
            coords[i * 4 + 1] = leaf_coord[1];
            coords[i * 4 + 2] = leaf_coord[2];
            coords[i * 4 + 3] = leaf_coord[3];
            hbs[i] = leaf_hb;
            continue;
        }

        /* Interior: defensive child-range check. tier_tree's add_node
         * already validates this, but if someone bypasses the builder
         * we surface the error rather than reading OOB. */
        if ((size_t)first >= count || (size_t)first + (size_t)cnt > count) {
            return -1;
        }
        /* All children have strictly smaller idx than i (topological add
         * invariant), so their ids/coords are already populated. */

        /* id = merkle(tier, child_ids contiguous). */
        hash128_merkle(tiers[i], &ids[first], (size_t)cnt, &ids[i]);

        /* coord = centroid(child_coords flat n*4). */
        double centroid[4];
        math4d_centroid(&coords[(size_t)first * 4], (size_t)cnt, centroid);
        coords[i * 4 + 0] = centroid[0];
        coords[i * 4 + 1] = centroid[1];
        coords[i * 4 + 2] = centroid[2];
        coords[i * 4 + 3] = centroid[3];

        /* hilbert = encode(coord). */
        hilbert4d_encode(centroid, &hbs[i]);
    }
    return 0;
}
