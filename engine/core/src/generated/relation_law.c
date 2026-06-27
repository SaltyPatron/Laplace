#include "laplace/core/relation_law.h"

#include <stdio.h>
#include <string.h>

#include "laplace/core/hash128.h"

static const laplace_relation_def_t k_relations[] = {
    { "ADJACENT_TO_PIXEL", {0}, 0.36, LAPLACE_REL_SYMMETRY_SYMMETRIC, -1, 0, 0 },
    { "ALSO_SEE", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 135, 135, 0 },
    { "ATTENDS", {0}, 0.27, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 135, 135, 0 },
    { "AT_LOCATION", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 3, 0 },
    { "BORROWED_FROM", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 48, 48, 0 },
    { "CALLS", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 135, 135, 0 },
    { "CANONICAL_DECOMPOSES_TO", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 6, 0 },
    { "CAPABLE_OF", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 7, 0 },
    { "CAPTIONS", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 8, 0 },
    { "CAUSATIVE_OF", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 9, 0 },
    { "CAUSES", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 10, 0 },
    { "CAUSES_DESIRE", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 11, 0 },
    { "COMPATIBILITY_DECOMPOSES_TO", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 12, 0 },
    { "COMPLETES_TO", {0}, 0.27, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 13, 0 },
    { "CONFUSABLE_WITH", {0}, 0.08, LAPLACE_REL_SYMMETRY_SYMMETRIC, -1, 14, 0 },
    { "CONTAINS", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 73, 73, 0 },
    { "CONTINUES_TO", {0}, 0.27, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 16, 0 },
    { "CORRESPONDS_TO", {0}, 0.82, LAPLACE_REL_SYMMETRY_SYMMETRIC, -1, 17, 0 },
    { "CREATED_BY", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 18, 0 },
    { "DECODES_TO", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 19, 0 },
    { "DEPENDS_ON", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 20, 0 },
    { "DEPICTS", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 21, 0 },
    { "DERIVATIONALLY_RELATED", {0}, 0.36, LAPLACE_REL_SYMMETRY_SYMMETRIC, 135, 135, 0 },
    { "DERIVED_FROM", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 22, 135, 0 },
    { "DESIRES", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 24, 0 },
    { "DISTINCT_FROM", {0}, 0.45, LAPLACE_REL_SYMMETRY_SYMMETRIC, -1, 25, 0 },
    { "ENHANCED_DEPENDS_ON", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 26, 0 },
    { "ENTAILS", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 27, 0 },
    { "ETYMOLOGICALLY_DERIVED_FROM", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 48, 48, 0 },
    { "ETYMOLOGICALLY_RELATED_TO", {0}, 0.36, LAPLACE_REL_SYMMETRY_SYMMETRIC, 48, 48, 0 },
    { "EVOKES_FRAME", {0}, 0.9, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 30, 0 },
    { "EXCLUDES", {0}, 0.45, LAPLACE_REL_SYMMETRY_SYMMETRIC, -1, 31, 0 },
    { "FORM_OF", {0}, 0.82, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 135, 135, 0 },
    { "FRAME_USES", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 135, 135, 0 },
    { "HAS_A", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 73, 73, 0 },
    { "HAS_AGE", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 35, 0 },
    { "HAS_ATTRIBUTE", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 36, 0 },
    { "HAS_BIDI_CLASS", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 37, 0 },
    { "HAS_BLOCK", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 38, 0 },
    { "HAS_COMBINING_CLASS", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 39, 0 },
    { "HAS_CONTEXT", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 40, 0 },
    { "HAS_DBPEDIA_RELATION", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 41, 0 },
    { "HAS_DEFINITION", {0}, 0.97, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 42, 0 },
    { "HAS_DOMAIN_REGION", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 43, 0 },
    { "HAS_DOMAIN_TOPIC", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 44, 0 },
    { "HAS_DOMAIN_USAGE", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 45, 0 },
    { "HAS_EAST_ASIAN_WIDTH", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 46, 0 },
    { "HAS_EMOJI_PROPERTY", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 47, 0 },
    { "HAS_ETYMOLOGY", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 48, 0 },
    { "HAS_EXAMPLE", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 49, 0 },
    { "HAS_EXTERNAL_ID", {0}, 0.12, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 50, 0 },
    { "HAS_FEATURE", {0}, 0.18, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 51, 0 },
    { "HAS_FIRST_SUBEVENT", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 80, 80, 0 },
    { "HAS_FRAME_ELEMENT", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 53, 0 },
    { "HAS_GENERAL_CATEGORY", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 54, 0 },
    { "HAS_ISO639_1_CODE", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 55, 0 },
    { "HAS_ISO639_2B_CODE", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 56, 0 },
    { "HAS_ISO639_2T_CODE", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 57, 0 },
    { "HAS_ISO639_2_CODE", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 58, 0 },
    { "HAS_JOINING_TYPE", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 59, 0 },
    { "HAS_LANGUAGE", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 60, 0 },
    { "HAS_LANGUAGE_SCOPE", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 61, 0 },
    { "HAS_LANGUAGE_TYPE", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 62, 0 },
    { "HAS_LAST_SUBEVENT", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 80, 80, 0 },
    { "HAS_LEX_CATEGORY", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 44, 44, 0 },
    { "HAS_LINE_BREAK", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 65, 0 },
    { "HAS_LOWERCASE_MAPPING", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 66, 0 },
    { "HAS_MEMBER", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 73, 73, 0 },
    { "HAS_MIRROR", {0}, 0.08, LAPLACE_REL_SYMMETRY_SYMMETRIC, -1, 68, 0 },
    { "HAS_NAME", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 69, 0 },
    { "HAS_NAME_ALIAS", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 70, 0 },
    { "HAS_NUMERIC_TYPE", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 71, 0 },
    { "HAS_NUMERIC_VALUE", {0}, 0.12, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 72, 0 },
    { "HAS_PART", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 73, 0 },
    { "HAS_POS", {0}, 0.18, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 74, 0 },
    { "HAS_PREREQUISITE", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 75, 0 },
    { "HAS_PROPERTY", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 36, 36, 0 },
    { "HAS_SCRIPT", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 77, 0 },
    { "HAS_SEMANTIC_ROLE", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 78, 0 },
    { "HAS_SENSE", {0}, 0.82, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 79, 0 },
    { "HAS_SUBEVENT", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 80, 0 },
    { "HAS_SUBSTANCE", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 73, 73, 0 },
    { "HAS_SYNSET_KEY", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 82, 0 },
    { "HAS_THEMATIC_ROLE", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 83, 0 },
    { "HAS_TITLECASE_MAPPING", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 84, 0 },
    { "HAS_UPPERCASE_MAPPING", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 85, 0 },
    { "HAS_USAGE_REGISTER", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 86, 0 },
    { "HAS_UTF8_ROLE", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 87, 0 },
    { "HAS_VALENCE_PATTERN", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 88, 0 },
    { "HAS_VARIANT_OF", {0}, 0.82, LAPLACE_REL_SYMMETRY_SYMMETRIC, 135, 135, 0 },
    { "HAS_VERB_FRAME", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 90, 0 },
    { "HAS_XPOS", {0}, 0.18, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 74, 74, 0 },
    { "INCHOATIVE_OF", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 92, 0 },
    { "INHERITED_FROM", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 48, 48, 0 },
    { "INHERITS_FROM", {0}, 0.9, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 94, 0 },
    { "IN_VERB_GROUP_WITH", {0}, 0.36, LAPLACE_REL_SYMMETRY_SYMMETRIC, 135, 135, 0 },
    { "IS_A", {0}, 0.9, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 96, 0 },
    { "IS_AFTER", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 97, 0 },
    { "IS_ANTONYM_OF", {0}, 0.45, LAPLACE_REL_SYMMETRY_SYMMETRIC, -1, 98, 0 },
    { "IS_AT_SAMPLE", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 99, 0 },
    { "IS_BEFORE", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 100, 0 },
    { "IS_COORDINATE_TERM_WITH", {0}, 0.36, LAPLACE_REL_SYMMETRY_SYMMETRIC, 135, 135, 0 },
    { "IS_INSTANCE_OF", {0}, 0.9, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 96, 96, 0 },
    { "IS_LANGUAGE_CODE", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 103, 0 },
    { "IS_LEMMA_OF", {0}, 0.18, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 104, 0 },
    { "IS_PARTICIPLE_OF", {0}, 0.82, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 105, 0 },
    { "IS_PIXEL_OF", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 106, 0 },
    { "IS_SENSE_OF", {0}, 0.82, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 107, 0 },
    { "IS_SIMILAR_TO", {0}, 0.82, LAPLACE_REL_SYMMETRY_SYMMETRIC, 135, 135, 0 },
    { "IS_SYNONYM_OF", {0}, 0.82, LAPLACE_REL_SYMMETRY_SYMMETRIC, 135, 135, 0 },
    { "IS_TRANSLATION_OF", {0}, 0.82, LAPLACE_REL_SYMMETRY_SYMMETRIC, 135, 135, 0 },
    { "IS_TYPED_AS", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 111, 0 },
    { "LOCATED_NEAR", {0}, 0.36, LAPLACE_REL_SYMMETRY_SYMMETRIC, -1, 112, 0 },
    { "MADE_UP_OF", {0}, 0.73, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 73, 73, 0 },
    { "MANNER_OF", {0}, 0.9, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 96, 96, 0 },
    { "MEMBER_OF_MACROLANGUAGE", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 115, 0 },
    { "MEMBER_OF_VERBNET_CLASS", {0}, 0.9, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 116, 0 },
    { "MERGES_WITH", {0}, 0.27, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 117, 0 },
    { "MOTIVATED_BY_GOAL", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 118, 0 },
    { "NORMALIZES_TO", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 119, 0 },
    { "NOT_CAPABLE_OF", {0}, 0.45, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 120, 0 },
    { "NOT_DESIRES", {0}, 0.45, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 121, 0 },
    { "NOT_HAS_PROPERTY", {0}, 0.45, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 122, 0 },
    { "NOT_USED_FOR", {0}, 0.45, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 123, 0 },
    { "OBJECT_USE", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 124, 0 },
    { "OBSTRUCTED_BY", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 125, 0 },
    { "OV_RELATES", {0}, 0.27, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 135, 135, 0 },
    { "O_EFFECT", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 127, 0 },
    { "O_REACT", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 128, 0 },
    { "O_WANT", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 129, 0 },
    { "PERSPECTIVE_ON", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 135, 135, 0 },
    { "PERTAINS_TO", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 135, 135, 0 },
    { "PRECEDES", {0}, 0.18, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 132, 0 },
    { "RECEIVES_ACTION", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 133, 0 },
    { "REFERENCES", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 135, 135, 0 },
    { "RELATED_TO", {0}, 0.36, LAPLACE_REL_SYMMETRY_SYMMETRIC, -1, 135, 0 },
    { "REQUIRES", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 136, 0 },
    { "ROLE_CORRESPONDS_TO", {0}, 0.82, LAPLACE_REL_SYMMETRY_SYMMETRIC, 17, 17, 0 },
    { "SUPERSEDED_BY", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 138, 0 },
    { "SYMBOL_OF", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 139, 0 },
    { "TOKEN_MAPS_TO", {0}, 0.27, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 140, 0 },
    { "TRANSCRIBES_AS", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 141, 0 },
    { "USED_FOR", {0}, 0.36, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 142, 0 },
    { "USES_SCRIPT", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 143, 0 },
    { "USES_SCRIPT_EXTENSION", {0}, 0.08, LAPLACE_REL_SYMMETRY_ASYMMETRIC, 143, 143, 0 },
    { "X_ATTR", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 145, 0 },
    { "X_EFFECT", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 146, 0 },
    { "X_FILLED_BY", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 147, 0 },
    { "X_INTENT", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 148, 0 },
    { "X_NEED", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 149, 0 },
    { "X_REACT", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 150, 0 },
    { "X_REASON", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 151, 0 },
    { "X_WANT", {0}, 0.64, LAPLACE_REL_SYMMETRY_ASYMMETRIC, -1, 152, 0 },
};

