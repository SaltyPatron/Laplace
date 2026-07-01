#include "laplace/core/etl_ingest.h"

#include <stdlib.h>
#include <string.h>

#include "laplace/core/attestation_engine.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/grammar_decomposer.h"

typedef struct {
    const char* rel_utf8;
    const char* type_name;
} cn_rel_t;

static const cn_rel_t k_cn_rels[] = {
    {"RelatedTo", "RELATED_TO"},           {"FormOf", "FORM_OF"},
    {"IsA", "IS_A"},                       {"PartOf", "IS_PART_OF"},
    {"HasA", "HAS_A"},                     {"UsedFor", "USED_FOR"},
    {"CapableOf", "CAPABLE_OF"},           {"AtLocation", "AT_LOCATION"},
    {"Causes", "CAUSES"},                  {"HasSubevent", "HAS_SUBEVENT"},
    {"HasFirstSubevent", "HAS_FIRST_SUBEVENT"},
    {"HasLastSubevent", "HAS_LAST_SUBEVENT"},
    {"HasPrerequisite", "HAS_PREREQUISITE"},
    {"HasProperty", "HAS_PROPERTY"},       {"MotivatedByGoal", "MOTIVATED_BY_GOAL"},
    {"ObstructedBy", "OBSTRUCTED_BY"},     {"Desires", "DESIRES"},
    {"CreatedBy", "CREATED_BY"},           {"Synonym", "IS_SYNONYM_OF"},
    {"Antonym", "IS_ANTONYM_OF"},          {"DistinctFrom", "DISTINCT_FROM"},
    {"DerivedFrom", "DERIVED_FROM"},       {"SymbolOf", "SYMBOL_OF"},
    {"DefinedAs", "DEFINED_AS"},           {"MannerOf", "MANNER_OF"},
    {"LocatedNear", "LOCATED_NEAR"},       {"HasContext", "HAS_CONTEXT"},
    {"SimilarTo", "SIMILAR_TO"},
    {"EtymologicallyRelatedTo", "ETYMOLOGICALLY_RELATED_TO"},
    {"EtymologicallyDerivedFrom", "ETYMOLOGICALLY_DERIVED_FROM"},
    {"CausesDesire", "CAUSES_DESIRE"},     {"MadeOf", "MADE_UP_OF"},
    {"ReceivesAction", "RECEIVES_ACTION"}, {"InstanceOf", "IS_INSTANCE_OF"},
    {"NotDesires", "NOT_DESIRES"},         {"NotUsedFor", "NOT_USED_FOR"},
    {"NotCapableOf", "NOT_CAPABLE_OF"},    {"NotHasProperty", "NOT_HAS_PROPERTY"},
    {"Entails", "ENTAILS"},
};

static int trim_field(const uint8_t* utf8, size_t len, const uint8_t** out, size_t* out_len) {
    size_t s = 0;
    while (s < len && utf8[s] == ' ') ++s;
    size_t e = len;
    while (e > s && utf8[e - 1] == ' ') --e;
    *out = utf8 + s;
    *out_len = e - s;
    return (e > s) ? 0 : -1;
}

static int field_spans(const laplace_ast_t* ast, uint32_t* starts, uint32_t* ends, size_t cap,
                       size_t* out_n) {
    *out_n = 0;
    size_t n = laplace_ast_node_count(ast);
    for (size_t i = 0; i < n && *out_n < cap; ++i) {
        laplace_ast_node_t nd;
        if (laplace_ast_get_node(ast, i, &nd) != 0) continue;
        const char* tn = laplace_ast_type_name(ast, nd.type_id);
        if (!tn || strcmp(tn, "field") != 0) continue;
        starts[*out_n] = nd.start_byte;
        ends[*out_n] = nd.end_byte;
        ++(*out_n);
    }
    return 0;
}

