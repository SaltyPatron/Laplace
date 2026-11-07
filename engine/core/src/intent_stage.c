#include "intent_stage_internal.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/trajectory.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/relation_law.h"

#include <stdlib.h>
#include <string.h>

#define WKB_Z_FLAG    0x80000000u
#define WKB_M_FLAG    0x40000000u
#define WKB_SRID_FLAG 0x20000000u
#define WKB_POINT_TYPE      1u
#define WKB_LINESTRING_TYPE 2u

static const char* const kEntityColumns =
    "id, tier, type_id";
static const char* const kPhysicalityColumns =
    "id, entity_id, type, coord, hilbert_index, trajectory, "
    "n_constituents, alignment_residual, source_dim, observed_at";
static const char* const kAttestationColumns =
    "id, subject_id, type_id, object_id, source_id, context_id, "
    "outcome, last_observed_at, observation_count, "
    "sum_score_fp1e9, opponent_rd_fp1e9, opponent_rating_fp1e9, fold_replayable, qualifier_mask";

int intent_stage_tuple_payload_bound(size_t entity_count, size_t physicality_count,
    size_t stored_vertices, size_t attestation_count, size_t* out_bytes) {
    const size_t entity_width = 2u + ENTITY_COL_COUNT * 4u + 16u + 2u + 16u;
    const size_t physicality_width = 2u + PHYSICALITY_COL_COUNT * 4u +
        16u + 16u + 2u + (5u + 4u * sizeof(double)) + 16u + 9u +
        4u + sizeof(double) + 4u + 8u;
    const size_t attestation_width = 2u + ATTESTATION_COL_COUNT * 4u +
        6u * 16u + 2u + 5u * 8u + 1u + 32u;
    const size_t counts[4] = {entity_count, physicality_count, stored_vertices, attestation_count};
    const size_t widths[4] = {entity_width, physicality_width, 4u * sizeof(double), attestation_width};
    size_t bytes = 0u;
    if (out_bytes == NULL || (physicality_count == 0u && stored_vertices != 0u)) return -1;
    for (size_t i = 0u; i < 4u; ++i) {
        if (counts[i] > (SIZE_MAX - bytes) / widths[i]) return -2;
        bytes += counts[i] * widths[i];
    }
    *out_bytes = bytes;
    return 0;
}

int laplace_intent_buf_reserve(byte_buf_t* b, size_t additional) {
    if (b->len > SIZE_MAX - additional) return -1;
    const size_t needed = b->len + additional;
    if (needed <= b->cap) return 0;
    size_t new_cap = b->cap > 0 ? b->cap : 256;
    while (new_cap < needed) {
        if (new_cap > SIZE_MAX / 2) return -1;
        new_cap *= 2;
    }
    const int bounded = b->maximum_bytes != NULL && *b->maximum_bytes != SIZE_MAX;
    if (b->allocated_bytes != NULL) {
        const size_t remaining = *b->maximum_bytes - *b->allocated_bytes;
        if ((bounded ? needed : needed - b->cap) > remaining) {
            *b->allocation_failed = 1;
            return -1;
        }
        if ((bounded ? new_cap : new_cap - b->cap) > remaining) new_cap = needed;
    }
    uint8_t* p = (uint8_t*)realloc(b->data, new_cap);
    if (!p) {
        if (b->allocation_failed != NULL) *b->allocation_failed = 1;
        return -1;
    }
    if (bounded) {
        const size_t peak = *b->allocated_bytes + new_cap;
        if (peak > *b->peak_bytes) *b->peak_bytes = peak;
    }
    if (b->allocated_bytes != NULL) {
        *b->allocated_bytes += new_cap - b->cap;
        if (*b->allocated_bytes > *b->peak_bytes) *b->peak_bytes = *b->allocated_bytes;
    }
    b->data = p;
    b->cap = new_cap;
    return 0;
}

int laplace_intent_buf_append(byte_buf_t* b, const void* src, size_t n) {
    if (laplace_intent_buf_reserve(b, n) != 0) return -1;
    memcpy(b->data + b->len, src, n);
    b->len += n;
    return 0;
}

int laplace_intent_buf_append_u8(byte_buf_t* b, uint8_t v) {
    return laplace_intent_buf_append(b, &v, 1);
}

static int buf_append_be16(byte_buf_t* b, int16_t v) {
    const uint16_t be = htobe16((uint16_t)v);
    return laplace_intent_buf_append(b, &be, 2);
}

