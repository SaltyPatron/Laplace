#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Modality dispatch tags for witness type floors / emit.
 *
 * Tier-0 identity is ALWAYS Unicode codepoints (codepoint_table / T0 perfcache).
 * There is no image/audio leaf mint here — that forged floor was ripped.
 * Ladders: image_decomposer / audio_decomposer → modality_witness compose.
 */
typedef enum {
    LAPLACE_MODALITY_IMAGE = 1,
    LAPLACE_MODALITY_AUDIO = 2,
} laplace_modality_t;

#ifdef __cplusplus
}
#endif