static int parse_concept_uri(const uint8_t* uri, size_t uri_len, const uint8_t** lang,
                             size_t* lang_len, const uint8_t** term, size_t* term_len) {
    *lang = NULL;
    *lang_len = 0;
    *term = NULL;
    *term_len = 0;
    if (uri_len < 5 || uri[0] != '/' || uri[1] != 'c' || uri[2] != '/') return -1;
    size_t i = 3;
    size_t lang_start = i;
    while (i < uri_len && uri[i] != '/') ++i;
    if (i == lang_start || i >= uri_len) return -1;
    *lang = uri + lang_start;
    *lang_len = i - lang_start;
    ++i;
    size_t term_start = i;
    size_t term_end = term_start;
    while (term_end < uri_len && uri[term_end] != '/') ++term_end;
    if (term_end <= term_start) return -1;
    *term = uri + term_start;
    *term_len = term_end - term_start;
    return 0;
}

static int resolve_relation(const uint8_t* rel, size_t rel_len, const char** out_type) {
    if (rel_len < 4 || rel[0] != '/' || rel[1] != 'r' || rel[2] != '/') return -1;
    const uint8_t* name = rel + 3;
    size_t name_len = rel_len - 3;
    if (name_len >= 8 && memcmp(name, "dbpedia/", 8) == 0) return -1;
    if (name_len == 11 && memcmp(name, "ExternalURL", 11) == 0) return -1;
    char buf[64];
    if (name_len >= sizeof(buf)) return -1;
    memcpy(buf, name, name_len);
    buf[name_len] = '\0';
    for (size_t i = 0; i < sizeof(k_cn_rels) / sizeof(k_cn_rels[0]); ++i) {
        if (strcmp(buf, k_cn_rels[i].rel_utf8) == 0) {
            *out_type = k_cn_rels[i].type_name;
            return 0;
        }
    }
    return -1;
}

static double parse_weight_json(const uint8_t* json, size_t json_len) {
    if (json_len == 0) return 1.0;
    const char* needle = "\"weight\"";
    const size_t needle_len = 8;
    for (size_t i = 0; i + needle_len < json_len; ++i) {
        if (memcmp(json + i, needle, needle_len) != 0) continue;
        size_t j = i + needle_len;
        while (j < json_len && (json[j] == ' ' || json[j] == ':' || json[j] == '\t')) ++j;
        if (j >= json_len) return 1.0;
        char* end = NULL;
        char tmp[64];
        size_t copy = json_len - j;
        if (copy >= sizeof(tmp)) copy = sizeof(tmp) - 1;
        memcpy(tmp, json + j, copy);
        tmp[copy] = '\0';
        double v = strtod(tmp, &end);
        if (end != tmp) return v;
        return 1.0;
    }
    return 1.0;
}

static int extract_assertion(const uint8_t* line, size_t line_len, const laplace_ast_t* ast,
                            const uint8_t** rel, size_t* rel_len, const uint8_t** start_uri,
                            size_t* start_len, const uint8_t** end_uri, size_t* end_len,
                            const uint8_t** meta, size_t* meta_len) {
    uint32_t starts[8], ends[8];
    size_t nf = 0;
    field_spans(ast, starts, ends, 8, &nf);
    if (nf < 5) return -1;
    if (trim_field(line + starts[1], ends[1] - starts[1], rel, rel_len) != 0) return -1;
    if (trim_field(line + starts[2], ends[2] - starts[2], start_uri, start_len) != 0) return -1;
    if (trim_field(line + starts[3], ends[3] - starts[3], end_uri, end_len) != 0) return -1;
    if (trim_field(line + starts[4], ends[4] - starts[4], meta, meta_len) != 0) *meta_len = 0;
    return 0;
}

