








#pragma once

#include <stdbool.h>
#include <stdint.h>


void laplace_substrate_perfcache_init(void);

int laplace_substrate_native_mkl_threads(void);




bool laplace_perfcache_ready(void);

bool laplace_highway_ready(void);

/* The governed vocabulary ROM (laplace_vocabulary_perfcache.bin). */
bool laplace_vocabulary_ready(void);

/* Chess position id -> coordinate map (laplace_chess_position_perfcache.bin). */
bool laplace_chess_position_ready(void);

/* Under shared_preload_libraries: map and validate the perfcache blobs and
 * build the codepoint reverse index in the postmaster so forked backends
 * inherit them copy-on-write. No-op unless preloading. */
void laplace_substrate_perfcache_prewarm(void);





bool laplace_perfcache_codepoint_for_id(const uint8_t id[16], uint32_t *out_cp);
