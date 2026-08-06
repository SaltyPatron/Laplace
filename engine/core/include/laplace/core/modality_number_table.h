#pragma once

#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/modality_number_perfcache_format.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * mmap load + O(1) lookup for laplace_modality_number_perfcache.bin.
 * Key = unsigned number value (v1: 0..255 channel byte). Index = value.
 */

int modality_number_table_load(const char* path);
void modality_number_table_unload(void);
int modality_number_table_is_loaded(void);

/* O(1) dense index. Returns NULL on miss / not loaded / value out of range. */
const laplace_modality_number_perfcache_record_t*
modality_number_table_lookup(uint32_t value);

/* Copy id out for callers that should not hold mmap pointers across unload. */
int modality_number_table_lookup_id(uint32_t value, hash128_t* out_id);

int modality_number_table_lookup_geom(uint32_t value,
                                      hash128_t* out_id,
                                      double out_coord[4],
                                      hilbert128_t* out_hb,
                                      uint32_t* out_n,
                                      uint8_t* out_tier);

int modality_number_table_record_count(uint64_t* out_count);

#ifdef __cplusplus
}
#endif
