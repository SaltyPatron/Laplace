#ifndef LAPLACE_SUBSTRATE_TRAJECTORY_GENERATE_INTERNAL_H
#define LAPLACE_SUBSTRATE_TRAJECTORY_GENERATE_INTERNAL_H

#include "postgres.h"

#include <math.h>

#include "catalog/pg_type.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "mb/pg_wchar.h"
#include "nodes/bitmapset.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "utils/tuplestore.h"

#include "laplace/core/hash128.h"

#include "cognition_program.h"
#include "consensus_scan.h"
#include "prompt_input.h"
#include "prompt_intent.h"
#include "query_evidence.h"
#include "task_shape.h"
#include "relation_symmetry.h"
#include "spi_common.h"
#include "trajectory_continuations.h"
#include "walk_score.h"

typedef struct EvidenceSummary
{
    bool has_positive;
    bool has_negative;
    LaplaceQueryChannel positive;
    LaplaceQueryChannel negative;
    int32 positive_covered_occurrences;
    int32 positive_required_occurrences;
    int32 positive_relation_families;
    int32 negative_covered_occurrences;
    int32 negative_relation_families;
} EvidenceSummary;

typedef struct GeometrySummary
{
    bool has_angular;
    bool has_frechet;
    bool has_hilbert;
    uint32 angular_rank;
    uint32 frechet_rank;
    uint32 hilbert_rank;
    double angular_distance;
    double frechet_distance;
    uint8 hilbert_delta[16];
} GeometrySummary;

typedef struct GeometryEntry
{
    hash128_t id;
    GeometrySummary summary;
} GeometryEntry;

typedef struct Candidate
{
    hash128_t id;
    int64 sequence_occurrences;
    int stride;
    EvidenceSummary query;
    EvidenceSummary query_traversal;
    EvidenceSummary projection;
    GeometrySummary geometry;
    bool intent_result;
} Candidate;

typedef struct CandidateIndex
{
    hash128_t id;
    int index;
} CandidateIndex;

typedef struct EvidenceEntry
{
    hash128_t id;
    EvidenceSummary summary;
} EvidenceEntry;

typedef struct OriginEntry
{
    hash128_t id;
    Bitmapset *occurrences;
} OriginEntry;

typedef struct EvidenceCoverageKey
{
    hash128_t id;
    int32 ordinal;
    uint8 polarity;
    uint8 reserved[3];
} EvidenceCoverageKey;

typedef struct EvidenceCoverage
{
    EvidenceCoverageKey key;
} EvidenceCoverage;

typedef struct EvidenceRelationKey
{
    hash128_t id;
    hash128_t relation;
    uint8 polarity;
    uint8 reserved[7];
} EvidenceRelationKey;

typedef struct EvidenceRelation
{
    EvidenceRelationKey key;
} EvidenceRelation;

typedef struct PromptFrontier
{
    HTAB *seen;
    ArrayBuildState *ids;
    MemoryContext owner;
} PromptFrontier;

/* trajectory_generate_evidence.c */
void laplace_tg_validate_id_array(ArrayType *array, const char *name, bool allow_nulls);
bool laplace_tg_relation_can_traverse_reverse(const hash128_t *type);
HTAB *laplace_tg_new_id_index(const char *name, long expected,
                              MemoryContext owner, Size entry_size);
void laplace_tg_prompt_frontier_add(PromptFrontier *frontier, const hash128_t *id);
ArrayType *laplace_tg_query_operands(ArrayType *ordered, ArrayType *frontier,
                                     ArrayType *discourse, MemoryContext owner,
                                     uint32 **roles_out, int *role_count);
OriginEntry *laplace_tg_origin_get(HTAB *origins, const hash128_t *id, bool create);
void laplace_tg_origin_merge(HTAB *origins, const hash128_t *target,
                             const hash128_t *source, MemoryContext owner);
void laplace_tg_origin_add_occurrences(HTAB *origins, const hash128_t *target,
                                       const Bitmapset *occurrences, MemoryContext owner);
void laplace_tg_origin_inherit_sequence(HTAB *origins, const hash128_t *target,
                                        Datum *context, int context_length,
                                        int stride, MemoryContext owner);
