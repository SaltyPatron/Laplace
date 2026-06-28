#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Native anchor resolution — the parsers + (later) map lookup + hashing that the C# orchestrator does
 * today (SourceEntityIdConventions). The native ETL must produce BIT-IDENTICAL anchor ids to the C#
 * path, so each function mirrors its C# counterpart exactly. This header starts with the pure key
 * parsers; the ILI/language maps and the resolver dispatch build on top.
 */

/*
 * Parse an MCR / Predicate-Matrix / WN-RDF synset key to (offset, ss_type). Mirrors
 * SourceEntityIdConventions.ParseMcrSynsetKey: strips a leading "<ns>:" namespace and an "ili-" prefix,
 * takes the ss_type after the last '-', and the offset after the last '-' of the remainder (or the
 * remainder itself). Accepts "30-02244956-v", "ili-30-02244956-v", "mcr:ili-30-...-v", "00009147-v".
 * Returns 1 on success (writes *out_offset > 0 and *out_ss in {n,v,a,s,r}), 0 on no-match.
 */
int lp_parse_mcr_synset(const char* s, size_t n, int64_t* out_offset, char* out_ss);

/*
 * Parse a MapNet / MultiWordNet synset key "<pos>#<offset>[<trailing non-digits>]" to (offset, ss_type).
 * Mirrors SourceEntityIdConventions.ParseMapNetSynsetKey: ss_type is the first char, the offset is the
 * digit run after '#' (stops at any non-digit, e.g. the '$' terminator). Accepts "a#00057580",
 * "a#00057580$". Returns 1 on success, 0 on no-match.
 */
int lp_parse_mapnet_synset(const char* s, size_t n, int64_t* out_offset, char* out_ss);

/*
 * The ILI offset map: WordNet (offset, ss_type) -> ILI id string ("iN"). Mirrors the C# IliMap exactly,
 * including the adjective a/s collapse (an adjective offset is 'a' XOR 's', so OMW's '-a' satellite
 * lookups resolve the pwn '-s' entries) and the 3-column older-map form (ili \t offset-pos \t confidence).
 */
typedef struct lp_ili_map lp_ili_map_t;

/* Load from a .tab file (pwn30 or an older-version map). Returns NULL if the file is missing/empty. */
lp_ili_map_t* lp_ili_map_load(const char* tab_path);

/* Resolve (offset, ss_type) -> "iN", or NULL if absent. ss_type 'a' and 's' are equivalent. */
const char* lp_ili_map_resolve(const lp_ili_map_t* map, int64_t offset, char ss);

/* Number of loaded entries (for diagnostics/tests). */
size_t lp_ili_map_count(const lp_ili_map_t* map);

void lp_ili_map_free(lp_ili_map_t* map);

/*
 * Resolve a WordNet synset reference to a content-addressed anchor id, mirroring C#
 * SourceEntityIdConventions.ResolveSynsetAnchor -> ConceptAnchor.SynsetId: trims, rejects NULL, strips
 * everything up to the last '/', parses the MCR then MapNet form, maps (offset, ss_type) -> "iN" via
 * `map`, and hashes "iN" with laplace_content_root_id (the tree-composed RootId). Writes *out_id and
 * returns 1 on success; returns 0 if the key doesn't parse or the offset isn't in the map.
 * (Sense-key fallback / language path land in later increments.)
 */
int lp_resolve_synset_anchor(const lp_ili_map_t* map, const char* raw, size_t n, hash128_t* out_id);

/*
 * Resolve a WordNet sense key (lemma%ss:lex:id[...]) to a content anchor id, mirroring SenseAnchor.Id:
 * NormalizeSenseKey (trim, drop leading '?'/'!', lemma '_'->' ', first three ':'-fields) then
 * laplace_content_root_id of "lemma%f0:f1:f2". Writes *out_id, returns 1; 0 if the key doesn't normalize.
 */
int lp_resolve_sense_anchor(const char* raw, size_t n, hash128_t* out_id);

/*
 * Resolve a category key (FrameNet frame / LU / VerbNet class / PropBank roleset) to a content anchor
 * id, mirroring CategoryAnchor.Id: trim then laplace_content_root_id. Writes *out_id, returns 1; 0 if
 * the key is empty after trimming.
 */
int lp_resolve_category_anchor(const char* raw, size_t n, hash128_t* out_id);

#ifdef __cplusplus
}
#endif
