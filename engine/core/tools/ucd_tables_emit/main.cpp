#include <algorithm>
#include <array>
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

#include "laplace/core/hash128.h"
#include "laplace/core/ucd_xml.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/super_fibonacci.h"
#include "laplace/core/perfcache_format.h"
#include "laplace/core/ucd_property_values.h"
#include "laplace/core/unicode_seed.h"

namespace fs = std::filesystem;

static const uint32_t CP_COUNT = LAPLACE_PERFCACHE_RECORD_COUNT;

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

struct UcdData {
    std::vector<uint8_t> gb, wb, sb, incb, ccc;
    std::vector<uint8_t> ext_pict;
    std::unordered_map<uint32_t, std::vector<uint32_t>> decomp;
    std::vector<uint8_t> comp_ex;
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

static const char* attr(const char** a, const char* n) {
    if (!a) return nullptr;
    for (int i = 0; a[i]; i += 2) if (std::strcmp(a[i], n) == 0) return a[i+1];
    return nullptr;
}

struct SaxCtx { UcdData* d; bool in_rep = false; };

extern "C" void on_start(void* u, const char* name, const char** a) {
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
    if (first == last && dt && dm && std::strcmp((const char*)dt,"can")==0
        && std::strcmp((const char*)dm,"#")!=0) {
        std::vector<uint32_t> seq;
        const char* p = (const char*)dm; char* e;
        while (*p) { uint32_t v=(uint32_t)std::strtoul(p,&e,16); if(e==p)break; seq.push_back(v); p=e; while(*p==' ')++p; }
        if (!seq.empty()) d->decomp[first] = seq;
    }
}
extern "C" void on_end(void* u, const char* name) {
    auto* ctx = (SaxCtx*)u;
    if (std::strcmp((const char*)name,"repertoire")==0) ctx->in_rep = false;
}

static void put_u32(std::vector<uint8_t>& b, uint32_t v) { for(int i=0;i<4;++i) b.push_back((uint8_t)(v>>(i*8))); }
static void put_u64(std::vector<uint8_t>& b, uint64_t v) { for(int i=0;i<8;++i) b.push_back((uint8_t)(v>>(i*8))); }
static void put_h128(std::vector<uint8_t>& b, const hash128_t& h) {
    const uint8_t* p = (const uint8_t*)&h; for (int i=0;i<16;++i) b.push_back(p[i]);
}


int main(int argc, char** argv) {
    Cli cli = parse_cli(argc, argv);

    std::vector<laplace_perfcache_record_t> rec_array(CP_COUNT);
    int rc = laplace_unicode_seed_compute(cli.ucdxml.string().c_str(),
                                          cli.ducet.string().c_str(),
                                          rec_array.data(), rec_array.size());
    if (rc != 0) {
        std::fprintf(stderr, "laplace_unicode_seed_compute returned %d\n", rc);
        return 4;
    }
    std::vector<uint8_t> records;
    records.resize(sizeof(laplace_perfcache_record_t) * CP_COUNT);
    std::memcpy(records.data(), rec_array.data(), records.size());

    UcdData d;
    SaxCtx ctx{&d, false};
    std::vector<uint8_t> doc;
    {
        std::ifstream xf(cli.ucdxml, std::ios::binary);
        if (!xf) { std::fprintf(stderr, "cannot open %s\n", cli.ucdxml.string().c_str()); return 4; }
        doc.assign(std::istreambuf_iterator<char>(xf), std::istreambuf_iterator<char>());
    }
    int xml_rc = laplace_ucd_xml_parse(doc.data(), doc.size(), on_start, on_end, &ctx);
    if (xml_rc != 0) {
        std::fprintf(stderr, "UCDXML parse failed (rc=%d; -1=args, -2=malformed)\n", xml_rc);
        return 4;
    }
    std::vector<uint8_t>().swap(doc);

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

    auto ccc_of = [&](uint32_t cp){ return cp < CP_COUNT ? d.ccc[cp] : 0; };
    std::vector<std::array<uint32_t,3>> comps;
    for (auto& kv : d.decomp) {
        uint32_t cp = kv.first; const auto& seq = kv.second;
        if (seq.size() != 2) continue;
        if (d.comp_ex[cp]) continue;
        if (ccc_of(seq[0]) != 0) continue;
        if (ccc_of(cp) != 0) continue;
        comps.push_back({seq[0], seq[1], cp});
    }
    std::sort(comps.begin(), comps.end(), [](auto&a, auto&b){
        return a[0]!=b[0] ? a[0]<b[0] : a[1]<b[1];
    });
    std::vector<uint8_t> compose_recs;
    for (auto& c : comps) { put_u32(compose_recs, c[0]); put_u32(compose_recs, c[1]); put_u32(compose_recs, c[2]); }

    const uint64_t HDR = 128;
    uint64_t off_records   = HDR;
    uint64_t off_decomp_r  = off_records  + records.size();
    uint64_t off_decomp_d  = off_decomp_r + decomp_recs.size();
    uint64_t off_compose_r = off_decomp_d + decomp_data.size();

    std::vector<uint8_t> blob;
    blob.reserve(off_compose_r + compose_recs.size() + 16);
    put_u32(blob, LAPLACE_PERFCACHE_MAGIC);
    put_u32(blob, LAPLACE_PERFCACHE_VERSION);
    { char v[8]={0}; std::memcpy(v, cli.ucd_version.c_str(), std::min<size_t>(cli.ucd_version.size(), 8)); for(int i=0;i<8;++i) blob.push_back((uint8_t)v[i]); }
    { char v[8]={0}; std::memcpy(v, cli.uca_version.c_str(), std::min<size_t>(cli.uca_version.size(), 8)); for(int i=0;i<8;++i) blob.push_back((uint8_t)v[i]); }
    put_u64(blob, CP_COUNT);
    put_u64(blob, 80);
    put_u64(blob, off_records);
    put_u64(blob, decomps.size());
    put_u64(blob, off_decomp_r);
    put_u64(blob, data_idx);
    put_u64(blob, off_decomp_d);
    put_u64(blob, comps.size());
    put_u64(blob, off_compose_r);
    { hash128_t z; hash128_zero(&z); put_h128(blob, z); }
    for (int i=0;i<16;++i) blob.push_back(0);
    blob.insert(blob.end(), records.begin(), records.end());
    blob.insert(blob.end(), decomp_recs.begin(), decomp_recs.end());
    blob.insert(blob.end(), decomp_data.begin(), decomp_data.end());
    blob.insert(blob.end(), compose_recs.begin(), compose_recs.end());
    hash128_t crc; hash128_blake3(blob.data(), blob.size(), &crc);
    put_h128(blob, crc);

    /* Write-if-changed: the runtime mmaps this blob and Windows refuses to
     * truncate a user-mapped file, so a regeneration that produced identical
     * bytes (the normal, deterministic case) must not fail the build under a
     * live ingest. A CHANGED blob still writes — and correctly fails while
     * anything has the old one mapped. */
    {
        std::ifstream prev(cli.output, std::ios::binary);
        if (prev) {
            std::vector<uint8_t> old((std::istreambuf_iterator<char>(prev)),
                                     std::istreambuf_iterator<char>());
            if (old.size() == blob.size()
                && std::memcmp(old.data(), blob.data(), blob.size()) == 0) {
                std::fprintf(stderr, "perfcache: unchanged (%.1f MiB) — write skipped\n",
                             blob.size()/1048576.0);
                return 0;
            }
        }
    }
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
