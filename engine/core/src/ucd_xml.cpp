#include "laplace/core/ucd_xml.h"

#include <cstring>
#include <string>
#include <vector>

#include "tree_sitter/api.h"

#include "laplace/core/grammar_registry.h"

namespace {

std::string node_text(TSNode n, const uint8_t* buf) {
    uint32_t a = ts_node_start_byte(n), b = ts_node_end_byte(n);
    return std::string((const char*)buf + a, b - a);
}

/* AttValue's span includes its quote delimiters; the value is what's inside. */
std::string attvalue_text(TSNode n, const uint8_t* buf) {
    uint32_t a = ts_node_start_byte(n), b = ts_node_end_byte(n);
    if (b - a >= 2 && (buf[a] == '"' || buf[a] == '\'') && buf[b - 1] == buf[a]) {
        a += 1; b -= 1;
    }
    return std::string((const char*)buf + a, b - a);
}

struct TagEvent {
    std::string name;
    std::vector<std::string> kv;      /* n0,v0,n1,v1,... */
    std::vector<const char*> attrs;   /* pointers into kv + trailing NULL */

    void load(TSNode tag, const uint8_t* buf) {
        name.clear(); kv.clear(); attrs.clear();
        uint32_t n = ts_node_named_child_count(tag);
        for (uint32_t i = 0; i < n; ++i) {
            TSNode c = ts_node_named_child(tag, i);
            const char* t = ts_node_type(c);
            if (std::strcmp(t, "Name") == 0 && name.empty()) {
                name = node_text(c, buf);
            } else if (std::strcmp(t, "Attribute") == 0) {
                std::string an, av;
                uint32_t m = ts_node_named_child_count(c);
                for (uint32_t j = 0; j < m; ++j) {
                    TSNode g = ts_node_named_child(c, j);
                    const char* gt = ts_node_type(g);
                    if (std::strcmp(gt, "Name") == 0) an = node_text(g, buf);
                    else if (std::strcmp(gt, "AttValue") == 0) av = attvalue_text(g, buf);
                }
                kv.push_back(std::move(an));
                kv.push_back(std::move(av));
            }
        }
        for (const auto& s : kv) attrs.push_back(s.c_str());
        attrs.push_back(nullptr);
    }

    /* SAX2 passes NULL when the element has no attributes. */
    const char** attr_array() { return kv.empty() ? nullptr : attrs.data(); }
};

}  // namespace

extern "C" int laplace_ucd_xml_parse(const uint8_t* buf, size_t len,
                                     laplace_ucd_xml_start_cb on_start,
                                     laplace_ucd_xml_end_cb on_end,
                                     void* user) {
    if (!buf || !on_start || !on_end) return -1;
    const TSLanguage* lang = laplace_grammar_lookup_by_id("xml");
    if (!lang) return -1;

    TSParser* parser = ts_parser_new();
    if (!parser) return -1;
    if (!ts_parser_set_language(parser, lang)) { ts_parser_delete(parser); return -1; }

    TSTree* tree = ts_parser_parse_string(parser, nullptr, (const char*)buf, (uint32_t)len);
    ts_parser_delete(parser);
    if (!tree) return -2;

    TSNode root = ts_tree_root_node(tree);
    if (ts_node_has_error(root)) { ts_tree_delete(tree); return -2; }

    /* Iterative DFS firing start/end at STag/EmptyElemTag/ETag reproduces SAX
     * event order exactly; tag internals are never descended into. */
    TSTreeCursor cur = ts_tree_cursor_new(root);
    TagEvent ev;
    for (;;) {
        TSNode n = ts_tree_cursor_current_node(&cur);
        const char* t = ts_node_type(n);
        bool descend = true;
        if (std::strcmp(t, "STag") == 0) {
            ev.load(n, buf);
            on_start(user, ev.name.c_str(), ev.attr_array());
            descend = false;
        } else if (std::strcmp(t, "EmptyElemTag") == 0) {
            ev.load(n, buf);
            on_start(user, ev.name.c_str(), ev.attr_array());
            on_end(user, ev.name.c_str());
            descend = false;
        } else if (std::strcmp(t, "ETag") == 0) {
            ev.load(n, buf);
            on_end(user, ev.name.c_str());
            descend = false;
        }
        if (descend && ts_tree_cursor_goto_first_child(&cur)) continue;
        bool at_root = false;
        while (!ts_tree_cursor_goto_next_sibling(&cur)) {
            if (!ts_tree_cursor_goto_parent(&cur)) { at_root = true; break; }
        }
        if (at_root) break;
    }
    ts_tree_cursor_delete(&cur);
    ts_tree_delete(tree);
    return 0;
}
