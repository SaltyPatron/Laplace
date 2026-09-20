#include "laplace/core/unicode_seed.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iterator>
#include <string>
#include <unordered_map>
#include <vector>

#include <zlib.h>

#include "laplace/core/hash128.h"
#include "laplace/core/ucd_xml.h"
#include "laplace/core/utf8.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/super_fibonacci.h"
#include "laplace/core/perfcache_format.h"
#include "laplace/core/ucd_property_values.h"

namespace {

constexpr uint32_t CP_COUNT = LAPLACE_PERFCACHE_RECORD_COUNT;

uint8_t map_gb(const char* s) {
    static const std::unordered_map<std::string, uint8_t> m = {
        {"XX",LAPLACE_GB_OTHER},{"CR",LAPLACE_GB_CR},{"LF",LAPLACE_GB_LF},
        {"CN",LAPLACE_GB_CONTROL},{"EX",LAPLACE_GB_EXTEND},{"ZWJ",LAPLACE_GB_ZWJ},
        {"RI",LAPLACE_GB_REGIONAL_INDICATOR},{"PP",LAPLACE_GB_PREPEND},
        {"SM",LAPLACE_GB_SPACINGMARK},{"L",LAPLACE_GB_L},{"V",LAPLACE_GB_V},
        {"T",LAPLACE_GB_T},{"LV",LAPLACE_GB_LV},{"LVT",LAPLACE_GB_LVT},
    };
    auto it = m.find(s); return it == m.end() ? (uint8_t)LAPLACE_GB_OTHER : it->second;
}
uint8_t map_wb(const char* s) {
    static const std::unordered_map<std::string, uint8_t> m = {
        {"XX",LAPLACE_WB_OTHER},{"CR",LAPLACE_WB_CR},{"LF",LAPLACE_WB_LF},
        {"NL",LAPLACE_WB_NEWLINE},{"Extend",LAPLACE_WB_EXTEND},{"ZWJ",LAPLACE_WB_ZWJ},
        {"RI",LAPLACE_WB_REGIONAL_INDICATOR},{"FO",LAPLACE_WB_FORMAT},
        {"KA",LAPLACE_WB_KATAKANA},{"HL",LAPLACE_WB_HEBREW_LETTER},{"LE",LAPLACE_WB_ALETTER},
        {"SQ",LAPLACE_WB_SINGLE_QUOTE},{"DQ",LAPLACE_WB_DOUBLE_QUOTE},
        {"MB",LAPLACE_WB_MIDNUMLET},{"ML",LAPLACE_WB_MIDLETTER},{"MN",LAPLACE_WB_MIDNUM},
        {"NU",LAPLACE_WB_NUMERIC},{"EX",LAPLACE_WB_EXTENDNUMLET},{"WSegSpace",LAPLACE_WB_WSEGSPACE},
    };
    auto it = m.find(s); return it == m.end() ? (uint8_t)LAPLACE_WB_OTHER : it->second;
}
uint8_t map_sb(const char* s) {
    static const std::unordered_map<std::string, uint8_t> m = {
        {"XX",LAPLACE_SB_OTHER},{"CR",LAPLACE_SB_CR},{"LF",LAPLACE_SB_LF},
        {"EX",LAPLACE_SB_EXTEND},{"SE",LAPLACE_SB_SEP},{"FO",LAPLACE_SB_FORMAT},
        {"SP",LAPLACE_SB_SP},{"LO",LAPLACE_SB_LOWER},{"UP",LAPLACE_SB_UPPER},
        {"LE",LAPLACE_SB_OLETTER},{"NU",LAPLACE_SB_NUMERIC},{"AT",LAPLACE_SB_ATERM},
        {"SC",LAPLACE_SB_SCONTINUE},{"ST",LAPLACE_SB_STERM},{"CL",LAPLACE_SB_CLOSE},
    };
    auto it = m.find(s); return it == m.end() ? (uint8_t)LAPLACE_SB_OTHER : it->second;
}
uint8_t map_incb(const char* s) {
    if (std::strcmp(s, "Consonant") == 0) return LAPLACE_INCB_CONSONANT;
    if (std::strcmp(s, "Linker") == 0)    return LAPLACE_INCB_LINKER;
    if (std::strcmp(s, "Extend") == 0)    return LAPLACE_INCB_EXTEND;
    return LAPLACE_INCB_NONE;
}

struct UcdData {
    std::vector<uint8_t> gb, wb, sb, incb, ccc, white_space;
    UcdData() {
        gb.assign(CP_COUNT, LAPLACE_GB_OTHER);
        wb.assign(CP_COUNT, LAPLACE_WB_OTHER);
        sb.assign(CP_COUNT, LAPLACE_SB_OTHER);
        incb.assign(CP_COUNT, LAPLACE_INCB_NONE);
        ccc.assign(CP_COUNT, 0);
        white_space.assign(CP_COUNT, 0);
    }
};

const char* attr(const char** a, const char* n) {
    if (!a) return nullptr;
    for (int i = 0; a[i]; i += 2) if (std::strcmp(a[i], n) == 0) return a[i+1];
    return nullptr;
}

struct SaxCtx { UcdData* d; bool in_rep = false; };

void on_start(void* u, const char* name, const char** a) {
    auto* ctx = (SaxCtx*)u;
    if (std::strcmp((const char*)name, "repertoire") == 0) { ctx->in_rep = true; return; }
    if (!ctx->in_rep) return;
    const char* nm = (const char*)name;
    if (std::strcmp(nm,"char") && std::strcmp(nm,"reserved")
     && std::strcmp(nm,"noncharacter") && std::strcmp(nm,"surrogate")) return;

    uint32_t first, last;
    auto cp = attr(a, "cp");
    if (cp) { first = last = (uint32_t)std::stoul((const char*)cp, nullptr, 16); }
    else {
        auto f = attr(a, "first-cp"); auto l = attr(a, "last-cp");
        if (!f || !l) return;
        first = (uint32_t)std::stoul((const char*)f, nullptr, 16);
        last  = (uint32_t)std::stoul((const char*)l, nullptr, 16);
    }
    if (last >= CP_COUNT) return;

    UcdData* d = ctx->d;
    auto gcb = attr(a,"GCB"); auto wbv = attr(a,"WB"); auto sbv = attr(a,"SB");
    auto inc = attr(a,"InCB"); auto cccv = attr(a,"ccc"); auto ep = attr(a,"ExtPict");
    auto ws = attr(a,"WSpace");

    uint8_t gbid = gcb ? map_gb((const char*)gcb) : LAPLACE_GB_OTHER;
    if (ep && std::strcmp((const char*)ep,"Y")==0) gbid = LAPLACE_GB_EXTENDED_PICTOGRAPHIC;
    uint8_t wbid = wbv ? map_wb((const char*)wbv) : LAPLACE_WB_OTHER;
    uint8_t sbid = sbv ? map_sb((const char*)sbv) : LAPLACE_SB_OTHER;
    uint8_t inid = inc ? map_incb((const char*)inc) : LAPLACE_INCB_NONE;
    uint8_t ccv  = cccv ? (uint8_t)std::stoul((const char*)cccv, nullptr, 10) : 0;
    uint8_t wsv  = (ws && std::strcmp((const char*)ws,"Y") == 0) ? 1u : 0u;

    for (uint32_t c = first; c <= last; ++c) {
        d->gb[c]=gbid; d->wb[c]=wbid; d->sb[c]=sbid; d->incb[c]=inid;
        d->ccc[c]=ccv; d->white_space[c]=wsv;
    }
}
void on_end(void* u, const char* name) {
    auto* ctx = (SaxCtx*)u;
    if (std::strcmp(name,"repertoire")==0) ctx->in_rep = false;
}

struct DucetKeys {
    std::vector<uint64_t> key;
    std::vector<uint8_t>  explicit_;
    DucetKeys() { key.assign(CP_COUNT, 0); explicit_.assign(CP_COUNT, 0); }
};
struct ImplicitRange { uint32_t first, last; uint32_t base; };

int parse_ducet(const char* path, DucetKeys& dk) {
    std::ifstream f(path);
    if (!f) return -3;
    std::vector<ImplicitRange> implicits;
    std::string line;
    while (std::getline(f, line)) {
        size_t h = line.find('#'); if (h != std::string::npos) line = line.substr(0, h);
        while (!line.empty() && (line.back()==' '||line.back()=='\t'||line.back()=='\r')) line.pop_back();
        if (line.empty()) continue;
        if (line[0] == '@') {
            if (line.rfind("@implicitweights", 0) == 0) {
                const char* p = line.c_str() + 16; char* e;
                uint32_t lo = (uint32_t)std::strtoul(p, &e, 16); p = e;
                while (*p == '.') ++p;
                uint32_t hi = (uint32_t)std::strtoul(p, &e, 16); p = e;
                while (*p && *p != ';') ++p; if (*p==';') ++p;
                uint32_t base = (uint32_t)std::strtoul(p, &e, 16);
                implicits.push_back({lo, hi, base});
            }
            continue;
        }
        size_t semi = line.find(';'); if (semi == std::string::npos) continue;
        std::string lhs = line.substr(0, semi);
        const char* p = lhs.c_str(); char* e;
        uint32_t cps[4]; int ncp = 0;
        while (*p && ncp < 4) {
            while (*p==' '||*p=='\t') ++p; if (!*p) break;
            uint32_t v = (uint32_t)std::strtoul(p, &e, 16); if (e == p) break;
            cps[ncp++] = v; p = e;
        }
        if (ncp != 1) continue;
        uint32_t cp = cps[0]; if (cp >= CP_COUNT) continue;
        std::string rhs = line.substr(semi + 1);
        size_t b = rhs.find('['); if (b == std::string::npos) continue;
        const char* q = rhs.c_str() + b + 1;
        if (*q=='.'||*q=='*') ++q;
        uint32_t pw = (uint32_t)std::strtoul(q, &e, 16); q = e; if (*q=='.') ++q;
        uint32_t sw = (uint32_t)std::strtoul(q, &e, 16); q = e; if (*q=='.') ++q;
        uint32_t tw = (uint32_t)std::strtoul(q, &e, 16);
        dk.key[cp] = ((uint64_t)pw << 48) | ((uint64_t)sw << 32) | ((uint64_t)tw << 16);
        dk.explicit_[cp] = 1;
    }
    auto base_for = [&](uint32_t cp) -> uint32_t {
        for (const auto& r : implicits) if (cp >= r.first && cp <= r.last) return r.base;
        if ((cp>=0x4E00&&cp<=0x9FFF)||(cp>=0xF900&&cp<=0xFAFF)) return 0xFB40;
        if ((cp>=0x3400&&cp<=0x4DBF)||(cp>=0x20000&&cp<=0x3FFFF)) return 0xFB80;
        return 0xFBC0;
    };
    {
        const uint32_t SBase = 0xAC00, LBase = 0x1100;
        const uint32_t VCount = 21, TCount = 28;
        const uint32_t NCount = VCount * TCount;
        const uint32_t SCount = 19 * NCount;
        for (uint32_t s = SBase; s < SBase + SCount; ++s) {
            if (dk.explicit_[s]) continue;
            uint32_t L = LBase + (s - SBase) / NCount;
            if (!dk.explicit_[L]) continue;
            dk.key[s] = dk.key[L];
            dk.explicit_[s] = 1;
        }
    }

    for (uint32_t cp = 0; cp < CP_COUNT; ++cp) {
        if (dk.explicit_[cp]) continue;
        uint32_t base = base_for(cp);
        uint32_t AAAA = base + (cp >> 15);
        uint32_t BBBB = (cp & 0x7FFF) | 0x8000;
        dk.key[cp] = ((uint64_t)AAAA << 48) | ((uint64_t)BBBB << 16);
    }
    return 0;
}

}