int laplace_intent_buf_append_be32(byte_buf_t* b, int32_t v) {
    const uint32_t be = htobe32((uint32_t)v);
    return laplace_intent_buf_append(b, &be, 4);
}

static int buf_append_be64(byte_buf_t* b, int64_t v) {
    const uint64_t be = htobe64((uint64_t)v);
    return laplace_intent_buf_append(b, &be, 8);
}

static int buf_append_be_double(byte_buf_t* b, double v) {
    uint64_t bits;
    memcpy(&bits, &v, sizeof(bits));
    bits = htobe64(bits);
    return laplace_intent_buf_append(b, &bits, 8);
}

static int buf_append_le_double(byte_buf_t* b, double v) {
    uint64_t bits;
    memcpy(&bits, &v, sizeof(bits));
    bits = htole64(bits);
    return laplace_intent_buf_append(b, &bits, 8);
}

static int buf_append_le_u32(byte_buf_t* b, uint32_t v) {
    const uint32_t le = htole32(v);
    return laplace_intent_buf_append(b, &le, 4);
}

static int buf_append_field_null(byte_buf_t* b) {
    return laplace_intent_buf_append_be32(b, -1);
}

static int buf_append_field_bytes(byte_buf_t* b, const void* data, uint32_t len) {
    if (laplace_intent_buf_append_be32(b, (int32_t)len) != 0) return -1;
    if (len == 0) return 0;
    return laplace_intent_buf_append(b, data, len);
}

static int buf_append_field_hash128(byte_buf_t* b, const hash128_t* h) {
    return buf_append_field_bytes(b, h, 16);
}

static int buf_append_field_int2(byte_buf_t* b, int16_t v) {
    if (laplace_intent_buf_append_be32(b, 2) != 0) return -1;
    return buf_append_be16(b, v);
}

static int buf_append_field_int4(byte_buf_t* b, int32_t v) {
    if (laplace_intent_buf_append_be32(b, 4) != 0) return -1;
    return laplace_intent_buf_append_be32(b, v);
}

static int buf_append_field_int8(byte_buf_t* b, int64_t v) {
    if (laplace_intent_buf_append_be32(b, 8) != 0) return -1;
    return buf_append_be64(b, v);
}

static int buf_append_field_bool(byte_buf_t* b, uint8_t v) {
    if (laplace_intent_buf_append_be32(b, 1) != 0) return -1;
    return laplace_intent_buf_append_u8(b, v ? 1 : 0);
}

static int buf_append_field_float8(byte_buf_t* b, double v) {
    if (laplace_intent_buf_append_be32(b, 8) != 0) return -1;
    return buf_append_be_double(b, v);
}

static int buf_append_field_timestamptz(byte_buf_t* b, int64_t unix_us) {
    if (unix_us < INT64_MIN + INTENT_STAGE_PG_EPOCH_UNIX_US) return -1;
    const int64_t pg_us = unix_us - INTENT_STAGE_PG_EPOCH_UNIX_US;
    return buf_append_field_int8(b, pg_us);
}

static int buf_append_field_pointzm(byte_buf_t* b, const double coord[4]) {
    if (laplace_intent_buf_append_be32(b, 37) != 0) return -1;
    if (laplace_intent_buf_append_u8(b, 0x01) != 0) return -1;
    if (buf_append_le_u32(b, WKB_POINT_TYPE | WKB_Z_FLAG | WKB_M_FLAG) != 0) return -1;
    if (buf_append_le_double(b, coord[0]) != 0) return -1;
    if (buf_append_le_double(b, coord[1]) != 0) return -1;
    if (buf_append_le_double(b, coord[2]) != 0) return -1;
    if (buf_append_le_double(b, coord[3]) != 0) return -1;
    return 0;
}

static int buf_append_field_linestringzm(
    byte_buf_t*    b,
    const double*  xyzm,
    uint32_t       n_vertices) {
    const uint32_t field_len = 1u + 4u + 4u + 32u * n_vertices;
    if (laplace_intent_buf_append_be32(b, (int32_t)field_len) != 0) return -1;
    if (laplace_intent_buf_append_u8(b, 0x01) != 0) return -1;
    if (buf_append_le_u32(b, WKB_LINESTRING_TYPE | WKB_Z_FLAG | WKB_M_FLAG) != 0) return -1;
    if (buf_append_le_u32(b, n_vertices) != 0) return -1;
    for (uint32_t i = 0; i < n_vertices; ++i) {
        if (buf_append_le_double(b, xyzm[i * 4 + 0]) != 0) return -1;
        if (buf_append_le_double(b, xyzm[i * 4 + 1]) != 0) return -1;
        if (buf_append_le_double(b, xyzm[i * 4 + 2]) != 0) return -1;
        if (buf_append_le_double(b, xyzm[i * 4 + 3]) != 0) return -1;
    }
    return 0;
}

