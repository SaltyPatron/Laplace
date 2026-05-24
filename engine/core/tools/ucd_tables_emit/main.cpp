// engine/core/tools/ucd_tables_emit/main.cpp
//
// Build-time codegen tool: reads UCD .txt files and emits compact C
// lookup tables for UAX#29 segmentation (Grapheme / Word / Sentence
// Break Property) + NFC normalization (CCC, canonical decomposition,
// composition exclusions) — per ADR 0047 + ADR 0053.
//
// Usage:
//   laplace_ucd_tables_emit \
//       --ucd-path     /vault/Data/Unicode/Public/<ver>/ucd \
//       --emoji-path   /vault/Data/Unicode/Public/<ver>/ucd/emoji \
//       --out-header   .../ucd_tables.generated.h \
//       --out-source   .../ucd_tables.generated.c \
//       --version      18.0.0
//
// Determinism contract (RULES R7): same UCD content + same emit-tool
// source → byte-identical generated files. We never iterate maps without
// sorting; never use timestamps; never use uninitialized memory.

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
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

namespace fs = std::filesystem;

// ===== CLI =====

struct Cli {
    fs::path ucd_path;
    fs::path emoji_path;
    fs::path out_header;
    fs::path out_source;
    std::string version;
};

static Cli parse_cli(int argc, char** argv) {
    Cli cli;
    for (int i = 1; i < argc; ++i) {
        std::string_view a = argv[i];
        auto next = [&]() -> std::string {
            if (i + 1 >= argc) {
                std::fprintf(stderr, "argument %s requires a value\n", argv[i]);
                std::exit(2);
            }
            return argv[++i];
        };
        if (a == "--ucd-path")     cli.ucd_path   = next();
        else if (a == "--emoji-path") cli.emoji_path = next();
        else if (a == "--out-header") cli.out_header = next();
        else if (a == "--out-source") cli.out_source = next();
        else if (a == "--version")    cli.version    = next();
        else { std::fprintf(stderr, "unknown arg %s\n", argv[i]); std::exit(2); }
    }
    if (cli.ucd_path.empty() || cli.out_header.empty() || cli.out_source.empty()) {
        std::fprintf(stderr, "required: --ucd-path --out-header --out-source\n");
        std::exit(2);
    }
    if (cli.emoji_path.empty()) cli.emoji_path = cli.ucd_path / "emoji";
    return cli;
}

// ===== UCD .txt parsing =====

// Returns (start, end) inclusive for a codepoint or range field
// "0600", "0600..0605".
static std::pair<uint32_t, uint32_t> parse_cp_or_range(std::string_view tok) {
    size_t dot = tok.find("..");
    auto parse_hex = [](std::string_view s) {
        return (uint32_t)std::stoul(std::string(s), nullptr, 16);
    };
    if (dot == std::string_view::npos) {
        uint32_t cp = parse_hex(tok);
        return {cp, cp};
    }
    return {parse_hex(tok.substr(0, dot)), parse_hex(tok.substr(dot + 2))};
}

// Trim ASCII whitespace from both ends.
static std::string_view trim(std::string_view s) {
    while (!s.empty() && std::isspace((unsigned char)s.front())) s.remove_prefix(1);
    while (!s.empty() && std::isspace((unsigned char)s.back()))  s.remove_suffix(1);
    return s;
}

// Strip an in-line "#" comment; returns the data part trimmed.
static std::string_view strip_comment(std::string_view line) {
    size_t hash = line.find('#');
    if (hash != std::string_view::npos) line = line.substr(0, hash);
    return trim(line);
}