static bool read_zip_single_entry(const char* zip_path, std::vector<uint8_t>& out) {
    std::ifstream f(zip_path, std::ios::binary);
    if (!f) return false;
    std::vector<uint8_t> z((std::istreambuf_iterator<char>(f)),
                            std::istreambuf_iterator<char>());
    if (z.size() < 22) return false;

    auto rd16 = [&](size_t o) -> uint32_t {
        return (uint32_t)z[o] | ((uint32_t)z[o + 1] << 8);
    };
    auto rd32 = [&](size_t o) -> uint32_t {
        return (uint32_t)z[o] | ((uint32_t)z[o + 1] << 8)
             | ((uint32_t)z[o + 2] << 16) | ((uint32_t)z[o + 3] << 24);
    };

    size_t eocd = SIZE_MAX;
    size_t floor_off = (z.size() > 22 + 0xFFFFu) ? z.size() - (22 + 0xFFFFu) : 0;
    for (size_t i = z.size() - 21; i-- > floor_off; ) {
        if (rd32(i) == 0x06054b50u) { eocd = i; break; }
    }
    if (eocd == SIZE_MAX) return false;

    uint32_t n_entries = rd16(eocd + 10);
    uint32_t cd_off = rd32(eocd + 16);
    if (n_entries == 0 || (size_t)cd_off + 46 > z.size()) return false;

    size_t cd = cd_off;
    if (rd32(cd) != 0x02014b50u) return false;
    uint32_t method = rd16(cd + 10);
    uint32_t crc = rd32(cd + 16);
    uint32_t comp_size = rd32(cd + 20);
    uint32_t uncomp_size = rd32(cd + 24);
    uint32_t lh_off = rd32(cd + 42);

    if ((size_t)lh_off + 30 > z.size() || rd32(lh_off) != 0x04034b50u) return false;
    size_t data = (size_t)lh_off + 30 + rd16(lh_off + 26) + rd16(lh_off + 28);
    if (data + comp_size > z.size()) return false;

    if (method == 0) {
        if (comp_size != uncomp_size) return false;
        out.assign(z.begin() + data, z.begin() + data + comp_size);
    } else if (method == 8) {
        out.assign(uncomp_size, 0);
        z_stream s{};
        if (inflateInit2(&s, -MAX_WBITS) != Z_OK) return false;
        s.next_in = z.data() + data;
        s.avail_in = comp_size;
        s.next_out = out.data();
        s.avail_out = uncomp_size;
        int rc = inflate(&s, Z_FINISH);
        inflateEnd(&s);
        if (rc != Z_STREAM_END || s.total_out != uncomp_size) return false;
    } else {
        return false;
    }

    return crc32(0, out.data(), (uInt)out.size()) == crc;
}

