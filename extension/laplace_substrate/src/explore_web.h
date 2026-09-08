#ifndef LAPLACE_EXPLORE_WEB_H
#define LAPLACE_EXPLORE_WEB_H

#include "postgres.h"
#include "utils/array.h"
#include "laplace/core/hash128.h"

typedef struct LaplaceWebEdge
{
    hash128_t source, type_id, object;
    int16 hop;
    int64 rating, rd, witnesses;
} LaplaceWebEdge;

typedef void (*LaplaceWebVisitor)(const LaplaceWebEdge *edge, void *context);
void laplace_explore_web(ArrayType *seeds, int hops, int fanout, int max_nodes,
    bool respect_direction, LaplaceWebVisitor visit, void *context);

#endif