// Parses a 3-field UCD property file (DerivedCoreProperties.txt format):
//   <code-or-range> ; <property-name> ; <property-value> # <comment>
static void parse_prop_file_3field(
    const fs::path& p,
    const std::function<void(uint32_t, uint32_t, const std::string&, const std::string&)>& consume) {
    std::ifstream f(p);
    if (!f) {
        std::fprintf(stderr, "cannot open %s\n", p.string().c_str());
        std::exit(3);
    }
    std::string line;
    while (std::getline(f, line)) {
        auto sv = strip_comment(line);
        if (sv.empty()) continue;
        size_t s1 = sv.find(';');
        if (s1 == std::string_view::npos) continue;
        size_t s2 = sv.find(';', s1 + 1);
        if (s2 == std::string_view::npos) continue;
        auto range_tok = trim(sv.substr(0, s1));
        auto name_tok  = trim(sv.substr(s1 + 1, s2 - s1 - 1));
        auto val_tok   = trim(sv.substr(s2 + 1));
        auto [start, end] = parse_cp_or_range(range_tok);
        consume(start, end, std::string(name_tok), std::string(val_tok));
    }
}

// Parses a UCD property file (2-field):
//   <code-or-range> ; <property> # <comment>
// Calls `consume(start, end, property)` per record. Ignores blank lines + comments.
static void parse_prop_file(const fs::path& p,
                            const std::function<void(uint32_t, uint32_t, const std::string&)>& consume) {
    std::ifstream f(p);
    if (!f) {
        std::fprintf(stderr, "cannot open %s\n", p.string().c_str());
        std::exit(3);
    }
    std::string line;
    while (std::getline(f, line)) {
        auto sv = strip_comment(line);
        if (sv.empty()) continue;
        // split on ';'
        size_t semi = sv.find(';');
        if (semi == std::string_view::npos) continue;
        auto range_tok = trim(sv.substr(0, semi));
        auto prop_tok  = trim(sv.substr(semi + 1));
        auto [start, end] = parse_cp_or_range(range_tok);
        consume(start, end, std::string(prop_tok));
    }
}

// ===== Range table representation =====

// Each generated table is a sorted array of {start, end, prop_id} records.
// Lookup is binary search. The prop_id is the index into a per-table
// label vector emitted alongside the data (so test diagnostics can map
// id back to property name).

struct RangeRecord {
    uint32_t start;
    uint32_t end;
    uint8_t  prop_id;
};

struct PropTable {
    std::string                       name;       // e.g. "gb" (grapheme)
    std::vector<std::string>          labels;     // ordered; index = prop_id
    std::vector<RangeRecord>          records;
    uint8_t                           default_id; // for codepoints not listed
};

// Coalesce adjacent records with the same prop_id into a single range.
// Records must be sorted by start beforehand.
static void coalesce(std::vector<RangeRecord>& rs) {
    if (rs.empty()) return;
    std::vector<RangeRecord> out;
    out.reserve(rs.size());
    out.push_back(rs[0]);
    for (size_t i = 1; i < rs.size(); ++i) {
        auto& last = out.back();
        if (rs[i].start == last.end + 1 && rs[i].prop_id == last.prop_id) {
            last.end = rs[i].end;
        } else {
            out.push_back(rs[i]);
        }
    }
    rs = std::move(out);
}

// Returns the id for the label, allocating a new id if first seen.
static uint8_t intern_label(PropTable& t, const std::string& label) {
    for (size_t i = 0; i < t.labels.size(); ++i) {
        if (t.labels[i] == label) return (uint8_t)i;
    }
    if (t.labels.size() >= 255) {
        std::fprintf(stderr, "table %s exceeds 255 labels\n", t.name.c_str());
        std::exit(4);
    }
    t.labels.push_back(label);
    return (uint8_t)(t.labels.size() - 1);
}

// Read a UCD property file into a PropTable.
// "default_label" is the property value assigned to codepoints not in the file
// (per the @missing directive at top of each UCD prop file).
static PropTable load_prop_table(const fs::path& path, std::string name, std::string default_label) {
    PropTable t;
    t.name = std::move(name);
    t.default_id = intern_label(t, default_label);
    parse_prop_file(path, [&](uint32_t s, uint32_t e, const std::string& prop) {
        uint8_t id = intern_label(t, prop);
        t.records.push_back({s, e, id});
    });
    std::sort(t.records.begin(), t.records.end(),
              [](const auto& a, const auto& b) { return a.start < b.start; });
    coalesce(t.records);
    return t;
}

