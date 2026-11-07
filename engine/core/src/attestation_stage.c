#include "attestation_engine_internal.h"

#include <stdlib.h>
#include <string.h>
#include <math.h>

#include "laplace/core/glicko2.h"
#include "laplace/core/utf8.h"

int laplace_attestation_codepoint_range_add(
    intent_stage_t* stage,
    uint32_t first_codepoint,
    uint32_t last_codepoint,
    const hash128_t* type_id,
    const hash128_t* object_id,
    uint8_t object_is_null,
    const hash128_t* source_id,
    const hash128_t* context_id,
    uint8_t context_is_null,
    double source_trust,
    int confirm,
    int64_t observation_count) {
    if (!stage || !type_id || object_is_null > 1
        || (!object_is_null && !object_id) || !source_id
        || context_is_null > 1 || (!context_is_null && !context_id)
        || (confirm != 0 && confirm != 1)
        || first_codepoint > last_codepoint || last_codepoint > 0x10FFFFu
        || observation_count <= 0) return -1;

    enum { CHUNK = 256 };
    laplace_attestation_staged_t staged[CHUNK];
    uint32_t cp = first_codepoint;
    while (cp <= last_codepoint) {
        size_t n = 0;
        while (n < CHUNK && cp <= last_codepoint) {
            uint8_t u8[4];
            size_t len = laplace_utf8_encode(cp, u8);
            hash128_t subject;
            hash128_blake3(u8, len, &subject);
            int rc = laplace_attestation_resolved_build(
                &subject, type_id, object_id, object_is_null, source_id,
                context_is_null ? NULL : context_id, context_is_null,
                source_trust, confirm, observation_count, 0, &staged[n]);
            if (rc != 0) return rc;
            ++n;
            if (cp == 0x10FFFFu) { ++cp; break; }
            ++cp;
        }
        if (laplace_attestation_staged_batch_add(stage, staged, n, NULL) != 0)
            return -2;
    }
    return 0;
}

