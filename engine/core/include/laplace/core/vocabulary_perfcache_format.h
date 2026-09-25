#pragma once

/*
 * Governed vocabulary perfcache v1: derived ROM for the linguistic vocabularies in
 * engine/manifest/vocabulary/ (UPOS, universal relations, relation subtypes, features,
 * feature values). See docs/specs/33_Perfcache_Blob_Law.md.
 *
 * A vocabulary value is content. Its record carries the id, placement and Hilbert key
 * the ordinary content path gives the same text ("NOUN", "nsubj", "pass", "Number"),
 * and a feature value is the ordered composition [feature, value] exactly as a recipe
 * composes "Number=Plur". The governed code is the value's append-only position in its
 * vocabulary: the bit it owns in an entity's OR-mask and the code a parse vertex packs.
 *
 * Records are grouped by family and ordered by code, so (family, code) is a direct
 * index; an open-addressed index over (family, id) answers the reverse. One entity may
 * hold a code in several vocabularies: "advcl" is a relation and a subtype. Never seed
 * database rows from this file; it is rebuildable derived state.
 */

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"

#ifdef __cplusplus
extern "C" {
#endif

/* 'LVCP' little-endian: Laplace Vocabulary Perfcache */
#define LAPLACE_VOCABULARY_PERFCACHE_MAGIC 0x5043564Cu
#define LAPLACE_VOCABULARY_PERFCACHE_VERSION 1u
#define LAPLACE_VOCABULARY_PERFCACHE_HEADER_SIZE 128u
#define LAPLACE_VOCABULARY_PERFCACHE_RECORD_SIZE 80u
#define LAPLACE_VOCABULARY_PERFCACHE_TRAILER_BYTES 16u
#define LAPLACE_VOCABULARY_PERFCACHE_GENERATOR_TAG "vocabulary_perfcache/v1"
#define LAPLACE_VOCABULARY_INDEX_EMPTY 0xFFFFFFFFu

typedef enum {
    LAPLACE_VOCABULARY_UPOS           = 0,
    LAPLACE_VOCABULARY_DEPREL         = 1,
    LAPLACE_VOCABULARY_DEPREL_SUBTYPE = 2,
    LAPLACE_VOCABULARY_FEATURE        = 3,
    LAPLACE_VOCABULARY_FEATURE_VALUE  = 4,
    LAPLACE_VOCABULARY_FAMILY_COUNT   = 5
} laplace_vocabulary_family_t;

typedef struct {
    hash128_t    id;           /* 16: content id (a feature value: [feature, value]) */
    double       coord[4];     /* 32 */
    hilbert128_t hilbert;      /* 16 */
    uint32_t     label_off;    /*  4: offset of the NUL-terminated label in strings */
    uint16_t     label_len;    /*  2 */
    uint16_t     code;         /*  2: append-only code, 1-based */
    uint16_t     parent_code;  /*  2: a feature value's feature code; 0 otherwise */
    uint8_t      family;       /*  1: laplace_vocabulary_family_t */
    uint8_t      tier;         /*  1 */
    uint8_t      _pad[4];      /*  4 */
} laplace_vocabulary_perfcache_record_t;

typedef struct {
    uint32_t  magic;
    uint32_t  format_version;
    uint64_t  record_count;
    uint64_t  record_size;
    uint64_t  records_offset;
    uint64_t  index_offset;    /* uint32 record indices, LAPLACE_VOCABULARY_INDEX_EMPTY = free */
    uint64_t  index_slots;     /* power of two */
    uint64_t  strings_offset;
    uint64_t  strings_length;
    hash128_t source_hash;     /* T0 generation + generator tag + manifest bytes */
    uint32_t  family_start[LAPLACE_VOCABULARY_FAMILY_COUNT];
    uint32_t  family_count[LAPLACE_VOCABULARY_FAMILY_COUNT];
    uint8_t   reserved[8];
} laplace_vocabulary_perfcache_header_t;

#ifdef __cplusplus
static_assert(sizeof(laplace_vocabulary_perfcache_record_t) == 80,
              "vocabulary perfcache record must be 80 bytes");
static_assert(sizeof(laplace_vocabulary_perfcache_header_t) == 128,
              "vocabulary perfcache header must be 128 bytes");
#else
_Static_assert(sizeof(laplace_vocabulary_perfcache_record_t) == 80,
               "vocabulary perfcache record must be 80 bytes");
_Static_assert(sizeof(laplace_vocabulary_perfcache_header_t) == 128,
               "vocabulary perfcache header must be 128 bytes");
#endif

#ifdef __cplusplus
}
#endif
