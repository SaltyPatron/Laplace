#include "attestation_engine_internal.h"

#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <stdatomic.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <time.h>
#endif

#include "laplace/core/glicko2.h"
#include "laplace/core/score.h"

static const double kPhiTrusted = 30.0;
static const double kPhiCrank   = 350.0;
static const double kOpponentNeutral = 1500.0;
static const double kOpponentSpan    = 320.0;
static const int64_t kScoreHalfFp = 500000000LL;
static const int64_t kDeterministicGenesisUs = 1577836800000000LL;

int laplace_attestation_corroboration_mask(
    const int16_t* left, const int16_t* right, size_t count, uint8_t* admitted) {
    if (count && (!left || !right || !admitted)) return -1;
    for (size_t i = 0; i < count; ++i) {
        if (left[i] < LAPLACE_ATTESTATION_OUTCOME_REFUTE ||
            left[i] > LAPLACE_ATTESTATION_OUTCOME_CONFIRM ||
            right[i] < LAPLACE_ATTESTATION_OUTCOME_REFUTE ||
            right[i] > LAPLACE_ATTESTATION_OUTCOME_CONFIRM) return -1;
    }
    for (size_t i = 0; i < count; ++i)
        admitted[i] = left[i] == right[i] && left[i] != LAPLACE_ATTESTATION_OUTCOME_DRAW;
    return 0;
}

int laplace_attestation_resolved_witness_parameters(
    const hash128_t* type_id, double source_trust,
    int64_t* opponent_rating_fp, int64_t* opponent_rd_fp) {
    if (!type_id || !opponent_rating_fp || !opponent_rd_fp ||
        !isfinite(source_trust) || source_trust < 0.0 || source_trust > 1.0) return -1;
    hash128_t zero = {0};
    laplace_attestation_staged_t witness;
    int rc = laplace_attestation_resolved_outcome_build(
        &zero, type_id, NULL, 1, &zero, NULL, 1, source_trust,
        LAPLACE_ATTESTATION_OUTCOME_DRAW, 1, 0, &witness);
    if (rc != 0) return rc;
    *opponent_rating_fp = witness.opponent_rating_fp1e9;
    *opponent_rd_fp = witness.opponent_rd_fp1e9;
    return 0;
}

static int64_t laplace_deterministic_time_us(void) {
    static _Atomic int64_t cached = -1;
    int64_t c = atomic_load_explicit(&cached, memory_order_relaxed);
    if (c != -1) return c;

    int64_t v = 0;
    const char* s = getenv("LAPLACE_DETERMINISTIC_TIME");
    if (s && *s) {
        char* end = NULL;
        long long parsed = strtoll(s, &end, 10);
        if (end && *end == '\0' && parsed > 0) v = (int64_t)parsed;
        else v = kDeterministicGenesisUs;
    }
    int64_t unread = -1;
    if (atomic_compare_exchange_strong_explicit(&cached, &unread, v,
            memory_order_relaxed, memory_order_relaxed)) return v;
    return unread;
}

