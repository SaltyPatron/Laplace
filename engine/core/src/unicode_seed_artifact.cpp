#include "laplace/core/unicode_seed.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
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
#include "laplace/core/ucd_xml.h"
#include "laplace/core/utf8.h"

namespace {

constexpr uint32_t CP_COUNT = LAPLACE_PERFCACHE_RECORD_COUNT;

struct DucetKeys {
    std::vector<uint64_t> key;
    std::vector<uint8_t> explicit_;
    DucetKeys() : key(CP_COUNT, 0), explicit_(CP_COUNT, 0) {}
};

struct ImplicitRange {
    uint32_t first;
    uint32_t last;
    uint32_t base;
};

int parse_ducet(const char* path, DucetKeys& dk) {
    std::ifstream f(path);
    if (!f) return -3;

    std::vector<ImplicitRange> implicits;
    std::string line;
    while (std::getline(f, line)) {
        size_t hash = line.find('#');
        if (hash != std::string::npos) line.resize(hash);
        while (!line.empty() &&
               (line.back() == ' ' || line.back() == '\t' || line.back() == '\r'))
            line.pop_back();
        if (line.empty()) continue;

        if (line[0] == '@') {
            if (line.rfind("@implicitweights", 0) == 0) {
                const char* p = line.c_str() + 16;
                char* end = nullptr;
                uint32_t lo = static_cast<uint32_t>(std::strtoul(p, &end, 16));
                p = end;
                while (*p == '.') ++p;
                uint32_t hi = static_cast<uint32_t>(std::strtoul(p, &end, 16));
                p = end;
                while (*p && *p != ';') ++p;
                if (*p == ';') ++p;
                uint32_t base = static_cast<uint32_t>(std::strtoul(p, &end, 16));
                implicits.push_back({lo, hi, base});
            }
            continue;
        }

        size_t semi = line.find(';');
        if (semi == std::string::npos) continue;
        std::string lhs = line.substr(0, semi);
        const char* p = lhs.c_str();
        char* end = nullptr;
        uint32_t cps[4]{};
        int ncp = 0;
        while (*p && ncp < 4) {
            while (*p == ' ' || *p == '\t') ++p;
            if (!*p) break;
            uint32_t value = static_cast<uint32_t>(std::strtoul(p, &end, 16));
            if (end == p) break;
            cps[ncp++] = value;
            p = end;
        }
        // The tier-0 geometry is a codepoint ordering. Multi-codepoint DUCET
        // contractions remain semantic UCA data but do not identify one codepoint.
        if (ncp != 1 || cps[0] >= CP_COUNT) continue;

        std::string rhs = line.substr(semi + 1);
        size_t bracket = rhs.find('[');
        if (bracket == std::string::npos) continue;
        const char* q = rhs.c_str() + bracket + 1;
        if (*q == '.' || *q == '*') ++q;
        uint32_t primary = static_cast<uint32_t>(std::strtoul(q, &end, 16));
        q = end;
        if (*q == '.') ++q;
        uint32_t secondary = static_cast<uint32_t>(std::strtoul(q, &end, 16));
        q = end;
        if (*q == '.') ++q;
        uint32_t tertiary = static_cast<uint32_t>(std::strtoul(q, &end, 16));

        uint32_t cp = cps[0];
        dk.key[cp] = (static_cast<uint64_t>(primary) << 48)
                   | (static_cast<uint64_t>(secondary) << 32)
                   | (static_cast<uint64_t>(tertiary) << 16);
        dk.explicit_[cp] = 1;
    }

    auto implicit_base = [&](uint32_t cp) -> uint32_t {
        for (const auto& range : implicits)
            if (cp >= range.first && cp <= range.last) return range.base;
        if ((cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0xF900 && cp <= 0xFAFF))
            return 0xFB40;
        if ((cp >= 0x3400 && cp <= 0x4DBF) || (cp >= 0x20000 && cp <= 0x3FFFF))
            return 0xFB80;
        return 0xFBC0;
    };

    // UCA gives Hangul syllables the leading-jamo primary where no explicit
    // single-codepoint row exists. Preserve the legacy seed's exact law.
    constexpr uint32_t SBase = 0xAC00;
    constexpr uint32_t LBase = 0x1100;
    constexpr uint32_t VCount = 21;
    constexpr uint32_t TCount = 28;
    constexpr uint32_t NCount = VCount * TCount;
    constexpr uint32_t SCount = 19 * NCount;
    for (uint32_t syllable = SBase; syllable < SBase + SCount; ++syllable) {
        if (dk.explicit_[syllable]) continue;
        uint32_t leading = LBase + (syllable - SBase) / NCount;
        if (!dk.explicit_[leading]) continue;
        dk.key[syllable] = dk.key[leading];
        dk.explicit_[syllable] = 1;
    }

    for (uint32_t cp = 0; cp < CP_COUNT; ++cp) {
        if (dk.explicit_[cp]) continue;
        uint32_t base = implicit_base(cp);
        uint32_t aaaa = base + (cp >> 15);
        uint32_t bbbb = (cp & 0x7FFF) | 0x8000;
        dk.key[cp] = (static_cast<uint64_t>(aaaa) << 48)
                   | (static_cast<uint64_t>(bbbb) << 16);
    }
    return 0;
}

bool read_zip_single_entry(const char* zip_path, std::vector<uint8_t>& out) {
    std::ifstream f(zip_path, std::ios::binary);
    if (!f) return false;
    std::vector<uint8_t> zip((std::istreambuf_iterator<char>(f)),
                             std::istreambuf_iterator<char>());
    if (zip.size() < 22) return false;

    auto read16 = [&](size_t offset) -> uint32_t {
        return static_cast<uint32_t>(zip[offset])
             | (static_cast<uint32_t>(zip[offset + 1]) << 8);
    };
    auto read32 = [&](size_t offset) -> uint32_t {
        return static_cast<uint32_t>(zip[offset])
             | (static_cast<uint32_t>(zip[offset + 1]) << 8)
             | (static_cast<uint32_t>(zip[offset + 2]) << 16)
             | (static_cast<uint32_t>(zip[offset + 3]) << 24);
    };

    size_t eocd = SIZE_MAX;
    size_t floor_offset = zip.size() > 22 + 0xFFFFu
        ? zip.size() - (22 + 0xFFFFu)
        : 0;
    for (size_t i = zip.size() - 21; i-- > floor_offset;) {
        if (read32(i) == 0x06054b50u) {
            eocd = i;
            break;
        }
    }
    if (eocd == SIZE_MAX) return false;

    uint32_t entries = read16(eocd + 10);
    uint32_t central_offset = read32(eocd + 16);
    if (entries == 0 || static_cast<size_t>(central_offset) + 46 > zip.size())
        return false;

    size_t central = central_offset;
    if (read32(central) != 0x02014b50u) return false;
    uint32_t method = read16(central + 10);
    uint32_t crc = read32(central + 16);
    uint32_t compressed_size = read32(central + 20);
    uint32_t uncompressed_size = read32(central + 24);
    uint32_t local_offset = read32(central + 42);
    if (static_cast<size_t>(local_offset) + 30 > zip.size()
        || read32(local_offset) != 0x04034b50u)
        return false;

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

bool read_xml_document(const char* path, std::vector<uint8_t>& out) {
    size_t length = std::strlen(path);
    bool zip = length > 4 && std::strcmp(path + length - 4, ".zip") == 0;
    if (zip) return read_zip_single_entry(path, out);

    std::ifstream f(path, std::ios::binary);
    if (!f) return false;
    out.assign(std::istreambuf_iterator<char>(f), std::istreambuf_iterator<char>());
    return !out.empty();
}

void validate_start(void*, const char*, const char**) {}
void validate_end(void*, const char*) {}

}  // namespace

extern "C" int laplace_unicode_seed_compute_ducet(
    const char* ducet_path,
    laplace_perfcache_record_t* out_records,
    size_t out_capacity) {
    if (!ducet_path || !out_records || out_capacity < CP_COUNT) return -1;

    DucetKeys ducet;
    int rc = parse_ducet(ducet_path, ducet);
    if (rc != 0) return rc;

    std::vector<uint32_t> order(CP_COUNT);
    for (uint32_t cp = 0; cp < CP_COUNT; ++cp) order[cp] = cp;
    std::sort(order.begin(), order.end(), [&](uint32_t left, uint32_t right) {
        if (ducet.key[left] != ducet.key[right]) return ducet.key[left] < ducet.key[right];
        return left < right;
    });

    std::vector<uint32_t> rank(CP_COUNT);
    for (uint32_t ordinal = 0; ordinal < CP_COUNT; ++ordinal)
        rank[order[ordinal]] = ordinal;

    std::vector<double> points(4ull * CP_COUNT);
    super_fibonacci(CP_COUNT, points.data());

    for (uint32_t cp = 0; cp < CP_COUNT; ++cp) {
        uint32_t ordinal = rank[cp];
        double coord[4] = {
            points[4ull * ordinal + 0], points[4ull * ordinal + 1],
            points[4ull * ordinal + 2], points[4ull * ordinal + 3]};
        hilbert128_t hilbert;
        hilbert4d_encode(coord, &hilbert);
        uint8_t utf8[4];
        size_t utf8_bytes = laplace_utf8_encode(cp, utf8);
        hash128_t hash;
        hash128_blake3(utf8, utf8_bytes, &hash);

        laplace_perfcache_record_t& record = out_records[cp];
        record.codepoint = cp;
        record.uca_order = ordinal;
        record.coord[0] = coord[0];
        record.coord[1] = coord[1];
        record.coord[2] = coord[2];
        record.coord[3] = coord[3];
        record.hilbert = hilbert;
        record.hash = hash;
        // This API is deliberately one-artifact: UCD XML owns segmentation flags.
        // Database tier-0 consumes only identity + UCA geometry from this result.
        record.flags = 0;
        record._pad = 0;
    }
    return 0;
}

extern "C" int laplace_unicode_seed_validate_ucdxml(const char* ucdxml_path) {
    if (!ucdxml_path) return -1;
    std::vector<uint8_t> document;
    if (!read_xml_document(ucdxml_path, document)) return -2;
    return laplace_ucd_xml_parse(
        document.data(), document.size(), validate_start, validate_end, nullptr) == 0
        ? 0
        : -2;
}
