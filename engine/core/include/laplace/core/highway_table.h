#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint32_t magic;
    uint32_t format_version;
    uint64_t relation_count;
    uint64_t relations_offset;
    uint64_t band_count;
    uint64_t band_masks_offset;
    uint64_t strings_offset;
    uint64_t strings_length;
    uint8_t  fingerprint[8];
    uint8_t  reserved[64];
} laplace_highway_header_t;

typedef struct {
    uint32_t name_off;
    uint8_t  name_len;
    uint8_t  rank_band;
    uint8_t  bit_pos;
    uint8_t  symmetry;
    float    rank;
    int16_t  parent_bit;
    uint8_t  _pad[18];
} laplace_highway_rel_rec_t;

typedef struct { uint64_t w[4]; } laplace_mask256_t;

int  highway_table_load(const char* path);

void highway_table_unload(void);

int  highway_table_is_loaded(void);

int highway_table_relation_by_hash(const hash128_t* type_id,
                                   uint8_t*         out_bit_pos,
                                   float*           out_rank,
                                   uint8_t*         out_band);

int highway_table_relation_by_bit(uint8_t      bit_pos,
                                  const char** out_canonical,
                                  float*       out_rank,
                                  uint8_t*     out_band);

int highway_table_band_mask(uint8_t band, laplace_mask256_t* out_mask);

laplace_mask256_t highway_table_mask_or (laplace_mask256_t a, laplace_mask256_t b);
laplace_mask256_t highway_table_mask_and(laplace_mask256_t a, laplace_mask256_t b);
int               highway_table_mask_test(const laplace_mask256_t* m, uint8_t bit);
void              highway_table_mask_set (laplace_mask256_t* m, uint8_t bit);
int               highway_table_mask_any(const laplace_mask256_t* m);

#ifdef __cplusplus
}
#endif
