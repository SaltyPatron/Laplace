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
#include "laplace/core/unicode_seed.h"

namespace fs = std::filesystem;

static const uint32_t CP_FULL = LAPLACE_PERFCACHE_RECORD_COUNT;

enum class ScopeKind { Ascii, Bmp, Full };

struct Cli {
    fs::path ucdxml, ducet, output;
    std::string ucd_version, uca_version;
    ScopeKind scope = ScopeKind::Full;
    uint32_t scope_count = CP_FULL;
    const char* scope_tag = "full";
};

static uint32_t scope_to_count(ScopeKind s) {
    switch (s) {
    case ScopeKind::Ascii: return 0x80u;
    case ScopeKind::Bmp:   return 0x10000u;
    case ScopeKind::Full:  return CP_FULL;
    }
    return CP_FULL;
}

static const char* scope_to_tag(ScopeKind s) {
    switch (s) {
    case ScopeKind::Ascii: return "ascii";
    case ScopeKind::Bmp:   return "bmp";
    case ScopeKind::Full:  return "full";
    }
    return "full";
}

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
        else if (a == "--scope") {
            auto v = nx();
            if (v == "ascii") c.scope = ScopeKind::Ascii;
            else if (v == "bmp") c.scope = ScopeKind::Bmp;
            else if (v == "full") c.scope = ScopeKind::Full;
            else { std::fprintf(stderr, "--scope must be ascii|bmp|full\n"); std::exit(2); }
        }
        else { std::fprintf(stderr, "unknown arg %s\n", argv[i]); std::exit(2); }
    }
    if (c.ucdxml.empty() || c.ducet.empty() || c.output.empty()) {
        std::fprintf(stderr, "required: --ucdxml --ducet --output\n"); std::exit(2);
    }
    c.scope_count = scope_to_count(c.scope);
    c.scope_tag = scope_to_tag(c.scope);
    return c;
}

/*
 * unicode_seed.cpp is the single authority for all UCD property -> packed T0
 * mappings. This emitter performs a second UCDXML pass only for the canonical
 * decomposition/composition tables that are not part of the fixed-size record.
 * Keeping GCB/WB/SB/InCB/CCC mappings here as well was an independent copy of
 * foundational semantics and allowed the two generated views to drift.
 */
struct UcdCompositionData {
    std::unordered_map<uint32_t, std::vector<uint32_t>> decomp;
    std::vector<uint8_t> comp_ex;
    UcdCompositionData() { comp_ex.assign(CP_FULL, 0); }
};

static const char* attr(const char** a, const char* n) {
    if (!a) return nullptr;
    for (int i = 0; a[i]; i += 2) if (std::strcmp(a[i], n) == 0) return a[i+1];
    return nullptr;
}

struct SaxCtx { UcdCompositionData* d; bool in_rep = false; };

extern "C" void on_start(void* u, const char* name, const char** a) {
    auto* ctx = (SaxCtx*)u;
    if (std::strcmp(name, "repertoire") == 0) { ctx->in_rep = true; return; }
    if (!ctx->in_rep) return;
    if (std::strcmp(name,"char") && std::strcmp(name,"reserved")
     && std::strcmp(name,"noncharacter") && std::strcmp(name,"surrogate")) return;

    uint32_t first, last;
    auto cp = attr(a, "cp");
    if (cp) { first = last = (uint32_t)std::stoul(cp, nullptr, 16); }
    else {
        auto f = attr(a, "first-cp"); auto l = attr(a, "last-cp");
        if (!f || !l) return;
        first = (uint32_t)std::stoul(f, nullptr, 16);
        last  = (uint32_t)std::stoul(l, nullptr, 16);
    }
    if (last >= CP_FULL) return;

    auto dt = attr(a,"dt");
    auto dm = attr(a,"dm");
    auto cex = attr(a,"Comp_Ex");
    const uint8_t cxv = (cex && std::strcmp(cex,"Y") == 0) ? 1u : 0u;
    for (uint32_t c = first; c <= last; ++c) ctx->d->comp_ex[c] = cxv;

    if (first == last && dt && dm && std::strcmp(dt,"can") == 0
        && std::strcmp(dm,"#") != 0) {
        std::vector<uint32_t> seq;
        const char* p = dm; char* e;
        while (*p) {
            uint32_t v = (uint32_t)std::strtoul(p, &e, 16);
            if (e == p) break;
            seq.push_back(v);
            p = e;
            while (*p == ' ') ++p;
        }
        if (!seq.empty()) ctx->d->decomp[first] = std::move(seq);
    }
}

extern "C" void on_end(void* u, const char* name) {
    auto* ctx = (SaxCtx*)u;
    if (std::strcmp(name,"repertoire") == 0) ctx->in_rep = false;
}

