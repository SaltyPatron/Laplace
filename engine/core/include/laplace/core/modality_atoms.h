#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Modality dispatch tags for witness type floors / emit.
 *
 * These tags select the image/audio ladder while preserving the shared content law:
 * arbitrary amplitudes/colors are NOT minted as private Tier-0 atoms. Finite numeric
 * values compose from the existing codepoint floor into reusable scalar roots; their
 * image/audio occurrences retain modality roles separately. See modality-ladder-law
 * and GH #1134 for reconstruction/occurrence requirements.
 */
typedef enum {
    LAPLACE_MODALITY_IMAGE = 1,
    LAPLACE_MODALITY_AUDIO = 2,
} laplace_modality_t;

#ifdef __cplusplus
}
#endif
