#ifndef LAPLACE_CORE_INTENT_STAGE_INTERNAL_H
#define LAPLACE_CORE_INTENT_STAGE_INTERNAL_H

#include "laplace/core/intent_stage.h"

#ifdef _WIN32
#include <stdlib.h>
#include <intrin.h>
#define htobe16(x) _byteswap_ushort(x)
#define htobe32(x) _byteswap_ulong(x)
#define htobe64(x) _byteswap_uint64(x)
#define htole32(x) (x)
#define htole64(x) (x)
#else
#include <endian.h>
#endif

#define INTENT_STAGE_HEADER_BYTES 19u
#define INTENT_STAGE_TRAILER_BYTES 2u

#define ENTITY_COL_COUNT       3
#define PHYSICALITY_COL_COUNT 10
#define ATTESTATION_COL_COUNT 14

typedef struct {
    uint8_t* data;
    size_t   len;
    size_t   cap;
    size_t   row_count;
    size_t*  allocated_bytes;
    size_t*  peak_bytes;
    size_t*  maximum_bytes;
    int*     allocation_failed;
} byte_buf_t;

struct intent_stage {
    byte_buf_t entities;
    byte_buf_t physicalities;
    byte_buf_t attestations;
    hash128_t* witness_slots;
    size_t     witness_cap;
    size_t     witness_count;
    size_t     allocated_bytes;
    size_t     peak_bytes;
    size_t     maximum_bytes;
    int        allocation_failed;
};

int laplace_intent_buf_reserve(byte_buf_t* b, size_t additional);
int laplace_intent_buf_append(byte_buf_t* b, const void* src, size_t n);
int laplace_intent_buf_append_u8(byte_buf_t* b, uint8_t v);
int laplace_intent_buf_append_be32(byte_buf_t* b, int32_t v);

size_t laplace_intent_row_byte_len(const uint8_t* src, size_t len, size_t off);
int laplace_intent_row_field_at(const uint8_t* data, size_t len, size_t off, int field_1based,
                                const uint8_t** out, int32_t* out_len);
const byte_buf_t* laplace_intent_stage_buf(const intent_stage_t* stage, intent_stage_table_t table);

static inline uint32_t laplace_intent_be32_at(const uint8_t* p) {
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16)
         | ((uint32_t)p[2] << 8)  | (uint32_t)p[3];
}

#endif
