// engine/core/tools/ucd_tables_emit/main.cpp
//
// Build-time codegen: reads UCDXML (UAX #42 schema) via libxml2 SAX and
// emits compact C lookup tables for UAX#29 (Grapheme / Word / Sentence
// break) + UAX #15 (NFC: CCC + canonical decomposition + composition
// exclusion).
//
// UCDXML is the substrate's canonical UCD parse path (GLOSSARY:
// "Primary parse path: UCDXML"). One XML pass replaces parsing 5+
// separate .txt files. The same XML feeds both this engine codegen
// (subset of properties) and the future UnicodeDecomposer #183
// (full property surface + Unihan via the all-flat variant).
//
// Usage:
//   laplace_ucd_tables_emit \
//       --ucdxml      .../ucd.nounihan.flat.xml \
//       --out-header  .../ucd_tables.generated.h \
//       --out-source  .../ucd_tables.generated.c \
//       --version     17.0.0
//
// Determinism (RULES R7): same UCDXML + same emit-tool source ->
// byte-identical generated files.

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <map>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

#include <libxml/parser.h>
#include <libxml/SAX2.h>

namespace fs = std::filesystem;

// ===== CLI =====

struct Cli {
    fs::path ucdxml;
    fs::path out_header;
    fs::path out_source;
    std::string version;
};

static Cli parse_cli(int argc, char** argv) {
    Cli cli;
    for (int i = 1; i < argc; ++i) {
        std::string_view a = argv[i];
        auto next = [&]() -> std::string {
            if (i + 1 >= argc) { std::fprintf(stderr, "%s needs a value\n", argv[i]); std::exit(2); }
            return argv[++i];
        };
        if      (a == "--ucdxml")     cli.ucdxml     = next();
        else if (a == "--out-header") cli.out_header = next();
        else if (a == "--out-source") cli.out_source = next();
        else if (a == "--version")    cli.version    = next();
        else { std::fprintf(stderr, "unknown arg %s\n", argv[i]); std::exit(2); }
    }
    if (cli.ucdxml.empty() || cli.out_header.empty() || cli.out_source.empty()) {
        std::fprintf(stderr, "required: --ucdxml --out-header --out-source\n");
        std::exit(2);
    }
    return cli;
}

// ===== Property value alias maps (UCDXML short -> long) =====

static const std::unordered_map<std::string, std::string>& gcb_map() {
    static const std::unordered_map<std::string, std::string> m = {
        {"XX", "Other"}, {"PP", "Prepend"}, {"CR", "CR"}, {"LF", "LF"},
        {"CN", "Control"}, {"EX", "Extend"}, {"RI", "Regional_Indicator"},
        {"SM", "SpacingMark"}, {"L", "L"}, {"V", "V"}, {"T", "T"},
        {"LV", "LV"}, {"LVT", "LVT"}, {"ZWJ", "ZWJ"},
    };
    return m;
}
static const std::unordered_map<std::string, std::string>& wb_map() {
    static const std::unordered_map<std::string, std::string> m = {
        {"XX", "Other"}, {"DQ", "Double_Quote"}, {"SQ", "Single_Quote"},
        {"HL", "Hebrew_Letter"}, {"LE", "ALetter"}, {"NU", "Numeric"},
        {"EX", "ExtendNumLet"}, {"ML", "MidLetter"}, {"MB", "MidNumLet"},
        {"MN", "MidNum"}, {"KA", "Katakana"}, {"RI", "Regional_Indicator"},
        {"CR", "CR"}, {"LF", "LF"}, {"NL", "Newline"}, {"ZWJ", "ZWJ"},
        {"FO", "Format"}, {"Extend", "Extend"}, {"WSegSpace", "WSegSpace"},
    };
    return m;
}
static const std::unordered_map<std::string, std::string>& sb_map() {
    static const std::unordered_map<std::string, std::string> m = {
        {"XX", "Other"}, {"EX", "Extend"}, {"FO", "Format"}, {"SP", "Sp"},
        {"LO", "Lower"}, {"UP", "Upper"}, {"LE", "OLetter"}, {"NU", "Numeric"},
        {"AT", "ATerm"}, {"ST", "STerm"}, {"CL", "Close"}, {"SC", "SContinue"},
        {"SE", "Sep"}, {"CR", "CR"}, {"LF", "LF"},
    };
    return m;
}

