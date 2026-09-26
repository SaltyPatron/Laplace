#ifndef LAPLACE_TRAJECTORY_CONTINUATIONS_H
#define LAPLACE_TRAJECTORY_CONTINUATIONS_H

#include "postgres.h"
#include "utils/array.h"
#include "laplace/core/hash128.h"
#include "prompt_input.h"

typedef struct LaplaceContinuation
{
    hash128_t id;
    int64 occurrences;
    int stride;
} LaplaceContinuation;

/* Structural relations read from one packed trajectory: container,
 * constituent, predecessor, successor, co-occurrence, and exact continuation of
 * a whole ordered context (a stronger fact than a one-hop successor). They are
 * trajectory facts, not attested testimony. */
enum LaplaceStructuralRelation
{
    LAPLACE_STRUCTURAL_CONTAINER    = 1u << 0,
    LAPLACE_STRUCTURAL_CONSTITUENT  = 1u << 1,
    LAPLACE_STRUCTURAL_PREDECESSOR  = 1u << 2,
    LAPLACE_STRUCTURAL_SUCCESSOR    = 1u << 3,
    LAPLACE_STRUCTURAL_COOCCUR      = 1u << 4,
    LAPLACE_STRUCTURAL_CONTINUATION = 1u << 5,
};

typedef struct LaplaceStructuralCandidate
{
    hash128_t source;
    hash128_t id;
    /* One route bit per row. Routes reaching the same target stay separate
     * candidates with their own occurrence and gap state into COUPLE. */
    uint32 relation_mask;
    int64 occurrences;
    uint64 nearest_gap;
} LaplaceStructuralCandidate;

/* Request-scoped trajectory set: the observation context roots of the
 * operands, each loaded once as packed WKB under the request snapshot, plus the
 * occurrence positions the request is advancing through. Sharing a root
 * nominates candidates; it is not co-occurrence or agreement. */
typedef struct LaplaceTrajectoryScope LaplaceTrajectoryScope;
LaplaceTrajectoryScope *laplace_trajectory_scope_create(void);
void laplace_trajectory_scope_extend(LaplaceTrajectoryScope *scope, ArrayType *operands);
/* Adds the trajectories of stored entities whose membership contains every
 * member. Containment nominates; order is established by the ordered matcher.
 * No per-member attestation is read. Empty members add nothing. */
void laplace_trajectory_scope_extend_containing(LaplaceTrajectoryScope *scope, ArrayType *members);
/* Binds the whole input: for each distinct tier cut of its tree, highest
 * altitude first, membership nominates trajectories and the exact matcher
 * records every occurrence of the complete cut. The ordinals after those
 * occurrences become the scope's live positions and the scope advances. */
void laplace_trajectory_scope_bind_input(LaplaceTrajectoryScope *scope,
    const LaplacePromptInput *input);
/* Keeps the positions whose successor is the selected id and advances each by
 * one ordinal (ordered=false drops all). An exhausted trajectory ends; it is
 * not re-matched at a suffix. */
void laplace_trajectory_scope_select(LaplaceTrajectoryScope *scope, Datum selected,
                                    bool ordered);
LaplaceContinuation *laplace_trajectory_continuations_scoped(
    ArrayType *context, bool suffix_backoff, LaplaceTrajectoryScope *scope, int *count);

/* Structural crossings of the source ids over the scope's trajectories. Each
 * trajectory's constituents are decoded natively, runs expanded to logical
 * ordinals. Rows are keyed by source/target/route; occurrences sum and the
 * nearest ordinal gap is kept within a route, so routes reaching one target
 * stay separate for COUPLE. Which input occurrence a source came from is the
 * caller's to carry. */
LaplaceStructuralCandidate *laplace_trajectory_structural_candidates(
    LaplaceTrajectoryScope *scope, ArrayType *sources, uint32 relation_mask, int *count);

/* Successors of the context over stored trajectories at the greatest exact
 * stride, ordered by occurrence count, allocated in the caller's context.
 * suffix_backoff lets shorter context suffixes propose when the full context
 * has no successor. */
LaplaceContinuation *laplace_trajectory_continuations(
    ArrayType *context, bool suffix_backoff, int *count);

#endif