static void put_u32(std::vector<uint8_t>& b, uint32_t v) { for(int i=0;i<4;++i) b.push_back((uint8_t)(v>>(i*8))); }
static void put_u64(std::vector<uint8_t>& b, uint64_t v) { for(int i=0;i<8;++i) b.push_back((uint8_t)(v>>(i*8))); }
static void put_h128(std::vector<uint8_t>& b, const hash128_t& h) {
    const uint8_t* p = (const uint8_t*)&h; for (int i=0;i<16;++i) b.push_back(p[i]);
}

// Generator identity for the source-hash gate. This USED to be a hand-edited
// string with a comment asking the author to "bump when the emit LOGIC changes",
// and nothing enforced it.
//
// MEASURED 2026-08-10: the Hangul collation fix changed rank assignment in
// unicode_seed.cpp and left this constant alone. source_hash was therefore
// unchanged, the gate below matched, this tool printed "sources unchanged
// (source-hash match) -- crawl skipped", and every build -- including a full
// green CI run reporting "Build: engine, extensions, app, perfcache success" --
// kept the OLD geometry. A staleness key that depends on human memory reports
// "current" precisely when it is wrong.
//
// LAPLACE_GENERATOR_FINGERPRINT is a build-time SHA256 over this file plus
// unicode_seed.cpp / super_fibonacci.c / hilbert4d.c (see this tool's
// CMakeLists.txt). Any edit to the emit logic or the seed computation changes
// it, which changes source_hash, which forces the regenerate. Nobody has to
// remember. The literal fallback only applies to a build system that does not
// define it, and is deliberately marked so such a blob is identifiable.
#ifdef LAPLACE_GENERATOR_FINGERPRINT
static const char* GENERATOR_TAG = "ucd_tables_emit/v3-fp/" LAPLACE_GENERATOR_FINGERPRINT;
#else
static const char* GENERATOR_TAG = "ucd_tables_emit/v3-NO-FINGERPRINT";
#endif

static bool read_file_bytes(const fs::path& p, std::vector<uint8_t>& out) {
    std::ifstream f(p, std::ios::binary);
    if (!f) return false;
    out.assign(std::istreambuf_iterator<char>(f), std::istreambuf_iterator<char>());
    return true;
}

// source_hash = blake3( blake3(xml) || blake3(ducet) || ucd_ver || uca_ver || tag || scope ).
// Scope enters the hash so ascii/bmp/full blobs no-op independently.
static void compute_source_hash(const std::vector<uint8_t>& xml,
                                const std::vector<uint8_t>& ducet,
                                const std::string& ucd_ver, const std::string& uca_ver,
                                const char* scope_tag,
                                hash128_t* out) {
    hash128_t hx, hd;
    hash128_blake3(xml.data(), xml.size(), &hx);
    hash128_blake3(ducet.data(), ducet.size(), &hd);
    std::vector<uint8_t> mix;
    put_h128(mix, hx);
    put_h128(mix, hd);
    for (char c : ucd_ver) mix.push_back((uint8_t)c);
    mix.push_back(0);
    for (char c : uca_ver) mix.push_back((uint8_t)c);
    mix.push_back(0);
    for (const char* t = GENERATOR_TAG; *t; ++t) mix.push_back((uint8_t)*t);
    mix.push_back(0);
    for (const char* t = scope_tag; *t; ++t) mix.push_back((uint8_t)*t);
    hash128_blake3(mix.data(), mix.size(), out);
}