static std::string translate(const std::unordered_map<std::string, std::string>& m,
                             const std::string& short_val) {
    auto it = m.find(short_val);
    if (it != m.end()) return it->second;
    return short_val;  // InCB values are already long form
}

// ===== Table representation =====

struct RangeRecord { uint32_t start, end; uint8_t prop_id; };

struct PropTable {
    std::string              name;
    std::vector<std::string> labels;
    std::vector<RangeRecord> records;
    uint8_t                  default_id;
};

static uint8_t intern_label(PropTable& t, const std::string& label) {
    for (size_t i = 0; i < t.labels.size(); ++i)
        if (t.labels[i] == label) return (uint8_t)i;
    if (t.labels.size() >= 255) {
        std::fprintf(stderr, "table %s exceeds 255 labels\n", t.name.c_str());
        std::exit(4);
    }
    t.labels.push_back(label);
    return (uint8_t)(t.labels.size() - 1);
}

static void coalesce(std::vector<RangeRecord>& rs) {
    if (rs.empty()) return;
    std::sort(rs.begin(), rs.end(),
              [](const auto& a, const auto& b) { return a.start < b.start; });
    std::vector<RangeRecord> out;
    out.reserve(rs.size());
    out.push_back(rs[0]);
    for (size_t i = 1; i < rs.size(); ++i) {
        auto& last = out.back();
        if (rs[i].start == last.end + 1 && rs[i].prop_id == last.prop_id) last.end = rs[i].end;
        else out.push_back(rs[i]);
    }
    rs = std::move(out);
}

struct NfcDecomp { uint32_t codepoint; std::vector<uint32_t> decomp; };
struct NfcCompose { uint32_t first, second, composed; };

// ===== SAX state =====

struct SaxState {
    PropTable gb, wb, sb, incb;
    std::vector<std::pair<uint32_t, uint8_t>> ccc_pairs;
    std::unordered_map<uint32_t, std::vector<uint32_t>> base_decomp;
    std::vector<uint32_t> excluded;
    bool in_repertoire = false;
};

static const xmlChar* get_attr(const xmlChar** attrs, const char* name) {
    if (!attrs) return nullptr;
    for (int i = 0; attrs[i]; i += 2)
        if (std::strcmp((const char*)attrs[i], name) == 0) return attrs[i + 1];
    return nullptr;
}

static std::pair<uint32_t, uint32_t> get_cp_range(const xmlChar** attrs) {
    auto cp = get_attr(attrs, "cp");
    if (cp) {
        uint32_t v = (uint32_t)std::stoul((const char*)cp, nullptr, 16);
        return {v, v};
    }
    auto first = get_attr(attrs, "first-cp");
    auto last  = get_attr(attrs, "last-cp");
    if (first && last)
        return { (uint32_t)std::stoul((const char*)first, nullptr, 16),
                 (uint32_t)std::stoul((const char*)last, nullptr, 16) };
    return {0xFFFFFFFFu, 0xFFFFFFFFu};
}

