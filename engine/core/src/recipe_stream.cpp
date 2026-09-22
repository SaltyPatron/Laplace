#include "laplace/core/recipe_stream.h"
#include "laplace/core/xml_stream.h"
#include "laplace/core/utf8.h"
#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/attestation_engine.h"
#include "laplace/core/ordered_composition.h"
#include "laplace/core/trajectory.h"
#include "recipe_delimited.hpp"
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <deque>
#include <limits>
#include <memory>
#include <map>
#include <array>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace {
struct image_reader {
    const uint8_t* p; size_t remaining;
    uint32_t number() {
        if (remaining < 4) throw std::runtime_error("truncated recipe instruction image");
        uint32_t v = uint32_t(p[0]) | uint32_t(p[1]) << 8 | uint32_t(p[2]) << 16 | uint32_t(p[3]) << 24;
        p += 4; remaining -= 4; return v;
    }
    std::string text() {
        size_t n = number();
        if (n > remaining) throw std::runtime_error("truncated recipe string");
        std::string result(reinterpret_cast<const char*>(p), n); p += n; remaining -= n;
        return result;
    }
    hash128_t hash() {
        if (remaining < 16) throw std::runtime_error("truncated recipe identity");
        hash128_t h; std::memcpy(&h, p, 16); p += 16; remaining -= 16; return h;
    }
    double real() {
        if (remaining < 8) throw std::runtime_error("truncated recipe rank");
        double d; std::memcpy(&d, p, 8); p += 8; remaining -= 8;
        if (!std::isfinite(d) || d < 0 || d > 1) throw std::runtime_error("invalid recipe rank");
        return d;
    }
};
struct field_rule {
    std::string path, absent, separator, object_namespace, context_field, default_value;
    uint32_t kind, disposition, codec;
    bool has_default = false, omit_default_testimony = false;
    hash128_t relation, parent, entity_type, lexical_relation;
    double rank;
    std::unordered_map<std::string, std::string> aliases;
};
struct structure_rule {
    std::string path, semantic_type;
    uint32_t disposition = 0;
};
struct route_rule {
    std::string name, ns, prefix, identity, first, last, object_namespace, separator;
    std::string range_first, range_last;
    hash128_t range_relation{};
    std::unordered_map<std::string, std::string> aliases;
    uint32_t kind, subject_codec;
    hash128_t entity_type;
    std::unordered_map<std::string, std::string> children;
    std::vector<std::string> structures;
    bool inherit_parent_attributes = false;
};
struct node {
    std::string name, ns;
    std::map<std::string, std::string> attributes;
    std::vector<node> children;
    std::string get(const std::string& key) const {
        auto i = attributes.find(key); return i == attributes.end() ? "" : i->second;
    }
};
struct ordinal_span {
    uint32_t first = 0, last = 0;
};
struct fact {
    hash128_t relation{}, object{}, context{};
    bool has_object = false, has_context = false, confirm = true, explicit_rank = false;
    double rank = 1;
};
struct content_form {
    hash128_t id{};
    double coord[4]{};
    hilbert128_t hilbert{};
    uint8_t tier = 0;
};
using stage_ptr = std::unique_ptr<intent_stage_t, decltype(&intent_stage_free)>;
static bool nonzero(const hash128_t& h) { return h.hi != 0 || h.lo != 0; }
static std::string alias_key(const std::string& s) {
    std::string r;
    for (unsigned char c : s) {
        if (c == '_' || c == '-' || c == ' ' || c == '\t') continue;
        r += char(c >= 'a' && c <= 'z' ? c - ('a' - 'A') : c);
    }
    return r;
}
static uint32_t point(const std::string& s) {
    size_t i = s.rfind("U+", 0) == 0 || s.rfind("u+", 0) == 0 ? 2 : 0;
    if (i == s.size()) throw std::runtime_error("empty ordinal reference");
    uint32_t value = 0;
    for (; i < s.size(); ++i) {
        unsigned char c = s[i];
        unsigned d = c >= '0' && c <= '9' ? c - '0' :
            c >= 'A' && c <= 'F' ? c - 'A' + 10 : c >= 'a' && c <= 'f' ? c - 'a' + 10 : 16;
        if (d == 16 || value > (0x10ffffu - d) / 16) throw std::runtime_error("invalid ordinal reference: " + s);
        value = value * 16 + d;
    }
    return value;
}
static std::string point_text(uint32_t cp) {
    char text[7]{};
    const int n = std::snprintf(text, sizeof(text), "%04X", cp);
    if (n <= 0 || static_cast<size_t>(n) >= sizeof(text))
        throw std::runtime_error("ordinal formatting failed");
    return std::string(text, static_cast<size_t>(n));
}
static hash128_t point_id(uint32_t cp) {
    if (const auto* entry = codepoint_table_lookup(cp)) return entry->hash;
    uint8_t b[4]; size_t n;
    // T0 contains every codepoint position, including surrogate positions. Their
    // canonical preimage is positional; they must never pass through a scalar
    // codec that substitutes U+FFFD and collapses distinct position identities.
    if (cp >= 0xd800 && cp <= 0xdfff) {
        b[0] = uint8_t(0xe0 | (cp >> 12));
        b[1] = uint8_t(0x80 | ((cp >> 6) & 0x3f));
        b[2] = uint8_t(0x80 | (cp & 0x3f)); n = 3;
    } else n = laplace_utf8_encode(cp, b);
    hash128_t h;
    hash128_blake3(b, n, &h); return h;
}
static std::vector<std::string> split(const std::string& raw, const std::string& separator) {
    if (separator.empty()) throw std::runtime_error("sequence recipe requires a separator");
    std::vector<std::string> out; size_t start = 0;
    for (;;) {
        size_t end = raw.find(separator, start);
        std::string value = raw.substr(start, end == std::string::npos ? end : end - start);
        size_t first = value.find_first_not_of(" \r\n\t"), last = value.find_last_not_of(" \r\n\t");
        if (first != std::string::npos) out.push_back(value.substr(first, last - first + 1));
        if (end == std::string::npos) break;
        start = end + separator.size();
    }
    return out;
}
static std::string sequence_text(const std::string& raw, const std::string& separator) {
    std::string out;
    for (const auto& token : split(raw, separator.empty() ? " " : separator)) {
        uint32_t cp = point(token);
        if (cp >= 0xd800 && cp <= 0xdfff) throw std::runtime_error("non-scalar sequence reference");
        uint8_t bytes[4]; size_t n = laplace_utf8_encode(cp, bytes);
        out.append(reinterpret_cast<const char*>(bytes), n);
    }
    return out;
}
static size_t rows(const intent_stage_t* s) {
    return intent_stage_entity_count(s) + intent_stage_physicality_count(s) + intent_stage_attestation_count(s);
}
}

