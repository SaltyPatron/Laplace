




#ifndef LAPLACE_GRAPH_TAXONOMY_H
#define LAPLACE_GRAPH_TAXONOMY_H

#include "laplace/core/hash128.h"

/* Initial buffer sizing only — the walk grows its buffers as needed. A fixed
 * 2048-node cap used to hard-error here ("taxonomy walk node cap exceeded"):
 * at live scale ordinary words exceed it (emperor's depth-7 IS_A closure =
 * 8,225 nodes, measured 2026-07-24). The walk is finitely bounded by depth ×
 * the deduped closure, never by an invented cap. */
#define TAX_WALK_INITIAL 2048



typedef struct {
    hash128_t  id;
    int        depth;
    int        parent;
    hash128_t  via_type;
    int64_t    rating;
    int64_t    rd;
} TaxNode;






/* Allocates and grows *nodes_out internally (palloc/repalloc in the caller's
 * memory context); returns the node count. */
extern int tax_bfs_up(const hash128_t *seeds, int seed_n, int max_depth,
                      const hash128_t *up_types, int up_type_n,
                      TaxNode **nodes_out);

#endif                          