static void handle_char_element(SaxState* st, const xmlChar** attrs) {
    auto [first, last] = get_cp_range(attrs);
    if (first == 0xFFFFFFFFu) return;

    auto gcb_v  = get_attr(attrs, "GCB");
    auto wb_v   = get_attr(attrs, "WB");
    auto sb_v   = get_attr(attrs, "SB");
    auto incb_v = get_attr(attrs, "InCB");
    auto ccc_v  = get_attr(attrs, "ccc");
    auto dt_v   = get_attr(attrs, "dt");
    auto dm_v   = get_attr(attrs, "dm");
    auto cex_v  = get_attr(attrs, "Comp_Ex");
    auto ep_v   = get_attr(attrs, "ExtPict");

    if (gcb_v) {
        std::string gcb = translate(gcb_map(), (const char*)gcb_v);
        if (ep_v && std::strcmp((const char*)ep_v, "Y") == 0) gcb = "Extended_Pictographic";
        st->gb.records.push_back({first, last, intern_label(st->gb, gcb)});
    }
    if (wb_v) {
        std::string wb = translate(wb_map(), (const char*)wb_v);
        st->wb.records.push_back({first, last, intern_label(st->wb, wb)});
    }
    if (sb_v) {
        std::string sb = translate(sb_map(), (const char*)sb_v);
        st->sb.records.push_back({first, last, intern_label(st->sb, sb)});
    }
    if (incb_v) {
        std::string incb = (const char*)incb_v;
        st->incb.records.push_back({first, last, intern_label(st->incb, incb)});
    }
    if (ccc_v && first == last) {
        uint8_t cc = (uint8_t)std::stoul((const char*)ccc_v, nullptr, 10);
        if (cc != 0) st->ccc_pairs.emplace_back(first, cc);
    }
    if (dt_v && dm_v && first == last) {
        std::string dt = (const char*)dt_v;
        std::string dm = (const char*)dm_v;
        if (dt == "can" && dm != "#") {
            std::vector<uint32_t> d;
            const char* p = dm.c_str();
            char* endp;
            while (*p) {
                uint32_t v = (uint32_t)std::strtoul(p, &endp, 16);
                if (endp == p) break;
                d.push_back(v);
                p = endp;
                while (*p == ' ') p += 1;
            }
            if (!d.empty()) st->base_decomp[first] = d;
        }
    }
    if (cex_v && std::strcmp((const char*)cex_v, "Y") == 0 && first == last)
        st->excluded.push_back(first);
}

extern "C" void sax_start_element(void* user, const xmlChar* name, const xmlChar** attrs) {
    auto* st = (SaxState*)user;
    if (std::strcmp((const char*)name, "repertoire") == 0) { st->in_repertoire = true; return; }
    if (!st->in_repertoire) return;
    if (std::strcmp((const char*)name, "char") == 0
     || std::strcmp((const char*)name, "reserved") == 0
     || std::strcmp((const char*)name, "noncharacter") == 0
     || std::strcmp((const char*)name, "surrogate") == 0)
        handle_char_element(st, attrs);
}

extern "C" void sax_end_element(void* user, const xmlChar* name) {
    auto* st = (SaxState*)user;
    if (std::strcmp((const char*)name, "repertoire") == 0) st->in_repertoire = false;
}

// ===== Emission =====

static std::string sanitize(const std::string& s) {
    std::string out;
    out.reserve(s.size());
    for (char c : s)
        out.push_back(std::isalnum((unsigned char)c) || c == '_'
                      ? (char)std::toupper((unsigned char)c) : '_');
    return out;
}

static void emit_table_header(std::ostream& h, const PropTable& t) {
    h << "/* " << t.name << " property labels */\n";
    for (size_t i = 0; i < t.labels.size(); ++i)
        h << "#define LAPLACE_UCD_" << sanitize(t.name) << "_"
          << sanitize(t.labels[i]) << " " << i << "u\n";
    h << "#define LAPLACE_UCD_" << sanitize(t.name) << "_DEFAULT "
      << (unsigned)t.default_id << "u\n\n";
    h << "extern const laplace_ucd_range_t laplace_ucd_" << t.name << "_ranges[];\n";
    h << "extern const size_t              laplace_ucd_" << t.name << "_count;\n";
    h << "uint8_t laplace_ucd_" << t.name << "_lookup(uint32_t codepoint);\n\n";
}

static void emit_table_source(std::ostream& c, const PropTable& t) {
    c << "const laplace_ucd_range_t laplace_ucd_" << t.name << "_ranges[] = {\n";
    for (const auto& r : t.records)
        c << "    { 0x" << std::hex << r.start << ", 0x" << r.end
          << ", " << std::dec << (unsigned)r.prop_id << " },\n";
    c << "};\n";
    c << "const size_t laplace_ucd_" << t.name << "_count = " << t.records.size() << ";\n\n";
    c << "uint8_t laplace_ucd_" << t.name << "_lookup(uint32_t cp) {\n"
      << "    size_t lo = 0, hi = " << t.records.size() << ";\n"
      << "    while (lo < hi) {\n"
      << "        size_t mid = lo + ((hi - lo) >> 1);\n"
      << "        const laplace_ucd_range_t* r = &laplace_ucd_" << t.name << "_ranges[mid];\n"
      << "        if (cp < r->start) hi = mid;\n"
      << "        else if (cp > r->end) lo = mid + 1;\n"
      << "        else return r->prop_id;\n"
      << "    }\n"
      << "    return " << (unsigned)t.default_id << ";\n"
      << "}\n\n";
}

