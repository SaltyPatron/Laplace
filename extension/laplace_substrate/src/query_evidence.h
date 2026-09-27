#ifndef LAPLACE_QUERY_EVIDENCE_H
#define LAPLACE_QUERY_EVIDENCE_H

#include "postgres.h"
#include "utils/array.h"

#include "laplace/core/hash128.h"
#include "consensus_scan.h"

/*
 * Operand role is an explicit input coordinate, never inferred from identity.
 * One entity may occur in the observation, prior discourse, a semantic seed, a
 * physicality crossing, a geometric neighbor or generated working state; each
 * occurrence addresses the same consensus cell as a distinct evidence route.
 */
typedef enum LaplaceQueryOperandRole
{
    LAPLACE_QUERY_OPERAND_OBSERVATION = 1,
    LAPLACE_QUERY_OPERAND_SEMANTIC_SEED = 2,
    LAPLACE_QUERY_OPERAND_DISCOURSE = 3,
    LAPLACE_QUERY_OPERAND_PHYSICALITY = 4,
    LAPLACE_QUERY_OPERAND_WORKING = 5,
    LAPLACE_QUERY_OPERAND_GEOMETRY = 6
} LaplaceQueryOperandRole;

/*
 * One COUPLE channel: an operand occurrence bound through one typed relation
 * cell to one candidate. It is not a relevance scalar: occurrence ordinal,
 * relation and direction stay beside the cell's consensus standing and the
 * attestation outcome/source/context topology behind it, so a query-relative
 * operator can be computed downstream without erasing which route it came by.
 */
typedef struct LaplaceQueryChannel
{
    int32 ordinal;              /* 1-based occurrence in the ordered operand array */
    uint32 operand_role;         /* exact observation/discourse/working-state role   */
    hash128_t anchor;           /* exact query/working-state operand                 */
    hash128_t candidate;        /* addressed value-side endpoint                    */
    hash128_t relation;         /* typed relation; never a generic adjacency         */
    bool outbound;              /* anchor is subject when true, object when false     */

    /* Consensus standing of this relation cell as four typed Glicko-2
     * coordinates; volatility is state, not a ranking term. */
    int64 rating;
    int64 rd;
    int64 volatility;
    int64 witnesses;

    /* Attestation topology of the cell, kept apart from standing. The
     * provenance root is a Merkle digest over the bound attestation rows
     * (source, context, outcome, occurrences), so two witness sets of equal
     * size but different testimony stay distinct. */
    int64 confirm_occurrences;
    int64 draw_occurrences;
    int64 refute_occurrences;
    int64 observation_occurrences;
    int32 observation_rows;
    int32 distinct_sources;
    int32 distinct_contexts;
    hash128_t provenance_root;

    /* The subset of the topology above whose attestations carry the
     * derivation/calculation qualifier: a versioned calculation witnessing
     * the same cell, reported as its own response rather than folded into the
     * recorded observations. */
    int64 calculation_confirm_occurrences;
    int64 calculation_draw_occurrences;
    int64 calculation_refute_occurrences;
    int64 calculation_occurrences;
    int32 calculation_rows;
    int32 distinct_calculation_sources;
    int32 distinct_calculation_contexts;
    hash128_t calculation_provenance_root;
} LaplaceQueryChannel;

typedef struct LaplaceQueryEvidenceStats
{
    LaplaceConsensusScanStats forward;
    LaplaceConsensusScanStats reverse;
    uint64 observation_bindings;
    uint64 calculation_bindings;
    uint64 channels;
} LaplaceQueryEvidenceStats;

typedef struct LaplaceQueryState LaplaceQueryState;

/*
 * COUPLE over every ordered occurrence in operands: both directions of the
 * consensus relation cells touching each operand, bounded to `fanout`
 * candidates per (occurrence, relation, direction) plane, then bound to their
 * attestation topology. Incoming cells are kept with outbound=false, never
 * inverted into symmetric traversal.
 *
 * types == NULL makes every stored relation eligible; an empty array is the
 * empty relation set. The result is allocated in CurrentMemoryContext.
 */
extern LaplaceQueryChannel *laplace_query_evidence_channels(
    ArrayType *operands,
    ArrayType *types,
    int fanout,
    int *count,
    LaplaceQueryEvidenceStats *stats);

/*
 * The coupled field retained across one cognition pass. Initial operands
 * couple in one batch; selected values are appended later as new occurrences
 * and only they are coupled, without rescanning earlier operands.
 */
extern LaplaceQueryState *laplace_query_state_create(
    ArrayType *operands,
    ArrayType *types,
    int fanout,
    LaplaceQueryEvidenceStats *stats);

/*
 * Assign input roles to the retained operands (positional, one per operand)
 * and relabel their channels. Roles annotate channels only; standing and
 * attestations are untouched, so a role never becomes testimony.
 */
extern void laplace_query_state_set_operand_roles(
    LaplaceQueryState *state,
    const uint32 *roles,
    int role_count);

extern void laplace_query_state_extend(
    LaplaceQueryState *state,
    Datum selected,
    LaplaceQueryEvidenceStats *stats);

/* Couple a whole selected frontier in one set read, as working-state
 * occurrences; duplicates keep their own ordinals. */
extern void laplace_query_state_extend_batch(
    LaplaceQueryState *state,
    ArrayType *selected,
    LaplaceQueryEvidenceStats *stats);

extern void laplace_query_state_extend_batch_role(
    LaplaceQueryState *state,
    ArrayType *selected,
    uint32 operand_role,
    LaplaceQueryEvidenceStats *stats);

extern const LaplaceQueryChannel *laplace_query_state_channels(
    const LaplaceQueryState *state,
    int *count);

/*
 * Evidence for a proposed candidate set against every retained occurrence.
 * Unlike coupling, this reads every stored cell between operands and
 * candidates in both directions, including negative standing, with no fanout
 * bound, then binds each cell's attestation topology.
 *
 * candidates is a 1-D bytea[]; NULL is an error, empty yields no channels.
 * The result is allocated in CurrentMemoryContext.
 */
extern LaplaceQueryChannel *laplace_query_state_candidate_evidence(
    const LaplaceQueryState *state,
    ArrayType *candidates,
    int *count,
    LaplaceQueryEvidenceStats *stats);

/* Distinct candidates / relation ids across the retained channels, allocated
 * in CurrentMemoryContext. */
extern ArrayType *laplace_query_state_candidates(const LaplaceQueryState *state);
extern ArrayType *laplace_query_state_relation_types(const LaplaceQueryState *state);

extern void laplace_query_state_destroy(LaplaceQueryState **state);

#endif
