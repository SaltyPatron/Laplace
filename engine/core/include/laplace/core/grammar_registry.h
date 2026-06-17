#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif





typedef struct TSLanguage TSLanguage;



const TSLanguage* laplace_grammar_lookup_by_id(const char* modality_id);
const TSLanguage* laplace_grammar_lookup_by_ext(const char* ext);


size_t laplace_grammar_list(const char** out, size_t cap);

#ifdef __cplusplus
}
#endif
