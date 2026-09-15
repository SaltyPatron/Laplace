#pragma once

#include <stddef.h>
#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Exact schema-v1 identities emitted by UdParseStructure. These name the
 * serialization grammar; they do not assign meanings to language surfaces. */
typedef struct {
    hash128_t schema_v1;
    hash128_t none;
    hash128_t root;
    hash128_t present;
    hash128_t features_end;
    hash128_t enhanced_end;
    hash128_t misc_end;
    hash128_t tokens_end;
    hash128_t mwt_end;
} laplace_ud_markers_t;

void laplace_ud_markers_init(laplace_ud_markers_t *out);

/* Borrowed flat pairs: items[2*i] and items[2*i+1]. Keeping the original ID
 * array avoids copying variable annotations or interpreting rendered labels. */
typedef struct {
    const hash128_t *items;
    size_t count;
} laplace_ud_pairs_t;

typedef struct {
    hash128_t ref_id;
    hash128_t form_id;
    hash128_t lemma_id;
    hash128_t upos_id;
    hash128_t xpos_id;
    laplace_ud_pairs_t features; /* relation ID, value ID */
    hash128_t head_ref_id;
    hash128_t deprel_id;
    laplace_ud_pairs_t enhanced; /* head reference ID, relation ID */
    laplace_ud_pairs_t misc;     /* key ID, value ID */
} laplace_ud_token_t;

typedef struct {
    hash128_t start_ref_id;
    hash128_t end_ref_id;
    hash128_t form_id;
    laplace_ud_pairs_t misc;
} laplace_ud_mwt_t;

typedef struct {
    hash128_t sentence_id;
    hash128_t language_id;
    laplace_ud_token_t *tokens;
    size_t token_count;
    laplace_ud_mwt_t *mwts;
    size_t mwt_count;
} laplace_ud_parse_t;

typedef enum {
    LAPLACE_UD_PARSE_OK = 0,
    LAPLACE_UD_PARSE_ARGUMENT = -1,
    LAPLACE_UD_PARSE_SCHEMA = -2,
    LAPLACE_UD_PARSE_MALFORMED = -3,
    LAPLACE_UD_PARSE_MEMORY = -4
} laplace_ud_parse_status_t;

/* Decode one complete, already unpacked ParseStructure trajectory. The caller
 * supplies its full logical constituent count and verifies content identity at
 * the trajectory boundary: schema-v1 has no total-count/end-of-document field,
 * so a record-boundary prefix cannot be detected from the payload alone.
 *
 * Successful results own token/MWT arrays and borrow annotation IDs from flat;
 * flat must remain alive and unchanged while the result is used. On failure,
 * out is zeroed and no partial token set is exposed. Pass a fresh or freed out.
 * Unique references, head targets and MWT endpoints are validated after the
 * entire structure is read; None basic heads remain valid absent annotation.
 * No surface decoding, dependency-role inference or speech-act policy occurs.
 */
laplace_ud_parse_status_t laplace_ud_parse_decode(
    const hash128_t *flat, size_t count, laplace_ud_parse_t *out);

void laplace_ud_parse_free(laplace_ud_parse_t *parse);

#ifdef __cplusplus
}
#endif
