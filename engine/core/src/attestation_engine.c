#include "laplace/core/attestation_engine.h"

#include <string.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <time.h>
#endif

#include "laplace/core/glicko2.h"
#include "laplace/core/score.h"

static const double kPhiTrusted = 30.0;
static const double kPhiCrank   = 350.0;
static const int64_t kScoreHalfFp = 500000000LL;

static int64_t unix_us_now(void) {
#ifdef _WIN32
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER uli;
    uli.LowPart = ft.dwLowDateTime;
    uli.HighPart = ft.dwHighDateTime;
    const int64_t us = (int64_t)((uli.QuadPart - 116444736000000000ULL) / 10ULL);
    return us;
#else
    struct timespec ts;
    if (clock_gettime(CLOCK_REALTIME, &ts) != 0) return INTENT_STAGE_PG_EPOCH_UNIX_US;
    return (int64_t)ts.tv_sec * 1000000LL + (int64_t)ts.tv_nsec / 1000LL;
#endif
}

int laplace_relation_resolve(const char* surface, hash128_t* out_type_id) {
    return laplace_relation_resolve_surface(surface, out_type_id, NULL, NULL, NULL, NULL);
}

size_t laplace_relation_manifest_count(void) {
    return laplace_relation_table_count;
}

const char* laplace_relation_manifest_canonical(size_t idx) {
    if (idx >= laplace_relation_table_count) return NULL;
    return laplace_relation_table[idx].canonical;
}

const char* laplace_relation_canonical_for_type_id(const hash128_t* type_id) {
    const laplace_relation_def_t* def = NULL;
    if (!type_id) return NULL;
    if (laplace_relation_lookup(type_id, &def) == 0 && def) return def->canonical;
    return NULL;
}

int laplace_attestation_orient(
    const hash128_t* type_id,
    uint8_t          flip,
    laplace_rel_symmetry_t symmetry,
    hash128_t*       subject,
    hash128_t*       object,
    uint8_t*         object_is_null) {
    if (!type_id || !subject) return -1;
    if (object_is_null && *object_is_null) return 0;
    if (!object) return -1;

    if (flip) {
        hash128_t tmp = *subject;
        *subject = *object;
        *object = tmp;
    }
    if (symmetry == LAPLACE_REL_SYMMETRY_SYMMETRIC) {
        if (hash128_compare(subject, object) > 0) {
            hash128_t tmp = *subject;
            *subject = *object;
            *object = tmp;
        }
    }
    return 0;
}

int laplace_attestation_id_compute(
    const hash128_t* subject_id,
    const hash128_t* type_id,
    const hash128_t* object_id,
    uint8_t          object_is_null,
    const hash128_t* source_id,
    const hash128_t* context_id,
    uint8_t          context_is_null,
    hash128_t*       out_id) {
    if (!subject_id || !type_id || !source_id || !out_id) return -1;
    hash128_t zero;
    hash128_zero(&zero);
    uint8_t buf[80];
    memcpy(buf, subject_id, 16);
    memcpy(buf + 16, type_id, 16);
    if (object_is_null) memcpy(buf + 32, &zero, 16);
    else if (object_id) memcpy(buf + 32, object_id, 16);
    else return -1;
    memcpy(buf + 48, source_id, 16);
    if (context_is_null) memcpy(buf + 64, &zero, 16);
    else if (context_id) memcpy(buf + 64, context_id, 16);
    else return -1;
    hash128_blake3(buf, sizeof(buf), out_id);
    return 0;
}

double laplace_attestation_witness_phi(double witness_weight) {
    double w = witness_weight;
    if (w < 0.0) w = 0.0;
    if (w > 1.0) w = 1.0;
    return kPhiCrank + (kPhiTrusted - kPhiCrank) * w;
}

int laplace_attestation_outcome_from_score_fp(int64_t score_fp, int16_t* out_outcome) {
    if (!out_outcome) return -1;
    if (score_fp > kScoreHalfFp) *out_outcome = LAPLACE_ATTESTATION_OUTCOME_CONFIRM;
    else if (score_fp < kScoreHalfFp) *out_outcome = LAPLACE_ATTESTATION_OUTCOME_REFUTE;
    else *out_outcome = LAPLACE_ATTESTATION_OUTCOME_DRAW;
    return 0;
}