struct laplace_recipe_stream {
    laplace_xml_stream_t* parser = nullptr;
    std::unique_ptr<recipe_delimited_stream> delimited;
    hash128_t witness{};
    double trust = 0;
    int depth = 2;
    bool final = false, failed = false, active = false;
    std::string error, record_context;
    std::unordered_map<std::string, field_rule> fields;
    std::unordered_map<std::string, structure_rule> structures;
    std::unordered_map<std::string, route_rule> routes;
    std::vector<node> stack;
    node parent_scope{};
    std::vector<ordinal_span> parent_spans;
    bool parent_scope_active = false;
    std::deque<node> pending;
    uint32_t cursor = 0, end = 0;
    bool range = false, membership = false, record_facts_done = false;
    size_t fact_offset = 0;
    hash128_t membership_relation{};
    stage_ptr ready{nullptr, intent_stage_free};
    uint64_t ready_completed = 0;
    hash128_t subject{};
    std::vector<fact> facts;
    std::unordered_map<std::string, content_form> content_cache;
    size_t content_cache_bytes = 0, content_cache_limit = 0, content_cache_entries = 0;
    std::unordered_set<std::string> declared_parents;

    ~laplace_recipe_stream() { laplace_xml_stream_free(parser); }
    void check(int result, const char* action) {
        if (result != 0) throw std::runtime_error(std::string("recipe ") + action + " failed (" + std::to_string(result) + ")");
    }
    void entity(intent_stage_t* stage, hash128_t id, hash128_t type) {
        if (intent_stage_witness_seen(stage, &id)) {
            check(intent_stage_add_entity_interpretation(stage, &id, 2, &type, &witness), "entity interpretation");
            return;
        }
        check(intent_stage_add_entity(stage, &id, 2, &type, &witness), "entity admission");
        intent_stage_witness_record(stage, &id);
        if (intent_stage_allocation_failed(stage))
            throw std::runtime_error("recipe entity deduplication exceeded the admitted byte envelope");
    }
    content_form compose_content(intent_stage_t* stage, const std::string& text) {
        if (text.empty()) throw std::runtime_error("empty content reference");
        auto i = content_cache.find(text); if (i != content_cache.end()) return i->second;
        tier_tree_t* raw = nullptr;
        check(content_witness_source_tree_build(
            reinterpret_cast<const uint8_t*>(text.data()), text.size(), &raw), "content composition");
        std::unique_ptr<tier_tree_t, decltype(&tier_tree_free)> tree(raw, tier_tree_free);
        tier_node_view_t root_node{};
        check(content_witness_tree_root_node(tree.get(), &root_node), "content root");
        hash128_t root;
        check(content_witness_emit_tree(stage, tree.get(), &witness, nullptr, 0, &root), "content admission");
        if (!hash128_equals(&root, &root_node.id))
            throw std::runtime_error("content root changed between composition and admission");
        content_form result{};
        result.id = root;
        std::memcpy(result.coord, root_node.coord, sizeof(result.coord));
        result.hilbert = root_node.hilbert;
        result.tier = root_node.tier;
        const size_t entry_overhead = sizeof(content_form) + sizeof(std::string) + 4 * sizeof(void*);
        if (content_cache.size() < content_cache_entries &&
            entry_overhead <= content_cache_limit - content_cache_bytes &&
            text.size() <= content_cache_limit - content_cache_bytes - entry_overhead) {
            content_cache.emplace(text, result);
            content_cache_bytes += text.size() + entry_overhead;
        }
        return result;
    }
    hash128_t content(intent_stage_t* stage, const std::string& text) {
        return compose_content(stage, text).id;
    }
    void project_named_identity(intent_stage_t* stage, hash128_t id, hash128_t type,
                                const std::string& canonical) {
        const bool already = intent_stage_witness_seen(stage, &id) != 0;
        content_form form = compose_content(stage, canonical);
        entity(stage, id, type);
        if (already || hash128_equals(&id, &form.id)) return;

        double trajectory[4]{};
        check(trajectory_build(&form.id, 1, trajectory), "named identity trajectory");
        hash128_t physicality;
        laplace_physicality_id_compute(id, 3, &physicality);
        check(intent_stage_add_physicality(
            stage, &physicality, &id, 3,
            form.coord, &form.hilbert,
            trajectory, 1, 1,
            1, 0.0, 1, 0, INTENT_STAGE_PG_EPOCH_UNIX_US),
            "named identity physicality");
    }
    hash128_t classifier(intent_stage_t* stage, const std::string& ns,
                         const std::string& value, hash128_t type) {
        if (ns.empty() || value.empty()) throw std::runtime_error("empty classifier binding");
        std::string canonical = ns + "/" + value + "/v1";
        hash128_t id;
        hash128_blake3_str(canonical.c_str(), &id);
        project_named_identity(stage, id, type, canonical);
        return id;
    }
    void relation_entity(intent_stage_t* stage, hash128_t id, hash128_t type) {
        const char* canonical = laplace_relation_canonical_for_type_id(&id);
        if (!canonical || !*canonical)
            throw std::runtime_error("recipe relation identity has no canonical preimage");
        project_named_identity(stage, id, type, canonical);
    }
    void build_attestation(hash128_t subj, const fact& f, laplace_attestation_staged_t& row) {
        const laplace_relation_def_t* definition = nullptr;
        double weight = laplace_relation_lookup(&f.relation, &definition) == 0 && definition
            ? trust : trust * f.rank;
        check(laplace_attestation_resolved_build(&subj, &f.relation, f.has_object ? &f.object : nullptr,
            f.has_object ? 0 : 1, &witness, f.has_context ? &f.context : nullptr,
            f.has_context ? 0 : 1, weight, f.confirm ? 1 : 0, 1, 0, &row), "testimony");
        if (f.explicit_rank) {
            // The compiled field rank includes any explicit recipe override.
            // Resolve orientation/identity through the shared builder, then use
            // its shared witness-strength transforms with that declared rank.
            const double declared_weight = trust * f.rank;
            row.opponent_rd_fp1e9 = static_cast<int64_t>(laplace_attestation_witness_phi(declared_weight) * LAPLACE_GLICKO2_FP_SCALE);
            row.opponent_rating_fp1e9 = static_cast<int64_t>(laplace_attestation_witness_opponent_rating(declared_weight) * LAPLACE_GLICKO2_FP_SCALE);
        }
    }
    void attest(intent_stage_t* stage, hash128_t subj, const fact& f) {
        laplace_attestation_staged_t row{};
        build_attestation(subj, f, row);
        check(laplace_attestation_staged_batch_add(stage, &row, 1, nullptr), "testimony admission");
    }
    size_t attest_facts(intent_stage_t* stage, hash128_t subj, size_t offset, size_t count) {
        constexpr size_t chunk = 256;
        laplace_attestation_staged_t staged[chunk];
        size_t emitted = 0;
        while (emitted < count) {
            const size_t n = std::min(chunk, count - emitted);
            for (size_t i = 0; i < n; ++i) {
                staged[i] = {};
                build_attestation(subj, facts[offset + emitted + i], staged[i]);
            }
            check(laplace_attestation_staged_batch_add(stage, staged, n, nullptr), "testimony admission");
            emitted += n;
        }
        return emitted;
    }
    size_t attest_range_facts(intent_stage_t* stage, uint32_t first, uint32_t last) {
        constexpr size_t chunk = 256;
        laplace_attestation_staged_t staged[chunk];
        size_t staged_n = 0;
        size_t emitted = 0;
        for (uint32_t cp = first;; ++cp) {
            const hash128_t subj = point_id(cp);
            for (const fact& f : facts) {
                staged[staged_n] = {};
                build_attestation(subj, f, staged[staged_n++]);
                if (staged_n == chunk) {
                    check(laplace_attestation_staged_batch_add(
                        stage, staged, staged_n, nullptr), "testimony admission");
                    emitted += staged_n;
                    staged_n = 0;
                }
            }
            if (cp == last) break;
        }
        if (staged_n) {
            check(laplace_attestation_staged_batch_add(
                stage, staged, staged_n, nullptr), "testimony admission");
            emitted += staged_n;
        }
        return emitted;
    }
    hash128_t interval_subject(intent_stage_t* stage, uint32_t first, uint32_t last) {
        if (first == last) return point_id(first);

        laplace_ordered_component_t components[2]{};
        const uint32_t atoms[2] = {first, last};
        for (size_t i = 0; i < 2; ++i) {
            components[i].atom = atoms[i];
            components[i].tier = 0;
            components[i].has_atom = 1;
            hilbert128_t ignored{};
            check(codepoint_table_resolve_atom(
                atoms[i], &components[i].id, components[i].coord, &ignored),
                "range endpoint");
        }

        hash128_t range_type;
        hash128_blake3_str("Range", &range_type);
        laplace_ordered_composition_request_t request{
            components, 2, range_type, witness, INTENT_STAGE_PG_EPOCH_UNIX_US
        };
        laplace_ordered_composition_result_t result{};
        check(laplace_ordered_composition_compose_batch(&request, 1, &result),
            "range composition");

        if (!intent_stage_witness_seen(stage, &result.id)) {
            check(intent_stage_add_entity(
                stage, &result.id, result.tier, &range_type, &witness), "range entity");

            hash128_t ids[2] = {components[0].id, components[1].id};
            double trajectory[8]{};
            check(trajectory_build(ids, 2, trajectory), "range trajectory");
            hash128_t physicality;
            laplace_physicality_id_compute(result.id, 10, &physicality);
            check(intent_stage_add_physicality(
                stage, &physicality, &result.id, 10,
                result.coord, &result.hilbert,
                trajectory, 2, 2,
                1, 0.0, 1, 0, INTENT_STAGE_PG_EPOCH_UNIX_US),
                "range physicality");
            check(intent_stage_witness_record(stage, &result.id), "range witness");
            if (intent_stage_allocation_failed(stage))
                throw std::runtime_error("range staging exceeded the admitted byte envelope");
        }
        return result.id;
    }

