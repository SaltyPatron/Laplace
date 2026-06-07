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

#ifdef __cplusplus
}
#endif
