#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct feature_extractor feature_extractor_t;

feature_extractor_t* feature_extractor_load(const char* extractor_name);

int feature_extractor_extract(const feature_extractor_t* fe,
                              const void*                entity_hash,
                              double*                    out_features,
                              size_t                     out_dim);

size_t feature_extractor_output_dim(const feature_extractor_t* fe);

void feature_extractor_free(feature_extractor_t* fe);

#ifdef __cplusplus
}
#endif
