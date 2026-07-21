#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/pos_law.h"
#include "laplace/core/relation_law.h"

#ifdef __cplusplus
extern "C" {
#endif

#define LAPLACE_ATTESTATION_OUTCOME_REFUTE  0
#define LAPLACE_ATTESTATION_OUTCOME_DRAW    1
#define LAPLACE_ATTESTATION_OUTCOME_CONFIRM 2

#define LAPLACE_GLICKO2_FP_SCALE INT64_C(1000000000)

typedef struct {
    hash128_t id;
    hash128_t subject_id;
    hash128_t type_id;
    hash128_t object_id;
    hash128_t source_id;
    hash128_t context_id;
    int16_t   outcome;
    int64_t   last_observed_at_unix_us;
    int64_t   observation_count;
    int64_t   score_fp1e9;
    int64_t   opponent_rd_fp1e9;
    int64_t   sum_score_fp1e9;
    uint8_t   object_is_null;
    uint8_t   context_is_null;
    uint8_t   is_aggregated;
} laplace_attestation_staged_t;

int laplace_relation_resolve(const char* surface, hash128_t* out_type_id);

int laplace_relation_resolve_deprel(const char* deprel, hash128_t* out_type_id,
                                    double* out_rank, laplace_rel_symmetry_t* out_symmetry,
                                    uint8_t* out_flip, hash128_t* out_parent_id);
int laplace_relation_resolve_enhanced_deprel(const char* deprel, hash128_t* out_type_id,
                                             double* out_rank, laplace_rel_symmetry_t* out_symmetry,
                                             uint8_t* out_flip, hash128_t* out_parent_id);
int laplace_relation_resolve_feature(const char* feature_name, hash128_t* out_type_id,
                                     double* out_rank, laplace_rel_symmetry_t* out_symmetry,
                                     uint8_t* out_flip, hash128_t* out_parent_id);

size_t      laplace_relation_manifest_count(void);
const char* laplace_relation_manifest_canonical(size_t idx);
const char* laplace_relation_canonical_for_type_id(const hash128_t* type_id);

int laplace_attestation_orient(
    const hash128_t* type_id,
    uint8_t          flip,
    laplace_rel_symmetry_t symmetry,
    hash128_t*       subject,
    hash128_t*       object,
    uint8_t*         object_is_null);

int laplace_attestation_id_compute(
    const hash128_t* subject_id,
    const hash128_t* type_id,
    const hash128_t* object_id,
    uint8_t          object_is_null,
    const hash128_t* source_id,
    const hash128_t* context_id,
    uint8_t          context_is_null,
    hash128_t*       out_id);

double laplace_attestation_witness_phi(double witness_weight);

int laplace_attestation_outcome_from_score(double score, int16_t* out_outcome);

int laplace_attestation_outcome_from_score_fp(int64_t score_fp, int16_t* out_outcome);

/* Net outcome of an aggregated (games, sum_score_fp) cell. THE definition of the
 * draw threshold for every caller, managed included — see the note in the .c. */
int laplace_attestation_outcome_from_totals_fp(
    int64_t games, int64_t sum_score_fp, int16_t* out_outcome);

/* The draw threshold itself, so no other layer has to restate the literal. */
int64_t laplace_attestation_score_draw_fp(void);

int laplace_attestation_categorical_build(
    const char*      surface_relation,
    const hash128_t* subject,
    const hash128_t* object,
    uint8_t          object_is_null,
    const hash128_t* source,
    const hash128_t* context,
    uint8_t          context_is_null,
    double           trust_weight,
    int              confirm,
    int64_t          observation_count,
    int64_t          now_unix_us,
    laplace_attestation_staged_t* out);

int laplace_attestation_resolved_build(
    const hash128_t* subject,
    const hash128_t* type_id,
    const hash128_t* object,
    uint8_t          object_is_null,
    const hash128_t* source,
    const hash128_t* context,
    uint8_t          context_is_null,
    double           witness_weight,
    int              confirm,
    int64_t          observation_count,
    int64_t          now_unix_us,
    laplace_attestation_staged_t* out);

int laplace_attestation_categorical_scored_build(
    const char*      surface_relation,
    const hash128_t* subject,
    const hash128_t* object,
    uint8_t          object_is_null,
    const hash128_t* source,
    const hash128_t* context,
    uint8_t          context_is_null,
    double           trust_weight,
    double           magnitude,
    double           arena_scale,
    int64_t          observation_count,
    int64_t          now_unix_us,
    laplace_attestation_staged_t* out);

int laplace_attestation_resolved_scored_build(
    const hash128_t* subject,
    const hash128_t* type_id,
    const hash128_t* object,
    uint8_t          object_is_null,
    const hash128_t* source,
    const hash128_t* context,
    uint8_t          context_is_null,
    double           witness_weight,
    double           magnitude,
    double           arena_scale,
    int64_t          observation_count,
    int64_t          now_unix_us,
    laplace_attestation_staged_t* out);

int laplace_attestation_aggregated_build(
    const hash128_t* subject,
    const hash128_t* type_id,
    const hash128_t* object,
    uint8_t          object_is_null,
    const hash128_t* source,
    const hash128_t* context,
    uint8_t          context_is_null,
    double           witness_weight,
    int64_t          games,
    int64_t          sum_score_fp1e9,
    int64_t          now_unix_us,
    laplace_attestation_staged_t* out);


/* Upper bound on a plausible per-attestation games/observation count. The fold engine
 * already bounds per-relation games at 1<<27; anything near 1e9 in a games slot is a
 * fixed-point (1e9-scaled) value in the wrong argument position, not a real count. */
#define LAPLACE_ATTESTATION_GAMES_MAX 100000000LL

typedef struct {
    hash128_t subject;
    hash128_t object;
    uint8_t   object_is_null;
    int64_t   games;
    int64_t   sum_score_fp1e9;
} laplace_attestation_aggregated_cell_t;





int laplace_attestation_aggregated_batch_build(
    const laplace_attestation_aggregated_cell_t* cells,
    size_t           n,
    const hash128_t* type_id,
    const hash128_t* source,
    const hash128_t* context,
    uint8_t          context_is_null,
    double           witness_weight,
    int64_t          now_unix_us,
    laplace_attestation_staged_t* out);

int laplace_attestation_categorical_add(
    intent_stage_t*  stage,
    const char*      surface_relation,
    const hash128_t* subject,
    const hash128_t* object,
    uint8_t          object_is_null,
    const hash128_t* source,
    const hash128_t* context,
    uint8_t          context_is_null,
    double           trust_weight,
    int              confirm,
    int64_t          observation_count);

/* Parameter order deliberately matches laplace_attestation_aggregated_build exactly.
 * These two must never diverge: a positional mismatch between them compiled silently
 * (int64<->double implicit conversions) and corrupted every attestation written through
 * this path (observation_count = games*1e9, sum_score truncated to 0, outcome forced
 * REFUTE) — see .scratchpad/02 Issue 32. */
int laplace_attestation_aggregated_add(
    intent_stage_t*  stage,
    const hash128_t* subject,
    const hash128_t* type_id,
    const hash128_t* object,
    uint8_t          object_is_null,
    const hash128_t* source,
    const hash128_t* context,
    uint8_t          context_is_null,
    double           witness_weight,
    int64_t          games,
    int64_t          sum_score_fp1e9,
    int64_t          now_unix_us);

typedef struct {
    const char*      surface_relation;
    const hash128_t* subject;
    const hash128_t* object;
    uint8_t          object_is_null;
    const hash128_t* context;
    uint8_t          context_is_null;
    double           trust_weight;
    int              confirm;
    int64_t          observation_count;
} laplace_attestation_witness_edge_t;

int laplace_attestation_staged_batch_add(
    intent_stage_t*                     stage,
    const laplace_attestation_staged_t* rows,
    size_t                              n,
    const uint8_t*                      masks /* n*32 bytes or NULL */);

int laplace_attestation_witness_batch_add(
    intent_stage_t*                        stage,
    const laplace_attestation_witness_edge_t* edges,
    size_t                               n,
    const hash128_t*                     source,
    int64_t                              now_unix_us);

int laplace_attestation_pos_upos(
    intent_stage_t*  stage,
    const hash128_t* subject,
    const char*      upos_tag,
    const hash128_t* source,
    const hash128_t* context,
    uint8_t          context_is_null,
    double           trust_weight,
    int64_t          observation_count);

int laplace_attestation_pos_xpos(
    intent_stage_t*  stage,
    const hash128_t* subject,
    const hash128_t* xpos_entity,
    const hash128_t* source,
    const hash128_t* context,
    uint8_t          context_is_null,
    double           trust_weight,
    int64_t          observation_count);

#ifdef __cplusplus
}
#endif
