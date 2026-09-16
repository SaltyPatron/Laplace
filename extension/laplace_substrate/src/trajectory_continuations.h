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

/* Physicality is more than a next-token stream. One packed manifest witnesses
 * containment, membership, predecessor/successor order and co-occurrence in the
 * same exact observation. These flags stay separate from semantic testimony:
 * they are structural facts derived from a stored trajectory, not relation cells. */
enum LaplaceStructuralRelation
{
    LAPLACE_STRUCTURAL_CONTAINER   = 1u << 0,
    LAPLACE_STRUCTURAL_CONSTITUENT = 1u << 1,
    LAPLACE_STRUCTURAL_PREDECESSOR = 1u << 2,
    LAPLACE_STRUCTURAL_SUCCESSOR   = 1u << 3,
    LAPLACE_STRUCTURAL_COOCCUR     = 1u << 4,
};

typedef struct LaplaceStructuralCandidate
{
    hash128_t id;
    uint32 relation_mask;
    int64 occurrences;
    uint64 nearest_gap;
} LaplaceStructuralCandidate;

/* A request-snapshot projection of observed operands and their witnessed
 * context roots. This is candidate support, never a claim that sharing
 * a context (which may be a language) proves co-occurrence or agreement. */
typedef struct LaplaceTrajectoryScope LaplaceTrajectoryScope;
LaplaceTrajectoryScope *laplace_trajectory_scope_create(void);
void laplace_trajectory_scope_extend(LaplaceTrajectoryScope *scope, ArrayType *operands);
/* Admit physical observations containing the complete declared member set.
 * Containment discovers roots; the shared ordered matcher establishes sequence.
 * This needs no semantic attestation for each member. Empty means no roots. */
void laplace_trajectory_scope_extend_containing(LaplaceTrajectoryScope *scope, ArrayType *members);
/* Establish full-input occurrences at every canonical tree altitude before
 * the first election. Membership nominates; exact native ordinals bind. */
void laplace_trajectory_scope_bind_input(LaplaceTrajectoryScope *scope,
    const LaplacePromptInput *input);
/* Retain only witnessed occurrences supporting the selection, then advance
 * their ordinals. Exhausted observations end; they do not restart at a suffix. */
void laplace_trajectory_scope_select(LaplaceTrajectoryScope *scope, Datum selected,
                                    bool ordered);
LaplaceContinuation *laplace_trajectory_continuations_scoped(
    ArrayType *context, bool suffix_backoff, LaplaceTrajectoryScope *scope, int *count);

/* Enumerate exact structural crossings for active source identities over the
 * trajectories already retained in the request scope. RLE multiplicity and
 * logical ordinals are decoded natively; no SQL relation synthesis and no
 * trajectory-as-geometry shortcut. Results are deduplicated by target identity
 * while preserving which structural families responded and how often. */
LaplaceStructuralCandidate *laplace_trajectory_structural_candidates(
    LaplaceTrajectoryScope *scope, ArrayType *sources, uint32 relation_mask, int *count);

/* Complete successor set, allocated in the caller's memory context. Exact
 * reads and longest-suffix proposal share this indexed native operation. */
LaplaceContinuation *laplace_trajectory_continuations(
    ArrayType *context, bool suffix_backoff, int *count);

#endif
