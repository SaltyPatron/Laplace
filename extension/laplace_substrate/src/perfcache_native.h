








#pragma once

#include <stdbool.h>
#include <stdint.h>


void laplace_substrate_perfcache_init(void);




bool laplace_perfcache_ready(void);





bool laplace_perfcache_codepoint_for_id(const uint8_t id[16], uint32_t *out_cp);
