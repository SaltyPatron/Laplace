#include "laplace/synthesis/recipe.h"

#include <cctype>
#include <cstddef>
#include <cstring>
#include <map>
#include <string>

namespace {

const char* skip_ws(const char* p, const char* end) {
    while (p < end && std::isspace((unsigned char)*p)) ++p;
    return p;
}

const char* parse_json_string(const char* p, const char* end, std::string& out) {
    if (p >= end || *p != '"') return nullptr;
    ++p;
    out.clear();
    while (p < end && *p != '"') {
        if (*p == '\\' && p + 1 < end) {
            ++p;
            switch (*p) {
                case '"':  out += '"';  break;
                case '\\': out += '\\'; break;
                case '/':  out += '/';  break;
                case 'n':  out += '\n'; break;
                case 'r':  out += '\r'; break;
                case 't':  out += '\t'; break;
                default:   out += *p;   break;
            }
        } else {
            out += *p;
        }
        ++p;
    }
    if (p >= end) return nullptr;
    return p + 1;
}

const char* parse_json_primitive(const char* p, const char* end, std::string& out) {
    const char* start = p;
    while (p < end && *p != ',' && *p != '}' && *p != ']' && !std::isspace((unsigned char)*p))
        ++p;
    out.assign(start, p);
    return p;
}

const char* parse_json_array_first_string(const char* p, const char* end, std::string& out) {
    if (p >= end || *p != '[') return nullptr;
    ++p;
    p = skip_ws(p, end);
    if (p < end && *p == '"') {
        p = parse_json_string(p, end, out);
    }
    int depth = 1;
    while (p && p < end && depth > 0) {
        if (*p == '[') ++depth;
        else if (*p == ']') --depth;
        if (depth > 0) ++p;
    }
    if (p && p < end && *p == ']') ++p;
    return p;
}

}

struct recipe {
    std::map<std::string, std::string> fields;
};

extern "C" recipe_t* recipe_parse(const char* json_text, size_t len) {
    if (!json_text || len == 0) return nullptr;

    const char* p   = json_text;
    const char* end = json_text + len;

    p = skip_ws(p, end);
    if (p >= end || *p != '{') return nullptr;
    ++p;

    auto* r = new recipe();

    while (p < end) {
        p = skip_ws(p, end);
        if (p >= end) break;
        if (*p == '}') break;
        if (*p == ',') { ++p; continue; }

        std::string key;
        p = parse_json_string(p, end, key);
        if (!p) { delete r; return nullptr; }

        p = skip_ws(p, end);
        if (p >= end || *p != ':') { delete r; return nullptr; }
        ++p;
        p = skip_ws(p, end);
        if (p >= end) { delete r; return nullptr; }

        std::string val;
        if (*p == '"') {
            p = parse_json_string(p, end, val);
        } else if (*p == '[') {
            p = parse_json_array_first_string(p, end, val);
        } else if (*p == '{') {
            int depth = 1;
            ++p;
            while (p < end && depth > 0) {
                if (*p == '{') ++depth;
                else if (*p == '}') --depth;
                if (depth > 0) ++p;
            }
            if (p < end) ++p;
            val = "<object>";
        } else {
            p = parse_json_primitive(p, end, val);
        }

        if (!p) { delete r; return nullptr; }
        r->fields[key] = val;
    }

    return r;
}

extern "C" const char* recipe_get_field(const recipe_t* r, const char* field_name) {
    if (!r || !field_name) return nullptr;
    auto it = r->fields.find(field_name);
    if (it == r->fields.end()) return nullptr;
    return it->second.c_str();
}

extern "C" void recipe_free(recipe_t* r) {
    delete r;
}
