#include "laplace/core/ucd_xml.h"

#include <cstring>
#include <string>
#include <vector>

/* UCD flat XML is a property TABLE — millions of shallow elements with fat
 * attribute lists — not a nested container. tree-sitter's full AST on the
 * 67MB nounihan flat file peaks at multiple GiB and returns NULL / ERROR
 * under ordinary build memory pressure (the "rc=-2 often OOM" failure).
 *
 * Tree-sitter remains the unpacker for nested containers (code, JSON, …).
 * This path keeps the same SAX2 callback shape and fires the same events;
 * it just does not materialize an AST. */

namespace {

struct TagEvent {
    std::string name;
    std::vector<std::string> kv;
    std::vector<const char*> attrs;

    void clear() {
        name.clear();
        kv.clear();
        attrs.clear();
    }

    const char** attr_array() {
        attrs.clear();
        if (kv.empty()) return nullptr;
        for (const auto& s : kv) attrs.push_back(s.c_str());
        attrs.push_back(nullptr);
        return attrs.data();
    }
};

/* True if buf[i..] starts with lit (byte compare). */
bool starts_with(const uint8_t* buf, size_t len, size_t i, const char* lit) {
    for (size_t k = 0; lit[k]; ++k) {
        if (i + k >= len || buf[i + k] != (uint8_t)lit[k]) return false;
    }
    return true;
}

size_t skip_ws(const uint8_t* buf, size_t len, size_t i) {
    while (i < len) {
        uint8_t c = buf[i];
        if (c == ' ' || c == '\t' || c == '\n' || c == '\r') ++i;
        else break;
    }
    return i;
}

/* NameStartChar / NameChar subset sufficient for UCD element + attribute names. */
bool is_name_start(uint8_t c) {
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || c == ':';
}
bool is_name_char(uint8_t c) {
    return is_name_start(c) || (c >= '0' && c <= '9') || c == '-' || c == '.';
}

size_t read_name(const uint8_t* buf, size_t len, size_t i, std::string& out) {
    out.clear();
    if (i >= len || !is_name_start(buf[i])) return i;
    size_t j = i + 1;
    while (j < len && is_name_char(buf[j])) ++j;
    out.assign((const char*)buf + i, j - i);
    return j;
}

/* Parse attributes from i up to the tag-closer (`>` or `/>`). Leaves i on the closer. */
int read_attrs(const uint8_t* buf, size_t len, size_t& i, TagEvent& ev) {
    for (;;) {
        i = skip_ws(buf, len, i);
        if (i >= len) return -2;
        if (buf[i] == '>' || (buf[i] == '/' && i + 1 < len && buf[i + 1] == '>'))
            return 0;
        std::string an;
        size_t j = read_name(buf, len, i, an);
        if (j == i || an.empty()) return -2;
        i = skip_ws(buf, len, j);
        if (i >= len || buf[i] != '=') return -2;
        i = skip_ws(buf, len, i + 1);
        if (i >= len || (buf[i] != '"' && buf[i] != '\'')) return -2;
        uint8_t q = buf[i++];
        size_t v0 = i;
        while (i < len && buf[i] != q) ++i;
        if (i >= len) return -2;
        ev.kv.push_back(std::move(an));
        ev.kv.emplace_back((const char*)buf + v0, i - v0);
        ++i; /* closing quote */
    }
}

}  // namespace

extern "C" int laplace_ucd_xml_parse(const uint8_t* buf, size_t len,
                                     laplace_ucd_xml_start_cb on_start,
                                     laplace_ucd_xml_end_cb on_end,
                                     void* user) {
    if (!buf || !on_start || !on_end) return -1;
    if (len == 0) return -2;

    TagEvent ev;
    size_t i = 0;
    while (i < len) {
        /* Advance to next '<'. */
        while (i < len && buf[i] != '<') ++i;
        if (i >= len) break;
        ++i;
        if (i >= len) return -2;

        /* Comment */
        if (starts_with(buf, len, i, "!--")) {
            i += 3;
            while (i + 2 < len &&
                   !(buf[i] == '-' && buf[i + 1] == '-' && buf[i + 2] == '>'))
                ++i;
            if (i + 2 >= len) return -2;
            i += 3;
            continue;
        }
        /* PI / XML decl */
        if (buf[i] == '?') {
            ++i;
            while (i + 1 < len && !(buf[i] == '?' && buf[i + 1] == '>')) ++i;
            if (i + 1 >= len) return -2;
            i += 2;
            continue;
        }
        /* CDATA — UCD flat has none, but reject cleanly if present. */
        if (starts_with(buf, len, i, "![CDATA[")) {
            i += 8;
            while (i + 2 < len &&
                   !(buf[i] == ']' && buf[i + 1] == ']' && buf[i + 2] == '>'))
                ++i;
            if (i + 2 >= len) return -2;
            i += 3;
            continue;
        }
        /* End tag */
        if (buf[i] == '/') {
            ++i;
            i = skip_ws(buf, len, i);
            ev.clear();
            size_t j = read_name(buf, len, i, ev.name);
            if (j == i || ev.name.empty()) return -2;
            i = skip_ws(buf, len, j);
            if (i >= len || buf[i] != '>') return -2;
            ++i;
            on_end(user, ev.name.c_str());
            continue;
        }
        /* Start / empty-element tag */
        ev.clear();
        size_t j = read_name(buf, len, i, ev.name);
        if (j == i || ev.name.empty()) return -2;
        i = j;
        if (read_attrs(buf, len, i, ev) != 0) return -2;
        if (i >= len) return -2;
        bool empty = false;
        if (buf[i] == '/') {
            empty = true;
            ++i;
            if (i >= len || buf[i] != '>') return -2;
        } else if (buf[i] != '>') {
            return -2;
        }
        ++i;
        on_start(user, ev.name.c_str(), ev.attr_array());
        if (empty) on_end(user, ev.name.c_str());
    }
    return 0;
}