static int witness_slot_empty(const hash128_t* h) { return (h->hi | h->lo) == 0; }
static int witness_contains_unlocked(const intent_stage_t* stage, const hash128_t* id);

static int witness_record_unlocked(intent_stage_t* stage, const hash128_t* id) {
    if (!stage || !id) return 0;
    hash128_t* slots = stage->witness_slots;
    size_t cap = stage->witness_cap;
    if (cap == 0) {
        cap = (size_t)1 << 16;
        if (cap > (stage->maximum_bytes - stage->allocated_bytes) / sizeof(hash128_t)) {
            stage->allocation_failed = 1;
            return 0;
        }
        slots = (hash128_t*)calloc(cap, sizeof(hash128_t));
        if (!slots) { stage->allocation_failed = 1; return 0; }
        stage->allocated_bytes += cap * sizeof(hash128_t);
        if (stage->allocated_bytes > stage->peak_bytes) stage->peak_bytes = stage->allocated_bytes;
        stage->witness_slots = slots;
        stage->witness_cap = cap;
    } else if ((stage->witness_count + 1) * 4 >= cap * 3) {
        if (witness_contains_unlocked(stage, id)) return 1;
        if (cap > SIZE_MAX / 2u) { stage->allocation_failed = 1; return 0; }
        size_t ncap = cap << 1;
        if (ncap > (stage->maximum_bytes - stage->allocated_bytes) / sizeof(hash128_t)) {
            stage->allocation_failed = 1;
            return 0;
        }
        hash128_t* ns = (hash128_t*)calloc(ncap, sizeof(hash128_t));
        if (ns) {
            const size_t peak = stage->allocated_bytes + ncap * sizeof(hash128_t);
            if (peak > stage->peak_bytes) stage->peak_bytes = peak;
            size_t nmask = ncap - 1;
            for (size_t i = 0; i < cap; ++i) {
                if (witness_slot_empty(&slots[i])) continue;
                size_t j = (size_t)slots[i].lo & nmask;
                while (!witness_slot_empty(&ns[j])) j = (j + 1) & nmask;
                ns[j] = slots[i];
            }
            free(slots);
            stage->allocated_bytes += (ncap - cap) * sizeof(hash128_t);
            slots = ns;
            stage->witness_slots = slots;
            stage->witness_cap = ncap;
            cap = ncap;
        } else { stage->allocation_failed = 1; return 0; }
    }
    size_t mask = cap - 1;
    size_t j = (size_t)id->lo & mask;
    while (!witness_slot_empty(&slots[j])) {
        if (hash128_equals(&slots[j], id)) return 1;
        j = (j + 1) & mask;
    }
    slots[j] = *id;
    stage->witness_count++;
    return 0;
}

static int witness_contains_unlocked(const intent_stage_t* stage, const hash128_t* id) {
    if (!stage || !id || stage->witness_cap == 0) return 0;
    const hash128_t* slots = stage->witness_slots;
    size_t mask = stage->witness_cap - 1;
    size_t j = (size_t)id->lo & mask;
    while (!witness_slot_empty(&slots[j])) {
        if (hash128_equals(&slots[j], id)) return 1;
        j = (j + 1) & mask;
    }
    return 0;
}

int intent_stage_witness_seen(const intent_stage_t* stage, const hash128_t* id) {
    return witness_contains_unlocked(stage, id);
}

int intent_stage_witness_record(intent_stage_t* stage, const hash128_t* id) {
    return witness_record_unlocked(stage, id);
}

