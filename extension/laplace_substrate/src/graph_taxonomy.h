/*
 * graph_taxonomy.h — the upward (hypernym / IS_A) taxonomy BFS engine, exported
 * so graph_contrast.c reuses it instead of carrying its own copy. Defined in
 * graph_taxonomy.c. Include after postgres.h.
 */
#ifndef LAPLACE_GRAPH_TAXONOMY_H
#define LAPLACE_GRAPH_TAXONOMY_H

#include "laplace/core/hash128.h"

#define TAX_WALK_CAP 2048

/* A node in the upward taxonomy walk: identity, BFS depth/parent, the relation
 * type it was reached by, and the seed→node edge's Glicko rating/rd. */
typedef struct {
    hash128_t  id;
    int        depth;
    int        parent;
    hash128_t  via_type;
    int64_t    rating;
    int64_t    rd;
} TaxNode;

/*
 * Breadth-first walk UP the consensus taxonomy from `seed_n` seeds along the
 * `up_type_n` relation types in `up_types`, to `max_depth`, filling `nodes`
 * (capacity `cap`). Returns the number of nodes discovered.
 */
extern int tax_bfs_up(const hash128_t *seeds, int seed_n, int max_depth,
                      const hash128_t *up_types, int up_type_n,
                      TaxNode *nodes, int cap);

#endif                          /* LAPLACE_GRAPH_TAXONOMY_H */
