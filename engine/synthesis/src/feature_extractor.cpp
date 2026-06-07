#include "laplace/synthesis/feature_extractor.h"

#include <cstddef>

struct feature_extractor {
    int _placeholder;
};

extern "C"
feature_extractor_t* feature_extractor_load(const char* extractor_name) {
    (void)extractor_name;
    return nullptr;
}

extern "C"
int feature_extractor_extract(const feature_extractor_t* fe,
                              const void*                entity_hash,
                              double*                    out_features,
                              size_t                     out_dim) {
    (void)fe; (void)entity_hash; (void)out_features; (void)out_dim;
    return -1;
}

extern "C"
size_t feature_extractor_output_dim(const feature_extractor_t* fe) {
    (void)fe;
    return 0;
}

extern "C" void feature_extractor_free(feature_extractor_t* fe) {
    delete fe;
}