int intent_stage_lower_entity_tier(intent_stage_t* stage, const hash128_t* id, int16_t tier) {
    if (!stage || !id || tier < 0 || tier > 255) return -1;
    byte_buf_t* entities = &stage->entities;
    size_t offset = 0;
    while (offset < entities->len) {
        size_t row_len = laplace_intent_row_byte_len(entities->data, entities->len, offset);
        if (row_len == 0) return -1;
        const uint8_t* staged_id = NULL;
        const uint8_t* staged_tier = NULL;
        int32_t id_len = 0, tier_len = 0;
        if (laplace_intent_row_field_at(entities->data, entities->len, offset, 1, &staged_id, &id_len) != 0
            || laplace_intent_row_field_at(entities->data, entities->len, offset, 2, &staged_tier, &tier_len) != 0)
            return -1;
        if (id_len == 16 && tier_len == 2 && memcmp(staged_id, id, 16) == 0) {
            const int16_t current = (int16_t)(((uint16_t)staged_tier[0] << 8) | staged_tier[1]);
            if (tier < current) {
                const uint16_t encoded = htobe16((uint16_t)tier);
                memcpy((uint8_t*)staged_tier, &encoded, sizeof(encoded));
            }
            return 1;
        }
        offset += row_len;
    }
    return 0;
}

intent_stage_t* intent_stage_new(size_t row_capacity_hint) {
    return intent_stage_new_bounded(row_capacity_hint, SIZE_MAX);
}

intent_stage_t* intent_stage_new_bounded(size_t row_capacity_hint, size_t maximum_bytes) {
    if (maximum_bytes < sizeof(intent_stage_t) || row_capacity_hint > SIZE_MAX / 256u) return NULL;
    intent_stage_t* s = (intent_stage_t*)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->allocated_bytes = sizeof(*s);
    s->peak_bytes = sizeof(*s);
    s->maximum_bytes = maximum_bytes;
    byte_buf_t* buffers[3] = {&s->entities, &s->physicalities, &s->attestations};
    for (size_t i = 0; i < 3u; ++i) {
        buffers[i]->allocated_bytes = &s->allocated_bytes;
        buffers[i]->peak_bytes = &s->peak_bytes;
        buffers[i]->maximum_bytes = &s->maximum_bytes;
        buffers[i]->allocation_failed = &s->allocation_failed;
    }
    if (row_capacity_hint > 0) {
        if (laplace_intent_buf_reserve(&s->entities,      row_capacity_hint * 80)  != 0
            || laplace_intent_buf_reserve(&s->physicalities, row_capacity_hint * 256) != 0
            || laplace_intent_buf_reserve(&s->attestations,  row_capacity_hint * 192) != 0) {
            intent_stage_free(s);
            return NULL;
        }
    }
    return s;
}

size_t intent_stage_retain_physicalities(intent_stage_t* stage) {
    if (stage == NULL) return 0u;
    size_t released = stage->witness_cap * sizeof(hash128_t);
    byte_buf_t* discarded[2] = {&stage->entities, &stage->attestations};
    for (size_t i = 0u; i < 2u; ++i) {
        byte_buf_t* buffer = discarded[i];
        released += buffer->cap;
        free(buffer->data);
        buffer->data = NULL;
        buffer->len = 0u;
        buffer->cap = 0u;
        buffer->row_count = 0u;
    }
    free(stage->witness_slots);
    stage->witness_slots = NULL;
    stage->witness_cap = 0u;
    stage->witness_count = 0u;
    stage->allocated_bytes -= released;
    return released;
}

size_t intent_stage_memory_bytes(const intent_stage_t* stage) {
    return stage == NULL ? 0u : stage->allocated_bytes;
}

size_t intent_stage_memory_peak_bytes(const intent_stage_t* stage) {
    return stage == NULL ? 0u : stage->peak_bytes;
}

int intent_stage_allocation_failed(const intent_stage_t* stage) {
    return stage == NULL || stage->allocation_failed;
}

void intent_stage_free(intent_stage_t* stage) {
    if (!stage) return;
    free(stage->entities.data);
    free(stage->physicalities.data);
    free(stage->attestations.data);
    free(stage->witness_slots);
    free(stage);
}

size_t intent_stage_entity_count(const intent_stage_t* stage)       { return stage ? stage->entities.row_count       : 0; }
size_t intent_stage_physicality_count(const intent_stage_t* stage)  { return stage ? stage->physicalities.row_count  : 0; }
size_t intent_stage_attestation_count(const intent_stage_t* stage)  { return stage ? stage->attestations.row_count   : 0; }

const char* intent_stage_copy_column_list(intent_stage_table_t table) {
    switch (table) {
        case INTENT_STAGE_TABLE_ENTITIES:      return kEntityColumns;
        case INTENT_STAGE_TABLE_PHYSICALITIES: return kPhysicalityColumns;
        case INTENT_STAGE_TABLE_ATTESTATIONS:  return kAttestationColumns;
        default:                               return NULL;
    }
}

