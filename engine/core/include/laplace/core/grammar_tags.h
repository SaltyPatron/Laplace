#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/grammar_registry.h"  

#ifdef __cplusplus
extern "C" {
#endif


enum {
    LAPLACE_TAG_OTHER        = 0,
    LAPLACE_TAG_NAME         = 1,  
    LAPLACE_TAG_DEF_FUNCTION = 2,  
    LAPLACE_TAG_DEF_TYPE     = 3,  
    LAPLACE_TAG_DEF_VAR      = 4,  
    LAPLACE_TAG_REF_CALL     = 5,  
    LAPLACE_TAG_REF_TYPE     = 6   
};

typedef struct {
    uint32_t match_id;       
    uint16_t capture_type;   
    uint16_t _pad;
    uint32_t start_byte;     
    uint32_t end_byte;
} laplace_tag_t;





int laplace_grammar_tags_run(const TSLanguage* lang,
                             const char* tags_scm, size_t tags_len,
                             const uint8_t* utf8, size_t len,
                             laplace_tag_t** out_tags, size_t* out_n);

void laplace_grammar_tags_free(laplace_tag_t* tags);

#ifdef __cplusplus
}
#endif