static bool read_ucd_document(const char* path, std::vector<uint8_t>& document) {
    size_t n = std::strlen(path);
    if (n > 4 && std::strcmp(path + n - 4, ".zip") == 0)
        return read_zip_single_entry(path, document);

    std::ifstream input(path, std::ios::binary);
    if (!input) return false;
    document.assign(std::istreambuf_iterator<char>(input),
                    std::istreambuf_iterator<char>());
    return !document.empty();
}

static int compute_from_document(
    const uint8_t* document,
    size_t document_size,
    const char* ducet_path,
    laplace_perfcache_record_t* out_records,
    size_t out_capacity) {
    if (!document || document_size == 0 || !ducet_path || !out_records) return -1;
    if (out_capacity < CP_COUNT) return -1;

    UcdData data;
    SaxCtx context{&data, false};
    if (laplace_ucd_xml_parse(
            document, document_size, on_start, on_end, &context) != 0)
        return -2;

    DucetKeys ducet;
    int rc = parse_ducet(ducet_path, ducet);
    if (rc != 0) return rc;

    std::vector<uint32_t> order(CP_COUNT);
    for (uint32_t i = 0; i < CP_COUNT; ++i) order[i] = i;
    std::sort(order.begin(), order.end(), [&](uint32_t a, uint32_t b){
        if (ducet.key[a] != ducet.key[b]) return ducet.key[a] < ducet.key[b];
        return a < b;
    });
    std::vector<uint32_t> uca_rank(CP_COUNT);
    for (uint32_t rank = 0; rank < CP_COUNT; ++rank)
        uca_rank[order[rank]] = rank;

    // DUCET rank is identity order, not latitude. Use the open radical-inverse
    // placement so every prefix spreads over the whole S3 shell.
    for (uint32_t cp = 0; cp < CP_COUNT; ++cp) {
        uint32_t rank = uca_rank[cp];
        double coord[4];
        super_fibonacci_point_open(rank, coord);
        hilbert128_t hilbert;
        hilbert4d_encode(coord, &hilbert);
        uint8_t utf8[4];
        size_t utf8_size = laplace_utf8_encode(cp, utf8);
        hash128_t hash;
        hash128_blake3(utf8, utf8_size, &hash);
        uint32_t flags = laplace_pc_pack_flags(
            data.gb[cp], data.wb[cp], data.sb[cp], data.incb[cp],
            data.ccc[cp], data.white_space[cp]);

        laplace_perfcache_record_t& record = out_records[cp];
        record.codepoint = cp;
        record.uca_order = rank;
        record.coord[0] = coord[0];
        record.coord[1] = coord[1];
        record.coord[2] = coord[2];
        record.coord[3] = coord[3];
        record.hilbert = hilbert;
        record.hash = hash;
        record.flags = flags;
        record._pad = 0;
    }
    return 0;
}

