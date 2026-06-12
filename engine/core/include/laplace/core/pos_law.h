#pragma once

#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    LAPLACE_POS_TAGSET_UPOS       = 0,
    LAPLACE_POS_TAGSET_WORDNET    = 1,
    LAPLACE_POS_TAGSET_WIKTIONARY = 2,
    LAPLACE_POS_TAGSET_FRAMENET   = 3,
} laplace_pos_tagset_t;

/* Returns 0 = canonical UPOS entity, 1 = probationary entity (unmapped tag --
 * the witnessing change MUST emit the probationary entity), <0 = error. */
int laplace_pos_resolve_entity(const char* tag, laplace_pos_tagset_t tagset, hash128_t* out_entity_id);
const char* const* laplace_pos_upos_canonical(size_t* out_count);

#ifdef __cplusplus
}
#endif
