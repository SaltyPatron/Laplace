/*
 * centroid_abi_v1.h — Laplace centroid mantissa bit-bang ABI, version 1.0.
 *
 * FROZEN as of substrate v1.0. Adding/removing/reordering bits invalidates
 * every centroid in every database. Reserved bits are for v1.x extension
 * with backward-compatible semantics (set to zero by v1.0 emitters, ignored
 * by v1.0 readers).
 *
 * Phase 2 / Track B / new service: CentroidAbiService.
 *
 * Layout (152 bits total, distributed across the 4 doubles of a POINT4D):
 *
 *   Bits   0..63  : prime-flag bitmask (universal, language-agnostic)
 *   Bits  64..95  : entity_id (32-bit substrate-monotonic; codepoint
 *                              integer for tier-0, allocated id for tier-1+)
 *   Bits  96..103 : modality enum (8 bits)
 *   Bits 104..119 : language id (16 bits, ISO 639 + extensions)
 *   Bits 120..127 : model id (8 bits — for fireflies; 0 for substrate atoms)
 *   Bits 128..131 : tier (4 bits)
 *   Bits 132..151 : reserved
 *
 * The bits are striped across the four mantissas of a POINT4D so that
 * geometric perturbation per-axis stays below 10^-15 (super-Fibonacci
 * spacing is ~10^-3, so 12+ orders of magnitude headroom).
 *
 * The bit POSITIONS below have no name in any natural language — they are
 * pure enumerations. cat / neko / gato / chat / кот / 猫 / kissa are peer
 * entities; cross-language equivalence is graph-emergent from ingested
 * sources (WordNet, OMW, Wiktionary, Tatoeba, UD), not from any anchor
 * entity. The flags here just mark per-entity ATTESTED CATEGORIES.
 */

#ifndef LAPLACE_CENTROID_ABI_V1_H
#define LAPLACE_CENTROID_ABI_V1_H

#include <stdint.h>

#include "laplace_pg/geometry4d.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ------------------------------------------------------------------ */
/* Prime-flag bitmask — bits 0..63 of the centroid mantissa payload.   */
/* ------------------------------------------------------------------ */

/* Part-of-speech (12 bits, 0..11). UD UPOS-aligned. */
#define LAPLACE_FLAG_NOUN          (1ULL <<  0)
#define LAPLACE_FLAG_VERB          (1ULL <<  1)
#define LAPLACE_FLAG_ADJ           (1ULL <<  2)
#define LAPLACE_FLAG_ADV           (1ULL <<  3)
#define LAPLACE_FLAG_PRON          (1ULL <<  4)
#define LAPLACE_FLAG_PREP          (1ULL <<  5)
#define LAPLACE_FLAG_DET           (1ULL <<  6)
#define LAPLACE_FLAG_CONJ          (1ULL <<  7)
#define LAPLACE_FLAG_INTERJ        (1ULL <<  8)
#define LAPLACE_FLAG_NUM           (1ULL <<  9)
#define LAPLACE_FLAG_PART          (1ULL << 10)
#define LAPLACE_FLAG_PUNCT         (1ULL << 11)

/* Semantic primitives (12 bits, 12..23). */
#define LAPLACE_FLAG_ANIMATE       (1ULL << 12)
#define LAPLACE_FLAG_CONCRETE      (1ULL << 13)
#define LAPLACE_FLAG_ABSTRACT      (1ULL << 14)
#define LAPLACE_FLAG_PERSON        (1ULL << 15)
#define LAPLACE_FLAG_PLACE         (1ULL << 16)
#define LAPLACE_FLAG_THING         (1ULL << 17)
#define LAPLACE_FLAG_ACTION        (1ULL << 18)
#define LAPLACE_FLAG_PROPERTY      (1ULL << 19)
#define LAPLACE_FLAG_RELATION      (1ULL << 20)
#define LAPLACE_FLAG_QUANTITY      (1ULL << 21)
#define LAPLACE_FLAG_EVENT         (1ULL << 22)
#define LAPLACE_FLAG_STATE         (1ULL << 23)

/* Number (4 bits, 24..27). */
#define LAPLACE_FLAG_SINGULAR      (1ULL << 24)
#define LAPLACE_FLAG_PLURAL        (1ULL << 25)
#define LAPLACE_FLAG_DUAL          (1ULL << 26)
#define LAPLACE_FLAG_MASS          (1ULL << 27)

/* Tense / aspect (8 bits, 28..35). */
#define LAPLACE_FLAG_PAST          (1ULL << 28)
#define LAPLACE_FLAG_PRESENT       (1ULL << 29)
#define LAPLACE_FLAG_FUTURE        (1ULL << 30)
#define LAPLACE_FLAG_PERFECT       (1ULL << 31)
#define LAPLACE_FLAG_IMPERFECT     (1ULL << 32)
#define LAPLACE_FLAG_CONTINUOUS    (1ULL << 33)
#define LAPLACE_FLAG_HABITUAL      (1ULL << 34)
#define LAPLACE_FLAG_GNOMIC        (1ULL << 35)

/* Case (8 bits, 36..43). */
#define LAPLACE_FLAG_CASE_NOM      (1ULL << 36)
#define LAPLACE_FLAG_CASE_ACC      (1ULL << 37)
#define LAPLACE_FLAG_CASE_DAT      (1ULL << 38)
#define LAPLACE_FLAG_CASE_GEN      (1ULL << 39)
#define LAPLACE_FLAG_CASE_INSTR    (1ULL << 40)
#define LAPLACE_FLAG_CASE_LOC      (1ULL << 41)
#define LAPLACE_FLAG_CASE_ABL      (1ULL << 42)
#define LAPLACE_FLAG_CASE_VOC      (1ULL << 43)