int laplace_attestation_outcome_from_score(double score, int16_t* out_outcome) {
    if (!out_outcome) return -1;
    double s = score;
    if (s < 0.0) s = 0.0;
    if (s > 1.0) s = 1.0;
    int64_t score_fp = (int64_t)(s * (double)LAPLACE_GLICKO2_FP_SCALE);
    return laplace_attestation_outcome_from_score_fp(score_fp, out_outcome);
}

static void staged_clear_aggregated(laplace_attestation_staged_t* out) {
    out->sum_score_fp1e9 = 0;
    out->is_aggregated = 0;
}

static int attestation_resolved_finish(
    hash128_t subj,
    hash128_t type_id,
    hash128_t obj,
    hash128_t src,
    hash128_t ctx,
    uint8_t obj_null,
    uint8_t ctx_null,
    double witness_weight,
    int64_t score_fp,
    int16_t outcome,
    int64_t observation_count,
    int64_t now_unix_us,
    int64_t sum_score_fp1e9,
    uint8_t is_aggregated,
    laplace_attestation_staged_t* out) {
    out->subject_id = subj;
    out->type_id = type_id;
    out->object_id = obj;
    out->source_id = src;
    out->context_id = ctx;
    out->object_is_null = obj_null;
    out->context_is_null = ctx_null;
    out->outcome = outcome;
    out->last_observed_at_unix_us = now_unix_us > 0 ? now_unix_us : unix_us_now();
    out->observation_count = observation_count > 0 ? observation_count : 1;
    out->score_fp1e9 = score_fp;
    out->opponent_rd_fp1e9 =
        (int64_t)(laplace_attestation_witness_phi(witness_weight) * (double)LAPLACE_GLICKO2_FP_SCALE);
    out->sum_score_fp1e9 = sum_score_fp1e9;
    out->is_aggregated = is_aggregated;

    return laplace_attestation_id_compute(
        &out->subject_id, &out->type_id,
        obj_null ? NULL : &out->object_id, obj_null,
        &out->source_id,
        ctx_null ? NULL : &out->context_id, ctx_null,
        &out->id);
}

