#include "laplace/core/intent_stage.h"

#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <stdlib.h>
#define htobe16(x) _byteswap_ushort(x)
#define htobe32(x) _byteswap_ulong(x)
#define htobe64(x) _byteswap_uint64(x)
#define htole32(x) (x)
#define htole64(x) (x)
#else
#include <endian.h>
#endif

static const uint8_t kCopyBinarySignature[11] = {
    'P', 'G', 'C', 'O', 'P', 'Y', '\n', 0xff, '\r', '\n', '\0'
};

#define INTENT_STAGE_HEADER_BYTES 19u
#define INTENT_STAGE_TRAILER_BYTES 2u

#define WKB_Z_FLAG    0x80000000u
#define WKB_M_FLAG    0x40000000u
#define WKB_SRID_FLAG 0x20000000u
#define WKB_POINT_TYPE      1u
#define WKB_LINESTRING_TYPE 2u

static const char* const kEntityColumns =
    "id, tier, type_id, first_observed_by";
static const char* const kPhysicalityColumns =
    "id, entity_id, source_id, type, coord, hilbert_index, trajectory, "
    "n_constituents, alignment_residual, source_dim, observed_at";
static const char* const kAttestationColumns =
    "id, subject_id, type_id, object_id, source_id, context_id, "
    "outcome, last_observed_at, observation_count";

#define ENTITY_COL_COUNT       4
#define PHYSICALITY_COL_COUNT 11
#define ATTESTATION_COL_COUNT  9

typedef struct {
    uint8_t* data;
    size_t   len;
    size_t   cap;
    size_t   row_count;
} byte_buf_t;

static int buf_reserve(byte_buf_t* b, size_t additional) {
    if (b->len > SIZE_MAX - additional) return -1;
    const size_t needed = b->len + additional;
    if (needed <= b->cap) return 0;
    size_t new_cap = b->cap > 0 ? b->cap : 256;
    while (new_cap < needed) {
        if (new_cap > SIZE_MAX / 2) return -1;
        new_cap *= 2;
    }
    uint8_t* p = (uint8_t*)realloc(b->data, new_cap);
    if (!p) return -1;
    b->data = p;
    b->cap = new_cap;
    return 0;
}

static int buf_append(byte_buf_t* b, const void* src, size_t n) {
    if (buf_reserve(b, n) != 0) return -1;
    memcpy(b->data + b->len, src, n);
    b->len += n;
    return 0;
}

static int buf_append_u8(byte_buf_t* b, uint8_t v) {
    return buf_append(b, &v, 1);
}

static int buf_append_be16(byte_buf_t* b, int16_t v) {
    const uint16_t be = htobe16((uint16_t)v);
    return buf_append(b, &be, 2);
}

static int buf_append_be32(byte_buf_t* b, int32_t v) {
    const uint32_t be = htobe32((uint32_t)v);
    return buf_append(b, &be, 4);
}

static int buf_append_be64(byte_buf_t* b, int64_t v) {
    const uint64_t be = htobe64((uint64_t)v);
    return buf_append(b, &be, 8);
}

static int buf_append_be_double(byte_buf_t* b, double v) {
    uint64_t bits;
    memcpy(&bits, &v, sizeof(bits));
    bits = htobe64(bits);
    return buf_append(b, &bits, 8);
}

static int buf_append_le_double(byte_buf_t* b, double v) {
    uint64_t bits;
    memcpy(&bits, &v, sizeof(bits));
    bits = htole64(bits);
    return buf_append(b, &bits, 8);
}

static int buf_append_le_u32(byte_buf_t* b, uint32_t v) {
    const uint32_t le = htole32(v);
    return buf_append(b, &le, 4);
}

static int buf_append_field_null(byte_buf_t* b) {
    return buf_append_be32(b, -1);
}

static int buf_append_field_bytes(byte_buf_t* b, const void* data, uint32_t len) {
    if (buf_append_be32(b, (int32_t)len) != 0) return -1;
    if (len == 0) return 0;
    return buf_append(b, data, len);
}

static int buf_append_field_hash128(byte_buf_t* b, const hash128_t* h) {
    return buf_append_field_bytes(b, h, 16);
}

static int buf_append_field_int2(byte_buf_t* b, int16_t v) {
    if (buf_append_be32(b, 2) != 0) return -1;
    return buf_append_be16(b, v);
}

static int buf_append_field_int4(byte_buf_t* b, int32_t v) {
    if (buf_append_be32(b, 4) != 0) return -1;
    return buf_append_be32(b, v);
}

static int buf_append_field_int8(byte_buf_t* b, int64_t v) {
    if (buf_append_be32(b, 8) != 0) return -1;
    return buf_append_be64(b, v);
}

static int buf_append_field_float8(byte_buf_t* b, double v) {
    if (buf_append_be32(b, 8) != 0) return -1;
    return buf_append_be_double(b, v);
}

static int buf_append_field_timestamptz(byte_buf_t* b, int64_t unix_us) {
    const int64_t pg_us = unix_us - INTENT_STAGE_PG_EPOCH_UNIX_US;
    return buf_append_field_int8(b, pg_us);
}

