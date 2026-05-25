// engine/core/tools/ucd_tables_emit/main.cpp
//
// Perf-cache emitter (ADR 0006 + ADR 0053). Reads UCDXML (UAX#42) +
// DUCET (UCA allkeys.txt) and writes the T0 codepoint perf-cache BINARY
// blob: per-codepoint { hash, uca_order, coord (super-Fibonacci on S^3),
// hilbert, packed GB/WB/SB/InCB/CCC } + canonical decomposition +
// composition side-tables.
//
// This is UnicodeDecomposer's build-time half. The blob is APP/reference
// data — the runtime (TextDecomposer + HashComposer) mmaps it to compute
// T>0 without the DB, and it deploys standalone to mobile/embedded. The
// sibling install-time half (1.1M codepoint entity + physicality DB seed,
// no complex attestations) reads the same derivation via SubstrateChange.
//
// Determinism (RULES R7): same UCDXML + same DUCET + same emit source ->
// byte-identical blob.
//
// Usage:
//   laplace_ucd_tables_emit \
//       --ucdxml      .../ucd.nounihan.flat.xml \
//       --ducet       .../allkeys.txt \
//       --ucd-version 17.0.0 --uca-version 17.0.0 \
//       --output      .../perfcache.bin

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#include <libxml/parser.h>
#include <libxml/SAX2.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/super_fibonacci.h"
#include "laplace/core/perfcache_format.h"
#include "laplace/core/ucd_property_values.h"
#include "laplace/core/unicode_seed.h"

namespace fs = std::filesystem;

static const uint32_t CP_COUNT = LAPLACE_PERFCACHE_RECORD_COUNT;  // 0x110000

// ===== CLI =====
struct Cli { fs::path ucdxml, ducet, output; std::string ucd_version, uca_version; };
static Cli parse_cli(int argc, char** argv) {
    Cli c;
    for (int i = 1; i < argc; ++i) {
        std::string_view a = argv[i];
        auto nx = [&]() -> std::string {
            if (i + 1 >= argc) { std::fprintf(stderr, "%s needs value\n", argv[i]); std::exit(2); }
            return argv[++i];
        };
        if      (a == "--ucdxml")      c.ucdxml = nx();
        else if (a == "--ducet")       c.ducet = nx();
        else if (a == "--output")      c.output = nx();
        else if (a == "--ucd-version") c.ucd_version = nx();
        else if (a == "--uca-version") c.uca_version = nx();
        else { std::fprintf(stderr, "unknown arg %s\n", argv[i]); std::exit(2); }
    }
    if (c.ucdxml.empty() || c.ducet.empty() || c.output.empty()) {
        std::fprintf(stderr, "required: --ucdxml --ducet --output\n"); std::exit(2);
    }
    return c;
}