/* Modality kind (8 bits, 44..51). Distinct from the per-centroid
 * `modality` byte at bits 96..103: this flag bitmask records which
 * modalities a composition spans (a parallel-corpus sentence might
 * have TEXT ∪ AUDIO if it has an audio recording). */
#define LAPLACE_FLAG_TEXT          (1ULL << 44)
#define LAPLACE_FLAG_SPEECH        (1ULL << 45)
#define LAPLACE_FLAG_IMAGE         (1ULL << 46)
#define LAPLACE_FLAG_AUDIO         (1ULL << 47)
#define LAPLACE_FLAG_VIDEO         (1ULL << 48)
#define LAPLACE_FLAG_MATH          (1ULL << 49)
#define LAPLACE_FLAG_CODE          (1ULL << 50)
#define LAPLACE_FLAG_SIGN          (1ULL << 51)

/* Structural (8 bits, 52..59). */
#define LAPLACE_FLAG_SELF_REF      (1ULL << 52)
#define LAPLACE_FLAG_NEGATION      (1ULL << 53)
#define LAPLACE_FLAG_INTERROG      (1ULL << 54)
#define LAPLACE_FLAG_IMPERATIVE    (1ULL << 55)
#define LAPLACE_FLAG_CONDITIONAL   (1ULL << 56)
#define LAPLACE_FLAG_COUNTERFACT   (1ULL << 57)
#define LAPLACE_FLAG_MODAL         (1ULL << 58)
#define LAPLACE_FLAG_EVIDENTIAL    (1ULL << 59)

/* Bits 60..63 reserved for v1.x prime extensions. */

/* ------------------------------------------------------------------ */
/* Modality enum — bits 96..103.                                       */
/* ------------------------------------------------------------------ */

#define LAPLACE_MODALITY_UNKNOWN     0
#define LAPLACE_MODALITY_TEXT        1
#define LAPLACE_MODALITY_SPEECH      2
#define LAPLACE_MODALITY_IMAGE       3
#define LAPLACE_MODALITY_AUDIO       4
#define LAPLACE_MODALITY_VIDEO       5
#define LAPLACE_MODALITY_MATH        6
#define LAPLACE_MODALITY_CODE        7
#define LAPLACE_MODALITY_SIGN        8
#define LAPLACE_MODALITY_STRUCTURED  9   /* JSON/XML/YAML/etc.    */
#define LAPLACE_MODALITY_TIMESERIES 10
#define LAPLACE_MODALITY_GEO        11
#define LAPLACE_MODALITY_NETWORK    12
#define LAPLACE_MODALITY_BIO        13
#define LAPLACE_MODALITY_CAD        14
#define LAPLACE_MODALITY_GAME       15
#define LAPLACE_MODALITY_ENCRYPTED  16
#define LAPLACE_MODALITY_COMPRESSED 17
#define LAPLACE_MODALITY_FILESYSTEM 18
/* 19..255 reserved for v1.x. */

/* ------------------------------------------------------------------ */
/* Encode / decode payload from a POINT4D centroid.                    */
/*                                                                     */
/* The payload is striped across the 4 mantissas — codec details are   */
/* opaque to callers; use these accessors exclusively.                 */
/* ------------------------------------------------------------------ */

typedef struct {
    uint64_t prime_flags;     /* 64-bit bitmask of LAPLACE_FLAG_* */
    uint32_t entity_id;
    uint8_t  modality;        /* LAPLACE_MODALITY_*            */
    uint16_t language_id;     /* ISO 639 + extensions          */
    uint8_t  model_id;        /* 0 for substrate atoms         */
    uint8_t  tier;            /* 0..15                         */
    uint32_t reserved;        /* bits 132..151 (use 0 in v1.0) */
} laplace_centroid_payload_v1_t;

/* Encode payload into the mantissa bits of `position`. Modifies the
 * mantissa, preserves sign + exponent, perturbs each axis by < 10^-15. */
void laplace_centroid_encode_v1(laplace_point4d_t                  *position,
                                const laplace_centroid_payload_v1_t *payload);

/* Decode payload out of the mantissa bits of `position`. */
void laplace_centroid_decode_v1(const laplace_point4d_t       *position,
                                laplace_centroid_payload_v1_t *out_payload);

/* Strip payload bits from each axis and return a "geometry-only" copy
 * suitable for arithmetic (centroid, slerp, distance) without payload
 * polluting the result. Use this before feeding a centroid into
 * laplace_point4d_vertex_centroid (or similar) when computing the
 * next-tier centroid; restuff the result via _encode_v1 with the new
 * tier's payload. */
void laplace_centroid_strip_payload_v1(const laplace_point4d_t *position,
                                       laplace_point4d_t       *out_geometry);

/* Convenience: bit-wise OR of two payloads (the default UNION
 * propagation rule for tier-(N+1) prime_flags computed from tier-N
 * constituents). */
static inline laplace_centroid_payload_v1_t
laplace_centroid_payload_union_v1(const laplace_centroid_payload_v1_t *a,
                                  const laplace_centroid_payload_v1_t *b)
{
    laplace_centroid_payload_v1_t out = *a;
    out.prime_flags |= b->prime_flags;
    return out;
}

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_CENTROID_ABI_V1_H */