int main(int argc, char** argv) {
    Cli cli = parse_cli(argc, argv);

    std::vector<uint8_t> xml_bytes, ducet_bytes;
    if (!read_file_bytes(cli.ucdxml, xml_bytes)) {
        std::fprintf(stderr, "cannot open %s\n", cli.ucdxml.string().c_str());
        return 4;
    }
    if (!read_file_bytes(cli.ducet, ducet_bytes)) {
        std::fprintf(stderr, "cannot open %s\n", cli.ducet.string().c_str());
        return 4;
    }
    hash128_t source_hash;
    compute_source_hash(xml_bytes, ducet_bytes, cli.ucd_version, cli.uca_version,
                        cli.scope_tag, &source_hash);
    {
        std::ifstream prev(cli.output, std::ios::binary);
        if (prev) {
            laplace_perfcache_header_t hdr{};
            prev.read((char*)&hdr, sizeof(hdr));
            if (prev.gcount() == (std::streamsize)sizeof(hdr)
                && hdr.magic == LAPLACE_PERFCACHE_MAGIC
                && hdr.format_version == LAPLACE_PERFCACHE_VERSION
                && std::memcmp(&hdr.ucd_hash, &source_hash, sizeof(hash128_t)) == 0) {
                std::fprintf(stderr,
                    "perfcache: sources unchanged (source-hash match) — crawl skipped\n");
                return 0;
            }
        }
    }

    // Full-universe compute so shared codepoints keep identical ids/coords across scopes;
    // the blob stores only the dense prefix [0, scope_count). unicode_seed is also the
    // sole property mapper for every packed T0 property bit.
    std::vector<laplace_perfcache_record_t> rec_array(CP_FULL);
    int rc = laplace_unicode_seed_compute(cli.ucdxml.string().c_str(),
                                          cli.ducet.string().c_str(),
                                          rec_array.data(), rec_array.size());
    if (rc != 0) {
        std::fprintf(stderr, "laplace_unicode_seed_compute returned %d\n", rc);
        return 4;
    }
    const uint32_t scope_count = cli.scope_count;
    std::vector<uint8_t> records;
    records.resize(sizeof(laplace_perfcache_record_t) * scope_count);
    std::memcpy(records.data(), rec_array.data(), records.size());

    UcdCompositionData d;
    SaxCtx ctx{&d, false};
    // The second parse recovers only canonical decomposition/composition metadata.
    // It deliberately does not interpret UAX#29/CCC/White_Space properties.
    std::vector<uint8_t> doc = std::move(xml_bytes);
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
    for (auto& kv : d.decomp) {
        if (kv.first >= scope_count) continue;
        std::vector<uint32_t> seq; full(kv.first, seq);
        bool in_scope = true;
        for (uint32_t c : seq) if (c >= scope_count) { in_scope = false; break; }
        if (!in_scope) continue;
        decomps.emplace_back(kv.first, std::move(seq));
    }
    std::sort(decomps.begin(), decomps.end(), [](auto&a, auto&b){ return a.first < b.first; });

    std::vector<uint8_t> decomp_recs, decomp_data;
    uint32_t data_idx = 0;
    for (auto& dd : decomps) {
        put_u32(decomp_recs, dd.first);
        put_u32(decomp_recs, data_idx);
        put_u32(decomp_recs, (uint32_t)dd.second.size());
        for (uint32_t c : dd.second) { put_u32(decomp_data, c); ++data_idx; }
    }

    // CCC comes from the already-authoritative packed records generated by
    // unicode_seed, so composition selection cannot drift from the runtime table.
    auto ccc_of = [&](uint32_t cp) -> uint8_t {
        return cp < CP_FULL ? laplace_pc_ccc(rec_array[cp].flags) : 0u;
    };
    std::vector<std::array<uint32_t,3>> comps;
    for (auto& kv : d.decomp) {
        uint32_t cp = kv.first; const auto& seq = kv.second;
        if (cp >= scope_count) continue;
        if (seq.size() != 2) continue;
        if (seq[0] >= scope_count || seq[1] >= scope_count) continue;
        if (d.comp_ex[cp]) continue;
        if (ccc_of(seq[0]) != 0) continue;
        if (ccc_of(cp) != 0) continue;
        comps.push_back({seq[0], seq[1], cp});
    }
    std::sort(comps.begin(), comps.end(), [](auto&a, auto&b){
        return a[0]!=b[0] ? a[0]<b[0] : a[1]<b[1];
    });
    std::vector<uint8_t> compose_recs;
    for (auto& c : comps) {
        put_u32(compose_recs, c[0]);
        put_u32(compose_recs, c[1]);
        put_u32(compose_recs, c[2]);
    }
    std::vector<laplace_perfcache_record_t>().swap(rec_array);

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
    put_u64(blob, scope_count);
    put_u64(blob, 80);
    put_u64(blob, off_records);
    put_u64(blob, decomps.size());
    put_u64(blob, off_decomp_r);
    put_u64(blob, data_idx);
    put_u64(blob, off_decomp_d);
    put_u64(blob, comps.size());
    put_u64(blob, off_compose_r);
    put_h128(blob, source_hash);
    for (int i=0;i<16;++i) blob.push_back(0);
    blob.insert(blob.end(), records.begin(), records.end());
    blob.insert(blob.end(), decomp_recs.begin(), decomp_recs.end());
    blob.insert(blob.end(), decomp_data.begin(), decomp_data.end());
    blob.insert(blob.end(), compose_recs.begin(), compose_recs.end());
    hash128_t crc; hash128_blake3(blob.data(), blob.size(), &crc);
    put_h128(blob, crc);

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
        "perfcache: ucd=%s uca=%s scope=%s -> %s\n"
        "  records=%u (%.1f MiB) decomp=%zu (data=%u) compose=%zu  total=%.1f MiB\n",
        cli.ucd_version.c_str(), cli.uca_version.c_str(), cli.scope_tag,
        cli.output.string().c_str(),
        scope_count, records.size()/1048576.0, decomps.size(), data_idx, comps.size(),
        blob.size()/1048576.0);
    return 0;
}
