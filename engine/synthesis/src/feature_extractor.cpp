#include "laplace/synthesis/feature_extractor.h"

#include <cmath>
#include <cstring>

struct feature_extractor {
    size_t output_dim;
};

namespace {

constexpr size_t kCanonicalCoordDim = 4;

/* Deterministic dense coord from a 16-byte entity hash — not an identity axis.
 * Maps hash bytes to a unit 4-vector (S³ projection) for pour-time feature lookup. */
void hash_to_unit4(const void* entity_hash, double* out4)
{
    const auto* h = static_cast<const unsigned char*>(entity_hash);
    double x = 1.0, y = 1.0, z = 1.0, w = 1.0;
    for (int i = 0; i < 16; ++i) {
        const double t = (double)h[i] / 255.0;
        x += t * std::sin((double)(i + 1) * 0.73);
        y += t * std::cos((double)(i + 1) * 1.17);
        z += t * std::sin((double)(i + 1) * 1.91);
        w += t * std::cos((double)(i + 1) * 2.41);
    }
    const double n = std::sqrt(x * x + y * y + z * z + w * w);
    if (n < 1e-12) {
        out4[0] = 0.0;
        out4[1] = 0.0;
        out4[2] = 0.0;
        out4[3] = 1.0;
        return;
    }
    const double inv = 1.0 / n;
    out4[0] = x * inv;
    out4[1] = y * inv;
    out4[2] = z * inv;
    out4[3] = w * inv;
}

} // namespace

extern "C"
feature_extractor_t* feature_extractor_load(const char* extractor_name) {
    if (!extractor_name) return nullptr;
    if (std::strcmp(extractor_name, "canonical_coord") != 0) return nullptr;
    auto* fe = new feature_extractor{};
    fe->output_dim = kCanonicalCoordDim;
    return fe;
}

extern "C"
int feature_extractor_extract(const feature_extractor_t* fe,
                              const void*                entity_hash,
                              double*                    out_features,
                              size_t                     out_dim) {
    if (!fe || !entity_hash || !out_features) return -1;
    if (out_dim < fe->output_dim) return -1;
    hash_to_unit4(entity_hash, out_features);
    for (size_t d = fe->output_dim; d < out_dim; ++d) out_features[d] = 0.0;
    return 0;
}

extern "C"
size_t feature_extractor_output_dim(const feature_extractor_t* fe) {
    return fe ? fe->output_dim : 0;
}

extern "C" void feature_extractor_free(feature_extractor_t* fe) {
    delete fe;
}