static int buf_append_field_pointzm(byte_buf_t* b, const double coord[4]) {
    if (buf_append_be32(b, 37) != 0) return -1;
    if (buf_append_u8(b, 0x01) != 0) return -1;
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
    if (buf_append_be32(b, (int32_t)field_len) != 0) return -1;
    if (buf_append_u8(b, 0x01) != 0) return -1;
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

struct intent_stage {
    byte_buf_t entities;
    byte_buf_t physicalities;
    byte_buf_t attestations;
};

intent_stage_t* intent_stage_new(size_t row_capacity_hint) {
    intent_stage_t* s = (intent_stage_t*)calloc(1, sizeof(*s));
    if (!s) return NULL;
    if (row_capacity_hint > 0) {
        /* Reserve each buffer by its OWN maximum row width so the fixed-width
         * tables (entities, attestations) never realloc mid-staging. A single
         * shared hint*128 reserve undersized the attestation buffer: an
         * attestation row is up to 152 bytes (2 field-count + 6 hash128 fields
         * at 20B + outcome int2 6B + two int8 fields at 12B), so a 128 B/row
         * reserve forced a realloc partway through the attestation loop. The
         * margins below bound every fixed-width row:
         *   entity      max = 68  bytes  -> 80
         *   attestation max = 152 bytes  -> 168
         * Physicalities carry a variable-length trajectory (LINESTRING ZM), so
         * they remain growth-capable; 256 B/row covers the common point/short
         * cases and grows as needed for long trajectories. */
        if (buf_reserve(&s->entities,      row_capacity_hint * 80)  != 0
            || buf_reserve(&s->physicalities, row_capacity_hint * 256) != 0
            || buf_reserve(&s->attestations,  row_capacity_hint * 168) != 0) {
            intent_stage_free(s);
            return NULL;
        }
    }
    return s;
}

void intent_stage_free(intent_stage_t* stage) {
    if (!stage) return;
    free(stage->entities.data);
    free(stage->physicalities.data);
    free(stage->attestations.data);
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

int intent_stage_add_entity(
    intent_stage_t*  stage,
    const hash128_t* id,
    int16_t          tier,
    const hash128_t* type_id,
    const hash128_t* first_observed_by) {
    if (!stage || !id || !type_id) return -1;
    if (tier < 0 || tier > 255) return -1;
    byte_buf_t* b = &stage->entities;

    if (buf_append_be16(b, ENTITY_COL_COUNT) != 0) return -1;
    if (buf_append_field_hash128(b, id) != 0) return -1;
    if (buf_append_field_int2(b, tier) != 0) return -1;
    if (buf_append_field_hash128(b, type_id) != 0) return -1;
    if (first_observed_by) {
        if (buf_append_field_hash128(b, first_observed_by) != 0) return -1;
    } else {
        if (buf_append_field_null(b) != 0) return -1;
    }
    b->row_count++;
    return 0;
}

int intent_stage_add_physicality(
    intent_stage_t*     stage,
    const hash128_t*    id,
    const hash128_t*    entity_id,
    const hash128_t*    source_id,
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
    if (!stage || !id || !entity_id || !source_id || !coord || !hilbert_index) return -1;
    if (n_constituents < 0) return -1;
    if (trajectory_n_vertices > 0 && !trajectory_xyzm) return -1;
    byte_buf_t* b = &stage->physicalities;

    if (buf_append_be16(b, PHYSICALITY_COL_COUNT) != 0) return -1;
    if (buf_append_field_hash128(b, id) != 0) return -1;
    if (buf_append_field_hash128(b, entity_id) != 0) return -1;
    if (buf_append_field_hash128(b, source_id) != 0) return -1;
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

int intent_stage_add_attestation(
    intent_stage_t*  stage,
    const hash128_t* id,
    const hash128_t* subject_id,
    const hash128_t* type_id,
    const hash128_t* object_id,
    const hash128_t* source_id,
    const hash128_t* context_id,
    int16_t          outcome,
    int64_t          last_observed_at_unix_us,
    int64_t          observation_count) {
    if (!stage || !id || !subject_id || !type_id || !source_id) return -1;
    if (observation_count < 0) return -1;
    if (outcome < 0 || outcome > 2) return -1;
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
    b->row_count++;
    return 0;
}

static const byte_buf_t* stage_buf(const intent_stage_t* stage, intent_stage_table_t table) {
    if (!stage) return NULL;
    switch (table) {
        case INTENT_STAGE_TABLE_ENTITIES:      return &stage->entities;
        case INTENT_STAGE_TABLE_PHYSICALITIES: return &stage->physicalities;
        case INTENT_STAGE_TABLE_ATTESTATIONS:  return &stage->attestations;
        default:                               return NULL;
    }
}

size_t intent_stage_emit_copy_binary(
    const intent_stage_t* stage,
    intent_stage_table_t  table,
    uint8_t*              buf,
    size_t                buf_capacity) {
    const byte_buf_t* src = stage_buf(stage, table);
    if (!src) return 0;
    const size_t required = (size_t)INTENT_STAGE_HEADER_BYTES + src->len + (size_t)INTENT_STAGE_TRAILER_BYTES;
    if (!buf || buf_capacity < required) return required;

    size_t off = 0;
    memcpy(buf + off, kCopyBinarySignature, sizeof(kCopyBinarySignature));
    off += sizeof(kCopyBinarySignature);
    memset(buf + off, 0, 4); off += 4;
    memset(buf + off, 0, 4); off += 4;
    if (src->len > 0) {
        memcpy(buf + off, src->data, src->len);
        off += src->len;
    }
    buf[off++] = 0xff;
    buf[off++] = 0xff;
    return required;
}

const uint8_t* intent_stage_tuple_ptr(
    const intent_stage_t* stage,
    intent_stage_table_t  table,
    size_t*               out_len) {
    const byte_buf_t* src = stage_buf(stage, table);
    if (!src) { if (out_len) *out_len = 0; return NULL; }
    if (out_len) *out_len = src->len;
    return src->data;
}
