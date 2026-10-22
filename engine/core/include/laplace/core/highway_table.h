#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/codepoint_table.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct { uint64_t w[4]; } laplace_mask256_t;

int  highway_table_load(const char* path);

void highway_table_unload(void);

int  highway_table_is_loaded(void);
/* Native owns validation, immutable mapping, identifier lookup and crosswalks.
 * Returned records remain valid for process lifetime, including after unload. */
int highway_table_records(const codepoint_entry_t **records, uint64_t *count);
const codepoint_entry_t *highway_table_find(const hash128_t *id);
const codepoint_entry_t *highway_table_at(const char *list, uint32_t slot);
int64_t highway_table_key(const char *list, const char *key);
int highway_table_fingerprint(uint8_t out[32]);
/* Borrowed Native lp_edge pairs (uint32 from, to); one immutable snapshot. */
int highway_table_edge_span(const char *from, uint32_t slot, const char *to,
                            const void **edges, uint64_t *count);
/* Relation selectors are compiled manifest metadata, independent of the Native
 * highway's 64-bit entity/occurrence carrier. No second ROM is generated. */

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
