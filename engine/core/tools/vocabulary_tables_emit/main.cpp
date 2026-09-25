/*
 * Build-time emit for laplace_vocabulary_perfcache.bin (spec 33).
 *
 * Load the T0 generation, compose every governed vocabulary value through the same
 * content kernels a recipe uses, and write one mmap blob. A label ("NOUN", "nsubj",
 * "pass", "Number") is content built by content_witness_source_tree_build; a feature
 * value is the ordered composition [feature, value] built by
 * laplace_ordered_composition_compose_batch, exactly as a recipe composes "Number=Plur".
 *
 * Inputs:  --t0 <T0 blob> --manifest <engine/manifest/vocabulary> --output <blob>
 * Output:  records grouped by family in code order, an id index, labels, BLAKE3 trailer.
 */

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <memory>
#include <sstream>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/ordered_composition.h"
#include "laplace/core/perfcache_format.h"
#include "laplace/core/tier_tree.h"
#include "laplace/core/vocabulary_perfcache_format.h"

namespace {

struct Cli {
    std::string t0, manifest, output;
};

Cli parse_cli(int argc, char** argv) {
    Cli c;
    for (int i = 1; i < argc; ++i) {
        std::string_view a = argv[i];
        auto next = [&]() -> std::string {
            if (i + 1 >= argc) {
                std::fprintf(stderr, "%s needs a value\n", argv[i]);
                std::exit(2);
            }
            return argv[++i];
        };
        if (a == "--t0") c.t0 = next();
        else if (a == "--manifest") c.manifest = next();
        else if (a == "--output") c.output = next();
        else {
            std::fprintf(stderr, "unknown argument %s\n", argv[i]);
            std::exit(2);
        }
    }
    if (c.t0.empty() || c.manifest.empty() || c.output.empty()) {
        std::fprintf(stderr, "required: --t0 --manifest --output\n");
        std::exit(2);
    }
    return c;
}

std::string read_file(const std::string& path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) {
        std::fprintf(stderr, "cannot read %s\n", path.c_str());
        std::exit(4);
    }
    std::ostringstream s;
    s << in.rdbuf();
    return s.str();
}

// Data rows of a generated vocabulary table: provenance comments and header skipped.
std::vector<std::vector<std::string>> read_table(const std::string& text) {
    std::vector<std::vector<std::string>> rows;
    std::istringstream in(text);
    std::string line;
    bool header = true;
    while (std::getline(in, line)) {
        if (line.empty() || line[0] == '#') continue;
        if (header) { header = false; continue; }
        std::vector<std::string> cols;
        size_t start = 0;
        for (size_t tab; (tab = line.find('\t', start)) != std::string::npos; start = tab + 1)
            cols.push_back(line.substr(start, tab - start));
        cols.push_back(line.substr(start));
        rows.push_back(std::move(cols));
    }
    return rows;
}

struct Value {
    hash128_t id{};
    double coord[4]{};
    hilbert128_t hilbert{};
    uint8_t tier = 0;
    uint32_t atom = 0;
};

Value compose_label(const std::string& text) {
    tier_tree_t* raw = nullptr;
    if (content_witness_source_tree_build(reinterpret_cast<const uint8_t*>(text.data()), text.size(), &raw) != 0
        || !raw) {
        std::fprintf(stderr, "cannot compose \"%s\"\n", text.c_str());
        std::exit(4);
    }
    std::unique_ptr<tier_tree_t, decltype(&tier_tree_free)> tree(raw, tier_tree_free);
    tier_node_view_t root{};
    if (content_witness_tree_root_node(tree.get(), &root) != 0) {
        std::fprintf(stderr, "no content root for \"%s\"\n", text.c_str());
        std::exit(4);
    }
    Value v;
    v.id = root.id;
    std::memcpy(v.coord, root.coord, sizeof(v.coord));
    v.hilbert = root.hilbert;
    v.tier = root.tier;
    v.atom = root.atom;
    return v;
}