extern "C" int laplace_unicode_seed_compute(const char* ucdxml_path,
                                            const char* ducet_path,
                                            laplace_perfcache_record_t* out_records,
                                            size_t out_capacity) {
    if (!ucdxml_path || !ducet_path || !out_records) return -1;
    if (out_capacity < CP_COUNT) return -1;

    try {
        std::vector<uint8_t> doc;
        if (!read_ucd_document(ucdxml_path, doc)) return -2;
        return compute_from_document(
            doc.data(), doc.size(), ducet_path, out_records, out_capacity);
    } catch (...) {
        return -4;
    }
}

struct laplace_unicode_seed_snapshot {
    std::vector<laplace_perfcache_record_t> records;
    std::vector<uint8_t> ucdxml;
};

extern "C" int laplace_unicode_seed_snapshot_open(
    const char* ucdxml_path,
    const char* ducet_path,
    laplace_unicode_seed_snapshot_t** out_snapshot) {
    if (!ucdxml_path || !ducet_path || !out_snapshot) return -1;
    *out_snapshot = nullptr;

    auto* snapshot = new (std::nothrow) laplace_unicode_seed_snapshot_t();
    if (!snapshot) return -4;
    try {
        snapshot->records.resize(CP_COUNT);
        if (!read_ucd_document(ucdxml_path, snapshot->ucdxml)) {
            delete snapshot;
            return -2;
        }
        int rc = compute_from_document(
            snapshot->ucdxml.data(), snapshot->ucdxml.size(), ducet_path,
            snapshot->records.data(), snapshot->records.size());
        if (rc != 0) {
            delete snapshot;
            return rc;
        }
    } catch (...) {
        delete snapshot;
        return -4;
    }
    *out_snapshot = snapshot;
    return 0;
}

