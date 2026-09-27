




#ifndef LAPLACE_GRAPH_TAXONOMY_H
#define LAPLACE_GRAPH_TAXONOMY_H

#include "laplace/core/hash128.h"

/* Initial buffer sizing only; the walk grows its buffers. It is bounded by
 * depth and the deduplicated closure, not by a node cap. */
#define TAX_WALK_INITIAL 2048



typedef struct {
    hash128_t  id;
    int        depth;
    int        parent;
    hash128_t  via_type;
    int64_t    rating;
    int64_t    rd;
    int64_t    path_mu;
    bool       path_mu_valid;
} TaxNode;






/* Allocates and grows *nodes_out internally (palloc/repalloc in the caller's
 * memory context); returns the node count. */
extern int tax_bfs_up(const hash128_t *seeds, int seed_n, int max_depth,
                      const hash128_t *up_types, int up_type_n,
                      TaxNode **nodes_out);

/* Upward walk over consensus relation cells whose seeds carry their own
 * standing (a seed reached through an attested edge). Among paths reaching a
 * node at the same minimum depth, the one with the widest bottleneck effective
 * mu is kept. NULL seed arrays: the first traversed edge starts the bottleneck. */
extern int tax_bfs_up_weighted(const hash128_t *seeds,
                               const int64_t *seed_mu,
                               const bool *seed_mu_valid,
                               int seed_n, int max_depth,
                               const hash128_t *up_types, int up_type_n,
                               TaxNode **nodes_out);

#endif                          
