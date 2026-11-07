#ifndef LAPLACE_SUBSTRATE_GRAPH_TAXONOMY_INTERNAL_H
#define LAPLACE_SUBSTRATE_GRAPH_TAXONOMY_INTERNAL_H

#include "postgres.h"
#include "executor/spi.h"
#include "utils/hsearch.h"
#include "laplace/core/hash128.h"
#include "graph_taxonomy.h"

/* id -> node-index map over the walk's nodes; ids are deduplicated on insert,
 * so the map is 1:1. */
typedef struct TaxIdxEntry
{
    char key[16];
    int  idx;
} TaxIdxEntry;

extern SPIPlanPtr tax_edges_plan;

int tax_find(const hash128_t *ids, int n, const hash128_t *key);
void ensure_tax_edges_plan(void);
int tax_idx_find(HTAB *map, const hash128_t *h);
void tax_idx_add(HTAB *map, const hash128_t *h, int idx);
void reconstruct_path(TaxNode *nodes, int idx, Datum **path, Datum **types, Datum *path_mu);

#endif