// Special handling: emoji-data.txt contains both "Emoji" and
// "Extended_Pictographic" + others. We only need Extended_Pictographic
// for grapheme break (GB11). Filter accordingly.
static void merge_extended_pictographic(PropTable& gb, const fs::path& emoji_data_txt) {
    std::vector<std::pair<uint32_t, uint32_t>> ranges;
    parse_prop_file(emoji_data_txt, [&](uint32_t s, uint32_t e, const std::string& prop) {
        if (prop == "Extended_Pictographic") ranges.emplace_back(s, e);
    });
    uint8_t id = intern_label(gb, "Extended_Pictographic");
    for (auto [s, e] : ranges) gb.records.push_back({s, e, id});
    std::sort(gb.records.begin(), gb.records.end(),
              [](const auto& a, const auto& b) {
                  return a.start != b.start ? a.start < b.start : a.end < b.end;
              });
    // After merge, two records may overlap (e.g. an Extend codepoint that
    // is also Extended_Pictographic). The GB rules read both properties
    // so we need to keep the most-specific. The convention used by the
    // UCD itself: Extended_Pictographic wins over the basic
    // grapheme-break property for the purposes of GB11. We model that by
    // KEEPING the Extended_Pictographic record and overlaying it as a
    // second pass at lookup time. For now, drop duplicate (start,end)
    // pairs and rely on the segmenter to consult both tables.
    //
    // Simpler approach: split overlapping ranges; for each codepoint in
    // an overlap, pick the Extended_Pictographic value (since the GB11
    // rule only fires on Extended_Pictographic).
    //
    // Implement: build a flat per-codepoint map for the affected range,
    // then re-coalesce. The number of affected codepoints is small
    // (~3K) so this is cheap.

    // Sort + walk.
    std::vector<RangeRecord> out;
    std::map<uint32_t, uint8_t> overlay;
    for (const auto& r : gb.records) {
        for (uint32_t cp = r.start; cp <= r.end; ++cp) {
            // Extended_Pictographic id wins
            auto it = overlay.find(cp);
            if (it == overlay.end() || gb.labels[r.prop_id] == "Extended_Pictographic") {
                overlay[cp] = r.prop_id;
            }
        }
    }
    // Walk sorted; coalesce runs of same id.
    uint32_t run_start = 0;
    uint8_t  run_id    = 0;
    bool     in_run    = false;
    uint32_t prev_cp   = 0;
    for (auto [cp, pid] : overlay) {
        if (!in_run) {
            run_start = cp; run_id = pid; in_run = true; prev_cp = cp;
            continue;
        }
        if (cp == prev_cp + 1 && pid == run_id) {
            prev_cp = cp;
        } else {
            out.push_back({run_start, prev_cp, run_id});
            run_start = cp; run_id = pid; prev_cp = cp;
        }
    }
    if (in_run) out.push_back({run_start, prev_cp, run_id});
    gb.records = std::move(out);
}

// ===== C-source emission =====

static std::string sanitize_label_for_enum(const std::string& s) {
    std::string out;
    out.reserve(s.size());
    for (char c : s) {
        if (std::isalnum((unsigned char)c) || c == '_') out.push_back((char)std::toupper((unsigned char)c));
        else out.push_back('_');
    }
    return out;
}

