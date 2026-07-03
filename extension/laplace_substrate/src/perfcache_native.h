








#pragma once

#include <stdbool.h>
#include <stdint.h>


void laplace_substrate_perfcache_init(void);

int laplace_substrate_native_mkl_threads(void);




bool laplace_perfcache_ready(void);

bool laplace_highway_ready(void);

/* Eager warm-up for shared_preload_libraries: mmap + CRC-validate both
 * perfcache blobs and build the codepoint reverse index in the POSTMASTER,
 * so forked backends inherit everything copy-on-write and never pay the
 * multi-second first-call load. No-op unless preloading. */
void laplace_substrate_perfcache_prewarm(void);





bool laplace_perfcache_codepoint_for_id(const uint8_t id[16], uint32_t *out_cp);
