#pragma once

#include <stdint.h>
#include "laplace/core/chess_perfcache_format.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"

#ifdef __cplusplus
extern "C" {
#endif

int chess_position_table_load(const char* path);
void chess_position_table_unload(void);
int chess_position_table_is_loaded(void);

/* Binary search on sorted records. Returns NULL on miss / not loaded. */
const laplace_chess_perfcache_record_t*
chess_position_table_lookup(const hash128_t* id);

/* Copy geometry out for managed callers (avoids marshaling mmap pointers). */
int chess_position_table_lookup_geom(const hash128_t* id,
                                     double out_coord[4],
                                     hilbert128_t* out_hb,
                                     uint32_t* out_n,
                                     uint8_t* out_tier);

int chess_position_table_record_count(uint64_t* out_count);

#ifdef __cplusplus
}
#endif