void laplace_tg_propagate_candidate_origins(HTAB *origins, HTAB *candidate_index,
                                            const LaplaceQueryChannel *channels, int count,
                                            const LaplacePromptIntent *intent,
                                            MemoryContext owner);
int laplace_tg_positive_channel_compare(const LaplaceQueryChannel *a,
                                        const LaplaceQueryChannel *b);
int laplace_tg_negative_channel_compare(const LaplaceQueryChannel *a,
                                        const LaplaceQueryChannel *b);
HTAB *laplace_tg_evidence_summaries_from_channels(const LaplaceQueryChannel *channels,
                                                  int count, bool projection_only,
                                                  HTAB *origins, const Bitmapset *required,
                                                  const LaplaceCognitionProgram *cognition,
                                                  MemoryContext owner);
HTAB *laplace_tg_evidence_summaries(const LaplaceQueryState *state, bool projection_only,
                                    HTAB *origins, const Bitmapset *required,
                                    const LaplaceCognitionProgram *cognition,
                                    MemoryContext owner);
int laplace_tg_positive_summary_compare(const EvidenceSummary *a, const EvidenceSummary *b);
int laplace_tg_opposition_summary_compare(const EvidenceSummary *a, const EvidenceSummary *b);

/* trajectory_generate_election.c */
double laplace_tg_rng_uniform(uint64 *state);
bool laplace_tg_context_contains_id(Datum *context, int count, const hash128_t *id);
int laplace_tg_candidate_add(Candidate **items, int *count, int *capacity, HTAB *index,
                             const hash128_t *id, int64 occurrences, int stride);
ArrayType *laplace_tg_candidate_id_array(const Candidate *candidates, int count);
bool laplace_tg_candidate_is_intent_result(const Candidate *candidate,
                                           const LaplacePromptIntent *intent);
bool laplace_tg_candidate_can_output(const Candidate *candidate,
                                     const LaplacePromptIntent *intent);
bool laplace_tg_relation_allowed_by_output_scope(const hash128_t *relation,
                                                 ArrayType *scope);
HTAB *laplace_tg_geometry_summaries(const LaplacePromptIntent *intent,
                                    MemoryContext owner);
int laplace_tg_geometry_summary_compare(const GeometrySummary *a,
                                        const GeometrySummary *b);
bool laplace_tg_geometry_summary_present(const GeometrySummary *summary);
void laplace_tg_order_leading_by_sharing(Candidate *candidates, int count);
int laplace_tg_candidate_compare(const void *left, const void *right);

/* trajectory_generate_couple.c */
void laplace_tg_emit_stage(ReturnSetInfo *result, int32 step, const char *event,
                           const LaplacePromptInput *input, int operand_count,
                           int candidate_count, int proposal_channels,
                           int exact_channels, int routing_round,
                           const LaplaceCognitionProgram *program);
void laplace_tg_emit_result(ReturnSetInfo *result, int32 step,
                            const Candidate *candidate, bool trace,
                            const LaplacePromptInput *input,
                            int candidate_count, int operand_count,
                            int query_channels, int exact_channels,
                            bool routing, int routing_round,
                            const LaplaceCognitionProgram *program);
void laplace_tg_emit_terminal(ReturnSetInfo *result, int32 step,
                              const LaplacePromptInput *input,
                              int operand_count, int routing_round,
                              const LaplaceCognitionProgram *program);
void laplace_tg_couple_and_orient(
    FunctionCallInfo fcinfo, ReturnSetInfo *result,
    const LaplacePromptInput *input, MemoryContext walk_context,
    ArrayType *operands, ArrayType *relation_types, ArrayType *output_relations,
    ArrayType *discourse, const hash128_t *invocation_context,
    int32 max_stride, int32 fanout, int semantic_hop_limit,
    int context_length, bool trace, int routing_hops,
    HTAB *origins, HTAB *route_seen,
    LaplaceQueryState *query_state, LaplaceQueryState **output_state_out,
    LaplaceTrajectoryScope **trajectory_scope_out,
    LaplacePromptIntent *coupled_intent_out,
    const LaplacePromptIntent **intent_inout,
    LaplaceCognitionProgram **cognition_out,
    int *coupling_hops_out, bool *explicit_observation_scope_out);

#endif
