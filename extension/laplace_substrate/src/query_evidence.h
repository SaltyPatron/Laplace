#ifndef LAPLACE_QUERY_EVIDENCE_H
#define LAPLACE_QUERY_EVIDENCE_H

#include "postgres.h"
#include "utils/array.h"

#include "laplace/core/hash128.h"
#include "consensus_scan.h"

/*
 * One query-side occurrence binding to one typed candidate/value address.
 *
 * This is deliberately not a relevance scalar.  The exact prompt occurrence
 * ordinal, relation identity and direction survive beside pooled standing and
 * the underlying attestation outcome/source/context topology.  Consumers may
 * calculate a query-relative operator from these channels; they may not erase
 * the channel identities and call the result equivalent evidence.
 */
typedef struct LaplaceQueryChannel
{
    int32 ordinal;              /* 1-based occurrence in the ordered operand array */
    hash128_t anchor;           /* exact query/working-state operand                 */
    hash128_t candidate;        /* addressed value-side endpoint                    */
    hash128_t relation;         /* typed relation; never a generic adjacency         */
    bool outbound;              /* anchor is subject when true, object when false     */

    /* Closed-epoch pooled standing for this exact relation cell. */
    int64 rating;
    int64 rd;
    int64 witnesses;

    /* Raw witnessed topology retained separately from pooled standing. */
    int64 confirm_occurrences;
    int64 draw_occurrences;
    int64 refute_occurrences;
    int64 observation_occurrences;
    int32 observation_rows;
    int32 distinct_sources;
    int32 distinct_contexts;
} LaplaceQueryChannel;

typedef struct LaplaceQueryEvidenceStats
{
    LaplaceConsensusScanStats forward;
    LaplaceConsensusScanStats reverse;
    uint64 observation_bindings;
    uint64 channels;
} LaplaceQueryEvidenceStats;

typedef struct LaplaceQueryState LaplaceQueryState;

/*
 * Build a bounded typed Q->K/evidence field over every ordered occurrence in
 * operands.  Candidate generation is bounded per occurrence after exact
 * relation-cell election.  Incoming asymmetric relations remain admissible as
 * evidence and are marked outbound=false; they are not silently inverted into
 * symmetric traversal.
 *
 * types == NULL means every stored relation family is eligible for candidate
 * generation.  An explicitly empty type array means the empty relation set.
 * The caller owns the returned array in CurrentMemoryContext.
 */
extern LaplaceQueryChannel *laplace_query_evidence_channels(
    ArrayType *operands,
    ArrayType *types,
    int fanout,
    int *count,
    LaplaceQueryEvidenceStats *stats);

/*
 * Retain the query-side evidence field across one cognition pass.  Initial
 * prompt occurrences are admitted in one batch.  A selected value may then be
 * appended as a new working-state occurrence without rescanning unchanged
 * query operands; only the newly active identity is probed for proposal state.
 */
extern LaplaceQueryState *laplace_query_state_create(
    ArrayType *operands,
    ArrayType *types,
    int fanout,
    LaplaceQueryEvidenceStats *stats);

extern void laplace_query_state_extend(
    LaplaceQueryState *state,
    Datum selected,
    LaplaceQueryEvidenceStats *stats);

extern const LaplaceQueryChannel *laplace_query_state_channels(
    const LaplaceQueryState *state,
    int *count);

/*
 * Adjudicate a bounded candidate set against every active ordered query
 * occurrence.  Unlike proposal generation, this reads every exact stored cell
 * between the active operands and candidates, including negative/refuted
 * standing, then binds the raw witness/source/context topology for those exact
 * typed cells.  This is the K->V/evidence binding stage after a bounded Q->K
 * proposal; it must not be replaced by a second top-K adjacency scan.
 *
 * candidates is a 1-D bytea[] of addressed identities.  NULL means invalid;
 * empty means no candidate evidence.  Returned channels belong to the caller's
 * CurrentMemoryContext.
 */
extern LaplaceQueryChannel *laplace_query_state_candidate_evidence(
    const LaplaceQueryState *state,
    ArrayType *candidates,
    int *count,
    LaplaceQueryEvidenceStats *stats);

/* Distinct candidate endpoints / relation ids represented by retained typed
 * proposal channels. Returned arrays are allocated in the caller's context. */
extern ArrayType *laplace_query_state_candidates(const LaplaceQueryState *state);
extern ArrayType *laplace_query_state_relation_types(const LaplaceQueryState *state);

extern void laplace_query_state_destroy(LaplaceQueryState **state);

#endif
