#pragma once

#include <stdint.h>
#include <stddef.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"

#ifdef __cplusplus
extern "C" {
#endif

#define LAPLACE_PERFCACHE_MAGIC 0x4652504Cu
#define LAPLACE_PERFCACHE_VERSION 2u
#define LAPLACE_PERFCACHE_RECORD_COUNT 1114112u

typedef struct {
    uint32_t     codepoint;
    uint32_t     uca_order;
    double       coord[4];
    hilbert128_t hilbert;
    hash128_t    hash;
    uint32_t     flags;
    uint32_t     _pad;
} laplace_perfcache_record_t;

#define LAPLACE_PC_GB_SHIFT   0u
#define LAPLACE_PC_GB_MASK    0x0000000Fu
#define LAPLACE_PC_WB_SHIFT   4u
#define LAPLACE_PC_WB_MASK    0x000001F0u
#define LAPLACE_PC_SB_SHIFT   9u
#define LAPLACE_PC_SB_MASK    0x00001E00u
#define LAPLACE_PC_INCB_SHIFT 13u
#define LAPLACE_PC_INCB_MASK  0x00006000u
#define LAPLACE_PC_CCC_SHIFT  15u
#define LAPLACE_PC_CCC_MASK   0x007F8000u

static inline uint32_t laplace_pc_pack_flags(uint8_t gb, uint8_t wb, uint8_t sb,
                                             uint8_t incb, uint8_t ccc) {
    return ((uint32_t)gb   << LAPLACE_PC_GB_SHIFT)
         | ((uint32_t)wb   << LAPLACE_PC_WB_SHIFT)
         | ((uint32_t)sb   << LAPLACE_PC_SB_SHIFT)
         | ((uint32_t)incb << LAPLACE_PC_INCB_SHIFT)
         | ((uint32_t)ccc  << LAPLACE_PC_CCC_SHIFT);
}
static inline uint8_t laplace_pc_gb(uint32_t f)   { return (uint8_t)((f & LAPLACE_PC_GB_MASK)   >> LAPLACE_PC_GB_SHIFT); }
static inline uint8_t laplace_pc_wb(uint32_t f)   { return (uint8_t)((f & LAPLACE_PC_WB_MASK)   >> LAPLACE_PC_WB_SHIFT); }
static inline uint8_t laplace_pc_sb(uint32_t f)   { return (uint8_t)((f & LAPLACE_PC_SB_MASK)   >> LAPLACE_PC_SB_SHIFT); }
static inline uint8_t laplace_pc_incb(uint32_t f) { return (uint8_t)((f & LAPLACE_PC_INCB_MASK) >> LAPLACE_PC_INCB_SHIFT); }
static inline uint8_t laplace_pc_ccc(uint32_t f)  { return (uint8_t)((f & LAPLACE_PC_CCC_MASK)  >> LAPLACE_PC_CCC_SHIFT); }

typedef struct {
    uint32_t cp;
    uint32_t start_idx;
    uint32_t length;
} laplace_perfcache_decomp_t;

typedef struct {
    uint32_t first;
    uint32_t second;
    uint32_t composed;
} laplace_perfcache_compose_t;

typedef struct {
    uint32_t magic;
    uint32_t format_version;
    char     ucd_version[8];
    char     uca_version[8];
    uint64_t record_count;
    uint64_t record_size;
    uint64_t records_offset;
    uint64_t decomp_record_count;
    uint64_t decomp_records_offset;
    uint64_t decomp_data_count;
    uint64_t decomp_data_offset;
    uint64_t compose_record_count;
    uint64_t compose_records_offset;
    hash128_t ucd_hash;
    uint8_t  reserved[16];
} laplace_perfcache_header_t;

#ifdef __cplusplus
static_assert(sizeof(laplace_perfcache_record_t) == 80, "perfcache record must be 80 bytes");
static_assert(sizeof(laplace_perfcache_header_t) == 128, "perfcache header must be 128 bytes");
#else
_Static_assert(sizeof(laplace_perfcache_record_t) == 80, "perfcache record must be 80 bytes");
_Static_assert(sizeof(laplace_perfcache_header_t) == 128, "perfcache header must be 128 bytes");
#endif

#define LAPLACE_PERFCACHE_TRAILER_BYTES 16u

#ifdef __cplusplus
}
#endif