static const laplace_relation_alias_t k_alias_storage[] = {
    { "DEFINED_AS", 42, 0 },
    { "DEFINES", 42, 0 },
    { "FOLLOWS", 132, 1 },
    { "HAS_HYPERNYM", 96, 0 },
    { "HAS_HYPONYM", 96, 1 },
    { "HAS_INSTANCE", 102, 1 },
    { "HAS_SENSE_OF", 107, 0 },
    { "HAS_UPOS", 74, 0 },
    { "HINDERED_BY", 125, 0 },
    { "IS_DOMAIN_REGION_MEMBER", 43, 1 },
    { "IS_DOMAIN_TOPIC_MEMBER", 44, 1 },
    { "IS_DOMAIN_USAGE_MEMBER", 45, 1 },
    { "IS_FILLED_BY", 147, 0 },
    { "IS_HYPERNYM_OF", 96, 1 },
    { "IS_HYPONYM_OF", 96, 0 },
    { "IS_INHERITED_BY", 94, 1 },
    { "IS_MEMBER_OF", 67, 1 },
    { "IS_PART_OF", 73, 1 },
    { "IS_SUBSTANCE_OF", 81, 1 },
    { "MADE_OF", 81, 0 },
    { "SIMILAR_TO", 108, 0 },
    { "SUBFRAME_OF", 80, 1 },
};

