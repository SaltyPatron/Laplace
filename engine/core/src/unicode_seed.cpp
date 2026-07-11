#include "laplace/core/unicode_seed.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <string>
#include <unordered_map>
#include <vector>

#include "laplace/core/hash128.h"
#include "laplace/core/ucd_xml.h"
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
    std::vector<uint8_t> gb, wb, sb, incb, ccc;
    UcdData() {
        gb.assign(CP_COUNT, LAPLACE_GB_OTHER);
        wb.assign(CP_COUNT, LAPLACE_WB_OTHER);
        sb.assign(CP_COUNT, LAPLACE_SB_OTHER);
        incb.assign(CP_COUNT, LAPLACE_INCB_NONE);
        ccc.assign(CP_COUNT, 0);
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

    uint8_t gbid = gcb ? map_gb((const char*)gcb) : LAPLACE_GB_OTHER;
    if (ep && std::strcmp((const char*)ep,"Y")==0) gbid = LAPLACE_GB_EXTENDED_PICTOGRAPHIC;
    uint8_t wbid = wbv ? map_wb((const char*)wbv) : LAPLACE_WB_OTHER;
    uint8_t sbid = sbv ? map_sb((const char*)sbv) : LAPLACE_SB_OTHER;
    uint8_t inid = inc ? map_incb((const char*)inc) : LAPLACE_INCB_NONE;
    uint8_t ccv  = cccv ? (uint8_t)std::stoul((const char*)cccv, nullptr, 10) : 0;

    for (uint32_t c = first; c <= last; ++c) {
        d->gb[c]=gbid; d->wb[c]=wbid; d->sb[c]=sbid; d->incb[c]=inid; d->ccc[c]=ccv;
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
    for (uint32_t cp = 0; cp < CP_COUNT; ++cp) {
        if (dk.explicit_[cp]) continue;
        uint32_t base = base_for(cp);
        uint32_t AAAA = base + (cp >> 15);
        uint32_t BBBB = (cp & 0x7FFF) | 0x8000;
        dk.key[cp] = ((uint64_t)AAAA << 48) | ((uint64_t)BBBB << 16);
    }
    return 0;
}

size_t utf8_encode(uint32_t cp, uint8_t o[4]) {
    if (cp < 0x80) { o[0]=(uint8_t)cp; return 1; }
    if (cp < 0x800) { o[0]=0xC0|(cp>>6); o[1]=0x80|(cp&0x3F); return 2; }
    if (cp < 0x10000) { o[0]=0xE0|(cp>>12); o[1]=0x80|((cp>>6)&0x3F); o[2]=0x80|(cp&0x3F); return 3; }
    o[0]=0xF0|(cp>>18); o[1]=0x80|((cp>>12)&0x3F); o[2]=0x80|((cp>>6)&0x3F); o[3]=0x80|(cp&0x3F); return 4;
}

}

extern "C" int laplace_unicode_seed_compute(const char* ucdxml_path,
                                            const char* ducet_path,
                                            laplace_perfcache_record_t* out_records,
                                            size_t out_capacity) {
    if (!ucdxml_path || !ducet_path || !out_records) return -1;
    if (out_capacity < CP_COUNT) return -1;

    UcdData d;
    SaxCtx ctx{&d, false};

    bool is_zip = false;
    {
        size_t n = std::strlen(ucdxml_path);
        is_zip = (n > 4 && std::strcmp(ucdxml_path + n - 4, ".zip") == 0);
    }
    /* Both branches produce the same in-memory document; only the FILE* source
     * differs (zip: streamed out of the archive; plain: the file itself). Parse
     * is the streaming UCDXML SAX in ucd_xml.cpp — not a full AST. */
    FILE* p;
    int (*closer)(FILE*);
    if (is_zip) {
#ifdef _WIN32
        std::string cmd = std::string("tar -xOf \"") + ucdxml_path + "\"";
        p = _popen(cmd.c_str(), "rb");
        closer = [](FILE* c) -> int { return _pclose(c); };
#else
        std::string cmd = std::string("unzip -p '") + ucdxml_path + "'";
        p = popen(cmd.c_str(), "r");
        closer = [](FILE* c) -> int { return pclose(c); };
#endif
    } else {
        p = std::fopen(ucdxml_path, "rb");
        closer = [](FILE* c) -> int { return std::fclose(c); };
    }
    if (!p) return -2;
    std::vector<uint8_t> doc;
    {
        uint8_t chunk[1 << 16];
        size_t got;
        while ((got = std::fread(chunk, 1, sizeof chunk, p)) > 0)
            doc.insert(doc.end(), chunk, chunk + got);
    }
    if (closer(p) != 0 && is_zip) return -2;
    if (doc.empty()) return -2;
    if (laplace_ucd_xml_parse(doc.data(), doc.size(), on_start, on_end, &ctx) != 0)
        return -2;
    std::vector<uint8_t>().swap(doc);

    DucetKeys dk;
    int rc = parse_ducet(ducet_path, dk);
    if (rc != 0) return rc;

    std::vector<uint32_t> order(CP_COUNT);
    for (uint32_t i = 0; i < CP_COUNT; ++i) order[i] = i;
    std::sort(order.begin(), order.end(), [&](uint32_t a, uint32_t b){
        if (dk.key[a] != dk.key[b]) return dk.key[a] < dk.key[b];
        return a < b;
    });
    std::vector<uint32_t> uca_rank(CP_COUNT);
    for (uint32_t r = 0; r < CP_COUNT; ++r) uca_rank[order[r]] = r;

    std::vector<double> sf(4ull * CP_COUNT);
    super_fibonacci(CP_COUNT, sf.data());

    for (uint32_t cp = 0; cp < CP_COUNT; ++cp) {
        uint32_t rank = uca_rank[cp];
        double coord[4] = { sf[4ull*rank+0], sf[4ull*rank+1], sf[4ull*rank+2], sf[4ull*rank+3] };
        hilbert128_t hb; hilbert4d_encode(coord, &hb);
        uint8_t u8[4]; size_t n = utf8_encode(cp, u8);
        hash128_t h; hash128_blake3(u8, n, &h);
        uint32_t flags = laplace_pc_pack_flags(d.gb[cp], d.wb[cp], d.sb[cp], d.incb[cp], d.ccc[cp]);

        laplace_perfcache_record_t& r = out_records[cp];
        r.codepoint = cp;
        r.uca_order = rank;
        r.coord[0] = coord[0]; r.coord[1] = coord[1]; r.coord[2] = coord[2]; r.coord[3] = coord[3];
        r.hilbert = hb;
        r.hash = h;
        r.flags = flags;
        r._pad = 0;
    }
    return 0;
}