int laplace_attestation_codepoint_range_relations_add(
    intent_stage_t* stage,
    uint32_t first_codepoint,
    uint32_t last_codepoint,
    const laplace_codepoint_range_relation_t* relations,
    size_t relation_count,
    const hash128_t* source_id,
    double source_trust) {
    if (!stage || (!relations && relation_count) || !source_id
        || first_codepoint > last_codepoint || last_codepoint > 0x10FFFFu
        || !isfinite(source_trust) || source_trust < 0.0 || source_trust > 1.0)
        return -1;
    if (relation_count == 0) return 0;

    enum { CHUNK = 256 };
    laplace_attestation_staged_t staged[CHUNK];
    uint32_t cp = first_codepoint;
    while (cp <= last_codepoint) {
        uint8_t u8[4];
        size_t len = laplace_utf8_encode(cp, u8);
        hash128_t subject;
        hash128_blake3(u8, len, &subject);

        for (size_t start = 0; start < relation_count; start += CHUNK) {
            size_t count = relation_count - start;
            if (count > CHUNK) count = CHUNK;
            for (size_t i = 0; i < count; ++i) {
                const laplace_codepoint_range_relation_t* relation = &relations[start + i];
                if ((relation->object_is_null != 0 && relation->object_is_null != 1)
                    || (relation->context_is_null != 0 && relation->context_is_null != 1)
                    || (relation->confirm != 0 && relation->confirm != 1)
                    || relation->reserved != 0 || relation->observation_count <= 0)
                    return -1;
                int rc = laplace_attestation_resolved_build(
                    &subject,
                    &relation->type_id,
                    relation->object_is_null ? NULL : &relation->object_id,
                    (uint8_t)relation->object_is_null,
                    source_id,
                    relation->context_is_null ? NULL : &relation->context_id,
                    (uint8_t)relation->context_is_null,
                    source_trust,
                    relation->confirm,
                    relation->observation_count,
                    0,
                    &staged[i]);
                if (rc != 0) return rc;
            }
            if (laplace_attestation_staged_batch_add(stage, staged, count, NULL) != 0)
                return -2;
        }
        if (cp == 0x10FFFFu) break;
        ++cp;
    }
    return 0;
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

    hash128_t ctx = {0};
    hash128_zero(&ctx);
    uint8_t ctx_null = context_is_null;
    if (!ctx_null && context) ctx = *context;

    const int64_t opponent_rd =
        (int64_t)(laplace_attestation_witness_phi(witness_weight) * (double)LAPLACE_GLICKO2_FP_SCALE);
    const int64_t opponent_rating =
        (int64_t)(laplace_attestation_witness_opponent_rating(witness_weight) * (double)LAPLACE_GLICKO2_FP_SCALE);
    const int64_t observed_at = now_unix_us > 0 ? now_unix_us : laplace_attestation_unix_us_now();

    for (size_t i = 0; i < n; ++i) {
        const laplace_attestation_aggregated_cell_t* c = &cells[i];
        if (c->games <= 0) return -1;

        hash128_t subj = c->subject;
        hash128_t obj;
        hash128_zero(&obj);
        uint8_t obj_null = c->object_is_null;
        if (!obj_null) {
            obj = c->object;
            if (laplace_attestation_orient(type_id, 0, symmetry, &subj, &obj, &obj_null) != 0)
                return -1;
        }

        int16_t outcome;
        if (laplace_attestation_outcome_from_totals_fp(c->games, c->sum_score_fp1e9, &outcome) != 0)
            return -1;

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
        o->opponent_rating_fp1e9 = opponent_rating;
        o->sum_score_fp1e9 = c->sum_score_fp1e9;
        o->is_aggregated = 1;
        o->fold_replayable = 1;

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

static int staged_sum_score_total(const laplace_attestation_staged_t* a, int64_t* out) {
    if (a->is_aggregated) {
        *out = a->sum_score_fp1e9;
        return 0;
    }
    if (a->score_fp1e9 < 0 || a->observation_count < 0) return -1;
    if (a->observation_count == 0 || a->score_fp1e9 == 0) { *out = 0; return 0; }
    if (a->observation_count > INT64_MAX / a->score_fp1e9)
        return -1;
    *out = a->score_fp1e9 * a->observation_count;
    return 0;
}

static int staged_to_intent_masked(intent_stage_t* stage, const laplace_attestation_staged_t* a,
                                   const uint8_t* qualifier_mask) {
    hash128_t* obj_ptr = a->object_is_null ? NULL : (hash128_t*)&a->object_id;
    hash128_t* ctx_ptr = a->context_is_null ? NULL : (hash128_t*)&a->context_id;
    int64_t sum_score;
    if (staged_sum_score_total(a, &sum_score) != 0) return -1;
    return intent_stage_add_attestation_mode(
        stage, &a->id, &a->subject_id, &a->type_id, obj_ptr, &a->source_id, ctx_ptr,
        a->outcome, a->last_observed_at_unix_us, a->observation_count,
        sum_score, a->opponent_rd_fp1e9, a->opponent_rating_fp1e9,
        a->fold_replayable, qualifier_mask);
}

static int staged_to_intent(intent_stage_t* stage, const laplace_attestation_staged_t* a) {
    return staged_to_intent_masked(stage, a, NULL);
}

static int staged_to_intent_surface(intent_stage_t* stage, const laplace_attestation_staged_t* a,
                                    const char* surface_relation) {
    const int bit = laplace_relation_surface_qualifier(surface_relation);
    if (bit < 0) return staged_to_intent(stage, a);
    uint8_t mask[32] = {0};
    mask[bit >> 3] = (uint8_t)(1u << (bit & 7));
    return staged_to_intent_masked(stage, a, mask);
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
    return staged_to_intent_surface(stage, &staged, surface_relation);
}

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

int laplace_attestation_staged_batch_add(
    intent_stage_t* stage,
    const laplace_attestation_staged_t* rows,
    size_t n,
    const uint8_t* masks) {
    if (!stage || (!rows && n > 0)) return -1;
    for (size_t i = 0; i < n; ++i) {
        const laplace_attestation_staged_t* a = &rows[i];
        const hash128_t* obj_ptr = a->object_is_null ? NULL : &a->object_id;
        const hash128_t* ctx_ptr = a->context_is_null ? NULL : &a->context_id;
        int64_t sum_score;
        if (staged_sum_score_total(a, &sum_score) != 0) return -2;
        if (intent_stage_add_attestation_mode(
                stage, &a->id, &a->subject_id, &a->type_id, obj_ptr,
                &a->source_id, ctx_ptr, a->outcome,
                a->last_observed_at_unix_us, a->observation_count,
                sum_score, a->opponent_rd_fp1e9, a->opponent_rating_fp1e9,
                a->fold_replayable, masks ? masks + (size_t)i * 32u : NULL) != 0)
            return -2;
    }
    return 0;
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
        if (staged_to_intent_surface(stage, &staged, e->surface_relation) != 0) return -2;
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
    if (laplace_pos_resolve_entity(upos_tag, LAPLACE_POS_TAGSET_UPOS, &pos_id) != 0) return -1;
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