const laplace_relation_def_t* laplace_relation_table = k_relations;
const size_t laplace_relation_table_count = 153;
const laplace_relation_alias_t* laplace_relation_alias_table = k_alias_storage;
const size_t laplace_relation_alias_table_count = 22;

static int cmp_str(const char* a, const char* b) {
    return strcmp(a, b);
}

static int type_id_from_canonical(const char* canonical_name, hash128_t* out_type_id) {
    if (!canonical_name || !out_type_id) return -1;
    char path[256];
    int n = snprintf(path, sizeof(path), "substrate/type/%s/v1", canonical_name);
    if (n <= 0 || (size_t)n >= sizeof(path)) return -1;
    hash128_blake3((const uint8_t*)path, (size_t)n, out_type_id);
    return 0;
}

static hash128_t k_relation_type_id_cache[153];

#ifdef _WIN32
#include <windows.h>
static volatile LONG g_relation_ids_state = 0;
static int ids_try_begin(void) { return InterlockedCompareExchange(&g_relation_ids_state, 1, 0) == 0; }
static void ids_mark_ready(void) { InterlockedExchange(&g_relation_ids_state, 2); }
static int ids_ready(void) { return InterlockedCompareExchange(&g_relation_ids_state, 2, 2) == 2; }
#else
static volatile int g_relation_ids_state = 0;
static int ids_try_begin(void) { int expected = 0; return __atomic_compare_exchange_n(&g_relation_ids_state, &expected, 1, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE); }
static void ids_mark_ready(void) { __atomic_store_n(&g_relation_ids_state, 2, __ATOMIC_RELEASE); }
static int ids_ready(void) { return __atomic_load_n(&g_relation_ids_state, __ATOMIC_ACQUIRE) == 2; }
#endif