Value compose_pair(const Value& a, const Value& b) {
    laplace_ordered_component_t parts[2]{};
    const Value* in[2] = {&a, &b};
    for (int i = 0; i < 2; ++i) {
        parts[i].id = in[i]->id;
        std::memcpy(parts[i].coord, in[i]->coord, sizeof(parts[i].coord));
        parts[i].tier = in[i]->tier;
        parts[i].atom = in[i]->atom;
        parts[i].has_atom = in[i]->tier == 0;
    }
    laplace_ordered_composition_request_t request{};
    request.components = parts;
    request.component_count = 2;
    laplace_ordered_composition_result_t result{};
    if (laplace_ordered_composition_compose_batch(&request, 1, &result) != 0) {
        std::fprintf(stderr, "pair composition failed\n");
        std::exit(4);
    }
    Value v;
    v.id = result.id;
    std::memcpy(v.coord, result.coord, sizeof(v.coord));
    v.hilbert = result.hilbert;
    v.tier = result.tier;
    return v;
}

template <typename T> void put(std::vector<uint8_t>& b, const T& v) {
    const auto* p = reinterpret_cast<const uint8_t*>(&v);
    b.insert(b.end(), p, p + sizeof(T));
}

}  // namespace

int main(int argc, char** argv) {
    const Cli cli = parse_cli(argc, argv);
    if (codepoint_table_load_perfcache(cli.t0.c_str()) != 0) {
        std::fprintf(stderr, "cannot load t0 perfcache %s\n", cli.t0.c_str());
        return 4;
    }
    laplace_perfcache_header_t t0{};
    {
        std::ifstream tf(cli.t0, std::ios::binary);
        tf.read(reinterpret_cast<char*>(&t0), sizeof(t0));
        if (!tf || t0.magic != LAPLACE_PERFCACHE_MAGIC) {
            std::fprintf(stderr, "bad t0 header\n");
            return 4;
        }
    }

    static const char* const kTables[LAPLACE_VOCABULARY_FAMILY_COUNT] = {
        "upos", "deprel", "deprel_subtype", "feature", "feature_value"};
    std::string texts[LAPLACE_VOCABULARY_FAMILY_COUNT];
    std::vector<uint8_t> mix;
    put(mix, t0.ucd_hash);
    for (const char* t = LAPLACE_VOCABULARY_PERFCACHE_GENERATOR_TAG; *t; ++t) mix.push_back(uint8_t(*t));
    for (int f = 0; f < LAPLACE_VOCABULARY_FAMILY_COUNT; ++f) {
        texts[f] = read_file(cli.manifest + "/" + kTables[f] + ".tsv");
        mix.push_back(0);
        mix.insert(mix.end(), texts[f].begin(), texts[f].end());
    }
    hash128_t source_hash;
    hash128_blake3(mix.data(), mix.size(), &source_hash);

    {
        std::ifstream prev(cli.output, std::ios::binary);
        laplace_vocabulary_perfcache_header_t old{};
        if (prev && prev.read(reinterpret_cast<char*>(&old), sizeof(old))
            && old.magic == LAPLACE_VOCABULARY_PERFCACHE_MAGIC
            && old.format_version == LAPLACE_VOCABULARY_PERFCACHE_VERSION
            && std::memcmp(&old.source_hash, &source_hash, sizeof(hash128_t)) == 0) {
            std::fprintf(stderr, "vocabulary_perfcache: sources unchanged; emit skipped\n");
            return 0;
        }
    }

    std::vector<laplace_vocabulary_perfcache_record_t> records;
    std::string strings;
    laplace_vocabulary_perfcache_header_t header{};
    std::vector<Value> features;  // by feature code - 1
    std::unordered_map<std::string, uint16_t> feature_codes;

    for (int f = 0; f < LAPLACE_VOCABULARY_FAMILY_COUNT; ++f) {
        const auto rows = read_table(texts[f]);
        header.family_start[f] = uint32_t(records.size());
        header.family_count[f] = uint32_t(rows.size());
        for (size_t i = 0; i < rows.size(); ++i) {
            const auto& row = rows[i];
            const long code = std::strtol(row[0].c_str(), nullptr, 10);
            if (code != long(i + 1) || code > 0xFFFF) {
                std::fprintf(stderr, "%s: codes must run 1..N (row %zu has %s)\n", kTables[f], i + 1, row[0].c_str());
                return 4;
            }
            laplace_vocabulary_perfcache_record_t r{};
            std::string label;
            Value v;
            if (f == LAPLACE_VOCABULARY_FEATURE_VALUE) {
                // [feature, value]: the feature's own record supplies its composed form.
                label = row[1] + "=" + row[2];
                const auto feature = feature_codes.find(row[1]);
                if (feature == feature_codes.end()) {
                    std::fprintf(stderr, "feature_value %s names no declared feature\n", label.c_str());
                    return 4;
                }
                r.parent_code = feature->second;
                v = compose_pair(features[feature->second - 1], compose_label(row[2]));
            } else {
                label = row[1];
                v = compose_label(label);
                if (f == LAPLACE_VOCABULARY_FEATURE) {
                    features.push_back(v);
                    feature_codes.emplace(label, uint16_t(code));
                }
            }
            r.id = v.id;
            std::memcpy(r.coord, v.coord, sizeof(r.coord));
            r.hilbert = v.hilbert;
            r.tier = v.tier;
            r.family = uint8_t(f);
            r.code = uint16_t(code);
            r.label_off = uint32_t(strings.size());
            r.label_len = uint16_t(label.size());
            strings += label;
            strings.push_back('\0');
            records.push_back(r);
        }
    }

    uint64_t slots = 1;
    while (slots < records.size() * 2) slots <<= 1;
    std::vector<uint32_t> index(slots, LAPLACE_VOCABULARY_INDEX_EMPTY);
    for (uint32_t i = 0; i < records.size(); ++i) {
        uint64_t slot = records[i].id.lo & (slots - 1);
        // One content entity may serve several vocabularies ("advcl" is a universal
        // relation and a subtype); within one vocabulary it has one code.
        while (index[slot] != LAPLACE_VOCABULARY_INDEX_EMPTY) {
            const auto& other = records[index[slot]];
            if (other.family == records[i].family && hash128_equals(&other.id, &records[i].id)) {
                std::fprintf(stderr, "%s has two codes in one vocabulary\n", strings.c_str() + records[i].label_off);
                return 4;
            }
            slot = (slot + 1) & (slots - 1);
        }
        index[slot] = i;
    }

    header.magic = LAPLACE_VOCABULARY_PERFCACHE_MAGIC;
    header.format_version = LAPLACE_VOCABULARY_PERFCACHE_VERSION;
    header.record_count = records.size();
    header.record_size = LAPLACE_VOCABULARY_PERFCACHE_RECORD_SIZE;
    header.records_offset = LAPLACE_VOCABULARY_PERFCACHE_HEADER_SIZE;
    header.index_offset = header.records_offset + records.size() * sizeof(records[0]);
    header.index_slots = slots;
    header.strings_offset = header.index_offset + slots * sizeof(uint32_t);
    header.strings_length = strings.size();
    header.source_hash = source_hash;

    std::vector<uint8_t> blob;
    put(blob, header);
    for (const auto& r : records) put(blob, r);
    for (uint32_t i : index) put(blob, i);
    blob.insert(blob.end(), strings.begin(), strings.end());
    hash128_t crc;
    hash128_blake3(blob.data(), blob.size(), &crc);
    put(blob, crc);

    const std::string tmp = cli.output + ".tmp";
    {
        std::ofstream out(tmp, std::ios::binary);
        if (!out.write(reinterpret_cast<const char*>(blob.data()), std::streamsize(blob.size()))) {
            std::fprintf(stderr, "cannot write %s\n", tmp.c_str());
            return 5;
        }
    }
    if (std::rename(tmp.c_str(), cli.output.c_str()) != 0) {
        std::fprintf(stderr, "cannot publish %s\n", cli.output.c_str());
        return 5;
    }
    std::fprintf(stderr, "vocabulary_perfcache: %zu values (upos %u, deprel %u, subtypes %u, features %u, values %u) -> %s\n",
                 records.size(), header.family_count[0], header.family_count[1], header.family_count[2],
                 header.family_count[3], header.family_count[4], cli.output.c_str());
    return 0;
}