static int attestation_orient_resolved(
    const hash128_t* type_id,
    const hash128_t* subject,
    const hash128_t* object,
    uint8_t          object_is_null,
    hash128_t*       out_subj,
    hash128_t*       out_obj,
    uint8_t*         out_obj_null) {
    if (!type_id || !subject || !out_subj) return -1;

    const laplace_relation_def_t* def = NULL;
    laplace_rel_symmetry_t symmetry = LAPLACE_REL_SYMMETRY_ASYMMETRIC;
    if (laplace_relation_lookup(type_id, &def) == 0 && def)
        symmetry = def->symmetry;

    *out_subj = *subject;
    *out_obj_null = object_is_null;
    if (!object_is_null && object) *out_obj = *object;

    if (!object_is_null) {
        if (laplace_attestation_orient(type_id, 0, symmetry, out_subj, out_obj, out_obj_null) != 0)
            return -1;
    }
    return 0;
}

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
    laplace_attestation_staged_t* out) {
    if (!surface_relation || !subject || !source || !out) return -1;
    if (observation_count < 0) return -1;

    hash128_t type_id;
    double rank = 0.09;
    laplace_rel_symmetry_t symmetry = LAPLACE_REL_SYMMETRY_ASYMMETRIC;
    uint8_t flip = 0;
    hash128_t parent_id;
    int rc = laplace_relation_resolve_surface(
        surface_relation, &type_id, &rank, &symmetry, &flip, &parent_id);
    if (rc < 0) return rc;
    (void)parent_id;

    hash128_t subj = *subject;
    hash128_t obj;
    hash128_t ctx;
    uint8_t obj_null = object_is_null;
    uint8_t ctx_null = context_is_null;
    if (!obj_null && object) obj = *object;
    if (!ctx_null && context) ctx = *context;

    if (!obj_null) {
        if (laplace_attestation_orient(&type_id, flip, symmetry, &subj, &obj, &obj_null) != 0)
            return -1;
    } else if (flip) {
        return -1;
    }

    double witness_weight = rank * trust_weight;
    double score = confirm ? 1.0 : 0.0;
    int64_t score_fp = (int64_t)(score * (double)LAPLACE_GLICKO2_FP_SCALE);
    int16_t outcome;
    if (laplace_attestation_outcome_from_score_fp(score_fp, &outcome) != 0) return -1;

    staged_clear_aggregated(out);
    return attestation_resolved_finish(
        subj, type_id, obj, *source, ctx, obj_null, ctx_null,
        witness_weight, score_fp, outcome, observation_count, now_unix_us, 0, 0, out);
}

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
    laplace_attestation_staged_t* out) {
    if (!surface_relation || !subject || !source || !out) return -1;
    if (observation_count < 0) return -1;

    hash128_t type_id;
    double rank = 0.09;
    laplace_rel_symmetry_t symmetry = LAPLACE_REL_SYMMETRY_ASYMMETRIC;
    uint8_t flip = 0;
    hash128_t parent_id;
    int rc = laplace_relation_resolve_surface(
        surface_relation, &type_id, &rank, &symmetry, &flip, &parent_id);
    if (rc < 0) return rc;
    (void)parent_id;

    hash128_t subj = *subject;
    hash128_t obj;
    hash128_t ctx;
    uint8_t obj_null = object_is_null;
    uint8_t ctx_null = context_is_null;
    if (!obj_null && object) obj = *object;
    if (!ctx_null && context) ctx = *context;

    if (!obj_null) {
        if (laplace_attestation_orient(&type_id, flip, symmetry, &subj, &obj, &obj_null) != 0)
            return -1;
    } else if (flip) {
        return -1;
    }

    double witness_weight = rank * trust_weight;
    int64_t score_fp = laplace_score_fp(magnitude, arena_scale);
    int16_t outcome;
    if (laplace_attestation_outcome_from_score_fp(score_fp, &outcome) != 0) return -1;

    staged_clear_aggregated(out);
    return attestation_resolved_finish(
        subj, type_id, obj, *source, ctx, obj_null, ctx_null,
        witness_weight, score_fp, outcome, observation_count, now_unix_us, 0, 0, out);
}

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
    laplace_attestation_staged_t* out) {
    if (!subject || !type_id || !source || !out) return -1;
    if (observation_count < 0) return -1;

    hash128_t subj;
    hash128_t obj;
    uint8_t obj_null = object_is_null;
    if (attestation_orient_resolved(type_id, subject, object, object_is_null, &subj, &obj, &obj_null) != 0)
        return -1;

    hash128_t ctx;
    uint8_t ctx_null = context_is_null;
    if (!ctx_null && context) ctx = *context;

    double score = confirm ? 1.0 : 0.0;
    int64_t score_fp = (int64_t)(score * (double)LAPLACE_GLICKO2_FP_SCALE);
    int16_t outcome;
    if (laplace_attestation_outcome_from_score_fp(score_fp, &outcome) != 0) return -1;

    staged_clear_aggregated(out);
    return attestation_resolved_finish(
        subj, *type_id, obj, *source, ctx, obj_null, ctx_null,
        witness_weight, score_fp, outcome, observation_count, now_unix_us, 0, 0, out);
}

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
    laplace_attestation_staged_t* out) {
    if (!subject || !type_id || !source || !out) return -1;
    if (observation_count < 0) return -1;

    hash128_t subj;
    hash128_t obj;
    uint8_t obj_null = object_is_null;
    if (attestation_orient_resolved(type_id, subject, object, object_is_null, &subj, &obj, &obj_null) != 0)
        return -1;

    hash128_t ctx;
    uint8_t ctx_null = context_is_null;
    if (!ctx_null && context) ctx = *context;

    int64_t score_fp = laplace_score_fp(magnitude, arena_scale);
    int16_t outcome;
    if (laplace_attestation_outcome_from_score_fp(score_fp, &outcome) != 0) return -1;

    staged_clear_aggregated(out);
    return attestation_resolved_finish(
        subj, *type_id, obj, *source, ctx, obj_null, ctx_null,
        witness_weight, score_fp, outcome, observation_count, now_unix_us, 0, 0, out);
}

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
    laplace_attestation_staged_t* out) {
    if (!subject || !type_id || !source || !out) return -1;
    /* Argument-swap tripwires (Issue 32): a legitimate witness weight is a rank*trust
     * product in [0,1], and a legitimate games count is far below 1e8 — a 1e9-scaled
     * fixed-point value landing in either slot means a caller has its arguments
     * rotated. Fail loudly instead of writing corrupt evidence. */
    if (games <= 0 || games > LAPLACE_ATTESTATION_GAMES_MAX) return -1;
    if (!(witness_weight >= 0.0 && witness_weight <= 1.0)) return -1;

    hash128_t subj;
    hash128_t obj;
    uint8_t obj_null = object_is_null;
    if (attestation_orient_resolved(type_id, subject, object, object_is_null, &subj, &obj, &obj_null) != 0)
        return -1;

    hash128_t ctx;
    hash128_zero(&ctx);
    uint8_t ctx_null = context_is_null;
    if (!ctx_null && context) ctx = *context;

    int64_t net_half = games * kScoreHalfFp;
    int16_t outcome;
    if (sum_score_fp1e9 > net_half) outcome = LAPLACE_ATTESTATION_OUTCOME_CONFIRM;
    else if (sum_score_fp1e9 < net_half) outcome = LAPLACE_ATTESTATION_OUTCOME_REFUTE;
    else outcome = LAPLACE_ATTESTATION_OUTCOME_DRAW;

    int64_t score_fp = sum_score_fp1e9 / games;

    return attestation_resolved_finish(
        subj, *type_id, obj, *source, ctx, obj_null, ctx_null,
        witness_weight, score_fp, outcome, games, now_unix_us, sum_score_fp1e9, 1, out);
}

