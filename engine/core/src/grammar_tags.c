#include "laplace/core/grammar_tags.h"

#include <stdlib.h>
#include <string.h>

#include "tree_sitter/api.h"



static uint16_t tag_type_of(const char* name, uint32_t len)
{
    if (len == 4 && strncmp(name, "name", 4) == 0) return LAPLACE_TAG_NAME;

    if (len > 11 && strncmp(name, "definition.", 11) == 0) {
        const char* s = name + 11;
        if (strncmp(s, "function", 8) == 0 || strncmp(s, "method", 6) == 0)
            return LAPLACE_TAG_DEF_FUNCTION;
        if (strncmp(s, "class", 5) == 0 || strncmp(s, "type", 4) == 0 ||
            strncmp(s, "module", 6) == 0 || strncmp(s, "interface", 9) == 0 ||
            strncmp(s, "struct", 6) == 0 || strncmp(s, "enum", 4) == 0 ||
            strncmp(s, "trait", 5) == 0)
            return LAPLACE_TAG_DEF_TYPE;
        return LAPLACE_TAG_DEF_VAR;  
    }

    if (len > 10 && strncmp(name, "reference.", 10) == 0) {
        const char* s = name + 10;
        if (strncmp(s, "call", 4) == 0) return LAPLACE_TAG_REF_CALL;
        return LAPLACE_TAG_REF_TYPE;
    }

    return LAPLACE_TAG_OTHER;
}

int laplace_grammar_tags_run(const TSLanguage* lang,
                             const char* tags_scm, size_t tags_len,
                             const uint8_t* utf8, size_t len,
                             laplace_tag_t** out_tags, size_t* out_n)
{
    if (!lang || !tags_scm || !utf8 || !out_tags || !out_n) return -1;
    *out_tags = NULL;
    *out_n = 0;

    uint32_t err_off = 0;
    TSQueryError err_type = TSQueryErrorNone;
    TSQuery* query = ts_query_new(lang, tags_scm, (uint32_t)tags_len, &err_off, &err_type);
    if (!query) return -2;

    TSParser* parser = ts_parser_new();
    if (!parser || !ts_parser_set_language(parser, lang)) {
        if (parser) ts_parser_delete(parser);
        ts_query_delete(query);
        return -3;
    }

    TSTree* tree = ts_parser_parse_string(parser, NULL, (const char*)utf8, (uint32_t)len);
    if (!tree) {
        ts_parser_delete(parser);
        ts_query_delete(query);
        return -3;
    }

    TSQueryCursor* cursor = ts_query_cursor_new();
    ts_query_cursor_exec(cursor, query, ts_tree_root_node(tree));

    size_t cap = 64, n = 0;
    laplace_tag_t* tags = (laplace_tag_t*)malloc(cap * sizeof(*tags));
    int rc = 0;
    if (!tags) { rc = -3; goto done; }

    TSQueryMatch match;
    uint32_t match_id = 0;
    while (ts_query_cursor_next_match(cursor, &match)) {
        for (uint16_t ci = 0; ci < match.capture_count; ++ci) {
            TSQueryCapture qc = match.captures[ci];
            uint32_t name_len = 0;
            const char* cname = ts_query_capture_name_for_id(query, qc.index, &name_len);

            if (n >= cap) {
                size_t ncap = cap * 2;
                laplace_tag_t* nt = (laplace_tag_t*)realloc(tags, ncap * sizeof(*tags));
                if (!nt) { free(tags); tags = NULL; rc = -3; goto done; }
                tags = nt;
                cap = ncap;
            }

            tags[n].match_id     = match_id;
            tags[n].capture_type = tag_type_of(cname, name_len);
            tags[n]._pad         = 0;
            tags[n].start_byte   = ts_node_start_byte(qc.node);
            tags[n].end_byte     = ts_node_end_byte(qc.node);
            ++n;
        }
        ++match_id;
    }

done:
    ts_query_cursor_delete(cursor);
    ts_tree_delete(tree);
    ts_parser_delete(parser);
    ts_query_delete(query);

    if (rc != 0) return rc;
    *out_tags = tags;
    *out_n = n;
    return 0;
}

void laplace_grammar_tags_free(laplace_tag_t* tags)
{
    free(tags);
}