int64_t laplace_attestation_unix_us_now(void) {
    int64_t det = laplace_deterministic_time_us();
    if (det > 0) return det;
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

const char* laplace_relation_manifest_successor(size_t idx) {
    if (idx >= laplace_relation_table_count) return NULL;
    int16_t successor = laplace_relation_table[idx].successor_idx;
    if (successor == -2) return "TRAJECTORY";
    return successor < 0 ? NULL : laplace_relation_table[successor].canonical;
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

double laplace_attestation_witness_opponent_rating(double witness_weight) {
    double w = witness_weight;
    if (w < 0.0) w = 0.0;
    if (w > 1.0) w = 1.0;
    return kOpponentNeutral + kOpponentSpan * (2.0 * w - 1.0);
}

int laplace_attestation_outcome_from_score_fp(int64_t score_fp, int16_t* out_outcome) {
    if (!out_outcome) return -1;
    if (score_fp > kScoreHalfFp) *out_outcome = LAPLACE_ATTESTATION_OUTCOME_CONFIRM;
    else if (score_fp < kScoreHalfFp) *out_outcome = LAPLACE_ATTESTATION_OUTCOME_REFUTE;
    else *out_outcome = LAPLACE_ATTESTATION_OUTCOME_DRAW;
    return 0;
}

int laplace_attestation_outcome_from_totals_fp(
    int64_t games, int64_t sum_score_fp, int16_t* out_outcome) {
    if (!out_outcome) return -1;
    if (games <= 0) { *out_outcome = LAPLACE_ATTESTATION_OUTCOME_DRAW; return 0; }

    if (games <= INT64_MAX / kScoreHalfFp) {
        int64_t draw_total = games * kScoreHalfFp;
        if (sum_score_fp > draw_total)      *out_outcome = LAPLACE_ATTESTATION_OUTCOME_CONFIRM;
        else if (sum_score_fp < draw_total) *out_outcome = LAPLACE_ATTESTATION_OUTCOME_REFUTE;
        else                                *out_outcome = LAPLACE_ATTESTATION_OUTCOME_DRAW;
        return 0;
    }

    return laplace_attestation_outcome_from_score_fp(sum_score_fp / games, out_outcome);
}

int64_t laplace_attestation_score_draw_fp(void) { return kScoreHalfFp; }

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

int laplace_attestation_resolved_finish(
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
    if (laplace_relation_retired(&type_id, NULL)) return LAPLACE_REL_RETIRED;
    out->subject_id = subj;
    out->type_id = type_id;
    out->object_id = obj;
    out->source_id = src;
    out->context_id = ctx;
    out->object_is_null = obj_null;
    out->context_is_null = ctx_null;
    out->outcome = outcome;
    out->last_observed_at_unix_us = now_unix_us > 0 ? now_unix_us : laplace_attestation_unix_us_now();
    out->observation_count = observation_count > 0 ? observation_count : 1;
    out->score_fp1e9 = score_fp;
    out->opponent_rd_fp1e9 =
        (int64_t)(laplace_attestation_witness_phi(witness_weight) * (double)LAPLACE_GLICKO2_FP_SCALE);
    out->opponent_rating_fp1e9 =
        (int64_t)(laplace_attestation_witness_opponent_rating(witness_weight) * (double)LAPLACE_GLICKO2_FP_SCALE);
    out->sum_score_fp1e9 = sum_score_fp1e9;
    out->is_aggregated = is_aggregated;
    out->fold_replayable = 1;

    return laplace_attestation_id_compute(
        &out->subject_id, &out->type_id,
        obj_null ? NULL : &out->object_id, obj_null,
        &out->source_id,
        ctx_null ? NULL : &out->context_id, ctx_null,
        &out->id);
}

static int attestation_token_relation(
    const char* token, hash128_t* type_id, laplace_rel_symmetry_t* symmetry) {
    if (!token || !type_id || !symmetry) return -1;
    int rc = laplace_relation_type_id(token, type_id);
    if (rc < 0) return rc;
    if (laplace_relation_retired(type_id, NULL)) return LAPLACE_REL_RETIRED;
    *symmetry = LAPLACE_REL_SYMMETRY_ASYMMETRIC;
    const laplace_relation_def_t* def = NULL;
    if (rc == 0 && laplace_relation_lookup(type_id, &def) == 0 && def)
        *symmetry = def->symmetry;
    return 0;
}

int laplace_attestation_orient_resolved(
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
    laplace_rel_symmetry_t symmetry = LAPLACE_REL_SYMMETRY_ASYMMETRIC;
    int rc = attestation_token_relation(surface_relation, &type_id, &symmetry);
    if (rc < 0) return rc;

    hash128_t subj = *subject;
    hash128_t obj;
    hash128_t ctx = {0};
    uint8_t obj_null = object_is_null;
    uint8_t ctx_null = context_is_null;
    if (!obj_null && object) obj = *object;
    if (!ctx_null && context) ctx = *context;

    if (!obj_null) {
        if (laplace_attestation_orient(&type_id, 0, symmetry, &subj, &obj, &obj_null) != 0)
            return -1;
    }

    double witness_weight = trust_weight;
    double score = confirm ? 1.0 : 0.0;
    int64_t score_fp = (int64_t)(score * (double)LAPLACE_GLICKO2_FP_SCALE);
    int16_t outcome;
    if (laplace_attestation_outcome_from_score_fp(score_fp, &outcome) != 0) return -1;

    staged_clear_aggregated(out);
    return laplace_attestation_resolved_finish(
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
    laplace_rel_symmetry_t symmetry = LAPLACE_REL_SYMMETRY_ASYMMETRIC;
    int rc = attestation_token_relation(surface_relation, &type_id, &symmetry);
    if (rc < 0) return rc;

    hash128_t subj = *subject;
    hash128_t obj;
    hash128_t ctx = {0};
    uint8_t obj_null = object_is_null;
    uint8_t ctx_null = context_is_null;
    if (!obj_null && object) obj = *object;
    if (!ctx_null && context) ctx = *context;

    if (!obj_null) {
        if (laplace_attestation_orient(&type_id, 0, symmetry, &subj, &obj, &obj_null) != 0)
            return -1;
    }

    double witness_weight = trust_weight;
    int64_t score_fp = laplace_score_fp(magnitude, arena_scale);
    int16_t outcome;
    if (laplace_attestation_outcome_from_score_fp(score_fp, &outcome) != 0) return -1;

    staged_clear_aggregated(out);
    return laplace_attestation_resolved_finish(
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
    return laplace_attestation_resolved_outcome_build(
        subject, type_id, object, object_is_null, source, context, context_is_null,
        witness_weight,
        confirm ? LAPLACE_ATTESTATION_OUTCOME_CONFIRM : LAPLACE_ATTESTATION_OUTCOME_REFUTE,
        observation_count, now_unix_us, out);
}

int laplace_attestation_resolved_outcome_build(
    const hash128_t* subject,
    const hash128_t* type_id,
    const hash128_t* object,
    uint8_t          object_is_null,
    const hash128_t* source,
    const hash128_t* context,
    uint8_t          context_is_null,
    double           witness_weight,
    int16_t          outcome,
    int64_t          observation_count,
    int64_t          now_unix_us,
    laplace_attestation_staged_t* out) {
    if (!subject || !type_id || !source || !out) return -1;
    if (observation_count < 0) return -1;
    if (outcome != LAPLACE_ATTESTATION_OUTCOME_REFUTE &&
        outcome != LAPLACE_ATTESTATION_OUTCOME_DRAW &&
        outcome != LAPLACE_ATTESTATION_OUTCOME_CONFIRM) return -1;

    hash128_t subj;
    hash128_t obj;
    uint8_t obj_null = object_is_null;
    if (laplace_attestation_orient_resolved(type_id, subject, object, object_is_null, &subj, &obj, &obj_null) != 0)
        return -1;

    hash128_t ctx = {0};
    uint8_t ctx_null = context_is_null;
    if (!ctx_null && context) ctx = *context;

    int64_t score_fp = outcome == LAPLACE_ATTESTATION_OUTCOME_CONFIRM
        ? LAPLACE_GLICKO2_FP_SCALE
        : outcome == LAPLACE_ATTESTATION_OUTCOME_DRAW
            ? LAPLACE_GLICKO2_FP_SCALE / 2
            : 0;

    staged_clear_aggregated(out);
    return laplace_attestation_resolved_finish(
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
    if (laplace_attestation_orient_resolved(type_id, subject, object, object_is_null, &subj, &obj, &obj_null) != 0)
        return -1;

    hash128_t ctx = {0};
    uint8_t ctx_null = context_is_null;
    if (!ctx_null && context) ctx = *context;

    int64_t score_fp = laplace_score_fp(magnitude, arena_scale);
    int16_t outcome;
    if (laplace_attestation_outcome_from_score_fp(score_fp, &outcome) != 0) return -1;

    staged_clear_aggregated(out);
    return laplace_attestation_resolved_finish(
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
    if (games <= 0) return -1;
    if (!(witness_weight >= 0.0 && witness_weight <= 1.0)) return -1;

    hash128_t subj;
    hash128_t obj;
    uint8_t obj_null = object_is_null;
    if (laplace_attestation_orient_resolved(type_id, subject, object, object_is_null, &subj, &obj, &obj_null) != 0)
        return -1;

    hash128_t ctx = {0};
    hash128_zero(&ctx);
    uint8_t ctx_null = context_is_null;
    if (!ctx_null && context) ctx = *context;

    int16_t outcome;
    if (laplace_attestation_outcome_from_totals_fp(games, sum_score_fp1e9, &outcome) != 0)
        return -1;

    int64_t score_fp = sum_score_fp1e9 / games;

    return laplace_attestation_resolved_finish(
        subj, *type_id, obj, *source, ctx, obj_null, ctx_null,
        witness_weight, score_fp, outcome, games, now_unix_us, sum_score_fp1e9, 1, out);
}