const byte_buf_t* laplace_intent_stage_buf(const intent_stage_t* stage, intent_stage_table_t table) {
    if (!stage) return NULL;
    switch (table) {
        case INTENT_STAGE_TABLE_ENTITIES:      return &stage->entities;
        case INTENT_STAGE_TABLE_PHYSICALITIES: return &stage->physicalities;
        case INTENT_STAGE_TABLE_ATTESTATIONS:  return &stage->attestations;
        default:                               return NULL;
    }
}

static int append_entity_row(
    byte_buf_t* b,
    const hash128_t* id,
    int16_t          tier,
    const hash128_t* type_id) {
    if (!b || !id || !type_id) return -1;
    if (tier < 0 || tier > 255) return -1;

    const size_t mark = b->len;
    if (buf_append_be16(b, ENTITY_COL_COUNT) != 0 ||
        buf_append_field_hash128(b, id) != 0 ||
        buf_append_field_int2(b, tier) != 0 ||
        buf_append_field_hash128(b, type_id) != 0) {
        b->len = mark;
        return -1;
    }
    b->row_count++;
    return 0;
}

int intent_stage_add_entity(
    intent_stage_t* stage, const hash128_t* id, int16_t tier, const hash128_t* type_id) {
    if (!stage || !id || !type_id || tier < 0 || tier > 255) return -1;
    return append_entity_row(&stage->entities, id, tier, type_id);
}

int intent_stage_add_physicality(
    intent_stage_t*     stage,
    const hash128_t*    id,
    const hash128_t*    entity_id,
    int16_t             type,
    const double        coord[4],
    const hilbert128_t* hilbert_index,
    const double*       trajectory_xyzm,
    uint32_t            trajectory_n_vertices,
    int32_t             n_constituents,
    int                 alignment_residual_is_null,
    double              alignment_residual,
    int                 source_dim_is_null,
    int32_t             source_dim,
    int64_t             observed_at_unix_us) {
    if (!stage || !id || !entity_id || !coord || !hilbert_index) return -1;
    if (n_constituents < 0) return -1;
    if (trajectory_n_vertices > 0 && !trajectory_xyzm) return -1;
    if (trajectory_n_vertices == 0 && n_constituents != 0) return -2;

    hash128_t expected_physicality_id;
    laplace_physicality_id_compute(*entity_id, type, &expected_physicality_id);
    if (!hash128_equals(id, &expected_physicality_id)) return -2;

    const int manifest_status = laplace_physicality_manifest_validate(
        entity_id, type, trajectory_xyzm, trajectory_n_vertices, n_constituents);
    if (manifest_status != 0) return manifest_status;

    byte_buf_t* b = &stage->physicalities;
    if (buf_append_be16(b, PHYSICALITY_COL_COUNT) != 0) return -1;
    if (buf_append_field_hash128(b, id) != 0) return -1;
    if (buf_append_field_hash128(b, entity_id) != 0) return -1;
    if (buf_append_field_int2(b, type) != 0) return -1;
    if (buf_append_field_pointzm(b, coord) != 0) return -1;
    if (buf_append_field_bytes(b, hilbert_index->bytes, 16) != 0) return -1;
    if (trajectory_xyzm == NULL || trajectory_n_vertices == 0) {
        if (buf_append_field_null(b) != 0) return -1;
    } else if (trajectory_n_vertices == 1) {
        if (buf_append_field_pointzm(b, trajectory_xyzm) != 0) return -1;
    } else {
        if (buf_append_field_linestringzm(b, trajectory_xyzm, trajectory_n_vertices) != 0) return -1;
    }
    if (buf_append_field_int4(b, n_constituents) != 0) return -1;
    if (alignment_residual_is_null) {
        if (buf_append_field_null(b) != 0) return -1;
    } else {
        if (buf_append_field_float8(b, alignment_residual) != 0) return -1;
    }
    if (source_dim_is_null) {
        if (buf_append_field_null(b) != 0) return -1;
    } else {
        if (buf_append_field_int4(b, source_dim) != 0) return -1;
    }
    if (buf_append_field_timestamptz(b, observed_at_unix_us) != 0) return -1;
    b->row_count++;
    return 0;
}

