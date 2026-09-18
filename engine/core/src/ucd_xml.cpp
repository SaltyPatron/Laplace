#include "laplace/core/ucd_xml.h"
#include "laplace/core/unicode_seed.h"

#include <algorithm>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iterator>
#include <string>
#include <vector>

#include <zlib.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/super_fibonacci.h"
#include "laplace/core/utf8.h"

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

bool starts_with(const uint8_t* buf, size_t len, size_t i, const char* lit) {
    for (size_t k = 0; lit[k]; ++k) {
        if (i + k >= len || buf[i + k] != static_cast<uint8_t>(lit[k])) return false;
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
    out.assign(reinterpret_cast<const char*>(buf) + i, j - i);
    return j;
}

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
        ev.kv.emplace_back(reinterpret_cast<const char*>(buf) + v0, i - v0);
        ++i;
    }
}

bool artifact_read_zip_single_entry(const char* zip_path, std::vector<uint8_t>& out) {
    std::ifstream f(zip_path, std::ios::binary);
    if (!f) return false;
    std::vector<uint8_t> zip((std::istreambuf_iterator<char>(f)),
                             std::istreambuf_iterator<char>());
    if (zip.size() < 22) return false;
    auto read16 = [&](size_t o) -> uint32_t {
        return static_cast<uint32_t>(zip[o]) | (static_cast<uint32_t>(zip[o + 1]) << 8);
    };
    auto read32 = [&](size_t o) -> uint32_t {
        return static_cast<uint32_t>(zip[o])
            | (static_cast<uint32_t>(zip[o + 1]) << 8)
            | (static_cast<uint32_t>(zip[o + 2]) << 16)
            | (static_cast<uint32_t>(zip[o + 3]) << 24);
    };
    size_t eocd = SIZE_MAX;
    size_t floor_offset = zip.size() > 22 + 0xFFFFu ? zip.size() - (22 + 0xFFFFu) : 0;
    for (size_t i = zip.size() - 21; i-- > floor_offset;) {
        if (read32(i) == 0x06054b50u) { eocd = i; break; }
    }
    if (eocd == SIZE_MAX) return false;
    uint32_t entries = read16(eocd + 10);
    uint32_t central_offset = read32(eocd + 16);
    if (entries == 0 || static_cast<size_t>(central_offset) + 46 > zip.size()) return false;
    size_t central = central_offset;
    if (read32(central) != 0x02014b50u) return false;
    uint32_t method = read16(central + 10);
    uint32_t crc = read32(central + 16);
    uint32_t compressed_size = read32(central + 20);
    uint32_t uncompressed_size = read32(central + 24);
    uint32_t local_offset = read32(central + 42);
    if (static_cast<size_t>(local_offset) + 30 > zip.size()
        || read32(local_offset) != 0x04034b50u) return false;
    size_t data = static_cast<size_t>(local_offset) + 30
        + read16(local_offset + 26) + read16(local_offset + 28);
    if (data + compressed_size > zip.size()) return false;
    if (method == 0) {
        if (compressed_size != uncompressed_size) return false;
        out.assign(zip.begin() + static_cast<std::ptrdiff_t>(data),
                   zip.begin() + static_cast<std::ptrdiff_t>(data + compressed_size));
    } else if (method == 8) {
        out.assign(uncompressed_size, 0);
        z_stream stream{};
        if (inflateInit2(&stream, -MAX_WBITS) != Z_OK) return false;
        stream.next_in = zip.data() + data;
        stream.avail_in = compressed_size;
        stream.next_out = out.data();
        stream.avail_out = uncompressed_size;
        int rc = inflate(&stream, Z_FINISH);
        inflateEnd(&stream);
        if (rc != Z_STREAM_END || stream.total_out != uncompressed_size) return false;
    } else {
        return false;
    }
    return crc32(0, out.data(), static_cast<uInt>(out.size())) == crc;
}

bool artifact_read_xml(const char* path, std::vector<uint8_t>& out) {
    size_t n = std::strlen(path);
    if (n > 4 && std::strcmp(path + n - 4, ".zip") == 0)
        return artifact_read_zip_single_entry(path, out);
    std::ifstream f(path, std::ios::binary);
    if (!f) return false;
    out.assign(std::istreambuf_iterator<char>(f), std::istreambuf_iterator<char>());
    return !out.empty();
}

void artifact_validate_start(void*, const char*, const char**) {}
void artifact_validate_end(void*, const char*) {}

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
        while (i < len && buf[i] != '<') ++i;
        if (i >= len) break;
        ++i;
        if (i >= len) return -2;

        if (starts_with(buf, len, i, "!--")) {
            i += 3;
            while (i + 2 < len &&
                   !(buf[i] == '-' && buf[i + 1] == '-' && buf[i + 2] == '>'))
                ++i;
            if (i + 2 >= len) return -2;
            i += 3;
            continue;
        }
        if (buf[i] == '?') {
            ++i;
            while (i + 1 < len && !(buf[i] == '?' && buf[i + 1] == '>')) ++i;
            if (i + 1 >= len) return -2;
            i += 2;
            continue;
        }
        if (starts_with(buf, len, i, "![CDATA[")) {
            i += 8;
            while (i + 2 < len &&
                   !(buf[i] == ']' && buf[i + 1] == ']' && buf[i + 2] == '>'))
                ++i;
            if (i + 2 >= len) return -2;
            i += 3;
            continue;
        }
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

extern "C" int laplace_unicode_seed_validate_ucdxml(const char* ucdxml_path) {
    if (!ucdxml_path) return -1;
    std::vector<uint8_t> document;
    if (!artifact_read_xml(ucdxml_path, document)) return -2;
    return laplace_ucd_xml_parse(
        document.data(), document.size(),
        artifact_validate_start, artifact_validate_end, nullptr) == 0 ? 0 : -2;
}
