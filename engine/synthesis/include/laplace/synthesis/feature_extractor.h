#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Feature extractor — per RULES.md R10 plugin (IFeatureExtractor).
 *
 * Extracts a fixed-dim feature vector from an entity + attestation cloud.
 * Used during Substrate Synthesis to populate hidden-state embeddings.
 *
 * Initial set (Chunk 7 Stories 7.4-7.10):
 *   - CanonicalCoordExtractor (dims 0-4)
 *   - POSFeatureExtractor (dims 5-25)
 *   - WordNetSynsetExtractor (dims 26-125)
 *   - ConceptNetRelationExtractor (dims 226-325)
 *   - CoOccurrenceExtractor (dims 626-925)
 *   - PhysicalityProjectionExtractor (per-source 4D)
 *   - RandomProjectionPadExtractor (remaining dims)
 *
 * Substrate-specific — no upstream equivalent. Opaque handle pattern. */
typedef struct feature_extractor feature_extractor_t;

/* Load a feature extractor by name. */
feature_extractor_t* feature_extractor_load(const char* extractor_name);

/* Run extraction. `entity_hash` is the subject; the engine reads the
 * attestation cloud via substrate query. Output vector is `out_dim`
 * doubles written to `out_features`. */
int feature_extractor_extract(const feature_extractor_t* fe,
                              const void*                entity_hash, /* hash128_t* */
                              double*                    out_features,
                              size_t                     out_dim);

/* Reports this extractor's output dimensionality. */
size_t feature_extractor_output_dim(const feature_extractor_t* fe);

void feature_extractor_free(feature_extractor_t* fe);

#ifdef __cplusplus
}
#endif
