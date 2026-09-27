#ifndef LAPLACE_STEER_CANDIDATES_H
#define LAPLACE_STEER_CANDIDATES_H

#include "consensus_scan.h"

typedef struct LaplaceSteeredCandidate
{
    hash128_t id;
    double steer;
    int64 edges;
    int64 covered;
} LaplaceSteeredCandidate;

/* One row per distinct candidate, ordered by id: summed steer score, cells
 * read, and frontier members covered. */
extern LaplaceSteeredCandidate *laplace_steer_candidates(
    ArrayType *candidates, ArrayType *frontier, ArrayType *types,
    int *count, LaplaceConsensusScanStats *stats);

#endif
