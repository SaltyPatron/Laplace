#ifndef LAPLACE_CONSENSUS_NEIGHBORS_H
#define LAPLACE_CONSENSUS_NEIGHBORS_H

#include "consensus_scan.h"

typedef struct LaplaceNeighbor
{
    hash128_t frontier;
    hash128_t neighbor;
    hash128_t type;
    int64 rating;
    int64 rd;
    int64 witnesses;
    bool outbound;
} LaplaceNeighbor;

/* Best stored edge per exact (frontier,neighbor) pair, ordered by frontier id,
 * conservative standing, neighbor id, relation id and orientation. The bound
 * is the caller's explicit per-frontier presentation/execution operand. */
extern LaplaceNeighbor *laplace_consensus_neighbors(
    ArrayType *frontier, ArrayType *types, int limit, bool include_default,
    int *count, LaplaceConsensusScanStats *stats);

#endif
