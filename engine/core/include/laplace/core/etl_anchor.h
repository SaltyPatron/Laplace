#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

int lp_parse_mcr_synset(const char* s, size_t n, int64_t* out_offset, char* out_ss);

int lp_parse_mapnet_synset(const char* s, size_t n, int64_t* out_offset, char* out_ss);

typedef struct lp_ili_map lp_ili_map_t;

lp_ili_map_t* lp_ili_map_load(const char* tab_path);

const char* lp_ili_map_resolve(const lp_ili_map_t* map, int64_t offset, char ss);

size_t lp_ili_map_count(const lp_ili_map_t* map);

void lp_ili_map_free(lp_ili_map_t* map);

int lp_resolve_synset_anchor(const lp_ili_map_t* map, const char* raw, size_t n, hash128_t* out_id);

int lp_resolve_sense_anchor(const char* raw, size_t n, hash128_t* out_id);

int lp_resolve_category_anchor(const char* raw, size_t n, hash128_t* out_id);

#ifdef __cplusplus
}
#endif
