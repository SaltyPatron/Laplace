#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/grammar_registry.h"  /* opaque TSLanguage */

#ifdef __cplusplus
extern "C" {
#endif

/* Capture classes, normalized across grammars from tags.scm capture names. */
enum {
    LAPLACE_TAG_OTHER        = 0,
    LAPLACE_TAG_NAME         = 1,  /* @name — the identifier inside a def/ref */
    LAPLACE_TAG_DEF_FUNCTION = 2,  /* @definition.function / .method */
    LAPLACE_TAG_DEF_TYPE     = 3,  /* @definition.class / .type / .module / .interface / .struct / .enum */
    LAPLACE_TAG_DEF_VAR      = 4,  /* @definition.constant / .var / .field */
    LAPLACE_TAG_REF_CALL     = 5,  /* @reference.call */
    LAPLACE_TAG_REF_TYPE     = 6   /* @reference.* (non-call) */
};

typedef struct {
    uint32_t match_id;       /* captures of one query match share this — pair @name with its @definition/@reference */
    uint16_t capture_type;   /* LAPLACE_TAG_* */
    uint16_t _pad;
    uint32_t start_byte;     /* [start_byte, end_byte) of the captured node */
    uint32_t end_byte;
} laplace_tag_t;

/* Run a grammar's tags query (its queries/tags.scm source) over utf8, returning def/ref captures.
 * The seam: the .scm source is passed in as a string (the caller reads the grammar's tags.scm);
 * no tree-sitter type crosses this boundary. The caller frees via laplace_grammar_tags_free.
 * Returns 0 on success; -1 bad args, -2 invalid query, -3 parse/allocation failure. */
int laplace_grammar_tags_run(const TSLanguage* lang,
                             const char* tags_scm, size_t tags_len,
                             const uint8_t* utf8, size_t len,
                             laplace_tag_t** out_tags, size_t* out_n);

void laplace_grammar_tags_free(laplace_tag_t* tags);

#ifdef __cplusplus
}
#endif