int laplace_attestation_aggregated_batch_build(
    const laplace_attestation_aggregated_cell_t* cells,
    size_t           n,
    const hash128_t* type_id,
    const hash128_t* source,
    const hash128_t* context,
    uint8_t          context_is_null,
    double           witness_weight,
    int64_t          now_unix_us,
    laplace_attestation_staged_t* out) {
    if (!cells || !type_id || !source || !out) return -1;
    if (!(witness_weight >= 0.0 && witness_weight <= 1.0)) return -1;

    const laplace_relation_def_t* def = NULL;
    laplace_rel_symmetry_t symmetry = LAPLACE_REL_SYMMETRY_ASYMMETRIC;
    if (laplace_relation_lookup(type_id, &def) == 0 && def)
        symmetry = def->symmetry;

    hash128_t ctx;
    hash128_zero(&ctx);
    uint8_t ctx_null = context_is_null;
    if (!ctx_null && context) ctx = *context;

    const int64_t opponent_rd =
        (int64_t)(laplace_attestation_witness_phi(witness_weight) * (double)LAPLACE_GLICKO2_FP_SCALE);
    const int64_t observed_at = now_unix_us > 0 ? now_unix_us : unix_us_now();

    for (size_t i = 0; i < n; ++i) {
        const laplace_attestation_aggregated_cell_t* c = &cells[i];
        if (c->games <= 0 || c->games > LAPLACE_ATTESTATION_GAMES_MAX) return -1;

        hash128_t subj = c->subject;
        hash128_t obj;
        hash128_zero(&obj);
        uint8_t obj_null = c->object_is_null;
        if (!obj_null) {
            obj = c->object;
            if (laplace_attestation_orient(type_id, 0, symmetry, &subj, &obj, &obj_null) != 0)
                return -1;
        }

        const int64_t net_half = c->games * kScoreHalfFp;
        int16_t outcome;
        if (c->sum_score_fp1e9 > net_half)      outcome = LAPLACE_ATTESTATION_OUTCOME_CONFIRM;
        else if (c->sum_score_fp1e9 < net_half) outcome = LAPLACE_ATTESTATION_OUTCOME_REFUTE;
        else                                    outcome = LAPLACE_ATTESTATION_OUTCOME_DRAW;

        laplace_attestation_staged_t* o = &out[i];
        o->subject_id = subj;
        o->type_id = *type_id;
        o->object_id = obj;
        o->source_id = *source;
        o->context_id = ctx;
        o->object_is_null = obj_null;
        o->context_is_null = ctx_null;
        o->outcome = outcome;
        o->last_observed_at_unix_us = observed_at;
        o->observation_count = c->games;
        o->score_fp1e9 = c->sum_score_fp1e9 / c->games;
        o->opponent_rd_fp1e9 = opponent_rd;
        o->sum_score_fp1e9 = c->sum_score_fp1e9;
        o->is_aggregated = 1;

        if (laplace_attestation_id_compute(
                &o->subject_id, &o->type_id,
                obj_null ? NULL : &o->object_id, obj_null,
                &o->source_id,
                ctx_null ? NULL : &o->context_id, ctx_null,
                &o->id) != 0)
            return -1;
    }
    return 0;
}