static void relation_ids_ensure(void) {
    if (ids_ready()) return;
    if (ids_try_begin()) {
        for (size_t i = 0; i < laplace_relation_table_count; ++i)
            type_id_from_canonical(laplace_relation_table[i].canonical, &k_relation_type_id_cache[i]);
        ids_mark_ready();
    } else {
        while (!ids_ready()) { }
    }
}

static int table_entry_type_id(size_t idx, hash128_t* out_type_id) {
    if (idx >= laplace_relation_table_count || !out_type_id) return -1;
    relation_ids_ensure();
    *out_type_id = k_relation_type_id_cache[idx];
    return 0;
}

int laplace_relation_type_id(const char* canonical_name, hash128_t* out_type_id) {
    if (!canonical_name || !out_type_id) return -1;
    for (size_t i = 0; i < laplace_relation_table_count; ++i) {
        if (cmp_str(laplace_relation_table[i].canonical, canonical_name) == 0) {
            return table_entry_type_id(i, out_type_id);
        }
    }
    return type_id_from_canonical(canonical_name, out_type_id) == 0 ? 1 : -1;
}

static int alias_lookup(const char* surface, int16_t* out_idx, uint8_t* out_flip) {
    for (size_t i = 0; i < laplace_relation_alias_table_count; ++i) {
        if (cmp_str(laplace_relation_alias_table[i].surface, surface) == 0) {
            *out_idx = laplace_relation_alias_table[i].canon_idx;
            *out_flip = laplace_relation_alias_table[i].flip;
            return 0;
        }
    }
    return -1;
}