    void field(intent_stage_t* stage, const std::string& path, const std::string& raw,
        bool subject_binding, const std::map<std::string, std::string>& attributes) {
        try { lower_field(stage, path, raw, subject_binding, attributes); }
        catch (const std::runtime_error& e) {
            throw std::runtime_error("field " + path + " in " + record_context + ": " + e.what());
        }
    }
    void lower_field(intent_stage_t* stage, const std::string& path, const std::string& raw,
        bool subject_binding, const std::map<std::string, std::string>& attributes) {
        auto i = fields.find(path);
        if (i == fields.end()) throw std::runtime_error("recipe has no field disposition");
        const auto& rule = i->second;
        if (rule.disposition & (1u << 10)) return;
        const std::string* context_value = nullptr;
        if (!rule.context_field.empty()) {
            const auto context = attributes.find(rule.context_field);
            if (context == attributes.end())
                throw std::runtime_error("missing declared context field " + rule.context_field);
            context_value = &context->second;
        }
        if (raw.empty() || (!rule.absent.empty() && raw == rule.absent)) return;
        const bool testimony = (rule.disposition & (1u << 6)) != 0;
        const bool ordinary_content = (rule.disposition & (1u << 1)) != 0;
        const bool reference = (rule.disposition & (1u << 5)) != 0;
        const bool default_value = rule.has_default && raw == rule.default_value;
        const bool emitted_testimony = testimony && !(default_value && rule.omit_default_testimony);
        if (!emitted_testimony && !ordinary_content) {
            if (testimony && default_value && rule.omit_default_testimony) return;
            if ((rule.disposition & (1u << 9)) || ((rule.disposition & 1u) && subject_binding)) return;
            throw std::runtime_error("field disposition has no executable lowering");
        }
        auto emit_fact = [&](const fact& value) { if (emitted_testimony) facts.push_back(value); };
        hash128_t relation_type; hash128_blake3_str("RelationType", &relation_type);
        if (emitted_testimony) relation_entity(stage, rule.relation, relation_type);
        if (emitted_testimony && nonzero(rule.parent)) {
            std::string declaration(reinterpret_cast<const char*>(&rule.relation), sizeof(rule.relation));
            declaration.append(reinterpret_cast<const char*>(&rule.parent), sizeof(rule.parent));
            if (declared_parents.find(declaration) == declared_parents.end()) {
                relation_entity(stage, rule.parent, relation_type);
                fact parent;
                check(laplace_relation_type_id("IS_A", &parent.relation), "relation identity");
                parent.object = rule.parent; parent.has_object = true;
                attest(stage, rule.relation, parent);
                declared_parents.emplace(std::move(declaration));
            }
        }
        fact f; f.relation = rule.relation; f.rank = rule.rank; f.explicit_rank = true;
        if (context_value && !context_value->empty()) {
            f.context = content(stage, *context_value);
            f.has_context = true;
        }
        if (rule.kind == 1) {
            if (raw == "Y" || raw == "Yes" || raw == "true" || raw == "True" || raw == "1") f.confirm = true;
            else if (raw == "N" || raw == "No" || raw == "false" || raw == "False" || raw == "0") f.confirm = false;
            else throw std::runtime_error("invalid boolean: " + raw);
            if (ordinary_content) content(stage, raw);
            emit_fact(f); return;
        }
        if (emitted_testimony && nonzero(rule.lexical_relation)) {
            relation_entity(stage, rule.lexical_relation, relation_type);
            fact lexical = f; lexical.relation = rule.lexical_relation; lexical.explicit_rank = false;
            lexical.object = content(stage, raw); lexical.has_object = true; emit_fact(lexical);
        }
        f.has_object = true;
        if (rule.codec == 3) {
            bool found = false;
            for (const auto& token : split(raw, " ")) {
                if (token.rfind("U+", 0) != 0 && token.rfind("u+", 0) != 0) continue;
                found = true; size_t q = token.find('<'); f.object = point_id(point(token.substr(0, q)));
                f.has_context = q != std::string::npos && q + 1 < token.size();
                if (f.has_context) f.context = content(stage, token.substr(q + 1));
                emit_fact(f);
            }
            if (!found) throw std::runtime_error("missing qualified reference");
            return;
        }
        if (rule.codec == 1 || (rule.codec == 0 && rule.kind == 6)) {
            const uint32_t cp = point(raw);
            f.object = point_id(cp);
            if (ordinary_content)
                check(content_witness_emit_floor_atom(stage, cp, &f.object,
                    INTENT_STAGE_PG_EPOCH_UNIX_US), "field floor physicality");
            if (reference) entity(stage, f.object, rule.entity_type);
        }
        else if (rule.codec == 2 || (rule.codec == 0 && rule.kind == 7)) {
            f.object = content(stage, sequence_text(raw, rule.separator));
            if (reference) entity(stage, f.object, rule.entity_type);
        }
        else if (rule.kind == 4 || rule.kind == 5) {
            const auto values = rule.separator.empty() ? std::vector<std::string>{raw} : split(raw, rule.separator);
            for (const auto& value : values) {
                f.object = content(stage, value);
                if (reference) entity(stage, f.object, rule.entity_type);
                emit_fact(f);
            }
            return;
        }
        else if (rule.kind == 8 || rule.kind == 9) {
            f.object = content(stage, raw);
            if (reference) entity(stage, f.object, rule.entity_type);
        }
        else {
            auto values = rule.kind == 3 ? split(raw, rule.separator) : std::vector<std::string>{raw};
            for (auto value : values) {
                auto alias = rule.aliases.find(alias_key(value)); if (alias != rule.aliases.end()) value = alias->second;
                f.object = classifier(stage, rule.object_namespace, value, rule.entity_type); emit_fact(f);
            }
            return;
        }
        emit_fact(f);
    }
    void prepare(intent_stage_t* stage) {
        const node& record = pending.front(); auto route_it = routes.find(record.name);
        if (route_it == routes.end()) throw std::runtime_error("recipe has no record route for " + record.name);
        const auto& route = route_it->second;
        const std::string identity = record.get(route.identity);
        record_context = record.name + " [" + (identity.empty()
            ? route.first + "=" + record.get(route.first) + ", " + route.last + "=" + record.get(route.last)
            : route.identity + "=" + identity.substr(0, 160) + (identity.size() > 160 ? "..." : "")) + "]";
        if (record.ns != route.ns) throw std::runtime_error("record namespace mismatch: " + record.name);
        range = route.kind == 0;
        membership = false; record_facts_done = false; fact_offset = 0;
        if (range) {
            std::string single = record.get(route.identity);
            cursor = point(single.empty() ? record.get(route.first) : single);
            end = single.empty() ? point(record.get(route.last)) : cursor;
            if (cursor > end) throw std::runtime_error("inverted ordinal range");
        } else if (route.kind == 3) {
            std::string single = record.get(route.identity);
            const uint32_t first = point(single.empty() ? record.get(route.first) : single);
            const uint32_t last = single.empty() ? point(record.get(route.last)) : first;
            if (first > last) throw std::runtime_error("inverted interval range");
            subject = interval_subject(stage, first, last);
        } else if (route.kind == 1) {
            const std::string value = record.get(route.identity);
            if (route.subject_codec == 0) subject = content(stage, value);
            else if (route.subject_codec == 2)
                subject = content(stage, sequence_text(value, route.separator));
            else if (route.subject_codec == 1) {
                const uint32_t cp = point(value);
                subject = point_id(cp);
                check(content_witness_emit_floor_atom(stage, cp, &subject,
                    INTENT_STAGE_PG_EPOCH_UNIX_US), "subject floor physicality");
            } else throw std::runtime_error("unsupported subject reference codec");
            entity(stage, subject, route.entity_type);
        } else if (route.kind == 2) {
            auto value = record.get(route.identity);
            const auto alias = route.aliases.find(alias_key(value));
            if (alias != route.aliases.end()) value = alias->second;
            subject = classifier(stage, route.object_namespace, value, route.entity_type);
        } else throw std::runtime_error("unknown subject instruction");
        if (!route.range_first.empty()) {
            cursor = point(record.get(route.range_first));
            end = point(record.get(route.range_last));
            if (cursor > end) throw std::runtime_error("inverted subject membership range");
            membership = true; membership_relation = route.range_relation;
            hash128_t relation_type; hash128_blake3_str("RelationType", &relation_type);
            relation_entity(stage, membership_relation, relation_type);
        }
        facts.clear();
        for (const auto& a : record.attributes) {
            const bool bound = a.first == route.identity || a.first == route.first || a.first == route.last ||
                a.first == route.range_first || a.first == route.range_last;
            field(stage, route.prefix + "/@" + a.first, a.second, bound, record.attributes);
        }
        for (const auto& child : record.children) {
            auto prefix = route.children.find(child.name);
            if (prefix == route.children.end()) throw std::runtime_error("recipe has no child disposition: " + child.name);
            if (child.ns != route.ns || !child.children.empty()) throw std::runtime_error("unaccounted nested structure");
            for (const auto& a : child.attributes)
                field(stage, prefix->second + "/@" + a.first, a.second, false, child.attributes);
        }
        active = true;
    }
};