// ===== short-code -> fixed id maps =====
static uint8_t map_gb(const char* s) {
    static const std::unordered_map<std::string, uint8_t> m = {
        {"XX",LAPLACE_GB_OTHER},{"CR",LAPLACE_GB_CR},{"LF",LAPLACE_GB_LF},
        {"CN",LAPLACE_GB_CONTROL},{"EX",LAPLACE_GB_EXTEND},{"ZWJ",LAPLACE_GB_ZWJ},
        {"RI",LAPLACE_GB_REGIONAL_INDICATOR},{"PP",LAPLACE_GB_PREPEND},
        {"SM",LAPLACE_GB_SPACINGMARK},{"L",LAPLACE_GB_L},{"V",LAPLACE_GB_V},
        {"T",LAPLACE_GB_T},{"LV",LAPLACE_GB_LV},{"LVT",LAPLACE_GB_LVT},
    };
    auto it = m.find(s); return it == m.end() ? (uint8_t)LAPLACE_GB_OTHER : it->second;
}
static uint8_t map_wb(const char* s) {
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
static uint8_t map_sb(const char* s) {
    static const std::unordered_map<std::string, uint8_t> m = {
        {"XX",LAPLACE_SB_OTHER},{"CR",LAPLACE_SB_CR},{"LF",LAPLACE_SB_LF},
        {"EX",LAPLACE_SB_EXTEND},{"SE",LAPLACE_SB_SEP},{"FO",LAPLACE_SB_FORMAT},
        {"SP",LAPLACE_SB_SP},{"LO",LAPLACE_SB_LOWER},{"UP",LAPLACE_SB_UPPER},
        {"LE",LAPLACE_SB_OLETTER},{"NU",LAPLACE_SB_NUMERIC},{"AT",LAPLACE_SB_ATERM},
        {"SC",LAPLACE_SB_SCONTINUE},{"ST",LAPLACE_SB_STERM},{"CL",LAPLACE_SB_CLOSE},
    };
    auto it = m.find(s); return it == m.end() ? (uint8_t)LAPLACE_SB_OTHER : it->second;
}
static uint8_t map_incb(const char* s) {
    if (std::strcmp(s, "Consonant") == 0) return LAPLACE_INCB_CONSONANT;
    if (std::strcmp(s, "Linker") == 0)    return LAPLACE_INCB_LINKER;
    if (std::strcmp(s, "Extend") == 0)    return LAPLACE_INCB_EXTEND;
    return LAPLACE_INCB_NONE;
}

// ===== per-codepoint property arrays (direct-indexed) =====
struct UcdData {
    std::vector<uint8_t> gb, wb, sb, incb, ccc;
    std::vector<uint8_t> ext_pict;
    std::unordered_map<uint32_t, std::vector<uint32_t>> decomp;  // canonical (dt=can) only
    std::vector<uint8_t> comp_ex;  // 1 if Comp_Ex=Y
    UcdData() {
        gb.assign(CP_COUNT, LAPLACE_GB_OTHER);
        wb.assign(CP_COUNT, LAPLACE_WB_OTHER);
        sb.assign(CP_COUNT, LAPLACE_SB_OTHER);
        incb.assign(CP_COUNT, LAPLACE_INCB_NONE);
        ccc.assign(CP_COUNT, 0);
        ext_pict.assign(CP_COUNT, 0);
        comp_ex.assign(CP_COUNT, 0);
    }
};

static const xmlChar* attr(const xmlChar** a, const char* n) {
    if (!a) return nullptr;
    for (int i = 0; a[i]; i += 2) if (std::strcmp((const char*)a[i], n) == 0) return a[i+1];
    return nullptr;
}

struct SaxCtx { UcdData* d; bool in_rep = false; };

extern "C" void on_start(void* u, const xmlChar* name, const xmlChar** a) {
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
    auto inc = attr(a,"InCB"); auto cccv = attr(a,"ccc");
    auto ep = attr(a,"ExtPict"); auto dt = attr(a,"dt"); auto dm = attr(a,"dm");
    auto cex = attr(a,"Comp_Ex");

    uint8_t gbid = gcb ? map_gb((const char*)gcb) : LAPLACE_GB_OTHER;
    if (ep && std::strcmp((const char*)ep,"Y")==0) gbid = LAPLACE_GB_EXTENDED_PICTOGRAPHIC;
    uint8_t wbid = wbv ? map_wb((const char*)wbv) : LAPLACE_WB_OTHER;
    uint8_t sbid = sbv ? map_sb((const char*)sbv) : LAPLACE_SB_OTHER;
    uint8_t inid = inc ? map_incb((const char*)inc) : LAPLACE_INCB_NONE;
    uint8_t ccv  = cccv ? (uint8_t)std::stoul((const char*)cccv, nullptr, 10) : 0;
    uint8_t epv  = (ep && std::strcmp((const char*)ep,"Y")==0) ? 1 : 0;
    uint8_t cxv  = (cex && std::strcmp((const char*)cex,"Y")==0) ? 1 : 0;

    for (uint32_t c = first; c <= last; ++c) {
        d->gb[c]=gbid; d->wb[c]=wbid; d->sb[c]=sbid; d->incb[c]=inid;
        d->ccc[c]=ccv; d->ext_pict[c]=epv; d->comp_ex[c]=cxv;
    }
    // Canonical decomposition (single cp only; dt=can)
    if (first == last && dt && dm && std::strcmp((const char*)dt,"can")==0
        && std::strcmp((const char*)dm,"#")!=0) {
        std::vector<uint32_t> seq;
        const char* p = (const char*)dm; char* e;
        while (*p) { uint32_t v=(uint32_t)std::strtoul(p,&e,16); if(e==p)break; seq.push_back(v); p=e; while(*p==' ')++p; }
        if (!seq.empty()) d->decomp[first] = seq;
    }
}
extern "C" void on_end(void* u, const xmlChar* name) {
    auto* ctx = (SaxCtx*)u;
    if (std::strcmp((const char*)name,"repertoire")==0) ctx->in_rep = false;
}

// ===== DUCET → per-codepoint collation sort key =====
// Sort key per cp: 64-bit (primary<<48 | secondary<<32 | tertiary<<16 | 0),
// with codepoint as final tiebreak. Single-cp explicit entries read their
// first CE's weights. Codepoints absent from allkeys get UCA §10.1.3
// implicit weights (Han/Tangut/Nushu/Khitan/other bases).
struct DucetKeys {
    std::vector<uint64_t> key;       // collation sort key per cp
    std::vector<uint8_t>  explicit_; // 1 if from allkeys, 0 if implicit
    DucetKeys() { key.assign(CP_COUNT, 0); explicit_.assign(CP_COUNT, 0); }
};

struct ImplicitRange { uint32_t first, last; uint32_t base; };

static void parse_ducet(const fs::path& path, DucetKeys& dk) {
    std::ifstream f(path);
    if (!f) { std::fprintf(stderr, "cannot open DUCET %s\n", path.string().c_str()); std::exit(3); }
    std::vector<ImplicitRange> implicit;
    std::string line;
    while (std::getline(f, line)) {
        if (line.empty()) continue;
        if (line[0] == '@') {
            // @implicitweights FIRST..LAST; BASE
            if (line.rfind("@implicitweights", 0) == 0) {
                const char* p = line.c_str() + 16;
                char* e;
                uint32_t lo = (uint32_t)std::strtoul(p, &e, 16); p = e;
                while (*p == '.' ) ++p;
                uint32_t hi = (uint32_t)std::strtoul(p, &e, 16); p = e;
                while (*p && *p != ';') ++p; if (*p==';') ++p;
                uint32_t base = (uint32_t)std::strtoul(p, &e, 16);
                implicit.push_back({lo, hi, base});
            }
            continue;
        }
        if (line[0] == '#') continue;
        // strip comment
        size_t h = line.find('#'); if (h != std::string::npos) line = line.substr(0, h);
        size_t semi = line.find(';'); if (semi == std::string::npos) continue;
        // left of ; = codepoints; we only handle single-cp entries
        std::string lhs = line.substr(0, semi);
        // count tokens in lhs
        const char* p = lhs.c_str(); char* e;
        uint32_t cps[4]; int ncp = 0;
        while (*p && ncp < 4) {
            while (*p==' '||*p=='\t') ++p;
            if (!*p) break;
            uint32_t v = (uint32_t)std::strtoul(p, &e, 16);
            if (e == p) break;
            cps[ncp++] = v; p = e;
        }
        if (ncp != 1) continue;          // skip contractions
        uint32_t cp = cps[0];
        if (cp >= CP_COUNT) continue;
        // first CE on rhs: [.PPPP.SSSS.TTTT] or [*PPPP....]
        std::string rhs = line.substr(semi + 1);
        size_t b = rhs.find('[');
        if (b == std::string::npos) continue;
        const char* q = rhs.c_str() + b + 1;
        if (*q=='.'||*q=='*') ++q;
        uint32_t pw = (uint32_t)std::strtoul(q, &e, 16); q = e; if (*q=='.') ++q;
        uint32_t sw = (uint32_t)std::strtoul(q, &e, 16); q = e; if (*q=='.') ++q;
        uint32_t tw = (uint32_t)std::strtoul(q, &e, 16);
        dk.key[cp] = ((uint64_t)pw << 48) | ((uint64_t)sw << 32) | ((uint64_t)tw << 16);
        dk.explicit_[cp] = 1;
    }
    // Implicit weights for codepoints not explicitly listed (UCA §10.1.3).
    auto base_for = [&](uint32_t cp) -> uint32_t {
        for (const auto& r : implicit) if (cp >= r.first && cp <= r.last) return r.base;
        // Core Han Unified + compat
        if ((cp>=0x4E00&&cp<=0x9FFF)||(cp>=0xF900&&cp<=0xFAFF)) return 0xFB40;
        // CJK extensions (A,B,...) + other ideographs
        if ((cp>=0x3400&&cp<=0x4DBF)||(cp>=0x20000&&cp<=0x3FFFF)) return 0xFB80;
        return 0xFBC0;  // all other unassigned
    };
    for (uint32_t cp = 0; cp < CP_COUNT; ++cp) {
        if (dk.explicit_[cp]) continue;
        uint32_t base = base_for(cp);
        uint32_t AAAA = base + (cp >> 15);
        uint32_t BBBB = (cp & 0x7FFF) | 0x8000;
        dk.key[cp] = ((uint64_t)AAAA << 48) | ((uint64_t)BBBB << 16);
    }
}

// ===== binary write helpers (little-endian) =====
static void put_u32(std::vector<uint8_t>& b, uint32_t v) { for(int i=0;i<4;++i) b.push_back((uint8_t)(v>>(i*8))); }
static void put_u64(std::vector<uint8_t>& b, uint64_t v) { for(int i=0;i<8;++i) b.push_back((uint8_t)(v>>(i*8))); }
static void put_f64(std::vector<uint8_t>& b, double d) { uint64_t v; std::memcpy(&v,&d,8); put_u64(b,v); }
static void put_h128(std::vector<uint8_t>& b, const hash128_t& h) {
    const uint8_t* p = (const uint8_t*)&h; for (int i=0;i<16;++i) b.push_back(p[i]);
}
static void put_hb128(std::vector<uint8_t>& b, const hilbert128_t& h) {
    for (int i=0;i<16;++i) b.push_back(h.bytes[i]);
}

static size_t utf8_encode(uint32_t cp, uint8_t o[4]) {
    if (cp < 0x80) { o[0]=(uint8_t)cp; return 1; }
    if (cp < 0x800) { o[0]=0xC0|(cp>>6); o[1]=0x80|(cp&0x3F); return 2; }
    if (cp < 0x10000) { o[0]=0xE0|(cp>>12); o[1]=0x80|((cp>>6)&0x3F); o[2]=0x80|(cp&0x3F); return 3; }
    o[0]=0xF0|(cp>>18); o[1]=0x80|((cp>>12)&0x3F); o[2]=0x80|((cp>>6)&0x3F); o[3]=0x80|(cp&0x3F); return 4;
}

int main(int argc, char** argv) {
    Cli cli = parse_cli(argc, argv);

    // --- Records: the ONE source of truth. Computed by the C ABI function in
    //     liblaplace_core; the C# UnicodeDecomposer calls the same function so
    //     the blob and the DB seed are byte-identical siblings, not one fed
    //     from the other. ---
    std::vector<laplace_perfcache_record_t> rec_array(CP_COUNT);
    int rc = laplace_unicode_seed_compute(cli.ucdxml.string().c_str(),
                                          cli.ducet.string().c_str(),
                                          rec_array.data(), rec_array.size());
    if (rc != 0) {
        std::fprintf(stderr, "laplace_unicode_seed_compute returned %d\n", rc);
        return 4;
    }
    // Serialise the records to the on-disk byte form (header section).
    std::vector<uint8_t> records;
    records.resize(sizeof(laplace_perfcache_record_t) * CP_COUNT);
    std::memcpy(records.data(), rec_array.data(), records.size());

    // --- The emitter ALSO needs a UCDXML pass for the decomp/compose
    //     side-tables (blob-only artifacts; not part of the DB seed). The
    //     records computed above are NOT overwritten by this. ---
    UcdData d;
    SaxCtx ctx{&d, false};
    xmlSAXHandler sax{}; sax.initialized = XML_SAX2_MAGIC;
    sax.startElement = on_start; sax.endElement = on_end;
    LIBXML_TEST_VERSION
    if (xmlSAXUserParseFile(&sax, &ctx, cli.ucdxml.string().c_str()) != 0) {
        std::fprintf(stderr, "UCDXML parse failed\n"); return 4;
    }
    xmlCleanupParser();

    // --- decomposition side-table (full canonical decomposition, recursive) ---
    std::function<void(uint32_t, std::vector<uint32_t>&)> full;
    full = [&](uint32_t cp, std::vector<uint32_t>& out){
        auto it = d.decomp.find(cp);
        if (it == d.decomp.end()) { out.push_back(cp); return; }
        for (uint32_t c : it->second) full(c, out);
    };
    std::vector<std::pair<uint32_t, std::vector<uint32_t>>> decomps;
    for (auto& kv : d.decomp) { std::vector<uint32_t> seq; full(kv.first, seq); decomps.emplace_back(kv.first, std::move(seq)); }
    std::sort(decomps.begin(), decomps.end(), [](auto&a, auto&b){ return a.first < b.first; });

    std::vector<uint8_t> decomp_recs, decomp_data;
    uint32_t data_idx = 0;
    for (auto& dd : decomps) {
        put_u32(decomp_recs, dd.first);
        put_u32(decomp_recs, data_idx);
        put_u32(decomp_recs, (uint32_t)dd.second.size());
        for (uint32_t c : dd.second) { put_u32(decomp_data, c); ++data_idx; }
    }

    // --- composition side-table (pairwise, exclusions filtered) ---
    auto ccc_of = [&](uint32_t cp){ return cp < CP_COUNT ? d.ccc[cp] : 0; };
    std::vector<std::array<uint32_t,3>> comps;
    for (auto& kv : d.decomp) {
        uint32_t cp = kv.first; const auto& seq = kv.second;
        if (seq.size() != 2) continue;
        if (d.comp_ex[cp]) continue;
        if (ccc_of(seq[0]) != 0) continue;   // non-starter decomposition (singleton excl)
        if (ccc_of(cp) != 0) continue;        // script-specific excl
        comps.push_back({seq[0], seq[1], cp});
    }
    std::sort(comps.begin(), comps.end(), [](auto&a, auto&b){
        return a[0]!=b[0] ? a[0]<b[0] : a[1]<b[1];
    });
    std::vector<uint8_t> compose_recs;
    for (auto& c : comps) { put_u32(compose_recs, c[0]); put_u32(compose_recs, c[1]); put_u32(compose_recs, c[2]); }

    // --- assemble blob: header(128) + records + decomp_recs + decomp_data + compose_recs + trailer(16) ---
    const uint64_t HDR = 128;
    uint64_t off_records   = HDR;
    uint64_t off_decomp_r  = off_records  + records.size();
    uint64_t off_decomp_d  = off_decomp_r + decomp_recs.size();
    uint64_t off_compose_r = off_decomp_d + decomp_data.size();

    std::vector<uint8_t> blob;
    blob.reserve(off_compose_r + compose_recs.size() + 16);
    // header
    put_u32(blob, LAPLACE_PERFCACHE_MAGIC);
    put_u32(blob, LAPLACE_PERFCACHE_VERSION);
    { char v[8]={0}; std::strncpy(v, cli.ucd_version.c_str(), 8); for(int i=0;i<8;++i) blob.push_back((uint8_t)v[i]); }
    { char v[8]={0}; std::strncpy(v, cli.uca_version.c_str(), 8); for(int i=0;i<8;++i) blob.push_back((uint8_t)v[i]); }
    put_u64(blob, CP_COUNT);
    put_u64(blob, 80);
    put_u64(blob, off_records);
    put_u64(blob, decomps.size());
    put_u64(blob, off_decomp_r);
    put_u64(blob, data_idx);
    put_u64(blob, off_decomp_d);
    put_u64(blob, comps.size());
    put_u64(blob, off_compose_r);
    { hash128_t z; hash128_zero(&z); put_h128(blob, z); }  // ucd_hash (fingerprint TODO; zero for now)
    for (int i=0;i<16;++i) blob.push_back(0);               // reserved[16] -> 128 B header
    // sections
    blob.insert(blob.end(), records.begin(), records.end());
    blob.insert(blob.end(), decomp_recs.begin(), decomp_recs.end());
    blob.insert(blob.end(), decomp_data.begin(), decomp_data.end());
    blob.insert(blob.end(), compose_recs.begin(), compose_recs.end());
    // trailer: BLAKE3-128 of everything so far
    hash128_t crc; hash128_blake3(blob.data(), blob.size(), &crc);
    put_h128(blob, crc);

    // write
    std::ofstream out(cli.output, std::ios::binary);
    if (!out) { std::fprintf(stderr, "cannot write %s\n", cli.output.string().c_str()); return 5; }
    out.write((const char*)blob.data(), (std::streamsize)blob.size());
    out.close();

    std::fprintf(stderr,
        "perfcache: ucd=%s uca=%s -> %s\n"
        "  records=%u (%.1f MiB) decomp=%zu (data=%u) compose=%zu  total=%.1f MiB\n",
        cli.ucd_version.c_str(), cli.uca_version.c_str(), cli.output.string().c_str(),
        CP_COUNT, records.size()/1048576.0, decomps.size(), data_idx, comps.size(),
        blob.size()/1048576.0);
    return 0;
}