static void emit_header(std::ostream& h,
                        const std::string& version,
                        const std::vector<const PropTable*>& tables) {
    h << "/* ucd_tables.generated.h — DO NOT EDIT.\n";
    h << " * Generated by laplace_ucd_tables_emit from UCD " << version << ".\n";
    h << " * Inputs: GraphemeBreakProperty / WordBreakProperty /\n";
    h << " *         SentenceBreakProperty (+ Extended_Pictographic for grapheme).\n";
    h << " * Per ADR 0047 + ADR 0053; deterministic per LAPLACE_UNICODE_VERSION.\n";
    h << " */\n";
    h << "#pragma once\n";
    h << "#include <stdint.h>\n";
    h << "#include <stddef.h>\n\n";
    h << "#define LAPLACE_UNICODE_VERSION_STRING \"" << version << "\"\n\n";
    h << "#ifdef __cplusplus\n";
    h << "extern \"C\" {\n";
    h << "#endif\n\n";
    h << "typedef struct {\n";
    h << "    uint32_t start;\n";
    h << "    uint32_t end;\n";
    h << "    uint8_t  prop_id;\n";
    h << "} laplace_ucd_range_t;\n\n";

    for (const auto* tp : tables) {
        const auto& t = *tp;
        // Per-table enum of property labels
        h << "/* " << t.name << " property labels (ids match prop_id in *_ranges). */\n";
        for (size_t i = 0; i < t.labels.size(); ++i) {
            h << "#define LAPLACE_UCD_" << sanitize_label_for_enum(t.name) << "_"
              << sanitize_label_for_enum(t.labels[i]) << " " << i << "u\n";
        }
        h << "#define LAPLACE_UCD_" << sanitize_label_for_enum(t.name) << "_DEFAULT "
          << (unsigned)t.default_id << "u\n\n";
        h << "extern const laplace_ucd_range_t laplace_ucd_" << t.name << "_ranges[];\n";
        h << "extern const size_t              laplace_ucd_" << t.name << "_count;\n\n";
        h << "/* Binary-search lookup. Returns DEFAULT when codepoint is not in any range. */\n";
        h << "uint8_t laplace_ucd_" << t.name << "_lookup(uint32_t codepoint);\n\n";
    }
    h << "#ifdef __cplusplus\n";
    h << "} /* extern \"C\" */\n";
    h << "#endif\n";
}

static void emit_source(std::ostream& c,
                        const std::string& version,
                        const std::vector<const PropTable*>& tables,
                        const fs::path& header_for_include) {
    c << "/* ucd_tables.generated.c — DO NOT EDIT.\n";
    c << " * Generated by laplace_ucd_tables_emit from UCD " << version << ".\n";
    c << " */\n";
    c << "#include \"" << header_for_include.filename().string() << "\"\n\n";

    for (const auto* tp : tables) {
        const auto& t = *tp;
        c << "const laplace_ucd_range_t laplace_ucd_" << t.name << "_ranges[] = {\n";
        for (const auto& r : t.records) {
            c << "    { 0x" << std::hex << r.start << ", 0x" << r.end
              << ", " << std::dec << (unsigned)r.prop_id << " },\n";
        }
        c << "};\n";
        c << "const size_t laplace_ucd_" << t.name << "_count = "
          << t.records.size() << ";\n\n";
        c << "uint8_t laplace_ucd_" << t.name << "_lookup(uint32_t cp) {\n";
        c << "    size_t lo = 0, hi = " << t.records.size() << ";\n";
        c << "    while (lo < hi) {\n";
        c << "        size_t mid = lo + ((hi - lo) >> 1);\n";
        c << "        const laplace_ucd_range_t* r = &laplace_ucd_" << t.name << "_ranges[mid];\n";
        c << "        if (cp < r->start) hi = mid;\n";
        c << "        else if (cp > r->end) lo = mid + 1;\n";
        c << "        else return r->prop_id;\n";
        c << "    }\n";
        c << "    return " << (unsigned)t.default_id << ";\n";
        c << "}\n\n";
    }
}

// ===== main =====

// ===== NFC tables =====

struct NfcDecomp {
    uint32_t              codepoint;
    std::vector<uint32_t> decomp;     // 1+ codepoints (canonical only)
};

struct NfcCompose {
    uint32_t first;     // starter
    uint32_t second;    // following codepoint
    uint32_t composed;
};

