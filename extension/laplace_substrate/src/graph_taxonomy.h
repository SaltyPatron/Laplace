




#ifndef LAPLACE_GRAPH_TAXONOMY_H
#define LAPLACE_GRAPH_TAXONOMY_H

#include "laplace/core/hash128.h"

#define TAX_WALK_CAP 2048



typedef struct {
    hash128_t  id;
    int        depth;
    int        parent;
    hash128_t  via_type;
    int64_t    rating;
    int64_t    rd;
} TaxNode;






extern int tax_bfs_up(const hash128_t *seeds, int seed_n, int max_depth,
                      const hash128_t *up_types, int up_type_n,
                      TaxNode *nodes, int cap);

#endif                          