extern "C" int laplace_recipe_stream_new(const uint8_t* program, size_t n,
    const hash128_t* witness, double trust, laplace_recipe_stream_t** out) {
    if (!program || !witness || !out || !std::isfinite(trust) || trust < 0 || trust > 1) return -1;
    *out = nullptr;
    std::unique_ptr<laplace_recipe_stream> s;
    try {
        s = std::make_unique<laplace_recipe_stream>(); s->witness = *witness; s->trust = trust;
        image_reader r{program,n};
        const uint32_t version = r.number();
        const bool rcp2_or_later = version == 0x32504352u || version == 0x33504352u
            || version == 0x34504352u || version == 0x35504352u;
        if (version != 0x31504352u && !rcp2_or_later)
            throw std::runtime_error("unsupported recipe instruction version");
        s->depth = int(r.number());
        if (s->depth < 0 || s->depth > 128) throw std::runtime_error("invalid record depth");
        uint32_t provider_kind = 0;
        if (rcp2_or_later) {
            provider_kind = r.number();
            if (provider_kind > 1) throw std::runtime_error("unsupported recipe syntax provider");
            if (provider_kind == 1) {
                recipe_delimited_config config;
                config.record_name = r.text(); config.namespace_uri = r.text();
                config.separator = r.text(); config.comment_prefix = r.text();
                const uint32_t trim = r.number();
                if (trim > 1) throw std::runtime_error("invalid field trimming instruction");
                config.trim_fields = trim != 0;
                config.directive_prefix = r.text(); config.directive_record_name = r.text();
                const uint32_t column_count = r.number();
                for (uint32_t i = 0; i < column_count; ++i) config.columns.push_back(r.text());
                const uint32_t directive_column_count = r.number();
                for (uint32_t i = 0; i < directive_column_count; ++i) config.directive_columns.push_back(r.text());
                config.range_column = r.text(); config.range_separator = r.text();
                config.range_first_field = r.text(); config.range_last_field = r.text();
                config.minimum_columns = r.number();
                const uint32_t trailing_empty = r.number();
                if (trailing_empty > 1) throw std::runtime_error("invalid trailing-column instruction");
                config.allow_trailing_empty_column = trailing_empty != 0;
                s->delimited = std::make_unique<recipe_delimited_stream>(std::move(config));
            }
        }
        uint32_t count = r.number();
        for (uint32_t j = 0; j < count; ++j) {
            field_rule f; f.path = r.text(); f.kind = r.number(); f.disposition = r.number(); f.codec = r.number();
            if (f.kind > 9 || f.codec > 3) throw std::runtime_error("unknown field opcode");
            f.absent = r.text(); f.separator = r.text(); f.object_namespace = r.text();
            f.relation = r.hash(); f.parent = r.hash(); f.entity_type = r.hash(); f.lexical_relation = r.hash(); f.rank = r.real();
            uint32_t aliases = r.number();
            for (uint32_t a = 0; a < aliases; ++a) { auto k = r.text(); auto v = r.text(); if (!f.aliases.emplace(alias_key(k),v).second) throw std::runtime_error("duplicate value alias instruction"); }
            if (version == 0x33504352u || version == 0x34504352u || version == 0x35504352u) {
                const uint32_t has_default = r.number();
                if (has_default > 1) throw std::runtime_error("invalid semantic-default instruction");
                f.has_default = has_default != 0;
                f.default_value = r.text();
                const uint32_t omit_default_testimony = r.number();
                if (omit_default_testimony > 1)
                    throw std::runtime_error("invalid default-testimony instruction");
                f.omit_default_testimony = omit_default_testimony != 0;
                if (f.omit_default_testimony && !f.has_default)
                    throw std::runtime_error("default testimony omission requires a semantic default");
            }
            if (rcp2_or_later) f.context_field = r.text();
            if (!f.context_field.empty() && f.codec == 3)
                throw std::runtime_error("field context conflicts with qualified-reference context at " + f.path);
            std::string key = f.path;
            if (!s->fields.emplace(key,std::move(f)).second) throw std::runtime_error("duplicate field instruction");
        }
        if (version == 0x34504352u || version == 0x35504352u) {
            count = r.number();
            for (uint32_t j = 0; j < count; ++j) {
                structure_rule structure;
                structure.path = r.text();
                structure.semantic_type = r.text();
                structure.disposition = r.number();
                if (structure.path.empty() || structure.semantic_type.empty() || structure.disposition == 0)
                    throw std::runtime_error("invalid structure instruction");
                std::string key = structure.path;
                if (!s->structures.emplace(key, std::move(structure)).second)
                    throw std::runtime_error("duplicate structure instruction");
            }
        }
        count = r.number();
        for (uint32_t j = 0; j < count; ++j) {
            route_rule route; route.name = r.text(); route.ns = r.text(); route.prefix = r.text(); route.kind = r.number();
            route.identity = r.text(); route.first = r.text(); route.last = r.text(); route.object_namespace = r.text();
            route.separator = r.text(); route.subject_codec = r.number(); route.entity_type = r.hash(); uint32_t children = r.number();
            for (uint32_t k = 0; k < children; ++k) { auto name = r.text(); auto prefix = r.text(); route.children.emplace(name,prefix); }
            route.range_first = r.text(); route.range_last = r.text(); route.range_relation = r.hash();
            uint32_t aliases = r.number();
            for (uint32_t a = 0; a < aliases; ++a) {
                auto key = alias_key(r.text()); auto value = r.text();
                if (!route.aliases.emplace(key, value).second)
                    throw std::runtime_error("duplicate subject alias instruction");
            }
            if (version == 0x34504352u || version == 0x35504352u) {
                const uint32_t structures = r.number();
                route.structures.reserve(structures);
                for (uint32_t k = 0; k < structures; ++k) {
                    std::string path = r.text();
                    if (s->structures.find(path) == s->structures.end())
                        throw std::runtime_error("route references unknown structure " + path);
                    route.structures.push_back(std::move(path));
                }
                if (version == 0x35504352u) {
                    const uint32_t inherit = r.number();
                    if (inherit > 1) throw std::runtime_error("invalid parent-attribute inheritance instruction");
                    route.inherit_parent_attributes = inherit != 0;
                }
            }
            if (route.kind > 3 || route.subject_codec > 2 || (route.range_first.empty() != route.range_last.empty()) ||
                (!route.range_first.empty() && (route.kind == 0 || !nonzero(route.range_relation))))
                throw std::runtime_error("invalid record route instruction");
            auto key = route.name;
            if (!s->routes.emplace(key,std::move(route)).second) throw std::runtime_error("duplicate route instruction");
        }
        if (r.remaining) throw std::runtime_error("trailing recipe instructions");
        if (provider_kind == 0)
            s->check(laplace_xml_stream_new(&s->parser), "XML provider creation");
        *out = s.release(); return 0;
    } catch (const std::bad_alloc&) { return -3; }
    catch (const std::exception& e) { if (s) { s->failed = true; try { s->error = e.what(); } catch (...) {} *out = s.release(); } return -2; }
    catch (...) { return -3; }
}
extern "C" int laplace_recipe_stream_feed(laplace_recipe_stream_t* s, const uint8_t* bytes, size_t n, int final) {
    if (!s || s->failed || s->final || !s->pending.empty() || s->active || s->ready) return -1;
    try {
        if (s->delimited) {
            s->delimited->feed(bytes, n, final != 0,
                [&](const std::string& name, const std::string& ns,
                    std::map<std::string, std::string>&& attributes) {
                    s->pending.push_back(node{name, ns, std::move(attributes), {}});
                });
            s->final = final != 0;
            return 0;
        }
        const laplace_xml_event_t* events = nullptr; size_t count = 0;
        if (laplace_xml_stream_feed(s->parser,bytes,n,final,&events,&count) != 0)
            throw std::runtime_error(laplace_xml_stream_error(s->parser));
        for (size_t i = 0; i < count; ++i) {
            const auto& e = events[i];
            if (e.depth + 1 == s->depth) {
                if (e.kind == 1) {
                    s->parent_scope = node{e.name, e.namespace_uri ? e.namespace_uri : "", {}, {}};
                    s->parent_spans.clear();
                    s->parent_scope_active = true;
                } else if (e.kind == 4 && s->parent_scope_active) {
                    if (e.namespace_uri && *e.namespace_uri)
                        throw std::runtime_error("recipe has no namespaced parent attribute disposition");
                    if (!s->parent_scope.attributes.emplace(
                            e.name, std::string(e.value, e.value_len)).second)
                        throw std::runtime_error("duplicate parent record attribute");
                } else if (e.kind == 2) {
                    if (s->parent_scope_active) {
                        auto route = s->routes.find(s->parent_scope.name);
                        if (route != s->routes.end()) {
                            const bool explicit_subject =
                                !s->parent_scope.get(route->second.identity).empty()
                                || !s->parent_scope.get(route->second.first).empty()
                                || !s->parent_scope.get(route->second.last).empty();
                            if (route->second.kind == 3 && !explicit_subject
                                && !s->parent_spans.empty()) {
                                std::sort(s->parent_spans.begin(), s->parent_spans.end(),
                                    [](const ordinal_span& a, const ordinal_span& b) {
                                        return a.first != b.first ? a.first < b.first : a.last < b.last;
                                    });
                                std::vector<ordinal_span> merged;
                                merged.reserve(s->parent_spans.size());
                                for (const ordinal_span span : s->parent_spans) {
                                    if (span.first > span.last)
                                        throw std::runtime_error("inverted parent coverage span");
                                    if (merged.empty()
                                        || static_cast<uint64_t>(span.first)
                                            > static_cast<uint64_t>(merged.back().last) + 1) {
                                        merged.push_back(span);
                                    } else if (span.last > merged.back().last) {
                                        merged.back().last = span.last;
                                    }
                                }
                                for (const ordinal_span span : merged) {
                                    node projected = s->parent_scope;
                                    projected.attributes[route->second.first] = point_text(span.first);
                                    projected.attributes[route->second.last] = point_text(span.last);
                                    s->pending.push_back(std::move(projected));
                                }
                            } else {
                                s->pending.push_back(std::move(s->parent_scope));
                            }
                        }
                    }
                    s->parent_scope = {};
                    s->parent_spans.clear();
                    s->parent_scope_active = false;
                }
                continue;
            }
            if (e.depth < s->depth) continue;
            if (e.kind == 1) s->stack.push_back(node{e.name,e.namespace_uri ? e.namespace_uri : "",{}, {}});
            else if (e.kind == 4) {
                if (s->stack.empty()) throw std::runtime_error("attribute outside record");
                if (e.namespace_uri && *e.namespace_uri)
                    throw std::runtime_error("recipe has no namespaced attribute disposition");
                if (!s->stack.back().attributes.emplace(e.name,std::string(e.value,e.value_len)).second)
                    throw std::runtime_error("duplicate record attribute");
            } else if (e.kind == 2) {
                if (s->stack.empty()) throw std::runtime_error("unbalanced record");
                node done = std::move(s->stack.back()); s->stack.pop_back();
                if (s->stack.empty()) {
                    auto route = s->routes.find(done.name);
                    if (s->parent_scope_active) {
                        auto parent_route = s->routes.find(s->parent_scope.name);
                        if (parent_route != s->routes.end() && parent_route->second.kind == 3
                            && route != s->routes.end() && route->second.kind == 3) {
                            const std::string single = done.get(route->second.identity);
                            const std::string first = single.empty()
                                ? done.get(route->second.first) : single;
                            const std::string last = single.empty()
                                ? done.get(route->second.last) : single;
                            if (!first.empty() && !last.empty()) {
                                const uint32_t lo = point(first), hi = point(last);
                                if (lo > hi)
                                    throw std::runtime_error("inverted child coverage span");
                                s->parent_spans.push_back({lo, hi});
                            }
                        }
                    }
                    if (route != s->routes.end() && route->second.inherit_parent_attributes) {
                        if (!s->parent_scope_active)
                            throw std::runtime_error("record requires inherited parent attributes");
                        if (done.ns != s->parent_scope.ns)
                            throw std::runtime_error("record/parent namespace mismatch");
                        for (const auto& a : s->parent_scope.attributes)
                            done.attributes.try_emplace(a.first, a.second);
                    }
                    s->pending.push_back(std::move(done));
                } else s->stack.back().children.push_back(std::move(done));
            } else if (e.kind == 3) {
                for (size_t b = 0; b < e.value_len; ++b)
                    if (e.value[b] != ' ' && e.value[b] != '\r' && e.value[b] != '\n' && e.value[b] != '\t')
                        throw std::runtime_error("recipe has no text-node disposition");
            }
        }
        s->final = final != 0; return 0;
    } catch (const std::exception& e) { s->failed = true; try { s->error = e.what(); } catch (...) {} return -2; }
    catch (...) { s->failed = true; return -3; }
}
namespace {
// Preserve all native interpretation/provenance tuples when coalescing records.
// The final import is one set operation, independent of semantic row count.
struct tuple_span {
    const uint8_t* bytes;
    size_t size;
};
struct tuple_selection {
    std::array<std::vector<tuple_span>, 4> spans;
    std::array<size_t, 4> byte_counts{};
    size_t row_count = 0;
};
struct tuple_batch {
    std::array<std::vector<uint8_t>, 4> buffers;
    // Keys contain the complete canonical tuple, not only its id. Distinct
    // type/provenance interpretations and physical realizations survive; hash
    // equality alone never establishes identity. Testimony is never indexed.
    std::array<std::unordered_set<std::string>, 4> canonical_rows;
    size_t row_count = 0, base_bytes;
    explicit tuple_batch(size_t base) : base_bytes(base) {}
    static const uint8_t* buffer(const intent_stage_t* stage, size_t index, size_t* size) {
        return index == 3 ? intent_stage_entity_interpretation_tuple_ptr(stage, size)
            : intent_stage_tuple_ptr(stage, static_cast<intent_stage_table_t>(index + 1), size);
    }
    static size_t tuple_size(const uint8_t* bytes, size_t available, uint16_t columns) {
        if (available < 2 || (uint16_t(bytes[0]) << 8 | bytes[1]) != columns)
            throw std::runtime_error("recipe canonical tuple has invalid columns");
        size_t offset = 2;
        for (uint16_t column = 0; column < columns; ++column) {
            if (available - offset < 4)
                throw std::runtime_error("recipe canonical tuple has truncated field length");
            const uint32_t length = uint32_t(bytes[offset]) << 24 |
                uint32_t(bytes[offset + 1]) << 16 | uint32_t(bytes[offset + 2]) << 8 |
                uint32_t(bytes[offset + 3]);
            offset += 4;
            if (length == UINT32_MAX) continue;
            if (length > INT32_MAX || length > available - offset)
                throw std::runtime_error("recipe canonical tuple has truncated field body");
            offset += length;
        }
        return offset;
    }
    tuple_selection select(const intent_stage_t* stage) const {
        tuple_selection selected;
        for (size_t i = 0; i < buffers.size(); ++i) {
            size_t size = 0; const uint8_t* bytes = buffer(stage, i, &size);
            if (!size) continue;
            if (i == 2) {
                // Every source observation, including repeated interval facts,
                // retains its exact testimony cardinality and replay semantics.
                selected.spans[i].push_back({bytes, size});
                selected.byte_counts[i] = size;
                selected.row_count += intent_stage_attestation_count(stage);
                continue;
            }
            std::unordered_set<std::string> local;
            size_t offset = 0;
            while (offset < size) {
                const size_t length = tuple_size(bytes + offset, size - offset, i == 1 ? 10 : 4);
                std::string key(reinterpret_cast<const char*>(bytes + offset), length);
                if (canonical_rows[i].find(key) == canonical_rows[i].end() &&
                    local.emplace(std::move(key)).second) {
                    selected.spans[i].push_back({bytes + offset, length});
                    selected.byte_counts[i] += length;
                    if (i < 3) ++selected.row_count;
                }
                offset += length;
            }
        }
        return selected;
    }
    bool fits(const tuple_selection& selected, size_t maximum_rows, size_t maximum_bytes) const {
        if (selected.row_count > maximum_rows - row_count || base_bytes > maximum_bytes) return false;
        size_t remaining = maximum_bytes - base_bytes;
        for (size_t i = 0; i < buffers.size(); ++i) {
            const size_t added = selected.byte_counts[i];
            if (added > SIZE_MAX - buffers[i].size()) return false;
            size_t needed = buffers[i].size() + added;
            if (needed > remaining) return false;
            if (!needed) continue;
            size_t capacity = 256;
            while (capacity < needed) {
                if (capacity > SIZE_MAX / 2) { capacity = needed; break; }
                capacity *= 2;
            }
            // Match the bounded stage allocator's exact-fit fallback.
            remaining -= capacity > remaining ? needed : capacity;
        }
        return true;
    }
    void append(const tuple_selection& selected) {
        for (size_t i = 0; i < buffers.size(); ++i)
            for (const auto& span : selected.spans[i]) {
                buffers[i].insert(buffers[i].end(), span.bytes, span.bytes + span.size);
                if (i != 2)
                    canonical_rows[i].emplace(reinterpret_cast<const char*>(span.bytes), span.size);
            }
        row_count += selected.row_count;
    }
    stage_ptr finish(size_t maximum_bytes) {
        intent_stage_t* raw = nullptr;
        int result = intent_stage_from_tuple_bytes(buffers[0].data(), buffers[0].size(),
            buffers[1].data(), buffers[1].size(), buffers[2].data(), buffers[2].size(), maximum_bytes, &raw);
        stage_ptr stage(raw, intent_stage_free);
        if (result != 0 || !stage) throw std::runtime_error("recipe tuple batch exceeds the admitted byte envelope");
        if (intent_stage_import_entity_interpretations(stage.get(), buffers[3].data(), buffers[3].size()) != 0)
            throw std::runtime_error("recipe interpretation batch exceeds the admitted byte envelope");
        return stage;
    }
};
static int fail_stream(laplace_recipe_stream_t* s, const char* message) noexcept {
    s->failed = true;
    try { s->error = message; } catch (...) {}
    return -2;
}
}
extern "C" int laplace_recipe_stream_drain(laplace_recipe_stream_t* s, size_t maximum_rows,
    size_t maximum_bytes, intent_stage_t** out, uint64_t* completed) {
    if (!s || s->failed || !out || !completed || !maximum_rows || !maximum_bytes) return -1;
    *out = nullptr; *completed = 0;
    if (s->pending.empty() && !s->ready) return 0;
    try {
        stage_ptr empty(intent_stage_new_bounded(0, maximum_bytes), intent_stage_free);
        if (!empty) throw std::runtime_error("recipe byte envelope cannot hold an output stage");
        const size_t base = intent_stage_memory_bytes(empty.get());
        empty.reset();
        tuple_batch batch(base);
        // Reuse exact value composition across physical records in this output
        // working set. Cache eviction/rebatching changes work, never identities
        // or field testimony. A deferred stage owns every row it has prepared.
        s->content_cache.clear(); s->content_cache_bytes = 0;
        s->content_cache_limit = maximum_bytes / 4;
        s->content_cache_entries = maximum_rows;
        uint64_t finished = 0;
        while (!s->pending.empty() || s->ready) {
            if (!s->ready) {
                s->ready.reset(intent_stage_new_bounded(0, maximum_bytes));
                if (!s->ready) throw std::bad_alloc();
                s->ready_completed = 0;
                if (!s->active) {
                    s->prepare(s->ready.get());
                } else {
                    // Reserve enough room for geometric buffer growth as well as
                    // the fixed-width attestation payload; resume within a subject
                    // when its facts exceed a batch, rather than replaying facts.
                    size_t width = 0;
                    if (intent_stage_tuple_payload_bound(0, 0, 0, 1, &width) != 0)
                        throw std::runtime_error("invalid testimony payload bound");
                    size_t capacity = std::min(maximum_rows, (maximum_bytes - base) / width / 4);
                    if (!capacity) throw std::runtime_error("recipe byte envelope cannot hold testimony");
                    while (rows(s->ready.get()) < capacity) {
                        if (!s->record_facts_done) {
                            const size_t available = capacity - rows(s->ready.get());
                            if (s->range && s->fact_offset == 0 && !s->facts.empty()
                                && available >= s->facts.size()) {
                                const size_t subjects = std::min<size_t>(
                                    available / s->facts.size(),
                                    static_cast<size_t>(s->end - s->cursor) + 1);
                                const uint32_t last = static_cast<uint32_t>(
                                    s->cursor + subjects - 1);
                                const size_t emitted = s->attest_range_facts(
                                    s->ready.get(), s->cursor, last);
                                if (emitted != subjects * s->facts.size())
                                    throw std::runtime_error("range testimony batch cardinality mismatch");
                                s->cursor = last;
                                if (s->cursor != s->end) {
                                    ++s->cursor;
                                    continue;
                                }
                                s->record_facts_done = true;
                            } else {
                                const hash128_t subj = s->range ? point_id(s->cursor) : s->subject;
                                const size_t remaining = s->facts.size() - s->fact_offset;
                                const size_t emit = std::min(available, remaining);
                                if (emit)
                                    s->fact_offset += s->attest_facts(
                                        s->ready.get(), subj, s->fact_offset, emit);
                                if (s->fact_offset != s->facts.size()) break;
                                s->fact_offset = 0;
                                if (s->range && s->cursor != s->end) {
                                    ++s->cursor;
                                    // Empty facts have no per-position work to perform.
                                    if (s->facts.empty()) s->cursor = s->end;
                                    continue;
                                }
                                s->record_facts_done = true;
                            }
                        }
                        if (s->membership) {
                            if (rows(s->ready.get()) == capacity) break;
                            fact member; member.relation = s->membership_relation;
                            member.object = s->subject; member.has_object = true;
                            s->attest(s->ready.get(), point_id(s->cursor), member);
                            if (s->cursor != s->end) { ++s->cursor; continue; }
                        }
                        s->active = false; s->pending.pop_front(); ++s->ready_completed;
                        break;
                    }
                }
            }
            const tuple_selection selected = batch.select(s->ready.get());
            if (!batch.fits(selected, maximum_rows, maximum_bytes)) {
                if (batch.row_count || finished) break;
                throw std::runtime_error("one record's structure exceeds the admitted output envelope");
            }
            batch.append(selected); finished += s->ready_completed;
            s->ready.reset(); s->ready_completed = 0;
            if (batch.row_count == maximum_rows) break;
        }
        stage_ptr result = batch.finish(maximum_bytes);
        *completed = finished; *out = result.release(); return 1;
    } catch (const std::exception& e) { return fail_stream(s, e.what()); }
    catch (...) { return fail_stream(s, "unknown native recipe failure"); }
}
extern "C" const char* laplace_recipe_stream_error(const laplace_recipe_stream_t* s) {
    return s ? s->error.c_str() : "missing recipe stream";
}
extern "C" void laplace_recipe_stream_free(laplace_recipe_stream_t* s) { delete s; }