// Parse UnicodeData.txt. Per UCD docs, each line has 14 semicolon-
// separated fields. We extract:
//   field[0]  = codepoint hex
//   field[3]  = canonical_combining_class (decimal 0..254)
//   field[5]  = decomposition mapping (may be empty; may start with
//              "<TYPE>" for compatibility — we IGNORE those for NFC)
//
// Range markers ("<...First>" / "<...Last>" name conventions) define
// CCC=0 ranges with no decomposition — we can ignore them since CCC=0
// is the default we already model.
static void parse_unicode_data(
    const fs::path& path,
    const std::function<void(uint32_t cp, uint8_t ccc, const std::vector<uint32_t>& decomp)>& consume) {
    std::ifstream f(path);
    if (!f) { std::fprintf(stderr, "cannot open %s\n", path.string().c_str()); std::exit(3); }
    std::string line;
    while (std::getline(f, line)) {
        if (line.empty() || line[0] == '#') continue;
        std::vector<std::string> fields;
        size_t pos = 0;
        while (pos != std::string::npos) {
            size_t next = line.find(';', pos);
            if (next == std::string::npos) { fields.push_back(line.substr(pos)); break; }
            fields.push_back(line.substr(pos, next - pos));
            pos = next + 1;
        }
        if (fields.size() < 6) continue;
        uint32_t cp = (uint32_t)std::stoul(fields[0], nullptr, 16);
        uint8_t  ccc = 0;
        try { ccc = (uint8_t)std::stoul(fields[3], nullptr, 10); } catch (...) {}

        std::vector<uint32_t> decomp;
        const std::string& d = fields[5];
        // Skip if starts with '<' (compatibility decomposition).
        if (!d.empty() && d[0] != '<') {
            std::istringstream iss(d);
            std::string tok;
            while (iss >> tok) {
                try { decomp.push_back((uint32_t)std::stoul(tok, nullptr, 16)); }
                catch (...) { decomp.clear(); break; }
            }
        }
        consume(cp, ccc, decomp);
    }
}

static std::vector<uint32_t> parse_composition_exclusions(const fs::path& path) {
    std::vector<uint32_t> out;
    std::ifstream f(path);
    if (!f) { std::fprintf(stderr, "cannot open %s\n", path.string().c_str()); std::exit(3); }
    std::string line;
    while (std::getline(f, line)) {
        auto sv = strip_comment(line);
        if (sv.empty()) continue;
        try { out.push_back((uint32_t)std::stoul(std::string(sv), nullptr, 16)); }
        catch (...) {}
    }
    std::sort(out.begin(), out.end());
    return out;
}

// Full-decompose a codepoint using the canonical decomposition table,
// recursing through any decompositions of its decomposition components.
// Result is the "Full canonical decomposition" per UAX #15.
static void full_decompose_into(
    uint32_t cp,
    const std::unordered_map<uint32_t, std::vector<uint32_t>>& base,
    std::vector<uint32_t>& out) {
    auto it = base.find(cp);
    if (it == base.end() || it->second.empty()) {
        out.push_back(cp);
        return;
    }
    for (uint32_t comp : it->second) full_decompose_into(comp, base, out);
}

