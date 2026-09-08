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

/* Complete successor set, allocated in the caller's memory context. Exact
 * reads and longest-suffix proposal share this indexed native operation. */
LaplaceContinuation *laplace_trajectory_continuations(
    ArrayType *context, bool suffix_backoff, int *count);

#endif
