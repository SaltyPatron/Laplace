#pragma once

#include <stddef.h>
#include "laplace/core/perfcache_format.h"

#ifdef __cplusplus
extern "C" {
#endif

int laplace_unicode_seed_compute(const char* ucdxml_path,
                                 const char* ducet_path,
                                 laplace_perfcache_record_t* out_records,
                                 size_t out_capacity);

/* One-artifact database geometry path. DUCET owns UCA rank and therefore the
 * tier-0 coordinate/hilbert placement. Segmentation flags are deliberately zero:
 * those belong to the separately claimed UCD XML artifact. */
int laplace_unicode_seed_compute_ducet(const char* ducet_path,
                                       laplace_perfcache_record_t* out_records,
                                       size_t out_capacity);

/* Fully read/decompress and parse one UCD XML artifact without coupling it to
 * DUCET or publishing another artifact's completion. */
int laplace_unicode_seed_validate_ucdxml(const char* ucdxml_path);

#ifdef __cplusplus
}
#endif