static void emit_nfc_tables(std::ostream& h, std::ostream& c,
                             const std::vector<std::pair<uint32_t, uint8_t>>& ccc,
                             const std::vector<NfcDecomp>& decomps,
                             const std::vector<std::pair<uint32_t, uint32_t>>& rec_offsets,
                             const std::vector<uint32_t>& flat_decomp_data,
                             const std::vector<NfcCompose>& composes) {
    h << "\n/* === NFC tables === */\n";
    h << "#ifdef __cplusplus\nextern \"C\" {\n#endif\n";

    // ccc lookup: sorted array of (cp, ccc)
    h << "typedef struct { uint32_t cp; uint8_t ccc; } laplace_ucd_ccc_record_t;\n";
    h << "extern const laplace_ucd_ccc_record_t laplace_ucd_ccc_records[];\n";
    h << "extern const size_t laplace_ucd_ccc_count;\n";
    h << "uint8_t laplace_ucd_ccc_lookup(uint32_t cp);\n\n";

    h << "typedef struct {\n";
    h << "    uint32_t cp;\n";
    h << "    uint32_t start_idx;    /* into laplace_ucd_decomp_data */\n";
    h << "    uint32_t length;\n";
    h << "} laplace_ucd_decomp_record_t;\n";
    h << "extern const laplace_ucd_decomp_record_t laplace_ucd_decomp_records[];\n";
    h << "extern const size_t laplace_ucd_decomp_count;\n";
    h << "extern const uint32_t laplace_ucd_decomp_data[];\n";
    h << "extern const size_t laplace_ucd_decomp_data_count;\n";
    h << "/* Binary-searches decomp_records by cp; returns start_idx + length\n";
    h << " * via out params, returns 1 if found, 0 otherwise. */\n";
    h << "int laplace_ucd_decomp_lookup(uint32_t cp, uint32_t* out_start_idx, uint32_t* out_length);\n\n";

    h << "typedef struct {\n";
    h << "    uint32_t first;\n";
    h << "    uint32_t second;\n";
    h << "    uint32_t composed;\n";
    h << "} laplace_ucd_compose_record_t;\n";
    h << "extern const laplace_ucd_compose_record_t laplace_ucd_compose_records[];\n";
    h << "extern const size_t laplace_ucd_compose_count;\n";
    h << "/* Binary-searches compose_records by (first, second); returns the\n";
    h << " * composed codepoint via out param. Returns 1 if found, 0 otherwise. */\n";
    h << "int laplace_ucd_compose_lookup(uint32_t first, uint32_t second, uint32_t* out_composed);\n\n";

    // Source: CCC
    c << "\nconst laplace_ucd_ccc_record_t laplace_ucd_ccc_records[] = {\n";
    for (auto [cp, v] : ccc) {
        c << "    { 0x" << std::hex << cp << ", " << std::dec << (unsigned)v << " },\n";
    }
    c << "};\n";
    c << "const size_t laplace_ucd_ccc_count = " << ccc.size() << ";\n\n";

    c << "uint8_t laplace_ucd_ccc_lookup(uint32_t cp) {\n";
    c << "    size_t lo = 0, hi = " << ccc.size() << ";\n";
    c << "    while (lo < hi) {\n";
    c << "        size_t mid = lo + ((hi - lo) >> 1);\n";
    c << "        const laplace_ucd_ccc_record_t* r = &laplace_ucd_ccc_records[mid];\n";
    c << "        if (cp < r->cp) hi = mid;\n";
    c << "        else if (cp > r->cp) lo = mid + 1;\n";
    c << "        else return r->ccc;\n";
    c << "    }\n";
    c << "    return 0;\n";
    c << "}\n\n";

    // Source: decomp data
    c << "const uint32_t laplace_ucd_decomp_data[] = {\n";
    for (size_t i = 0; i < flat_decomp_data.size(); i += 8) {
        c << "    ";
        for (size_t j = i; j < std::min(i + 8, flat_decomp_data.size()); ++j) {
            c << "0x" << std::hex << flat_decomp_data[j] << ", ";
        }
        c << "\n";
    }
    c << std::dec;
    c << "};\n";
    c << "const size_t laplace_ucd_decomp_data_count = " << flat_decomp_data.size() << ";\n\n";

    // Decomp records
    c << "const laplace_ucd_decomp_record_t laplace_ucd_decomp_records[] = {\n";
    for (size_t i = 0; i < decomps.size(); ++i) {
        c << "    { 0x" << std::hex << decomps[i].codepoint << ", "
          << std::dec << rec_offsets[i].first << ", " << rec_offsets[i].second
          << " },\n";
    }
    c << "};\n";
    c << "const size_t laplace_ucd_decomp_count = " << decomps.size() << ";\n\n";

    c << "int laplace_ucd_decomp_lookup(uint32_t cp, uint32_t* out_start_idx, uint32_t* out_length) {\n";
    c << "    size_t lo = 0, hi = " << decomps.size() << ";\n";
    c << "    while (lo < hi) {\n";
    c << "        size_t mid = lo + ((hi - lo) >> 1);\n";
    c << "        const laplace_ucd_decomp_record_t* r = &laplace_ucd_decomp_records[mid];\n";
    c << "        if (cp < r->cp) hi = mid;\n";
    c << "        else if (cp > r->cp) lo = mid + 1;\n";
    c << "        else { *out_start_idx = r->start_idx; *out_length = r->length; return 1; }\n";
    c << "    }\n";
    c << "    return 0;\n";
    c << "}\n\n";

    // Compose records
    c << "const laplace_ucd_compose_record_t laplace_ucd_compose_records[] = {\n";
    for (const auto& m : composes) {
        c << "    { 0x" << std::hex << m.first << ", 0x" << m.second
          << ", 0x" << m.composed << " },\n";
    }
    c << std::dec;
    c << "};\n";
    c << "const size_t laplace_ucd_compose_count = " << composes.size() << ";\n\n";

    c << "int laplace_ucd_compose_lookup(uint32_t first, uint32_t second, uint32_t* out_composed) {\n";
    c << "    size_t lo = 0, hi = " << composes.size() << ";\n";
    c << "    while (lo < hi) {\n";
    c << "        size_t mid = lo + ((hi - lo) >> 1);\n";
    c << "        const laplace_ucd_compose_record_t* r = &laplace_ucd_compose_records[mid];\n";
    c << "        if (first < r->first || (first == r->first && second < r->second)) hi = mid;\n";
    c << "        else if (first > r->first || (first == r->first && second > r->second)) lo = mid + 1;\n";
    c << "        else { *out_composed = r->composed; return 1; }\n";
    c << "    }\n";
    c << "    return 0;\n";
    c << "}\n\n";

    h << "#ifdef __cplusplus\n} /* extern \"C\" */\n#endif\n";
}

