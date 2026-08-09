/*
 * walk_score.h — the walk's per-edge score, in ONE place.
 *
 * `relation_rank(type) * laplace_walk_edge_weight(rating, rd, witnesses, kappa)`
 * is the Glicko-complete signed weight (doc 15 §3Ca, glicko2.h) and is the SAME
 * formula consensus_adjacency uses on the Foundry export side. It was previously
 * a file-static in generate_walk.c, which meant S7 (steer_candidates.c) could
 * only have it by copying — two bodies for one quantity, agreeing until one of
 * them is edited, which is precisely the divergence the implementation law names.
 *
 * Header-inline rather than a new translation unit: it is a table lookup and a
 * multiply, it is called per edge inside the beam, and the extension links the
 * engine statically, so there is nothing to gain by putting it out of line.
 *
 * Unresolvable type: rank 0.0, so the edge can never win a beam slot over a
 * resolvable candidate. Deliberately NOT an 8-hop SPI parent-chain walk per row.
 */
#ifndef LAPLACE_WALK_SCORE_H
#define LAPLACE_WALK_SCORE_H

#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "laplace/core/glicko2.h"

static inline double
walk_relation_rank(hash128_t type_id)
{
    const laplace_relation_def_t *def = NULL;

    if (laplace_relation_lookup(&type_id, &def) == 0 && def != NULL)
        return def->rank;
    return 0.0;
}

/*
 * The signed edge weight. Sign comes from the RATING (§5): a refuted edge goes
 * negative and dead-ends, while a wide-RD win stays positive but squashed by
 * exp(-kappa*rd) so it ranks low and remains walkable. Signing on the
 * conservative bound instead would collapse "uncertain" into "refuted".
 */
static inline double
walk_edge_score(hash128_t type_id, int64 rating, int64 rd, int64 witnesses,
                double kappa)
{
    return walk_relation_rank(type_id) *
           laplace_walk_edge_weight(rating, rd, witnesses, kappa);
}

#endif /* LAPLACE_WALK_SCORE_H */