static int attest_triple(intent_stage_t* stage, const laplace_etl_config_t* cfg,
                         const hash128_t* start_id, const hash128_t* end_id,
                         const char* type_name, double weight) {
    laplace_attestation_staged_t staged;
    int rc = laplace_attestation_categorical_scored_build(
        type_name, start_id, end_id, 0, &cfg->source_id, NULL, 1, cfg->trust_weight, weight, 1.0,
        1, cfg->now_unix_us, &staged);
    if (rc != 0) return rc;
    hash128_t* obj_ptr = staged.object_is_null ? NULL : (hash128_t*)&staged.object_id;
    hash128_t* ctx_ptr = staged.context_is_null ? NULL : (hash128_t*)&staged.context_id;
    return intent_stage_add_attestation(
        stage, &staged.id, &staged.subject_id, &staged.type_id, obj_ptr, &staged.source_id,
        ctx_ptr, staged.outcome, staged.last_observed_at_unix_us, staged.observation_count, NULL);
}

int etl_witness_conceptnet_row(intent_stage_t* stage, const laplace_etl_config_t* cfg,
                               const uint8_t* line, size_t line_len, const laplace_ast_t* ast) {
    const uint8_t *rel, *start_uri, *end_uri, *meta;
    size_t rel_len, start_len, end_len, meta_len;
    if (extract_assertion(line, line_len, ast, &rel, &rel_len, &start_uri, &start_len, &end_uri,
                          &end_len, &meta, &meta_len) != 0)
        return 0;

    const char* type_name = NULL;
    if (resolve_relation(rel, rel_len, &type_name) != 0) return 0;

    const uint8_t *slang, *sterm, *elang, *eterm;
    size_t slang_len, sterm_len, elang_len, eterm_len;
    if (parse_concept_uri(start_uri, start_len, &slang, &slang_len, &sterm, &sterm_len) != 0)
        return 0;
    if (parse_concept_uri(end_uri, end_len, &elang, &elang_len, &eterm, &eterm_len) != 0) return 0;

    hash128_t start_id, end_id;
    if (content_witness_add_underscored(stage, sterm, sterm_len, &cfg->source_id, &start_id) != 0)
        return -1;
    if (content_witness_add_underscored(stage, eterm, eterm_len, &cfg->source_id, &end_id) != 0)
        return -1;

    double weight = parse_weight_json(meta, meta_len);
    return attest_triple(stage, cfg, &start_id, &end_id, type_name, weight);
}

int etl_witness_conceptnet_trunk_skip(intent_stage_t* stage, const laplace_etl_config_t* cfg,
                                      const uint8_t* line, size_t line_len, const laplace_ast_t* ast,
                                      laplace_etl_exist_probe_fn probe, void* probe_ctx) {
    if (!probe) return 0;
    const uint8_t *rel, *start_uri, *end_uri, *meta;
    size_t rel_len, start_len, end_len, meta_len;
    if (extract_assertion(line, line_len, ast, &rel, &rel_len, &start_uri, &start_len, &end_uri,
                          &end_len, &meta, &meta_len) != 0)
        return 0;

    const char* type_name = NULL;
    if (resolve_relation(rel, rel_len, &type_name) != 0) return 0;

    const uint8_t *sterm, *eterm;
    size_t slang_len, sterm_len, elang_len, eterm_len;
    const uint8_t *slang, *elang;
    if (parse_concept_uri(start_uri, start_len, &slang, &slang_len, &sterm, &sterm_len) != 0)
        return 0;
    if (parse_concept_uri(end_uri, end_len, &elang, &elang_len, &eterm, &eterm_len) != 0) return 0;

    hash128_t start_id, end_id;
    if (content_witness_root_id_underscored(sterm, sterm_len, &start_id) != 0) return 0;
    if (content_witness_root_id_underscored(eterm, eterm_len, &end_id) != 0) return 0;

    hash128_t ids[2] = {start_id, end_id};
    uint8_t bm[1] = {0};
    if (probe(probe_ctx, ids, NULL, 2, bm, 2) != 0) return 0;
    if ((bm[0] & 0x03) != 0x03) return 0;

    double weight = parse_weight_json(meta, meta_len);
    return attest_triple(stage, cfg, &start_id, &end_id, type_name, weight) == 0 ? 1 : -1;
}
