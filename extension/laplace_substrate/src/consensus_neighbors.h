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
    int64 volatility;
    int64 witnesses;
    bool outbound;
} LaplaceNeighbor;

/* One star-expansion step over the consensus face: for each frontier id, the
 * best stored cell per exact (frontier, neighbor) pair read in both
 * orientations, at most `limit` per frontier (the fanout coordinate). Rows are
 * ordered by frontier id, conservative standing (rating - 2*rd), neighbor id,
 * relation id and orientation. respect_direction admits inbound cells only for
 * symmetric relation types. */
extern LaplaceNeighbor *laplace_consensus_neighbors(
    ArrayType *frontier, ArrayType *types, int limit, bool include_default,
    bool respect_direction, bool require_positive,
    int *count, LaplaceConsensusScanStats *stats);

#endif
