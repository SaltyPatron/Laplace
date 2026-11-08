#ifndef LAPLACE_SUBSTRATE_TRAJECTORY_CONTINUATIONS_INTERNAL_H
#define LAPLACE_SUBSTRATE_TRAJECTORY_CONTINUATIONS_INTERNAL_H

#include "postgres.h"
#include "miscadmin.h"
#include "catalog/pg_type.h"
#include "catalog/namespace.h"
#include "parser/parse_func.h"
#include "utils/lsyscache.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "spi_common.h"
#include "spi_nested.h"
#include "trajectory_wkb.h"
#include "trajectory_continuations.h"
#include "laplace/core/trajectory.h"
#include "content_trajectory_read.h"
#include "content_membership_read.h"
#include "observation_read.h"

typedef struct ScopedTrajectory
{
    struct ScopedTrajectory *next;
    hash128_t physicality;
    hash128_t entity;
    bytea *wkb;
    trajectory_ordinal_index_t *ordinal_index;
    MemoryContextCallback cleanup;
    bool structural;
} ScopedTrajectory;

typedef struct ScopedPosition
{
    ScopedTrajectory *trajectory;
    size_t ordinal;
    hash128_t successor;
    int stride;
} ScopedPosition;

typedef struct ScopedPhysicality
{
    hash128_t id;
    ScopedTrajectory *trajectory;
} ScopedPhysicality;

struct LaplaceTrajectoryScope
{
    MemoryContext owner;
    HTAB *operands;
    HTAB *roots;
    HTAB *physicalities;
    ScopedTrajectory *trajectories;
    Oid as_binary;
    ScopedPosition *positions;
    size_t position_count;
    size_t position_capacity;
    bool advancing;
};

typedef struct SuccessorState
{
    HTAB *successors;
    size_t stride;
    LaplaceTrajectoryScope *scope;
    ScopedTrajectory *trajectory;
    Datum physicality;
    Datum entity;
    Datum geometry;
    size_t input_stride;
    HTAB *input_positions;
} SuccessorState;

typedef struct MatcherCleanup
{
    MemoryContextCallback callback;
    trajectory_suffix_matcher_t *matcher;
} MatcherCleanup;

typedef struct MembershipMatcher
{
    trajectory_suffix_matcher_t *matcher;
    SuccessorState *state;
    Oid as_binary;
    MemoryContext row;
} MembershipMatcher;

void free_matcher(void *argument);
int record_successor(void *context, size_t ordinal, size_t stride,
                     const hash128_t *successor);

#endif