int laplace_relation_resolve_surface(const char* surface, hash128_t* out_type_id,
                                     double* out_rank, laplace_rel_symmetry_t* out_symmetry,
                                     uint8_t* out_flip, hash128_t* out_parent_id) {
    if (!surface || !out_type_id) return -1;
    const char* canon_name = surface;
    uint8_t flip = 0;
    int16_t idx = -1;
    if (alias_lookup(surface, &idx, &flip) == 0) {
        canon_name = laplace_relation_table[idx].canonical;
    } else {
        for (size_t i = 0; i < laplace_relation_table_count; ++i) {
            if (cmp_str(laplace_relation_table[i].canonical, surface) == 0) {
                idx = (int16_t)i;
                break;
            }
        }
    }
    if (idx < 0) {
        int rc = laplace_relation_type_id(surface, out_type_id);
        if (rc < 0) return rc;
        if (out_rank) *out_rank = 0.05;
        if (out_symmetry) *out_symmetry = LAPLACE_REL_SYMMETRY_ASYMMETRIC;
        if (out_flip) *out_flip = 0;
        if (out_parent_id) hash128_zero(out_parent_id);
        return 1;
    }
    const laplace_relation_def_t* def = &laplace_relation_table[idx];
    if (table_entry_type_id((size_t)idx, out_type_id) != 0) return -1;
    if (out_rank) *out_rank = def->rank;
    if (out_symmetry) *out_symmetry = def->symmetry;
    if (out_flip) *out_flip = flip;
    if (out_parent_id) {
        if (def->parent_idx >= 0)
            table_entry_type_id((size_t)def->parent_idx, out_parent_id);
        else
            hash128_zero(out_parent_id);
    }
    return 0;
}

int laplace_relation_lookup(const hash128_t* type_id, const laplace_relation_def_t** out_def) {
    if (!type_id || !out_def) return -1;
    relation_ids_ensure();
    for (size_t i = 0; i < laplace_relation_table_count; ++i) {
        if (hash128_equals(type_id, &k_relation_type_id_cache[i])) {
            *out_def = &laplace_relation_table[i];
            return 0;
        }
    }
    return -1;
}

static int family_contains(int16_t idx, int16_t root_idx) {
    if (idx < 0) return 0;
    if (idx == root_idx) return 1;
    int16_t cur = idx;
    for (int guard = 0; guard < 64; ++guard) {
        const laplace_relation_def_t* d = &laplace_relation_table[cur];
        if (d->family_root_idx == root_idx) return 1;
        if (d->parent_idx < 0) return 0;
        if (d->parent_idx == root_idx) return 1;
        cur = d->parent_idx;
    }
    return 0;
}

int laplace_relation_in_family(const hash128_t* type_id, const char* family_root, int* out) {
    if (!type_id || !family_root || !out) return -1;
    *out = 0;
    int16_t root_idx = -1;
    for (size_t i = 0; i < laplace_relation_table_count; ++i) {
        if (cmp_str(laplace_relation_table[i].canonical, family_root) == 0) {
            root_idx = (int16_t)i;
            break;
        }
    }
    if (root_idx < 0) return -1;
    hash128_t entry_id;
    if (table_entry_type_id((size_t)root_idx, &entry_id) == 0
        && hash128_equals(type_id, &entry_id)) {
        *out = 1;
        return 0;
    }
    for (size_t i = 0; i < laplace_relation_table_count; ++i) {
        if (table_entry_type_id(i, &entry_id) != 0) continue;
        if (hash128_equals(type_id, &entry_id)) {
            *out = family_contains((int16_t)i, root_idx);
            return 0;
        }
    }
    return 1;
}

