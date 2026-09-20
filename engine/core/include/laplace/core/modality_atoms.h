#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Modality dispatch tags for witness type floors / emit.
 *
 * These tags select the CURRENT image/audio implementation recipe.
 * The historical "Tier-0 is always Unicode codepoints for every modality" claim
 * is retired; Unicode is the selected textual generation, not the ontology of
 * every physical sample. No private disconnected modality identity world is
 * permitted either. docs/invention/modality-ladder-law.md and GH #1134 govern
 * the replacement of the legacy decimal/codepoint media recipe.
 */
typedef enum {
    LAPLACE_MODALITY_IMAGE = 1,
    LAPLACE_MODALITY_AUDIO = 2,
} laplace_modality_t;

#ifdef __cplusplus
}
#endif
