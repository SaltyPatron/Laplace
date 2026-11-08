#ifndef LAPLACE_SUBSTRATE_GENERATE_WALK_INTERNAL_H
#define LAPLACE_SUBSTRATE_GENERATE_WALK_INTERNAL_H

#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/lsyscache.h"
#include "utils/numeric.h"
#include "utils/hsearch.h"
#include "lib/stringinfo.h"
#include "mb/pg_wchar.h"

#include "laplace/core/relation_law.h"
#include "laplace/core/glicko2.h"
#include "laplace/core/highway_table.h"
#include "laplace/core/math4d.h"
#include "laplace/core/mantissa.h"

#include "spi_common.h"
#include "spi_nested.h"
#include "spi_stream.h"
#include "laplace/core/sql_catalog.h"
#include "perfcache_native.h"
#include "walk_score.h"
#include "relation_symmetry.h"

typedef struct RawEdge
{
    int32  idx;
    Datum  object;
    bool   object_is_relation_type;
    Datum  rel_type;
    int64  rating;
    int64  rd;
    int64  witnesses;
    Datum  highway_mask;
    bool   subj_coord_ok;
    double subj_xyzm[4];
    Oid    subj_partoid;
    bool   obj_coord_ok;
    double obj_xyzm[4];
    Oid    obj_partoid;
} RawEdge;

typedef struct RankedEdge
{
    Datum  object;
    Datum  rel_type;
    int64  rating;
    int64  rd;
    int64  witnesses;
    double base;
    bool   topic_match;
    bool   continuity_known;
    uint64 continuity_distance;
    bool   geometry_known;
    double geometry_distance;
} RankedEdge;

typedef struct WalkNode
{
    int     parent;
    int     depth;
    Datum   entity;
    Datum   rel_type;
    Datum   eff_mu;
    int64   path_mu_fp;
    int64   witnesses;
} WalkNode;

typedef struct WalkOrderKey
{
    int     depth;
    int64   path_mu_fp;
    int     idx;
} WalkOrderKey;

/* generate_walk_plans.c */
SPIPlanPtr laplace_walk_get_batch_plan(bool use_geometry, bool have_intent_mask);
int laplace_walk_raw_edge_cmp_idx(const void *a, const void *b);
int laplace_walk_ranked_edge_cmp_score_desc(const void *a, const void *b);
bool laplace_walk_mask_overlaps(Datum highway_mask_bytea,
                                const laplace_mask256_t *intent_mask);
bool laplace_walk_ordinal_continuity_distance(Datum subject_entity,
                                              Datum object_entity,
                                              uint64 *distance);

#endif