static void dyn_trim(char* s) {
    size_t n = strlen(s);
    while (n > 0 && (s[n - 1] == ' ' || s[n - 1] == '\t' || s[n - 1] == '\r' || s[n - 1] == '\n'))
        s[--n] = '\0';
    size_t i = 0;
    while (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')
        ++i;
    if (i > 0)
        memmove(s, s + i, strlen(s + i) + 1);
}

static void dyn_lower(char* s) {
    for (; *s; ++s) {
        if (*s >= 'A' && *s <= 'Z')
            *s = (char)(*s + 32);
    }
}

static void dyn_upper(char* s) {
    for (; *s; ++s) {
        if (*s >= 'a' && *s <= 'z')
            *s = (char)(*s - 32);
    }
}

static int dyn_build_prefixed(
    const char* input,
    const char* prefix,
    char sep,
    const char* root_canon,
    int lowercase_input,
    char* out_canon,
    size_t out_canon_sz,
    char* out_parent_canon,
    size_t out_parent_sz) {
    char norm[128];
    size_t i, j;
    if (!input || !prefix || !root_canon || !out_canon || !out_parent_canon)
        return -1;
    if (strlen(input) >= sizeof(norm))
        return -1;
    memcpy(norm, input, strlen(input) + 1);
    dyn_trim(norm);
    if (lowercase_input)
        dyn_lower(norm);
    if (norm[0] == '\0')
        return -1;
    if (snprintf(out_canon, out_canon_sz, "%s", prefix) <= 0)
        return -1;
    j = strlen(out_canon);
    for (i = 0; norm[i] != '\0' && j + 1 < out_canon_sz; ++i) {
        char c = norm[i];
        if (c == sep)
            c = '_';
        out_canon[j++] = c;
    }
    out_canon[j] = '\0';
    dyn_upper(out_canon + strlen(prefix));
    if (sep) {
        const char* colon = strchr(norm, sep);
        if (colon && colon > norm) {
            char parent_body[96];
            size_t plen = (size_t)(colon - norm);
            if (plen >= sizeof(parent_body))
                return -1;
            memcpy(parent_body, norm, plen);
            parent_body[plen] = '\0';
            if (snprintf(out_parent_canon, out_parent_sz, "%s%s", prefix, parent_body) <= 0)
                return -1;
            dyn_upper(out_parent_canon + strlen(prefix));
        } else if (snprintf(out_parent_canon, out_parent_sz, "%s", root_canon) <= 0) {
            return -1;
        }
    } else if (snprintf(out_parent_canon, out_parent_sz, "%s", root_canon) <= 0) {
        return -1;
    }
    return 0;
}

static int dyn_resolve_prefixed(
    const char* input,
    const char* prefix,
    char sep,
    const char* root_canon,
    int lowercase_input,
    double rank_val,
    laplace_rel_symmetry_t symmetry,
    hash128_t* out_type_id,
    double* out_rank,
    laplace_rel_symmetry_t* out_symmetry,
    uint8_t* out_flip,
    hash128_t* out_parent_id) {
    char canon[128], parent_canon[128];
    if (!out_type_id)
        return -1;
    if (dyn_build_prefixed(input, prefix, sep, root_canon, lowercase_input,
                           canon, sizeof(canon), parent_canon, sizeof(parent_canon)) != 0)
        return -1;
    {
        int rc = laplace_relation_type_id(canon, out_type_id);
        if (rc < 0)
            return -1;
    }
    if (out_parent_id) {
        int rc = laplace_relation_type_id(parent_canon, out_parent_id);
        if (rc < 0)
            return -1;
    }
    if (out_rank)
        *out_rank = rank_val;
    if (out_symmetry)
        *out_symmetry = symmetry;
    if (out_flip)
        *out_flip = 0;
    return 0;
}

int laplace_relation_resolve_deprel(
    const char* deprel,
    hash128_t* out_type_id,
    double* out_rank,
    laplace_rel_symmetry_t* out_symmetry,
    uint8_t* out_flip,
    hash128_t* out_parent_id) {
    return dyn_resolve_prefixed(deprel, "DEP_", ':',
                                "DEPENDS_ON", 1, 0.18, LAPLACE_REL_SYMMETRY_ASYMMETRIC,
                                out_type_id, out_rank, out_symmetry, out_flip, out_parent_id);
}

int laplace_relation_resolve_enhanced_deprel(
    const char* deprel,
    hash128_t* out_type_id,
    double* out_rank,
    laplace_rel_symmetry_t* out_symmetry,
    uint8_t* out_flip,
    hash128_t* out_parent_id) {
    return dyn_resolve_prefixed(deprel, "EDEP_", ':',
                                "ENHANCED_DEPENDS_ON", 1, 0.18, LAPLACE_REL_SYMMETRY_ASYMMETRIC,
                                out_type_id, out_rank, out_symmetry, out_flip, out_parent_id);
}

int laplace_relation_resolve_feature(
    const char* feature_name,
    hash128_t* out_type_id,
    double* out_rank,
    laplace_rel_symmetry_t* out_symmetry,
    uint8_t* out_flip,
    hash128_t* out_parent_id) {
    return dyn_resolve_prefixed(feature_name, "FEAT_", '\0',
                                "HAS_FEATURE", 0,
                                0.18, LAPLACE_REL_SYMMETRY_ASYMMETRIC,
                                out_type_id, out_rank, out_symmetry, out_flip, out_parent_id);
}
