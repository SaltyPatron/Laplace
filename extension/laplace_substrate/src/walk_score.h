/*
 * walk_score.h — the per-edge score every walk and STEER step reads off a
 * consensus cell: relation rank times the Glicko-2 signed edge weight of the
 * cell's folded rating and deviation.
 *
 * A relation type the manifest registry cannot resolve ranks 0.0, so its edge
 * never outscores a resolvable one.
 */
#ifndef LAPLACE_WALK_SCORE_H
#define LAPLACE_WALK_SCORE_H

#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "laplace/core/glicko2.h"
#include "laplace/core/firmware_law.h"

static inline double
walk_relation_rank(hash128_t type_id)
{
    const laplace_relation_def_t *def = NULL;

    if (laplace_relation_lookup(&type_id, &def) == 0 && def != NULL)
        return def->rank;
    return 0.0;
}

/*
 * Relation rank is read-time salience. A relation whose rank reaches the
 * firmware ROUTE salience floor can ground an occurrence; one below it stays
 * standing evidence but cannot cover or satisfy an occurrence by itself.
 */
static inline bool
walk_relation_salient(hash128_t type_id)
{
    return walk_relation_rank(type_id) >= laplace_firmware_default()->salience_floor;
}

/*
 * The signed edge weight is the folded state's Glicko expectation around
 * neutral. RD attenuates uncertain states through g(phi); witness count is
 * already represented by the folded rating/RD and is not applied again.
 */
static inline double
walk_edge_score(hash128_t type_id, int64 rating, int64 rd)
{
    return walk_relation_rank(type_id) *
           laplace_walk_edge_weight(rating, rd);
}

#endif /* LAPLACE_WALK_SCORE_H */