extern "C" void laplace_unicode_seed_snapshot_free(
    laplace_unicode_seed_snapshot_t* snapshot) {
    delete snapshot;
}

extern "C" size_t laplace_unicode_seed_snapshot_count(
    const laplace_unicode_seed_snapshot_t* snapshot) {
    return snapshot ? snapshot->records.size() : 0u;
}

extern "C" int laplace_unicode_seed_snapshot_xml_copy(
    const laplace_unicode_seed_snapshot_t* snapshot,
    size_t offset,
    uint8_t* destination,
    size_t destination_capacity,
    size_t* out_copied) {
    if (!snapshot || !out_copied || (!destination && destination_capacity != 0))
        return -1;
    *out_copied = 0;
    if (offset > snapshot->ucdxml.size()) return -1;
    size_t available = snapshot->ucdxml.size() - offset;
    size_t copied = std::min(available, destination_capacity);
    if (copied != 0)
        std::memcpy(destination, snapshot->ucdxml.data() + offset, copied);
    *out_copied = copied;
    return 0;
}

extern "C" int laplace_unicode_seed_snapshot_stage(
    const laplace_unicode_seed_snapshot_t* snapshot,
    size_t first,
    size_t count,
    intent_stage_t* stage,
    const hash128_t* source_id) {
    if (!snapshot || !stage || !source_id) return -1;
    if (first > snapshot->records.size()
        || count > snapshot->records.size() - first) return -1;

    const hash128_t codepoint_type = laplace_content_tier_type_id(0);
    for (size_t i = 0; i < count; ++i) {
        const laplace_perfcache_record_t& record = snapshot->records[first + i];
        if (intent_stage_add_entity(
                stage, &record.hash, 0, &codepoint_type, source_id) != 0)
            return -2;

        hash128_t physicality_id;
        laplace_physicality_id_compute(record.hash, 1, &physicality_id);
        if (intent_stage_add_physicality(
                stage, &physicality_id, &record.hash, 1,
                record.coord, &record.hilbert,
                nullptr, 0, 0,
                1, 0.0, 1, 0, 0) != 0)
            return -2;
    }
    return 0;
}

extern "C" int laplace_unicode_seed_snapshot_copy_record(
    const laplace_unicode_seed_snapshot_t* snapshot,
    size_t index,
    laplace_perfcache_record_t* out_record) {
    if (!snapshot || !out_record || index >= snapshot->records.size()) return -1;
    *out_record = snapshot->records[index];
    return 0;
}
