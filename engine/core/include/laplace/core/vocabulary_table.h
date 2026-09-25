#pragma once

#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/vocabulary_perfcache_format.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * mmap load + O(1) lookups for laplace_vocabulary_perfcache.bin.
 * Load returns 0, -1 (cannot map), -2 (magic/version), -3 (layout),
 * -4 (checksum), -5 (a family's codes are not 1..N in order).
 */
int  vocabulary_table_load(const char* path);
void vocabulary_table_unload(void);
int  vocabulary_table_is_loaded(void);

/* The record of a governed code (1-based); NULL on miss or when not loaded. */
const laplace_vocabulary_perfcache_record_t*
vocabulary_table_lookup(laplace_vocabulary_family_t family, uint16_t code);

/* The record of content `id` in one vocabulary; NULL when `id` has no code there. One
 * entity may hold a code in several vocabularies ("advcl" is a relation and a subtype). */
const laplace_vocabulary_perfcache_record_t*
vocabulary_table_find(laplace_vocabulary_family_t family, const hash128_t* id);

/* The NUL-terminated label of a record ("NOUN", "pass", "Number=Plur"). */
const char* vocabulary_table_label(const laplace_vocabulary_perfcache_record_t* record);

/* Number of codes in a family; 0 when not loaded. */
uint32_t vocabulary_table_family_count(laplace_vocabulary_family_t family);

#ifdef __cplusplus
}
#endif