static int staged_to_intent(intent_stage_t* stage, const laplace_attestation_staged_t* a) {
    hash128_t* obj_ptr = a->object_is_null ? NULL : (hash128_t*)&a->object_id;
    hash128_t* ctx_ptr = a->context_is_null ? NULL : (hash128_t*)&a->context_id;
    return intent_stage_add_attestation(
        stage, &a->id, &a->subject_id, &a->type_id, obj_ptr, &a->source_id, ctx_ptr,
        a->outcome, a->last_observed_at_unix_us, a->observation_count, NULL);
}

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
    int64_t          observation_count) {
    if (!stage) return -1;
    laplace_attestation_staged_t staged;
    int rc = laplace_attestation_categorical_build(
        surface_relation, subject, object, object_is_null, source, context, context_is_null,
        trust_weight, confirm, observation_count, 0, &staged);
    if (rc != 0) return rc;
    return staged_to_intent(stage, &staged);
}

/* Parameter order deliberately identical to laplace_attestation_aggregated_build — a
 * positional mismatch here compiled silently via int64<->double implicit conversions and
 * corrupted every attestation on this path (.scratchpad/02 Issue 32). */
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
    int64_t          now_unix_us) {
    if (!stage || !subject || !type_id || !source) return -1;
    laplace_attestation_staged_t staged;
    int rc = laplace_attestation_aggregated_build(
        subject, type_id, object, object_is_null, source, context, context_is_null,
        witness_weight, games, sum_score_fp1e9, now_unix_us, &staged);
    if (rc != 0) return rc;
    return staged_to_intent(stage, &staged);
}

int laplace_attestation_witness_batch_add(
    intent_stage_t*                        stage,
    const laplace_attestation_witness_edge_t* edges,
    size_t                               n,
    const hash128_t*                     source,
    int64_t                              now_unix_us) {
    if (!stage || !edges || !source) return -1;
    for (size_t i = 0; i < n; ++i) {
        const laplace_attestation_witness_edge_t* e = &edges[i];
        laplace_attestation_staged_t staged;
        int rc = laplace_attestation_categorical_build(
            e->surface_relation, e->subject, e->object, e->object_is_null,
            source, e->context, e->context_is_null,
            e->trust_weight, e->confirm, e->observation_count, now_unix_us, &staged);
        if (rc != 0) return -2;
        if (staged_to_intent(stage, &staged) != 0) return -2;
    }
    return 0;
}

int laplace_attestation_pos_upos(
    intent_stage_t*  stage,
    const hash128_t* subject,
    const char*      upos_tag,
    const hash128_t* source,
    const hash128_t* context,
    uint8_t          context_is_null,
    double           trust_weight,
    int64_t          observation_count) {
    if (!stage || !subject || !upos_tag || !source) return -1;
    hash128_t pos_id;
    if (laplace_pos_resolve_entity(upos_tag, LAPLACE_POS_TAGSET_UPOS, &pos_id) < 0) return -1;
    return laplace_attestation_categorical_add(
        stage, "HAS_UPOS", subject, &pos_id, 0, source, context, context_is_null,
        trust_weight, 1, observation_count);
}

int laplace_attestation_pos_xpos(
    intent_stage_t*  stage,
    const hash128_t* subject,
    const hash128_t* xpos_entity,
    const hash128_t* source,
    const hash128_t* context,
    uint8_t          context_is_null,
    double           trust_weight,
    int64_t          observation_count) {
    if (!stage || !subject || !xpos_entity || !source) return -1;
    return laplace_attestation_categorical_add(
        stage, "HAS_XPOS", subject, xpos_entity, 0, source, context, context_is_null,
        trust_weight, 1, observation_count);
}