int intent_stage_deposit_lexical_upos(
    intent_stage_t*     stage,
    const hash128_t*    entity_id,
    const double        coord[4],
    const hilbert128_t* hilbert_index,
    int32_t             slot) {
    if (!stage || !entity_id || !coord || !hilbert_index || slot < 0 || slot > 30) return -1;
    hash128_t physicality;
    laplace_physicality_id_compute(*entity_id, INTENT_STAGE_LEXICAL_UPOS, &physicality);
    if (intent_stage_witness_seen(stage, &physicality)) return 0;
    const uint64_t flags =
        ((uint64_t)(slot + 1) & LAPLACE_VFLAG_PARSE_UPOS_MASK) << LAPLACE_VFLAG_PARSE_UPOS_SHIFT;
    double trajectory[4];
    if (trajectory_build_flagged(entity_id, &flags, 1, trajectory) != 0) return -1;
    if (intent_stage_add_physicality(
            stage, &physicality, entity_id, INTENT_STAGE_LEXICAL_UPOS,
            coord, hilbert_index, trajectory, 1, 1,
            1, 0.0, 1, 0, 0) != 0) return -1;
    int recorded = intent_stage_witness_record(stage, &physicality);
    return recorded < 0 ? recorded : 0;
}

int intent_stage_add_attestation_mode(
    intent_stage_t*  stage,
    const hash128_t* id,
    const hash128_t* subject_id,
    const hash128_t* type_id,
    const hash128_t* object_id,
    const hash128_t* source_id,
    const hash128_t* context_id,
    int16_t          outcome,
    int64_t          last_observed_at_unix_us,
    int64_t          observation_count,
    int64_t          sum_score_fp1e9,
    int64_t          opponent_rd_fp1e9,
    int64_t          opponent_rating_fp1e9,
    uint8_t          fold_replayable,
    const uint8_t*   qualifier_mask) {
    if (!stage || !id || !subject_id || !type_id || !source_id) return -1;
    if (observation_count < 0) return -1;
    if (outcome < 0 || outcome > 2) return -1;
    if (laplace_relation_retired(type_id, NULL)) return LAPLACE_REL_RETIRED;
    byte_buf_t* b = &stage->attestations;

    if (buf_append_be16(b, ATTESTATION_COL_COUNT) != 0) return -1;
    if (buf_append_field_hash128(b, id) != 0) return -1;
    if (buf_append_field_hash128(b, subject_id) != 0) return -1;
    if (buf_append_field_hash128(b, type_id) != 0) return -1;
    if (object_id) {
        if (buf_append_field_hash128(b, object_id) != 0) return -1;
    } else {
        if (buf_append_field_null(b) != 0) return -1;
    }
    if (buf_append_field_hash128(b, source_id) != 0) return -1;
    if (context_id) {
        if (buf_append_field_hash128(b, context_id) != 0) return -1;
    } else {
        if (buf_append_field_null(b) != 0) return -1;
    }
    if (buf_append_field_int2(b, outcome) != 0) return -1;
    if (buf_append_field_timestamptz(b, last_observed_at_unix_us) != 0) return -1;
    if (buf_append_field_int8(b, observation_count) != 0) return -1;
    if (buf_append_field_int8(b, sum_score_fp1e9) != 0) return -1;
    if (buf_append_field_int8(b, opponent_rd_fp1e9) != 0) return -1;
    if (buf_append_field_int8(b, opponent_rating_fp1e9) != 0) return -1;
    if (buf_append_field_bool(b, fold_replayable) != 0) return -1;
    if (qualifier_mask) {
        if (buf_append_field_bytes(b, qualifier_mask, 32) != 0) return -1;
    } else {
        if (buf_append_field_null(b) != 0) return -1;
    }
    b->row_count++;
    return 0;
}

int intent_stage_add_attestation(
    intent_stage_t* stage, const hash128_t* id, const hash128_t* subject_id,
    const hash128_t* type_id, const hash128_t* object_id, const hash128_t* source_id,
    const hash128_t* context_id, int16_t outcome, int64_t last_observed_at_unix_us,
    int64_t observation_count, int64_t sum_score_fp1e9, int64_t opponent_rd_fp1e9,
    int64_t opponent_rating_fp1e9, const uint8_t* qualifier_mask) {
    return intent_stage_add_attestation_mode(
        stage, id, subject_id, type_id, object_id, source_id, context_id, outcome,
        last_observed_at_unix_us, observation_count, sum_score_fp1e9,
        opponent_rd_fp1e9, opponent_rating_fp1e9, 1, qualifier_mask);
}