int main(int argc, char** argv) {
    Cli cli = parse_cli(argc, argv);

    if (!fs::exists(cli.ucdxml)) {
        std::fprintf(stderr, "UCDXML file not found: %s\n", cli.ucdxml.string().c_str());
        return 3;
    }

    SaxState st;
    st.gb.name = "gb";     st.gb.default_id   = intern_label(st.gb, "Other");
    st.wb.name = "wb";     st.wb.default_id   = intern_label(st.wb, "Other");
    st.sb.name = "sb";     st.sb.default_id   = intern_label(st.sb, "Other");
    st.incb.name = "incb"; st.incb.default_id = intern_label(st.incb, "None");

    xmlSAXHandler sax{};
    sax.initialized  = XML_SAX2_MAGIC;
    sax.startElement = sax_start_element;
    sax.endElement   = sax_end_element;

    LIBXML_TEST_VERSION
    int rc = xmlSAXUserParseFile(&sax, &st, cli.ucdxml.string().c_str());
    if (rc != 0) {
        std::fprintf(stderr, "libxml2 SAX parse failed (rc=%d) on %s\n", rc, cli.ucdxml.string().c_str());
        return 4;
    }
    xmlCleanupParser();

    coalesce(st.gb.records);
    coalesce(st.wb.records);
    coalesce(st.sb.records);
    coalesce(st.incb.records);

    std::sort(st.ccc_pairs.begin(), st.ccc_pairs.end());
    st.ccc_pairs.erase(std::unique(st.ccc_pairs.begin(), st.ccc_pairs.end()), st.ccc_pairs.end());
    std::sort(st.excluded.begin(), st.excluded.end());
    st.excluded.erase(std::unique(st.excluded.begin(), st.excluded.end()), st.excluded.end());

    auto is_excluded = [&](uint32_t cp) {
        return std::binary_search(st.excluded.begin(), st.excluded.end(), cp);
    };
    auto find_ccc = [&](uint32_t cp) -> uint8_t {
        auto it = std::lower_bound(st.ccc_pairs.begin(), st.ccc_pairs.end(),
                                    std::make_pair(cp, (uint8_t)0));
        return (it != st.ccc_pairs.end() && it->first == cp) ? it->second : 0;
    };

    std::vector<NfcDecomp> decomps;
    decomps.reserve(st.base_decomp.size());
    std::function<void(uint32_t, std::vector<uint32_t>&)> full_decompose;
    full_decompose = [&](uint32_t cp, std::vector<uint32_t>& out) {
        auto it = st.base_decomp.find(cp);
        if (it == st.base_decomp.end() || it->second.empty()) { out.push_back(cp); return; }
        for (uint32_t c : it->second) full_decompose(c, out);
    };
    for (const auto& kv : st.base_decomp) {
        NfcDecomp d; d.codepoint = kv.first;
        full_decompose(kv.first, d.decomp);
        decomps.push_back(std::move(d));
    }
    std::sort(decomps.begin(), decomps.end(),
              [](const auto& a, const auto& b) { return a.codepoint < b.codepoint; });

    std::vector<uint32_t> flat_decomp;
    std::vector<std::pair<uint32_t, uint32_t>> rec_offsets;
    for (const auto& d : decomps) {
        rec_offsets.emplace_back((uint32_t)flat_decomp.size(), (uint32_t)d.decomp.size());
        for (uint32_t c : d.decomp) flat_decomp.push_back(c);
    }

    std::vector<NfcCompose> composes;
    for (const auto& kv : st.base_decomp) {
        const uint32_t cp = kv.first;
        const auto& decomp = kv.second;
        if (decomp.size() != 2) continue;
        if (is_excluded(cp)) continue;
        if (find_ccc(decomp[0]) != 0) continue;
        if (find_ccc(cp) != 0) continue;
        composes.push_back({decomp[0], decomp[1], cp});
    }
    std::sort(composes.begin(), composes.end(),
              [](const auto& a, const auto& b) {
                  return a.first != b.first ? a.first < b.first : a.second < b.second;
              });

    fs::create_directories(cli.out_header.parent_path());
    fs::create_directories(cli.out_source.parent_path());
    std::ofstream h(cli.out_header);
    std::ofstream c(cli.out_source);
    if (!h || !c) { std::fprintf(stderr, "cannot write outputs\n"); return 5; }

    h << "/* ucd_tables.generated.h - DO NOT EDIT.\n";
    h << " * Generated by laplace_ucd_tables_emit from UCDXML " << cli.version << ".\n";
    h << " * Input: " << cli.ucdxml.filename().string() << "\n";
    h << " * Per ADR 0047 + ADR 0053; deterministic per LAPLACE_UNICODE_VERSION.\n */\n";
    h << "#pragma once\n#include <stdint.h>\n#include <stddef.h>\n\n";
    h << "#define LAPLACE_UNICODE_VERSION_STRING \"" << cli.version << "\"\n\n";
    h << "#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n";
    h << "typedef struct { uint32_t start; uint32_t end; uint8_t prop_id; } laplace_ucd_range_t;\n\n";
    for (const auto* t : {&st.gb, &st.wb, &st.sb, &st.incb}) emit_table_header(h, *t);
    h << "/* === NFC tables === */\n";
    h << "typedef struct { uint32_t cp; uint8_t ccc; } laplace_ucd_ccc_record_t;\n";
    h << "extern const laplace_ucd_ccc_record_t laplace_ucd_ccc_records[];\n";
    h << "extern const size_t laplace_ucd_ccc_count;\n";
    h << "uint8_t laplace_ucd_ccc_lookup(uint32_t cp);\n\n";
    h << "typedef struct { uint32_t cp; uint32_t start_idx; uint32_t length; } laplace_ucd_decomp_record_t;\n";
    h << "extern const laplace_ucd_decomp_record_t laplace_ucd_decomp_records[];\n";
    h << "extern const size_t laplace_ucd_decomp_count;\n";
    h << "extern const uint32_t laplace_ucd_decomp_data[];\n";
    h << "extern const size_t laplace_ucd_decomp_data_count;\n";
    h << "int laplace_ucd_decomp_lookup(uint32_t cp, uint32_t* out_start_idx, uint32_t* out_length);\n\n";
    h << "typedef struct { uint32_t first; uint32_t second; uint32_t composed; } laplace_ucd_compose_record_t;\n";
    h << "extern const laplace_ucd_compose_record_t laplace_ucd_compose_records[];\n";
    h << "extern const size_t laplace_ucd_compose_count;\n";
    h << "int laplace_ucd_compose_lookup(uint32_t first, uint32_t second, uint32_t* out_composed);\n\n";
    h << "#ifdef __cplusplus\n} /* extern \"C\" */\n#endif\n";

    c << "/* ucd_tables.generated.c - DO NOT EDIT.\n";
    c << " * Generated by laplace_ucd_tables_emit from UCDXML " << cli.version << ".\n */\n";
    c << "#include \"" << cli.out_header.filename().string() << "\"\n\n";
    for (const auto* t : {&st.gb, &st.wb, &st.sb, &st.incb}) emit_table_source(c, *t);

    c << "const laplace_ucd_ccc_record_t laplace_ucd_ccc_records[] = {\n";
    for (auto [cp, v] : st.ccc_pairs)
        c << "    { 0x" << std::hex << cp << ", " << std::dec << (unsigned)v << " },\n";
    c << "};\n";
    c << "const size_t laplace_ucd_ccc_count = " << st.ccc_pairs.size() << ";\n\n";
    c << "uint8_t laplace_ucd_ccc_lookup(uint32_t cp) {\n"
      << "    size_t lo = 0, hi = " << st.ccc_pairs.size() << ";\n"
      << "    while (lo < hi) {\n"
      << "        size_t mid = lo + ((hi - lo) >> 1);\n"
      << "        const laplace_ucd_ccc_record_t* r = &laplace_ucd_ccc_records[mid];\n"
      << "        if (cp < r->cp) hi = mid;\n"
      << "        else if (cp > r->cp) lo = mid + 1;\n"
      << "        else return r->ccc;\n"
      << "    }\n    return 0;\n}\n\n";

    c << "const uint32_t laplace_ucd_decomp_data[] = {\n";
    for (size_t i = 0; i < flat_decomp.size(); i += 8) {
        c << "    ";
        for (size_t j = i; j < std::min(i + 8, flat_decomp.size()); ++j)
            c << "0x" << std::hex << flat_decomp[j] << ", ";
        c << "\n";
    }
    c << std::dec << "};\n";
    c << "const size_t laplace_ucd_decomp_data_count = " << flat_decomp.size() << ";\n\n";
    c << "const laplace_ucd_decomp_record_t laplace_ucd_decomp_records[] = {\n";
    for (size_t i = 0; i < decomps.size(); ++i)
        c << "    { 0x" << std::hex << decomps[i].codepoint << ", "
          << std::dec << rec_offsets[i].first << ", " << rec_offsets[i].second << " },\n";
    c << "};\n";
    c << "const size_t laplace_ucd_decomp_count = " << decomps.size() << ";\n\n";
    c << "int laplace_ucd_decomp_lookup(uint32_t cp, uint32_t* out_start_idx, uint32_t* out_length) {\n"
      << "    size_t lo = 0, hi = " << decomps.size() << ";\n"
      << "    while (lo < hi) {\n"
      << "        size_t mid = lo + ((hi - lo) >> 1);\n"
      << "        const laplace_ucd_decomp_record_t* r = &laplace_ucd_decomp_records[mid];\n"
      << "        if (cp < r->cp) hi = mid;\n"
      << "        else if (cp > r->cp) lo = mid + 1;\n"
      << "        else { *out_start_idx = r->start_idx; *out_length = r->length; return 1; }\n"
      << "    }\n    return 0;\n}\n\n";

    c << "const laplace_ucd_compose_record_t laplace_ucd_compose_records[] = {\n";
    for (const auto& m : composes)
        c << "    { 0x" << std::hex << m.first << ", 0x" << m.second
          << ", 0x" << m.composed << " },\n";
    c << std::dec << "};\n";
    c << "const size_t laplace_ucd_compose_count = " << composes.size() << ";\n\n";
    c << "int laplace_ucd_compose_lookup(uint32_t first, uint32_t second, uint32_t* out_composed) {\n"
      << "    size_t lo = 0, hi = " << composes.size() << ";\n"
      << "    while (lo < hi) {\n"
      << "        size_t mid = lo + ((hi - lo) >> 1);\n"
      << "        const laplace_ucd_compose_record_t* r = &laplace_ucd_compose_records[mid];\n"
      << "        if (first < r->first || (first == r->first && second < r->second)) hi = mid;\n"
      << "        else if (first > r->first || (first == r->first && second > r->second)) lo = mid + 1;\n"
      << "        else { *out_composed = r->composed; return 1; }\n"
      << "    }\n    return 0;\n}\n\n";

    std::fprintf(stderr,
        "laplace_ucd_tables_emit: ucdxml=%s ver=%s\n"
        "  gb:   %zu labels, %zu ranges\n"
        "  wb:   %zu labels, %zu ranges\n"
        "  sb:   %zu labels, %zu ranges\n"
        "  incb: %zu labels, %zu ranges\n"
        "  ccc:  %zu records\n"
        "  decomp: %zu records (%zu codepoints in flat data)\n"
        "  compose: %zu records\n",
        cli.ucdxml.string().c_str(), cli.version.c_str(),
        st.gb.labels.size(), st.gb.records.size(),
        st.wb.labels.size(), st.wb.records.size(),
        st.sb.labels.size(), st.sb.records.size(),
        st.incb.labels.size(), st.incb.records.size(),
        st.ccc_pairs.size(),
        decomps.size(), flat_decomp.size(),
        composes.size());
    return 0;
}