int main(int argc, char** argv) {
    Cli cli = parse_cli(argc, argv);

    auto aux = cli.ucd_path / "auxiliary";

    PropTable gb = load_prop_table(aux / "GraphemeBreakProperty.txt", "gb", "Other");
    merge_extended_pictographic(gb, cli.emoji_path / "emoji-data.txt");

    PropTable wb = load_prop_table(aux / "WordBreakProperty.txt", "wb", "Other");
    PropTable sb = load_prop_table(aux / "SentenceBreakProperty.txt", "sb", "Other");

    PropTable incb;
    incb.name = "incb";
    incb.default_id = intern_label(incb, "None");
    parse_prop_file_3field(
        cli.ucd_path / "DerivedCoreProperties.txt",
        [&](uint32_t s, uint32_t e, const std::string& name, const std::string& val) {
            if (name != "InCB") return;
            uint8_t id = intern_label(incb, val);
            incb.records.push_back({s, e, id});
        });
    std::sort(incb.records.begin(), incb.records.end(),
              [](const auto& a, const auto& b) { return a.start < b.start; });
    coalesce(incb.records);

    // === NFC tables ===
    std::vector<std::pair<uint32_t, uint8_t>> ccc_pairs;
    std::unordered_map<uint32_t, std::vector<uint32_t>> base_decomp;
    parse_unicode_data(cli.ucd_path / "UnicodeData.txt",
        [&](uint32_t cp, uint8_t ccc_v, const std::vector<uint32_t>& d) {
            if (ccc_v != 0) ccc_pairs.emplace_back(cp, ccc_v);
            if (!d.empty()) base_decomp[cp] = d;
        });
    std::sort(ccc_pairs.begin(), ccc_pairs.end());

    auto excl = parse_composition_exclusions(cli.ucd_path / "CompositionExclusions.txt");
    auto is_excluded = [&](uint32_t cp) {
        return std::binary_search(excl.begin(), excl.end(), cp);
    };

    // Full canonical decompositions (recursive) + flat data layout.
    std::vector<NfcDecomp> decomps;
    decomps.reserve(base_decomp.size());
    for (const auto& [cp, _] : base_decomp) {
        NfcDecomp d; d.codepoint = cp;
        full_decompose_into(cp, base_decomp, d.decomp);
        decomps.push_back(std::move(d));
    }
    std::sort(decomps.begin(), decomps.end(),
              [](const auto& a, const auto& b) { return a.codepoint < b.codepoint; });

    std::vector<uint32_t> flat_decomp;
    std::vector<std::pair<uint32_t, uint32_t>> rec_offsets;  // start_idx, length per record
    for (const auto& d : decomps) {
        rec_offsets.emplace_back((uint32_t)flat_decomp.size(), (uint32_t)d.decomp.size());
        for (uint32_t cc : d.decomp) flat_decomp.push_back(cc);
    }

    // Canonical compositions: for each base canonical decomposition that
    // is a pair (starter, X), if the codepoint is NOT in the exclusion
    // list AND the first decomp element is a starter (CCC == 0), emit a
    // composition record. Also exclude full-composition-excluded ones:
    // codepoints whose first decomp element has CCC != 0 (these are
    // "non-starter decompositions" — Singleton exclusion type).
    auto find_ccc = [&](uint32_t cp) -> uint8_t {
        auto it = std::lower_bound(ccc_pairs.begin(), ccc_pairs.end(),
                                    std::make_pair(cp, (uint8_t)0));
        if (it != ccc_pairs.end() && it->first == cp) return it->second;
        return 0;
    };

    std::vector<NfcCompose> composes;
    for (const auto& [cp, decomp] : base_decomp) {
        if (decomp.size() != 2) continue;
        if (is_excluded(cp)) continue;
        // Singleton exclusion: decomp[0] has CCC != 0 means non-starter
        // decomp.
        if (find_ccc(decomp[0]) != 0) continue;
        // Script-specific exclusion: cp itself has CCC != 0 means
        // non-starter — skip composition.
        if (find_ccc(cp) != 0) continue;
        composes.push_back({decomp[0], decomp[1], cp});
    }
    std::sort(composes.begin(), composes.end(),
              [](const auto& a, const auto& b) {
                  return a.first != b.first ? a.first < b.first : a.second < b.second;
              });

    std::vector<const PropTable*> tables = {&gb, &wb, &sb, &incb};

    fs::create_directories(cli.out_header.parent_path());
    fs::create_directories(cli.out_source.parent_path());

    std::ofstream h(cli.out_header);
    if (!h) { std::fprintf(stderr, "cannot write %s\n", cli.out_header.string().c_str()); return 5; }
    std::ofstream c(cli.out_source);
    if (!c) { std::fprintf(stderr, "cannot write %s\n", cli.out_source.string().c_str()); return 5; }

    emit_header(h, cli.version, tables);
    emit_source(c, cli.version, tables, cli.out_header);

    // Append NFC tables to both files.
    emit_nfc_tables(h, c, ccc_pairs, decomps, rec_offsets, flat_decomp, composes);

    // Close header's extern "C" block (emit_header opened it; emit_nfc_tables
    // appended INSIDE that block). Actually emit_header already closed the
    // block — re-open one for the NFC declarations and close it again here.
    // Simpler: emit_nfc_tables should not assume any extern "C" context; in
    // the C++ build (only for tests) the header will fail to link without
    // it. To keep this simple, wrap NFC decls in their own extern "C" block.

    std::fprintf(stderr,
        "laplace_ucd_tables_emit: ucd=%s ver=%s -> %s, %s\n"
        "  gb:   %zu labels, %zu ranges\n"
        "  wb:   %zu labels, %zu ranges\n"
        "  sb:   %zu labels, %zu ranges\n"
        "  incb: %zu labels, %zu ranges\n"
        "  ccc:  %zu records\n"
        "  decomp: %zu records (%zu codepoints in flat data)\n"
        "  compose: %zu records\n",
        cli.ucd_path.string().c_str(), cli.version.c_str(),
        cli.out_header.string().c_str(), cli.out_source.string().c_str(),
        gb.labels.size(), gb.records.size(),
        wb.labels.size(), wb.records.size(),
        sb.labels.size(), sb.records.size(),
        incb.labels.size(), incb.records.size(),
        ccc_pairs.size(),
        decomps.size(), flat_decomp.size(),
        composes.size());
    return 0;
}
