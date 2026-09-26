#include "laplace/core/recipe_stream.h"
#include "laplace/core/entity_type_law.h"
#include "laplace/core/utf8.h"
#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/attestation_engine.h"
#include "laplace/core/ordered_composition.h"
#include "laplace/core/trajectory.h"
#include "laplace/core/relation_law.h"
#include "laplace/core/pos_law.h"
#include "laplace/core/language_law.h"
#include "laplace/core/deprel_law.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/qualifier_law.h"
#include "recipe_provider.hpp"
#include "laplace/core/xml_stream.h"
#include <algorithm>
#include <cmath>
#include <cstdlib>
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
    // Grouped-record lowering (RCP6). subject_mode: 0 record subject; 1 the value
    // is the subject and the record subject the object; 2 the group trunk is the
    // subject. pair_mode: 0 single value; 1 items "key\x1fvalue" with the key as
    // relation and the value as object; 2 items "reference\x1fvalue" with the
    // referenced row as subject and the value as relation; 3 items "key=value" whose
    // object is the ordered composition [key, value] under the field's relation
    // (a UD feature "Number=Plur" is the composition [Number, Plur], never a string).
    std::string relation_field, trunk_field, pair_value_separator;
    uint32_t subject_mode = 0, pair_mode = 0, relation_resolver = 0;
    bool omit_equal_subject = false, group_once = false;
    uint32_t kind, disposition, codec;
    bool has_default = false, omit_default_testimony = false;
    hash128_t relation, parent, entity_type, lexical_relation;
    double rank;
    std::unordered_map<std::string, std::string> aliases;
    std::string identity_table, object_literal, context_literal, observation_of, score_of;
    struct vocabulary_rule { int kind = 0; int tagset = -1; } vocabulary;  // 1 pos/<tagset>, 2 lang/iso639
    bool aggregate = false;
    // Governed claim qualifiers (qualifier_law): static bits, plus one family whose value
    // the record's qualifier_field attribute names.
    std::array<uint8_t, 32> qualifiers{};
    bool has_qualifiers = false;
    std::string qualifier_family, qualifier_field;
    // RCP8: the object is the ordered composition [record subject, value], the same
    // identity a child subject of that record has (a FrameNet requiresFE names an FE
    // of the same frame).
    bool object_record_scoped = false;
};
struct identity_table_rule {
    std::string name, record, key_path, value_path;
    std::unordered_set<std::string> absent;
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
    std::string identity_table;
    // A grouped record lowered to its trunk's parse physicality (column positions).
    struct parse_rule {
        bool on = false;
        std::string trunk;
        size_t id = 0, form = 0, upos = 0, head = 0, deprel = 0;
        int upos_tagset = -1;
    } parse;
    std::vector<std::string> witness_fields;
    // A nested element that is its own subject (RCP8): the ordered composition
    // [record subject, content(child identity attribute)], typed, optionally linked
    // from the parent by a governed relation (a frame HAS_FRAME_ELEMENT its FE).
    // One part of a composed identity (RCP8): record/element-relative path values in
    // source order, joined, optionally resolved through a governed vocabulary; scoped
    // as [content(record attribute), value]; or the compositions of named children.
    struct identity_part {
        std::string path, join, scope, children;
        field_rule::vocabulary_rule vocabulary;
        hash128_t scope_type{};
    };
    std::vector<identity_part> subject_parts;
    struct element_rule {
        std::vector<identity_part> parts;
        hash128_t entity_type{}, relation{};
        std::string observation;
    };
    std::unordered_map<std::string, element_rule> elements;
    struct child_subject_rule {
        std::string identity;
        hash128_t relation{}, entity_type{};
        bool child_is_subject = false;   // child -rel-> parent (an LU EVOKES_FRAME its frame)
        // Several identity parts: a FrameNet lexical unit is [frame, its lexemes'
        // text, UPOS]. Empty = the identity attribute.
        std::vector<identity_part> parts;
    };
    std::unordered_map<std::string, child_subject_rule> child_subjects;
};
using node = recipe_node;
struct ordinal_span {
    uint32_t first = 0, last = 0;
};
struct fact {
    hash128_t relation{}, object{}, context{}, subject{};
    bool has_object = false, has_context = false, confirm = true, explicit_rank = false;
    bool has_subject = false;
    double rank = 1;
    hash128_t source{};           // the witness of this observation
    bool has_source = false;
    std::array<uint8_t, 32> qualifiers{};   // multi-select qualifier flags
    bool has_qualifiers = false;
    // A claim is a Glicko-2 game series: games observed, each scored in [0,1]
    // (win 1, draw 0.5, loss 0). A binary claim is one game at 1 or 0.
    int64_t games = 1;
    double score = -1;   // < 0: the categorical score of `confirm`
};
struct content_form {
    hash128_t id{};
    double coord[4]{};
    hilbert128_t hilbert{};
    uint8_t tier = 0;
    uint32_t atom = 0;
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
// Record-relative path: "@attr" on the record itself or "Child/.../@attr" on every
// matching descendant, in source order.
static void collect(const node& n, const std::string& path, std::vector<std::string>& out) {
    if (path.rfind("@", 0) == 0) {
        auto i = n.attributes.find(path.substr(1));
        if (i != n.attributes.end() && !i->second.empty()) out.push_back(i->second);
        return;
    }
    const size_t slash = path.find('/');
    if (slash == std::string::npos) throw std::runtime_error("invalid identity table path " + path);
    const std::string child = path.substr(0, slash), rest = path.substr(slash + 1);
    for (const auto& c : n.children) if (c.name == child) collect(c, rest, out);
}
// A governed vocabulary maps a source's value to the Laplace-standard label; a value
// the vocabulary does not map is the source's own and stays as written.
static field_rule::vocabulary_rule parse_vocabulary(const std::string& name, const std::string& where) {
    field_rule::vocabulary_rule v;
    if (name.rfind("pos/", 0) == 0 && (v.tagset = laplace_pos_tagset_from_name(name.c_str() + 4)) >= 0) v.kind = 1;
    else if (name == "lang/iso639") v.kind = 2;
    else throw std::runtime_error("undeclared vocabulary " + name + " at " + where);
    return v;
}
static std::string governed_value(const field_rule::vocabulary_rule& v, const std::string& value) {
    const char* canonical = nullptr;
    if (v.kind == 1)
        return laplace_pos_resolve_canonical(value.c_str(), static_cast<laplace_pos_tagset_t>(v.tagset),
                                             &canonical, nullptr) == 0 && canonical ? std::string(canonical) : value;
    if (v.kind == 2)
        return laplace_language_canonical(value.c_str(), &canonical) == 0 && canonical ? std::string(canonical) : value;
    return value;
}
static size_t rows(const intent_stage_t* s) {
    return intent_stage_entity_count(s) + intent_stage_physicality_count(s) + intent_stage_attestation_count(s);
}
}

struct laplace_recipe_stream {
    // The standard-format reader the recipe declares; the prescan reads with its own.
    recipe_provider_factory make_provider;
    std::unique_ptr<recipe_syntax_provider> provider, prescan_provider;
    // A delimited source's declared data columns (parse structures address them).
    std::vector<std::string> delimited_columns;
    bool delimited = false;
    hash128_t current_witness{};   // the witness of the record being lowered
    std::vector<identity_table_rule> table_rules;
    std::unordered_map<std::string, std::unordered_map<std::string, std::string>> tables;
    bool prescanned = false;
    hash128_t witness{};
    double trust = 0;
    int depth = 2;
    bool final = false, failed = false, active = false;
    std::string error, record_context, current_identity;
    std::unordered_map<std::string, field_rule> fields;
    std::unordered_map<std::string, structure_rule> structures;
    std::unordered_map<std::string, route_rule> routes;
    node parent_scope{};
    std::vector<ordinal_span> parent_spans;
    bool parent_scope_active = false;
    bool child_inherited = false;
    std::deque<node> pending;
    uint32_t cursor = 0, end = 0;
    bool range = false, membership = false, record_facts_done = false;
    size_t fact_offset = 0;
    hash128_t membership_relation{};
    stage_ptr ready{nullptr, intent_stage_free};
    uint64_t ready_completed = 0;
    hash128_t subject{};
    content_form subject_form{};     // the current subject's form, when it is content
    content_form record_subject_form{};  // the record's own subject (child subjects scope to it)
    bool subject_form_known = false;
    // Observation structure (not testimony): each record's observed content in source
    // order composes the record's trunk, and the records compose the file's trunk, so
    // every lowered content has containers back to the file it was observed in.
    bool observing = false, file_trunk_done = false;
    std::vector<laplace_ordered_component_t> record_parts, file_parts;
    std::unordered_set<std::string> record_seen;
    void observe(const content_form& c) {
        if (!observing) return;
        const std::string key(reinterpret_cast<const char*>(&c.id), sizeof(c.id));
        if (!record_seen.insert(key).second) return;
        laplace_ordered_component_t part{};
        part.id = c.id; std::memcpy(part.coord, c.coord, sizeof(part.coord));
        part.tier = c.tier; part.atom = c.atom; part.has_atom = c.tier == 0;
        record_parts.push_back(part);
    }
    laplace_ordered_component_t compose_trunk(intent_stage_t* stage,
        const std::vector<laplace_ordered_component_t>& parts, const char* type_name) {
        laplace_ordered_composition_request_t request{};
        request.components = parts.data(); request.component_count = parts.size();
        if (laplace_entity_type_id(type_name, &request.type_id) != 0)
            throw std::runtime_error(std::string("undeclared trunk entity type ") + type_name);
        request.source_id = witness;
        request.observed_at_unix_us = INTENT_STAGE_PG_EPOCH_UNIX_US;
        laplace_ordered_composition_result_t result{};
        check(laplace_ordered_composition_stage_batch(stage, &request, 1, &result), "observation trunk");
        laplace_ordered_component_t part{};
        part.id = result.id; std::memcpy(part.coord, result.coord, sizeof(part.coord));
        part.tier = result.tier;
        return part;
    }
    std::vector<fact> facts;
    std::unordered_map<std::string, content_form> content_cache;
    size_t content_cache_bytes = 0, content_cache_limit = 0, content_cache_entries = 0;

    // The current parent scope's witness self-description, when its route declares one.
    std::vector<std::string> scope_witness() const {
        const auto route = routes.find(parent_scope.name);
        if (route == routes.end() || route->second.witness_fields.empty()) return {};
        std::vector<std::string> parts;
        for (const auto& field : route->second.witness_fields) {
            const std::string value = parent_scope.get(field);
            if (value.empty()) return {};
            parts.push_back(value);
        }
        return parts;
    }
    // A reference names what the source says its id denotes; an unlisted id is a
    // dangling source reference, never silently kept as packaging.
    const std::string& identify(const std::string& table, const std::string& key) const {
        if (table.empty()) return key;
        const auto t = tables.find(table);
        if (t == tables.end()) throw std::runtime_error("identity table " + table + " is not declared");
        const auto i = t->second.find(key);
        if (i == t->second.end()) throw std::runtime_error("unresolved " + table + " reference: " + key);
        return i->second;
    }
    void harvest(const node& record) {
        for (const auto& rule : table_rules) {
            if (rule.record != record.name) continue;
            std::vector<std::string> keys, values;
            collect(record, rule.key_path, keys);
            collect(record, rule.value_path, values);
            std::string value = values.empty() ? std::string{} : values.front();
            if (rule.absent.count(value)) value.clear();
            auto& table = tables[rule.name];
            for (const auto& key : keys) {
                const std::string& denoted = value.empty() ? key : value;
                const auto placed = table.emplace(key, denoted);
                if (!placed.second && placed.first->second != denoted)
                    throw std::runtime_error("identity table " + rule.name + " states two values for " + key);
            }
        }
    }
    void check(int result, const char* action) {
        if (result != 0) throw std::runtime_error(std::string("recipe ") + action + " failed (" + std::to_string(result) + ")");
    }
    void entity(intent_stage_t* stage, hash128_t id, hash128_t type) {
        if (intent_stage_witness_seen(stage, &id)) return;
        check(intent_stage_add_entity(stage, &id, 2, &type), "entity admission");
        intent_stage_witness_record(stage, &id);
        if (intent_stage_allocation_failed(stage))
            throw std::runtime_error("recipe entity deduplication exceeded the admitted byte envelope");
    }
    content_form compose_content(intent_stage_t* stage, const std::string& text) {
        if (text.empty()) throw std::runtime_error("empty content reference");
        auto i = content_cache.find(text); if (i != content_cache.end()) { observe(i->second); return i->second; }
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
        result.atom = root_node.atom;
        const size_t entry_overhead = sizeof(content_form) + sizeof(std::string) + 4 * sizeof(void*);
        if (content_cache.size() < content_cache_entries &&
            entry_overhead <= content_cache_limit - content_cache_bytes &&
            text.size() <= content_cache_limit - content_cache_bytes - entry_overhead) {
            content_cache.emplace(text, result);
            content_cache_bytes += text.size() + entry_overhead;
        }
        observe(result);
        return result;
    }
    hash128_t content(intent_stage_t* stage, const std::string& text) {
        return compose_content(stage, text).id;
    }
    // [record subject, value]: the identity a child subject of this record named value has.
    hash128_t scoped_to_record(intent_stage_t* stage, const field_rule& rule, const std::string& value) {
        if (!subject_form_known) throw std::runtime_error("record-scoped object needs a content subject");
        const content_form b = compose_content(stage, value);
        laplace_ordered_component_t parts[2]{};
        parts[0].id = record_subject_form.id; std::memcpy(parts[0].coord, record_subject_form.coord, sizeof(parts[0].coord));
        parts[0].tier = record_subject_form.tier; parts[0].atom = record_subject_form.atom;
        parts[0].has_atom = record_subject_form.tier == 0;
        parts[1].id = b.id; std::memcpy(parts[1].coord, b.coord, sizeof(parts[1].coord));
        parts[1].tier = b.tier; parts[1].atom = b.atom; parts[1].has_atom = b.tier == 0;
        laplace_ordered_composition_request_t request{};
        request.components = parts; request.component_count = 2;
        request.type_id = rule.entity_type; request.source_id = current_witness;
        request.observed_at_unix_us = INTENT_STAGE_PG_EPOCH_UNIX_US;
        laplace_ordered_composition_result_t result{};
        check(laplace_ordered_composition_stage_batch(stage, &request, 1, &result), "record-scoped object");
        return result.id;
    }
    // The ordered composition [key, value] of two content parts, composed and staged
    // by the native ordered-composition kernel (identity, placement, trajectory).
    hash128_t compose_pair(intent_stage_t* stage, const field_rule& rule,
        const std::string& key, const std::string& value) {
        const content_form a = compose_content(stage, key), b = compose_content(stage, value);
        laplace_ordered_component_t parts[2]{};
        parts[0].id = a.id; std::memcpy(parts[0].coord, a.coord, sizeof(parts[0].coord));
        parts[0].tier = a.tier; parts[0].atom = a.atom; parts[0].has_atom = a.tier == 0;
        parts[1].id = b.id; std::memcpy(parts[1].coord, b.coord, sizeof(parts[1].coord));
        parts[1].tier = b.tier; parts[1].atom = b.atom; parts[1].has_atom = b.tier == 0;
        laplace_ordered_composition_request_t request{};
        request.components = parts; request.component_count = 2;
        request.type_id = rule.entity_type; request.source_id = current_witness;
        request.observed_at_unix_us = INTENT_STAGE_PG_EPOCH_UNIX_US;
        laplace_ordered_composition_result_t result{};
        check(laplace_ordered_composition_stage_batch(stage, &request, 1, &result), "pair composition");
        return result.id;
    }
    // The character data of an embedded XML fragment, in order, whitespace-collapsed.
    // A value that is not well-formed markup is its own text.
    static std::string markup_text(const std::string& raw) {
        if (raw.find('<') == std::string::npos) return raw;
        laplace_xml_stream_t* parser = nullptr;
        if (laplace_xml_stream_new(&parser) != 0 || !parser) return raw;
        const std::string wrapped = "<r>" + raw + "</r>";
        const laplace_xml_event_t* events = nullptr; size_t count = 0;
        std::string text;
        const bool ok = laplace_xml_stream_feed(parser, reinterpret_cast<const uint8_t*>(wrapped.data()),
            wrapped.size(), 1, &events, &count) == 0;
        if (ok)
            for (size_t i = 0; i < count; ++i)
                if (events[i].kind == 3) text.append(events[i].value, events[i].value_len);
        laplace_xml_stream_free(parser);
        if (!ok) return raw;
        std::string collapsed;
        bool space = false;
        for (char c : text) {
            const bool ws = c == ' ' || c == '\t' || c == '\r' || c == '\n';
            if (ws) { space = !collapsed.empty(); continue; }
            if (space) { collapsed.push_back(' '); space = false; }
            collapsed.push_back(c);
        }
        return collapsed.empty() ? raw : collapsed;
    }
    // A classifier value is content; its class follows from the claim that uses it.
    hash128_t classifier(intent_stage_t* stage, const std::string& value) {
        if (value.empty()) throw std::runtime_error("empty classifier binding");
        return content(stage, value);
    }

    void build_attestation(hash128_t subj, const fact& f, laplace_attestation_staged_t& row) {
        if (f.has_subject) subj = f.subject;
        // The witness's trust is the claim's strength; relation rank is read-time salience.
        const double weight = trust;
        const hash128_t* observer = f.has_source ? &f.source : &current_witness;
        if (f.games > 1 || f.score >= 0) {
            const double score = f.score >= 0 ? f.score : (f.confirm ? 1.0 : 0.0);
            const int64_t sum = static_cast<int64_t>(std::llround(score * 1e9)) * f.games;
        check(laplace_attestation_aggregated_build(&subj, &f.relation, f.has_object ? &f.object : nullptr,
                f.has_object ? 0 : 1, observer, f.has_context ? &f.context : nullptr,
                f.has_context ? 0 : 1, weight, f.games, sum, 0, &row), "graded testimony");
        } else
        check(laplace_attestation_resolved_build(&subj, &f.relation, f.has_object ? &f.object : nullptr,
            f.has_object ? 0 : 1, observer, f.has_context ? &f.context : nullptr,
            f.has_context ? 0 : 1, weight, f.confirm ? 1 : 0, 1, 0, &row), "testimony");
    }
    void attest(intent_stage_t* stage, hash128_t subj, const fact& f) {
        laplace_attestation_staged_t row{};
        build_attestation(subj, f, row);
        check(laplace_attestation_staged_batch_add(stage, &row, 1,
            f.has_qualifiers ? f.qualifiers.data() : nullptr), "testimony admission");
    }
    size_t attest_facts(intent_stage_t* stage, hash128_t subj, size_t offset, size_t count) {
        constexpr size_t chunk = 256;
        laplace_attestation_staged_t staged[chunk];
        uint8_t masks[chunk * 32];
        size_t emitted = 0;
        while (emitted < count) {
            const size_t n = std::min(chunk, count - emitted);
            bool any = false;
            for (size_t i = 0; i < n; ++i) {
                staged[i] = {};
                const fact& f = facts[offset + emitted + i];
                build_attestation(subj, f, staged[i]);
                std::memcpy(masks + i * 32, f.qualifiers.data(), 32);
                any = any || f.has_qualifiers;
            }
            // A mask batch writes every row's mask; rows without qualifiers carry zeros,
            // so only batches with a qualified claim pass masks at all.
            check(laplace_attestation_staged_batch_add(stage, staged, n, any ? masks : nullptr), "testimony admission");
            emitted += n;
        }
        return emitted;
    }
    size_t attest_range_facts(intent_stage_t* stage, uint32_t first, uint32_t last) {
        constexpr size_t chunk = 256;
        laplace_attestation_staged_t staged[chunk];
        uint8_t masks[chunk * 32];
        bool any = false;
        size_t staged_n = 0;
        size_t emitted = 0;
        for (uint32_t cp = first;; ++cp) {
            const hash128_t subj = point_id(cp);
            for (const fact& f : facts) {
                staged[staged_n] = {};
                std::memcpy(masks + staged_n * 32, f.qualifiers.data(), 32);
                any = any || f.has_qualifiers;
                build_attestation(subj, f, staged[staged_n++]);
                if (staged_n == chunk) {
                    check(laplace_attestation_staged_batch_add(
                        stage, staged, staged_n, any ? masks : nullptr), "testimony admission");
                    emitted += staged_n;
                    staged_n = 0; any = false;
                }
            }
            if (cp == last) break;
        }
        if (staged_n) {
            check(laplace_attestation_staged_batch_add(
                stage, staged, staged_n, any ? masks : nullptr), "testimony admission");
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
        (void)laplace_entity_type_id("Range", &range_type);
        laplace_ordered_composition_request_t request{
            components, 2, range_type, witness, INTENT_STAGE_PG_EPOCH_UNIX_US
        };
        laplace_ordered_composition_result_t result{};
        check(laplace_ordered_composition_compose_batch(&request, 1, &result),
            "range composition");

        if (!intent_stage_witness_seen(stage, &result.id)) {
            check(intent_stage_add_entity(
                stage, &result.id, result.tier, &range_type), "range entity");

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

    static std::string governed(const field_rule& rule, const std::string& value) {
        return governed_value(rule.vocabulary, value);
    }
    // Source attributes normalized once at parse (a lexicon's "language").
    std::unordered_map<std::string, field_rule::vocabulary_rule> attribute_vocabularies;
    std::string attribute_value(const std::string& key, std::string value) const {
        const auto v = attribute_vocabularies.find(key);
        return v == attribute_vocabularies.end() ? value : governed_value(v->second, value);
    }
    // "a|b": the first declared context the element carries, else its record carries
    // (a WN-LMF sense inherits its lexicon's language). None leaves the claim unqualified.
    const std::map<std::string, std::string>* scope_attributes = nullptr;
    const std::string* context_of(const field_rule& rule, const std::map<std::string, std::string>& attributes) const {
        size_t start = 0;
        for (;;) {
            const size_t bar = rule.context_field.find('|', start);
            const std::string name = rule.context_field.substr(start, bar == std::string::npos ? bar : bar - start);
            auto hit = attributes.find(name);
            if (hit != attributes.end() && !hit->second.empty()) return &hit->second;
            if (scope_attributes) {
                hit = scope_attributes->find(name);
                if (hit != scope_attributes->end() && !hit->second.empty()) return &hit->second;
            }
            if (bar == std::string::npos) return nullptr;
            start = bar + 1;
        }
    }
    std::unordered_map<std::string, size_t> last_fact;
    // A dynamic qualifier reads its value from a sibling field of the same record; that
    // field's value aliases name the governed qualifier (ISO 639-3 Ret_Reason "M" -> merge).
    void qualify(const std::string& path, const field_rule& rule,
        const std::map<std::string, std::string>& attributes, size_t from) {
        std::array<uint8_t, 32> mask = rule.qualifiers;
        bool any = rule.has_qualifiers;
        if (!rule.qualifier_family.empty()) {
            const std::string* value = nullptr;
            auto hit = attributes.find(rule.qualifier_field);
            if (hit != attributes.end()) value = &hit->second;
            else if (scope_attributes && (hit = scope_attributes->find(rule.qualifier_field)) != scope_attributes->end())
                value = &hit->second;
            std::string named;
            if (value && !value->empty()) {
                named = *value;
                const auto sibling = fields.find(path.substr(0, path.rfind('@') + 1) + rule.qualifier_field);
                if (sibling != fields.end()) {
                    const auto alias = sibling->second.aliases.find(alias_key(named));
                    if (alias != sibling->second.aliases.end()) named = alias->second;
                }
                value = &named;
                const int bit = laplace_qualifier_bit(rule.qualifier_family.c_str(), value->c_str());
                if (bit < 0) throw std::runtime_error("undeclared " + rule.qualifier_family + " qualifier " + *value);
                mask[static_cast<size_t>(bit) / 8] |= static_cast<uint8_t>(1u << (bit % 8));
                any = true;
            }
        }
        if (!any) return;
        for (size_t i = from; i < facts.size(); ++i) {
            for (size_t b = 0; b < 32; ++b) facts[i].qualifiers[b] |= mask[b];
            facts[i].has_qualifiers = true;
        }
    }
    // Aggregated claims: identical (subject, relation, object, context, outcome) facts
    // across the artifact become one graded game series staged at the end.
    std::unordered_map<std::string, fact> aggregates;
    void aggregate_facts(size_t from) {
        for (size_t i = from; i < facts.size(); ++i) {
            fact f = facts[i];
            if (!f.has_subject) { f.subject = subject; f.has_subject = true; }
            if (!f.has_source) { f.source = current_witness; f.has_source = true; }
            std::string key(reinterpret_cast<const char*>(&f.subject), sizeof(f.subject));
            key.append(reinterpret_cast<const char*>(&f.source), sizeof(f.source));
            key.append(reinterpret_cast<const char*>(&f.relation), sizeof(f.relation));
            key.append(reinterpret_cast<const char*>(&f.object), sizeof(f.object));
            key.append(reinterpret_cast<const char*>(&f.context), sizeof(f.context));
            key.push_back(static_cast<char>((f.has_object ? 1 : 0) | (f.has_context ? 2 : 0) | (f.confirm ? 4 : 0)));
            auto [it, fresh] = aggregates.emplace(std::move(key), f);
            if (!fresh) {
                it->second.games += f.games;
                for (size_t b = 0; b < 32; ++b) it->second.qualifiers[b] |= f.qualifiers[b];
                it->second.has_qualifiers = it->second.has_qualifiers || f.has_qualifiers;
            }
        }
        facts.resize(from);
    }
    // A record's own witness: the content composition of its self-description.
    hash128_t stage_witness(intent_stage_t* stage, const std::vector<std::string>& parts) {
        std::vector<laplace_ordered_component_t> components;
        for (const auto& part : parts) {
            const content_form form = compose_content(stage, part);
            laplace_ordered_component_t c{};
            c.id = form.id;
            std::memcpy(c.coord, form.coord, sizeof(c.coord));
            c.tier = form.tier; c.atom = form.atom; c.has_atom = form.tier == 0 ? 1 : 0;
            components.push_back(c);
        }
        if (components.size() == 1) return components[0].id;
        hash128_t version_type;
        (void)laplace_entity_type_id("Source_Version", &version_type);
        laplace_ordered_composition_request_t request{
            components.data(), components.size(), version_type, witness, INTENT_STAGE_PG_EPOCH_UNIX_US};
        laplace_ordered_composition_result_t result{};
        check(laplace_ordered_composition_stage_batch(stage, &request, 1, &result), "witness composition");
        return result.id;
    }
    // A sentence's parse: its token forms in order, each vertex carrying governed
    // codes (UPOS index, universal deprel code and subtype, head ordinal) in its metadata.
    void lower_parse(intent_stage_t* stage, const route_rule& route, const node& record) {
        const auto& p = route.parse;
        const auto trunk = record.attributes.find(p.trunk);
        if (trunk == record.attributes.end() || trunk->second.empty() || !record.group) return;
        const content_form sentence = compose_content(stage, trunk->second);
        const size_t width = std::max({p.id, p.form, p.upos, p.head, p.deprel}) + 1;
        std::vector<hash128_t> ids;
        std::vector<uint64_t> flags;
        for (const auto& line : *record.group) {
            if (line.size() < width) continue;
            const std::string& id = line[p.id];
            if (id.empty() || id.find_first_not_of("0123456789") != std::string::npos) continue;
            const std::string& form = line[p.form];
            if (form.empty() || form == "_") continue;
            const content_form token = compose_content(stage, form);
            const char* canonical = nullptr;
            int index = -1;
            const uint8_t upos1 = laplace_pos_resolve_canonical(line[p.upos].c_str(),
                static_cast<laplace_pos_tagset_t>(p.upos_tagset), &canonical, &index) == 0 && index >= 0
                ? static_cast<uint8_t>(index + 1) : 0;
            const uint8_t deprel = static_cast<uint8_t>(laplace_deprel_code(line[p.deprel].c_str()));
            const int subtype_code = laplace_deprel_subtype_code(line[p.deprel].c_str());
            const uint16_t subtype = subtype_code > 0 ? static_cast<uint16_t>(subtype_code) : 0;
            uint16_t head = 0xFFFF;
            const std::string& h = line[p.head];
            if (!h.empty() && h.size() <= 5 && h.find_first_not_of("0123456789") == std::string::npos) {
                const unsigned long v = std::stoul(h);
                if (v < 0xFFFF) head = static_cast<uint16_t>(v);
            }
            ids.push_back(token.id);
            flags.push_back(laplace_parse_vertex_flags(token.tier, upos1, deprel, head, subtype));
        }
        if (ids.empty()) return;
        std::vector<double> trajectory(ids.size() * 4);
        check(trajectory_build_flagged(ids.data(), flags.data(), ids.size(), trajectory.data()), "parse trajectory");
        hash128_t physicality;
        laplace_physicality_id_compute(sentence.id, 8, &physicality);
        check(intent_stage_add_physicality(stage, &physicality, &sentence.id, 8, sentence.coord,
            &sentence.hilbert, trajectory.data(), static_cast<uint32_t>(ids.size()),
            static_cast<int32_t>(ids.size()), 1, 0.0, 1, 0, INTENT_STAGE_PG_EPOCH_UNIX_US), "parse physicality");
    }
    void field(intent_stage_t* stage, const std::string& path, const std::string& raw,
        bool subject_binding, const std::map<std::string, std::string>& attributes) {
        const size_t before = facts.size();
        try {
            lower_field(stage, path, raw, subject_binding, attributes);
            const auto rule = fields.find(path);
            if (rule != fields.end() && facts.size() > before) qualify(path, rule->second, attributes, before);
            if (rule != fields.end() && rule->second.aggregate) aggregate_facts(before);
            else if (facts.size() > before) last_fact[path] = facts.size() - 1;
        }
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
        if (!rule.observation_of.empty() || !rule.score_of.empty()) {
            if (raw.empty()) return;
            const std::string& of = rule.observation_of.empty() ? rule.score_of : rule.observation_of;
            const auto target = last_fact.find(of);
            if (target == last_fact.end()) throw std::runtime_error("no " + of + " claim precedes this " + (rule.observation_of.empty() ? "score" : "observation count"));
            fact& claim = facts[target->second];
            char* end = nullptr;
            if (!rule.observation_of.empty()) {
                const long long n = std::strtoll(raw.c_str(), &end, 10);
                if (end == raw.c_str() || *end || n < 1) throw std::runtime_error("invalid observation count: " + raw);
                claim.games = n;
            } else {
                const double v = std::strtod(raw.c_str(), &end);
                if (end == raw.c_str() || *end || !(v >= 0 && v <= 1)) throw std::runtime_error("invalid score: " + raw);
                claim.score = v;
            }
            return;
        }
        const std::string* context_value = nullptr;
        if (!rule.context_field.empty()) context_value = context_of(rule, attributes);
        if (raw.empty() || (!rule.absent.empty() && raw == rule.absent)) return;
        if (rule.group_once) {
            const auto first = attributes.find("group:first");
            if (first != attributes.end() && first->second != "1") return;
        }
        if (rule.subject_mode != 0 || rule.pair_mode != 0 || !rule.relation_field.empty()) {
            lower_grouped(stage, rule, raw, context_value, attributes);
            return;
        }
        const bool testimony = (rule.disposition & (1u << 6)) != 0;
        const bool ordinary_content = (rule.disposition & (1u << 1)) != 0;
        const bool default_value = rule.has_default && raw == rule.default_value;
        const bool emitted_testimony = testimony && !(default_value && rule.omit_default_testimony);
        if (!emitted_testimony && !ordinary_content) {
            if (testimony && default_value && rule.omit_default_testimony) return;
            if ((rule.disposition & (1u << 9)) || ((rule.disposition & 1u) && subject_binding)) return;
            throw std::runtime_error("field disposition has no executable lowering");
        }
        auto emit_fact = [&](const fact& value) { if (emitted_testimony) facts.push_back(value); };
        // Relation family/parentage is governed by the native relation manifest,
        // not reified as content entities or source testimony here.
        fact f; f.relation = rule.relation; f.rank = rule.rank; f.explicit_rank = true;
        if (context_value && !context_value->empty()) {
            f.context = content(stage, *context_value);
            f.has_context = true;
        } else if (!rule.context_literal.empty()) {
            f.context = content(stage, rule.context_literal);
            f.has_context = true;
        }
        if (rule.kind == 1) {
            if (raw == "Y" || raw == "Yes" || raw == "true" || raw == "True" || raw == "1") f.confirm = true;
            else if (raw == "N" || raw == "No" || raw == "false" || raw == "False" || raw == "0") f.confirm = false;
            else throw std::runtime_error("invalid boolean: " + raw);
            if (ordinary_content) content(stage, raw);
            if (!rule.object_literal.empty()) { f.object = content(stage, rule.object_literal); f.has_object = true; }
            emit_fact(f); return;
        }
        if (emitted_testimony && nonzero(rule.lexical_relation)) {
            fact lexical = f; lexical.relation = rule.lexical_relation; lexical.explicit_rank = false;
            lexical.object = content(stage, raw); lexical.has_object = true; emit_fact(lexical);
        }
        f.has_object = true;
        if (rule.codec == 4) {
            const auto values = rule.separator.empty() ? std::vector<std::string>{raw} : split(raw, rule.separator);
            for (const auto& value : values) {
                f.object = content(stage, identify(rule.identity_table, value));
                emit_fact(f);
            }
            return;
        }
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
        }
        else if (rule.codec == 2 || (rule.codec == 0 && rule.kind == 7)) {
            f.object = content(stage, sequence_text(raw, rule.separator));
        }
        else if (rule.kind == 4 || rule.kind == 5) {
            const auto values = rule.separator.empty() ? std::vector<std::string>{raw} : split(raw, rule.separator);
            for (const auto& value : values) {
                f.object = content(stage, value);
                emit_fact(f);
            }
            return;
        }
        else if (rule.kind == 8) {
            const std::string value = governed(rule, identify(rule.identity_table, raw));
            f.object = rule.object_record_scoped ? scoped_to_record(stage, rule, value) : content(stage, value);
        }
        else if (rule.kind == 9) {
            // Structured text carries its own markup (FrameNet's <def-root>, <fen>,
            // <ex>): it is XML read by the same XML reader, and its knowledge text is
            // the markup's character data, never the tags.
            f.object = content(stage, markup_text(raw));
        }
        else {
            auto values = rule.kind == 3 || (rule.kind == 0 && !rule.separator.empty())
                ? split(raw, rule.separator) : std::vector<std::string>{raw};
            for (auto value : values) {
                value = identify(rule.identity_table, value);
                value = governed(rule, value);
                auto alias = rule.aliases.find(alias_key(value)); if (alias != rule.aliases.end()) value = alias->second;
                f.object = classifier(stage, value); emit_fact(f);
            }
            return;
        }
        emit_fact(f);
    }
    hash128_t resolve_relation(const field_rule& rule, const std::string& name, double* rank, bool* flip) {
        hash128_t id{}, parent{}; laplace_rel_symmetry_t symmetry{}; uint8_t flipped = 0;
        double resolved_rank = 1.0;
        int rc = -1;
        switch (rule.relation_resolver) {
        case 1: rc = laplace_relation_resolve_deprel(name.c_str(), &id, &resolved_rank, &symmetry, &flipped, &parent); break;
        case 2: rc = laplace_relation_resolve_enhanced_deprel(name.c_str(), &id, &resolved_rank, &symmetry, &flipped, &parent); break;
        case 3: rc = laplace_relation_resolve_feature(name.c_str(), &id, &resolved_rank, &symmetry, &flipped, &parent); break;
        case 4: rc = laplace_relation_resolve_surface(name.c_str(), &id, &resolved_rank, &symmetry, &flipped, &parent); break;
        default: throw std::runtime_error("relation resolver is not declared");
        }
        if (rc < 0 || !nonzero(id)) throw std::runtime_error("relation vocabulary has no entry for " + name);
        *rank = resolved_rank; *flip = flipped != 0;
        return id;
    }
    hash128_t endpoint(intent_stage_t* stage, const field_rule& rule, const std::string& value,
                       const std::map<std::string, std::string>& attributes) {
        if (value == recipe_group_trunk_value) return trunk(stage, rule, attributes);
        return content(stage, identify(rule.identity_table, value));
    }
    hash128_t trunk(intent_stage_t* stage, const field_rule& rule,
                    const std::map<std::string, std::string>& attributes) {
        const auto text = attributes.find(rule.trunk_field);
        if (rule.trunk_field.empty() || text == attributes.end() || text->second.empty())
            throw std::runtime_error("group trunk field is absent");
        return content(stage, text->second);
    }
    void grouped_fact(const field_rule& rule, hash128_t subj, hash128_t relation, hash128_t object,
                      double rank, bool flip, const std::string* context_value, intent_stage_t* stage) {
        fact f;
        f.relation = relation; f.rank = rank; f.explicit_rank = true;
        f.subject = flip ? object : subj; f.object = flip ? subj : object;
        f.has_subject = true; f.has_object = true;
        if (context_value && !context_value->empty()) {
            f.context = content(stage, *context_value); f.has_context = true;
        } else if (!rule.context_literal.empty()) {
            f.context = content(stage, rule.context_literal); f.has_context = true;
        }
        facts.push_back(f);
    }
    void lower_grouped(intent_stage_t* stage, const field_rule& rule, const std::string& raw,
                       const std::string* context_value,
                       const std::map<std::string, std::string>& attributes) {
        if ((rule.disposition & (1u << 6)) == 0)
            throw std::runtime_error("grouped lowering requires a testimony disposition");
        auto sibling = [&](const std::string& name) -> std::string {
            const auto v = attributes.find(name);
            return v == attributes.end() ? std::string{} : v->second;
        };
        if (rule.pair_mode != 0) {
            // Provider-resolved reference pairs use in-memory separators; source
            // pairs use the separators the recipe declares.
            const std::string item_separator = rule.pair_mode == 2
                ? std::string(1, recipe_pair_item_separator) : rule.separator;
            const std::string value_separator = rule.pair_mode == 2
                ? std::string(1, recipe_pair_value_separator) : rule.pair_value_separator;
            if (item_separator.empty() || value_separator.empty())
                throw std::runtime_error("pair field declares no item/value separators");
            if (raw.empty()) return;
            size_t start = 0;
            for (;;) {
                const size_t end = raw.find(item_separator, start);
                const std::string item = raw.substr(start, end == std::string::npos ? end : end - start);
                const size_t split_at = item.find(value_separator);
                const size_t skip = value_separator.size();
                if (split_at == std::string::npos || split_at == 0 || split_at + skip >= item.size())
                    throw std::runtime_error("malformed pair item: " + item);
                const std::string left = item.substr(0, split_at), right = item.substr(split_at + skip);
                double rank = rule.rank; bool flip = false;
                if (rule.pair_mode == 3) {
                    grouped_fact(rule, subject, rule.relation, compose_pair(stage, rule, left, right),
                                 rank, flip, context_value, stage);
                } else if (rule.pair_mode == 1) {
                    const hash128_t relation = resolve_relation(rule, left, &rank, &flip);
                    grouped_fact(rule, subject, relation, content(stage, right), rank, flip, context_value, stage);
                } else {
                    const hash128_t relation = resolve_relation(rule, right, &rank, &flip);
                    grouped_fact(rule, endpoint(stage, rule, left, attributes), relation, subject,
                                 rank, flip, context_value, stage);
                }
                if (end == std::string::npos) break;
                start = end + item_separator.size();
            }
            return;
        }
        if (rule.omit_equal_subject && raw == current_identity) return;
        double rank = rule.rank; bool flip = false;
        hash128_t relation = rule.relation;
        if (!rule.relation_field.empty()) {
            // "field>refinement": a present refinement attribute names the relation more
            // exactly than the field (WN-LMF relType="other" carries it in dc:type).
            const size_t refine = rule.relation_field.find('>');
            std::string name = refine == std::string::npos ? std::string{}
                : sibling(rule.relation_field.substr(refine + 1));
            if (name.empty()) name = sibling(rule.relation_field.substr(0, refine));
            if (name.empty() || name == "_") return;
            relation = resolve_relation(rule, name, &rank, &flip);
        }
        if (!nonzero(relation)) throw std::runtime_error("grouped testimony has no relation");
        const auto values = rule.separator.empty() ? std::vector<std::string>{raw} : split(raw, rule.separator);
        for (const auto& value : values) {
            switch (rule.subject_mode) {
            case 0: grouped_fact(rule, subject, relation, endpoint(stage, rule, value, attributes), rank, flip, context_value, stage); break;
            case 1: grouped_fact(rule, endpoint(stage, rule, value, attributes), relation, subject, rank, flip, context_value, stage); break;
            case 2: grouped_fact(rule, trunk(stage, rule, attributes), relation, endpoint(stage, rule, value, attributes), rank, flip, context_value, stage); break;
            default: throw std::runtime_error("unknown subject mode");
            }
        }
    }
    void prepare(intent_stage_t* stage) {
        const node& record = pending.front(); auto route_it = routes.find(record.name);
        if (route_it == routes.end()) throw std::runtime_error("recipe has no record route for " + record.name);
        const auto& route = route_it->second;
        const std::string identity = record.get(route.identity);
        current_identity = identity;
        record_context = record.name + " [" + (identity.empty()
            ? route.first + "=" + record.get(route.first) + ", " + route.last + "=" + record.get(route.last)
            : route.identity + "=" + identity.substr(0, 160) + (identity.size() > 160 ? "..." : "")) + "]";
        // A curated source record is packaging: its claims and declared content fields
        // are lowered below; the record's own syntax (element name, attribute pairs,
        // delimited cells) is never recorded as content.
        if (record.ns != route.ns) throw std::runtime_error("record namespace mismatch: " + record.name);
        current_witness = record.witness_parts.empty() ? witness : stage_witness(stage, record.witness_parts);
        range = route.kind == 0;
        membership = false; record_facts_done = false; fact_offset = 0; subject_form_known = false;
        record_parts.clear(); record_seen.clear(); observing = true;
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
            if (route.subject_codec == 0) {
                subject_form = compose_content(stage, identify(route.identity_table, value));
                subject = subject_form.id;
                subject_form_known = true;
                record_subject_form = subject_form;
            }
            else if (route.subject_codec == 2)
                subject = content(stage, sequence_text(value, route.separator));
            else if (route.subject_codec == 1) {
                const uint32_t cp = point(value);
                subject = point_id(cp);
                check(content_witness_emit_floor_atom(stage, cp, &subject,
                    INTENT_STAGE_PG_EPOCH_UNIX_US), "subject floor physicality");
            } else throw std::runtime_error("unsupported subject reference codec");
        } else if (route.kind == 4) {
            // The record's subject is the composition of its declared parts.
            scope_attributes = &record.attributes;
            std::vector<laplace_ordered_component_t> parts;
            compose_parts(stage, route, &record, route.prefix, route.subject_parts, parts);
            if (parts.size() != route.subject_parts.size())
                throw std::runtime_error("composition subject is missing a declared part in " + record.name);
            laplace_ordered_composition_request_t request{};
            request.components = parts.data(); request.component_count = parts.size();
            request.type_id = route.entity_type; request.source_id = current_witness;
            request.observed_at_unix_us = INTENT_STAGE_PG_EPOCH_UNIX_US;
            laplace_ordered_composition_result_t result{};
            check(laplace_ordered_composition_stage_batch(stage, &request, 1, &result), "composition subject");
            subject = result.id;
            subject_form.id = result.id; std::memcpy(subject_form.coord, result.coord, sizeof(subject_form.coord));
            subject_form.tier = result.tier; subject_form.atom = 0;
            subject_form_known = true;
            record_subject_form = subject_form;
        } else if (route.kind == 2) {
            auto value = identify(route.identity_table, record.get(route.identity));
            const auto alias = route.aliases.find(alias_key(value));
            if (alias != route.aliases.end()) value = alias->second;
            subject = classifier(stage, value);
        } else throw std::runtime_error("unknown subject instruction");
        if (!route.range_first.empty()) {
            cursor = point(record.get(route.range_first));
            end = point(record.get(route.range_last));
            if (cursor > end) throw std::runtime_error("inverted subject membership range");
            membership = true; membership_relation = route.range_relation;
        }
        facts.clear();
        last_fact.clear();
        scope_attributes = &record.attributes;
        // A score or observation count grades a sibling claim, so the record's claims
        // lower first whatever order the source's attributes sort in.
        for (const bool grading : {false, true})
        for (const auto& a : record.attributes) {
            // Group comment lines vary by corpus (sent_id, newdoc, translit, genre...).
            // Only those the recipe declares are lowered; the rest are packaging.
            const std::string path = route.prefix + "/@" + a.first;
            const auto rule = fields.find(path);
            if (a.first.rfind("group:", 0) == 0 && rule == fields.end())
                continue;
            if ((rule != fields.end() && (!rule->second.observation_of.empty() || !rule->second.score_of.empty())) != grading)
                continue;
            const bool bound = a.first == route.identity || a.first == route.first || a.first == route.last ||
                a.first == route.range_first || a.first == route.range_last;
            field(stage, path, a.second, bound, record.attributes);
        }
        if (recipe_has_text(record.text)) field(stage, route.prefix, record.text, false, record.attributes);
        for (const auto& child : record.children) lower_child(stage, route, child, child.name);
        if (route.parse.on) lower_parse(stage, route, record);
        scope_attributes = nullptr;
        observing = false;
        if (record_parts.size() == 1) file_parts.push_back(record_parts.front());
        else if (record_parts.size() > 1) file_parts.push_back(compose_trunk(stage, record_parts, "SourceRecord"));
        active = true;
    }
    // Components for identity parts, in declaration order. A part with no value is
    // absent from the composition (a valence unit with no grammatical function).
    void compose_parts(intent_stage_t* stage, const route_rule& route, const node* element,
        const std::string& path, const std::vector<route_rule::identity_part>& declared,
        std::vector<laplace_ordered_component_t>& out) {
        auto component = [](const hash128_t& id, const double* coord, uint8_t tier, uint32_t atom) {
            laplace_ordered_component_t c{};
            c.id = id; std::memcpy(c.coord, coord, sizeof(c.coord));
            c.tier = tier; c.atom = atom; c.has_atom = tier == 0;
            return c;
        };
        for (const auto& part : declared) {
            if (!part.children.empty()) {
                if (!element) throw std::runtime_error("a record subject cannot compose child elements");
                const std::string child_path = path + "/" + part.children;
                const auto rule = route.elements.find(child_path);
                if (rule == route.elements.end())
                    throw std::runtime_error("no element composition for " + child_path);
                for (const auto& child : element->children)
                    if (child.name == part.children) {
                        const auto composed = compose_element(stage, route, child, child_path, rule->second);
                        if (composed.first) out.push_back(composed.second);
                    }
                continue;
            }
            std::vector<std::string> values;
            if (element) collect(*element, part.path, values);
            else if (scope_attributes) {
                const std::string key = part.path.rfind("@", 0) == 0 ? part.path.substr(1) : part.path;
                const auto hit = scope_attributes->find(key);
                if (hit != scope_attributes->end()) values.push_back(hit->second);
            }
            std::string text;
            for (size_t k = 0; k < values.size(); ++k) {
                if (values[k].empty()) continue;
                if (!text.empty()) text += part.join.empty() ? " " : part.join;
                text += values[k];
            }
            if (text.empty()) continue;
            const content_form value = compose_content(stage, governed_value(part.vocabulary, text));
            if (part.scope.empty()) {
                out.push_back(component(value.id, value.coord, value.tier, value.atom));
                continue;
            }
            const std::string key = part.scope.rfind("@", 0) == 0 ? part.scope.substr(1) : part.scope;
            const auto scope_value = scope_attributes ? scope_attributes->find(key) : decltype(scope_attributes->end()){};
            if (!scope_attributes || scope_value == scope_attributes->end() || scope_value->second.empty())
                throw std::runtime_error("identity part scope is absent: " + part.scope);
            const content_form scope = compose_content(stage, scope_value->second);
            laplace_ordered_component_t pair[2] = {
                component(scope.id, scope.coord, scope.tier, scope.atom),
                component(value.id, value.coord, value.tier, value.atom)};
            laplace_ordered_composition_request_t request{};
            request.components = pair; request.component_count = 2;
            request.type_id = part.scope_type; request.source_id = current_witness;
            request.observed_at_unix_us = INTENT_STAGE_PG_EPOCH_UNIX_US;
            laplace_ordered_composition_result_t result{};
            check(laplace_ordered_composition_stage_batch(stage, &request, 1, &result), "scoped identity part");
            out.push_back(component(result.id, result.coord, result.tier, 0));
        }
    }
    std::pair<bool, laplace_ordered_component_t> compose_element(intent_stage_t* stage, const route_rule& route,
        const node& element, const std::string& path, const route_rule::element_rule& rule) {
        std::vector<laplace_ordered_component_t> parts;
        compose_parts(stage, route, &element, path, rule.parts, parts);
        if (parts.empty()) return {false, {}};
        laplace_ordered_composition_request_t request{};
        request.components = parts.data(); request.component_count = parts.size();
        request.type_id = rule.entity_type; request.source_id = current_witness;
        request.observed_at_unix_us = INTENT_STAGE_PG_EPOCH_UNIX_US;
        laplace_ordered_composition_result_t result{};
        check(laplace_ordered_composition_stage_batch(stage, &request, 1, &result), "element composition");
        laplace_ordered_component_t out{};
        out.id = result.id; std::memcpy(out.coord, result.coord, sizeof(out.coord)); out.tier = result.tier;
        return {true, out};
    }
    // The child is its own subject: [record subject, its identity content], composed
    // by the native ordered-composition kernel. Its fields and descendants are claims
    // about it; the parent links to it through the declared relation.
    void lower_child_subject(intent_stage_t* stage, const route_rule& route, const node& child,
        const std::string& path, const std::string& prefix, const route_rule::child_subject_rule& rule,
        const std::map<std::string, std::string>* ancestors) {
        if (!subject_form_known)
            throw std::runtime_error("child subjects compose over a content subject: " + path);
        std::vector<laplace_ordered_component_t> parts;
        auto add_part = [&](const content_form& c) {
            laplace_ordered_component_t part{};
            part.id = c.id; std::memcpy(part.coord, c.coord, sizeof(part.coord));
            part.tier = c.tier; part.atom = c.atom; part.has_atom = c.tier == 0;
            parts.push_back(part);
        };
        add_part(record_subject_form);
        if (rule.parts.empty()) {
            const std::string key = child.get(rule.identity);
            if (key.empty()) throw std::runtime_error("child subject has no identity value: " + path);
            add_part(compose_content(stage, key));
        } else {
            compose_parts(stage, route, &child, path, rule.parts, parts);
        }
        laplace_ordered_composition_request_t request{};
        request.components = parts.data(); request.component_count = parts.size();
        request.type_id = rule.entity_type; request.source_id = current_witness;
        request.observed_at_unix_us = INTENT_STAGE_PG_EPOCH_UNIX_US;
        laplace_ordered_composition_result_t result{};
        check(laplace_ordered_composition_stage_batch(stage, &request, 1, &result), "child subject composition");
        const hash128_t child_id = result.id;
        if (nonzero(rule.relation)) {
            fact f;
            f.relation = rule.relation; f.has_object = true;
            if (rule.child_is_subject) { f.subject = child_id; f.has_subject = true; f.object = subject; }
            else f.object = child_id;
            facts.push_back(f);
        }
        // The child is the current subject while it lowers, so subject modes and
        // record-scoped objects read relative to it; the record's subject returns after.
        const hash128_t outer_subject = subject;
        const content_form outer_form = subject_form;
        subject = child_id;
        subject_form.id = child_id; std::memcpy(subject_form.coord, result.coord, sizeof(subject_form.coord));
        subject_form.tier = result.tier; subject_form.atom = 0;
        const size_t first = facts.size();
        const auto seen = with_ancestors(child, ancestors);
        for (const auto& a : child.attributes) {
            if (a.first == rule.identity) continue;
            field(stage, prefix + "/@" + a.first, a.second, false, seen);
        }
        if (recipe_has_text(child.text)) field(stage, prefix, child.text, false, seen);
        for (const auto& grandchild : child.children)
            lower_child(stage, route, grandchild, path + "/" + grandchild.name, &seen);
        for (size_t k = first; k < facts.size(); ++k)
            if (!facts[k].has_subject) { facts[k].subject = child_id; facts[k].has_subject = true; }
        subject = outer_subject;
        subject_form = outer_form;
    }
    // Nested elements lower against the record's subject under the prefix the route
    // declares for their path ("Sense", "Sense/SenseRelation").
    // A nested element sees its ancestors' attributes (its own win): a FrameNet
    // relatedFrame's relation type is its enclosing frameRelation's @type.
    static std::map<std::string, std::string> with_ancestors(
        const node& child, const std::map<std::string, std::string>* ancestors) {
        std::map<std::string, std::string> merged = child.attributes;
        if (ancestors) for (const auto& a : *ancestors) merged.try_emplace(a.first, a.second);
        return merged;
    }
    void lower_child(intent_stage_t* stage, const route_rule& route, const node& child, const std::string& path,
        const std::map<std::string, std::string>* ancestors = nullptr) {
        auto prefix = route.children.find(path);
        if (prefix == route.children.end()) throw std::runtime_error("recipe has no child disposition: " + path);
        if (child.ns != route.ns) throw std::runtime_error("unaccounted nested structure: " + path);
        // An empty prefix is a declared excluded subtree (packaging, or knowledge
        // another artifact states), never an undeclared structure.
        if (prefix->second.empty() && !route.elements.count(path)) return;
        const auto composed = route.elements.find(path);
        if (composed != route.elements.end()) {
            const auto element = compose_element(stage, route, child, path, composed->second);
            if (element.first && nonzero(composed->second.relation)) {
                fact f;
                f.relation = composed->second.relation; f.object = element.second.id; f.has_object = true;
                if (!composed->second.observation.empty()) {
                    const std::string games = child.get(composed->second.observation);
                    if (!games.empty() && games.find_first_not_of("0123456789") == std::string::npos)
                        f.games = std::max<int64_t>(1, std::stoll(games));
                }
                facts.push_back(f);
            }
            return;
        }
        const auto own = route.child_subjects.find(path);
        if (own != route.child_subjects.end()) {
            lower_child_subject(stage, route, child, path, prefix->second, own->second, ancestors);
            return;
        }
        const auto seen = with_ancestors(child, ancestors);
        for (const auto& a : child.attributes)
            field(stage, prefix->second + "/@" + a.first, a.second, false, seen);
        if (recipe_has_text(child.text)) field(stage, prefix->second, child.text, false, seen);
        for (const auto& grandchild : child.children)
            lower_child(stage, route, grandchild, path + "/" + grandchild.name, &seen);
    }
};

extern "C" int laplace_recipe_stream_new(const uint8_t* program, size_t n,
    const hash128_t* witness, double trust, laplace_recipe_stream_t** out) {
    if (!program || !witness || !out || !std::isfinite(trust) || trust < 0 || trust > 1) return -1;
    *out = nullptr;
    std::unique_ptr<laplace_recipe_stream> s;
    try {
        s = std::make_unique<laplace_recipe_stream>(); s->witness = *witness; s->current_witness = *witness; s->trust = trust;
        image_reader r{program,n};
        const uint32_t version = r.number();
        // "RCPn": the generation digit is the high byte; each generation extends the last.
        const int generation = int(version >> 24) - '0';
        if ((version & 0x00ffffffu) != 0x00504352u || generation < 1 || generation > 8)
            throw std::runtime_error("unsupported recipe instruction version");
        const bool rcp6 = generation >= 6, rcp7 = generation >= 7, rcp8 = generation >= 8;
        const bool rcp2_or_later = generation >= 2;
        s->depth = int(r.number());
        if (s->depth < 0 || s->depth > 128) throw std::runtime_error("invalid record depth");
        uint32_t provider_kind = 0;
        if (rcp2_or_later) {
            provider_kind = r.number();
            if (provider_kind > 2 || (provider_kind == 2 && !rcp7))
                throw std::runtime_error("unsupported recipe syntax provider");
            if (provider_kind == 2) {
                recipe_turtle_config config;
                config.record_name = r.text(); config.namespace_uri = r.text();
                const uint32_t excluded = r.number();
                for (uint32_t k = 0; k < excluded; ++k) config.exclude_types.push_back(r.text());
                s->make_provider = [config] { return std::make_unique<recipe_turtle_provider>(config); };
            }
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
                if (rcp6) {
                    const uint32_t grouped = r.number();
                    if (grouped > 1) throw std::runtime_error("invalid record-group instruction");
                    config.group_blank_lines = grouped != 0;
                    config.group_attribute_separator = r.text();
                    config.skip_key_column = r.text(); config.skip_key_characters = r.text();
                    const uint32_t references = r.number();
                    for (uint32_t k = 0; k < references; ++k) {
                        recipe_delimited_reference ref;
                        ref.column = r.text(); ref.key_column = r.text(); ref.target_column = r.text();
                        ref.root_value = r.text(); ref.pair_separator = r.text(); ref.pair_value_separator = r.text();
                        if (ref.column.empty() || ref.key_column.empty() || ref.target_column.empty()
                            || ref.pair_separator.empty() != ref.pair_value_separator.empty())
                            throw std::runtime_error("invalid in-group reference instruction");
                        config.references.push_back(std::move(ref));
                    }
                    const uint32_t constants = r.number();
                    for (uint32_t k = 0; k < constants; ++k) {
                        auto key = r.text(); auto value = r.text();
                        if (!config.constants.emplace(std::move(key), std::move(value)).second)
                            throw std::runtime_error("duplicate record constant");
                    }
                    if (generation >= 7) config.header_lines = r.number();
                }
                s->delimited = true;
                s->delimited_columns = config.columns;
                s->make_provider = [config] { return std::make_unique<recipe_delimited_provider>(config); };
            }
        }
        uint32_t count = r.number();
        for (uint32_t j = 0; j < count; ++j) {
            field_rule f; f.path = r.text(); f.kind = r.number(); f.disposition = r.number(); f.codec = r.number();
            if (f.kind > 9 || f.codec > 4) throw std::runtime_error("unknown field opcode");
            f.absent = r.text(); f.separator = r.text(); f.object_namespace = r.text();
            f.relation = r.hash(); f.parent = r.hash(); f.entity_type = r.hash(); f.lexical_relation = r.hash(); f.rank = r.real();
            uint32_t aliases = r.number();
            for (uint32_t a = 0; a < aliases; ++a) { auto k = r.text(); auto v = r.text(); if (!f.aliases.emplace(alias_key(k),v).second) throw std::runtime_error("duplicate value alias instruction"); }
            if (generation >= 3) {
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
            if (rcp6) {
                f.subject_mode = r.number(); f.pair_mode = r.number(); f.relation_resolver = r.number();
                f.relation_field = r.text(); f.trunk_field = r.text(); f.pair_value_separator = r.text();
                const uint32_t omit_equal = r.number(), once = r.number();
                if (f.subject_mode > 2 || f.pair_mode > 3 || f.relation_resolver > 4 || omit_equal > 1 || once > 1)
                    throw std::runtime_error("invalid grouped-field instruction at " + f.path);
                f.omit_equal_subject = omit_equal != 0; f.group_once = once != 0;
                if (((f.pair_mode != 0 && f.pair_mode != 3) || !f.relation_field.empty()) && f.relation_resolver == 0)
                    throw std::runtime_error("dynamic relation requires a declared resolver at " + f.path);
            }
            if (rcp7) {
                f.identity_table = r.text(); f.object_literal = r.text(); f.context_literal = r.text();
                f.observation_of = r.text(); f.score_of = r.text();
                const std::string vocabulary = r.text();
                if (!vocabulary.empty()) f.vocabulary = parse_vocabulary(vocabulary, f.path);
                const uint32_t aggregate = r.number();
                if (aggregate > 1) throw std::runtime_error("invalid aggregate instruction at " + f.path);
                f.aggregate = aggregate != 0;
                const uint32_t qualifiers = r.number();
                for (uint32_t k = 0; k < qualifiers; ++k) {
                    const std::string q = r.text();
                    const size_t slash = q.find('/');
                    const int bit = slash == std::string::npos ? -1
                        : laplace_qualifier_bit(q.substr(0, slash).c_str(), q.c_str() + slash + 1);
                    if (bit < 0) throw std::runtime_error("undeclared qualifier " + q + " at " + f.path);
                    f.qualifiers[static_cast<size_t>(bit) / 8] |= static_cast<uint8_t>(1u << (bit % 8));
                    f.has_qualifiers = true;
                }
                f.qualifier_family = r.text(); f.qualifier_field = r.text();
            }
            if (rcp8) {
                const uint32_t scoped = r.number();
                if (scoped > 1) throw std::runtime_error("invalid object-scope instruction at " + f.path);
                f.object_record_scoped = scoped != 0;
            }
            if (!f.context_field.empty() && f.codec == 3)
                throw std::runtime_error("field context conflicts with qualified-reference context at " + f.path);
            std::string key = f.path;
            if (!s->fields.emplace(key,std::move(f)).second) throw std::runtime_error("duplicate field instruction");
        }
        if (generation >= 4) {
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
            if (generation >= 4) {
                const uint32_t structures = r.number();
                route.structures.reserve(structures);
                for (uint32_t k = 0; k < structures; ++k) {
                    std::string path = r.text();
                    if (s->structures.find(path) == s->structures.end())
                        throw std::runtime_error("route references unknown structure " + path);
                    route.structures.push_back(std::move(path));
                }
                if (generation >= 5) {
                    const uint32_t inherit = r.number();
                    if (inherit > 1) throw std::runtime_error("invalid parent-attribute inheritance instruction");
                    route.inherit_parent_attributes = inherit != 0;
                }
            }
            if (rcp7) {
                route.identity_table = r.text();
                const uint32_t parse = r.number();
                if (parse > 1) throw std::runtime_error("invalid parse-structure instruction");
                if (parse) {
                    if (!s->delimited) throw std::runtime_error("parse structure requires grouped delimited syntax");
                    const auto& columns = s->delimited_columns;
                    auto position = [&](const std::string& name) {
                        const auto at = std::find(columns.begin(), columns.end(), name);
                        if (at == columns.end()) throw std::runtime_error("parse structure names undeclared column " + name);
                        return static_cast<size_t>(at - columns.begin());
                    };
                    route.parse.on = true;
                    route.parse.trunk = r.text();
                    route.parse.id = position(r.text()); route.parse.form = position(r.text());
                    route.parse.upos = position(r.text()); route.parse.head = position(r.text());
                    route.parse.deprel = position(r.text());
                    const auto vocab = parse_vocabulary(r.text(), "parse structure");
                    if (vocab.kind != 1) throw std::runtime_error("parse structure UPOS vocabulary must be pos/<tagset>");
                    route.parse.upos_tagset = vocab.tagset;
                }
                const uint32_t witness_fields = r.number();
                for (uint32_t k = 0; k < witness_fields; ++k) route.witness_fields.push_back(r.text());
            }
            if (rcp8) {
                auto read_parts = [&](std::vector<route_rule::identity_part>& out, const std::string& where) {
                    const uint32_t count = r.number();
                    for (uint32_t q = 0; q < count; ++q) {
                        route_rule::identity_part part;
                        part.path = r.text();
                        const std::string vocabulary = r.text();
                        if (!vocabulary.empty()) part.vocabulary = parse_vocabulary(vocabulary, where);
                        part.join = r.text();
                        part.scope = r.text();
                        part.scope_type = r.hash();
                        part.children = r.text();
                        if (part.path.empty() && part.children.empty())
                            throw std::runtime_error("identity part names neither a path nor children at " + where);
                        out.push_back(std::move(part));
                    }
                };
                read_parts(route.subject_parts, route.name + " subject");
                const uint32_t element_count = r.number();
                for (uint32_t k = 0; k < element_count; ++k) {
                    auto path = r.text();
                    route_rule::element_rule rule;
                    read_parts(rule.parts, path);
                    rule.entity_type = r.hash();
                    rule.relation = r.hash();
                    rule.observation = r.text();
                    if (path.empty() || rule.parts.empty() || !route.children.count(path))
                        throw std::runtime_error("element composition names no declared child path: " + path);
                    if (!route.elements.emplace(std::move(path), std::move(rule)).second)
                        throw std::runtime_error("duplicate element composition instruction");
                }
                const uint32_t child_subjects = r.number();
                for (uint32_t k = 0; k < child_subjects; ++k) {
                    auto path = r.text();
                    route_rule::child_subject_rule rule;
                    rule.identity = r.text();
                    rule.relation = r.hash();
                    rule.entity_type = r.hash();
                    const uint32_t direction = r.number();
                    if (direction > 1) throw std::runtime_error("invalid child subject direction");
                    rule.child_is_subject = direction != 0;
                    read_parts(rule.parts, path);
                    if (path.empty() || rule.identity.empty() || !route.children.count(path))
                        throw std::runtime_error("child subject names no declared child path");
                    if (!route.child_subjects.emplace(std::move(path), std::move(rule)).second)
                        throw std::runtime_error("duplicate child subject instruction");
                }
                if (route.kind == 4 && route.subject_parts.empty())
                    throw std::runtime_error("composition subject declares no parts at " + route.name);
            }
            if (route.kind > 4 || route.subject_codec > 2 || (route.range_first.empty() != route.range_last.empty()) ||
                (!route.range_first.empty() && (route.kind == 0 || !nonzero(route.range_relation))))
                throw std::runtime_error("invalid record route instruction");
            auto key = route.name;
            if (!s->routes.emplace(key,std::move(route)).second) throw std::runtime_error("duplicate route instruction");
        }
        if (rcp7) {
            count = r.number();
            for (uint32_t j = 0; j < count; ++j) {
                identity_table_rule table;
                table.name = r.text(); table.record = r.text();
                table.key_path = r.text(); table.value_path = r.text();
                const uint32_t absent = r.number();
                for (uint32_t k = 0; k < absent; ++k) table.absent.insert(r.text());
                if (table.name.empty() || table.record.empty() || table.key_path.empty() || table.value_path.empty())
                    throw std::runtime_error("invalid identity table instruction");
                s->tables[table.name];
                s->table_rules.push_back(std::move(table));
            }
            const uint32_t attribute_vocabularies = r.number();
            for (uint32_t k = 0; k < attribute_vocabularies; ++k) {
                auto attribute = r.text(); auto name = r.text();
                s->attribute_vocabularies[attribute] = parse_vocabulary(name, "attribute " + attribute);
            }
            for (const auto& f : s->fields)
                if (!f.second.identity_table.empty() && !s->tables.count(f.second.identity_table))
                    throw std::runtime_error("field references undeclared identity table at " + f.first);
            for (const auto& route : s->routes)
                if (!route.second.identity_table.empty() && !s->tables.count(route.second.identity_table))
                    throw std::runtime_error("route references undeclared identity table " + route.first);
        }
        if (r.remaining) throw std::runtime_error("trailing recipe instructions");
        if (provider_kind == 0) {
            const int depth = s->depth;
            s->make_provider = [depth] { return std::make_unique<recipe_xml_provider>(depth); };
        }
        s->provider = s->make_provider();
        *out = s.release(); return 0;
    } catch (const std::bad_alloc&) { return -3; }
    catch (const std::exception& e) { if (s) { s->failed = true; try { s->error = e.what(); } catch (...) {} *out = s.release(); } return -2; }
    catch (...) { return -3; }
}
namespace {
// Values normalized once at parse (a lexicon's "language"), through the whole record tree.
void normalize(const laplace_recipe_stream& s, node& n) {
    for (auto& a : n.attributes) a.second = s.attribute_value(a.first, std::move(a.second));
    for (auto& a : n.own) a.second = s.attribute_value(a.first, std::move(a.second));
    for (auto& c : n.children) normalize(s, c);
}
// The prescan collects the recipe's identity tables from whole records; scopes carry none.
struct harvest_sink final : recipe_record_sink {
    laplace_recipe_stream& s;
    explicit harvest_sink(laplace_recipe_stream& stream) : s(stream) {}
    void scope_open(node&&) override {}
    void record(node&& done) override { s.harvest(done); }
    void scope_close() override {}
};
// The lowering read: a record's route decides whether it inherits its scope's
// attributes, and a coverage scope with no subject of its own is projected over
// the merged spans its records cover.
struct lowering_sink final : recipe_record_sink {
    laplace_recipe_stream& s;
    explicit lowering_sink(laplace_recipe_stream& stream) : s(stream) {}
    void scope_open(node&& scope) override {
        normalize(s, scope);
        s.parent_scope = std::move(scope);
        s.parent_spans.clear();
        s.parent_scope_active = true;
        s.child_inherited = false;
    }
    void record(node&& done) override {
        normalize(s, done);
        auto route = s.routes.find(done.name);
        if (s.parent_scope_active) {
            auto parent_route = s.routes.find(s.parent_scope.name);
            if (parent_route != s.routes.end() && parent_route->second.kind == 3
                && route != s.routes.end() && route->second.kind == 3) {
                const std::string single = done.get(route->second.identity);
                const std::string first = single.empty() ? done.get(route->second.first) : single;
                const std::string last = single.empty() ? done.get(route->second.last) : single;
                if (!first.empty() && !last.empty()) {
                    const uint32_t lo = point(first), hi = point(last);
                    if (lo > hi) throw std::runtime_error("inverted child coverage span");
                    s.parent_spans.push_back({lo, hi});
                }
            }
        }
        if (route != s.routes.end() && route->second.inherit_parent_attributes) {
            if (!s.parent_scope_active)
                throw std::runtime_error("record requires inherited parent attributes");
            if (done.ns != s.parent_scope.ns)
                throw std::runtime_error("record/parent namespace mismatch");
            for (const auto& a : s.parent_scope.attributes)
                done.attributes.try_emplace(a.first, a.second);
            s.child_inherited = true;
        }
        if (s.parent_scope_active) done.witness_parts = s.scope_witness();
        s.pending.push_back(std::move(done));
    }
    void scope_close() override {
        auto route = s.routes.find(s.parent_scope.name);
        if (route != s.routes.end()) {
            const bool explicit_subject =
                !s.parent_scope.get(route->second.identity).empty()
                || !s.parent_scope.get(route->second.first).empty()
                || !s.parent_scope.get(route->second.last).empty();
            if (!(s.child_inherited && !explicit_subject)
                && route->second.kind == 3 && !explicit_subject
                && !s.parent_spans.empty()) {
                std::sort(s.parent_spans.begin(), s.parent_spans.end(),
                    [](const ordinal_span& a, const ordinal_span& b) {
                        return a.first != b.first ? a.first < b.first : a.last < b.last;
                    });
                std::vector<ordinal_span> merged;
                merged.reserve(s.parent_spans.size());
                for (const ordinal_span span : s.parent_spans) {
                    if (span.first > span.last) throw std::runtime_error("inverted parent coverage span");
                    if (merged.empty()
                        || static_cast<uint64_t>(span.first) > static_cast<uint64_t>(merged.back().last) + 1)
                        merged.push_back(span);
                    else if (span.last > merged.back().last)
                        merged.back().last = span.last;
                }
                for (const ordinal_span span : merged) {
                    node projected = s.parent_scope;
                    projected.attributes[route->second.first] = point_text(span.first);
                    projected.attributes[route->second.last] = point_text(span.last);
                    s.pending.push_back(std::move(projected));
                }
            } else if (!(s.child_inherited && !explicit_subject)) {
                s.parent_scope.witness_parts = s.scope_witness();
                s.pending.push_back(std::move(s.parent_scope));
            }
        }
        s.parent_scope = {};
        s.parent_spans.clear();
        s.parent_scope_active = false;
    }
};
}
extern "C" int laplace_recipe_stream_requires_prescan(const laplace_recipe_stream_t* s) {
    return s && !s->table_rules.empty() ? 1 : 0;
}
extern "C" int laplace_recipe_stream_prescan(laplace_recipe_stream_t* s, const uint8_t* bytes, size_t n, int final) {
    if (!s || s->failed || s->prescanned || s->table_rules.empty()) return -1;
    try {
        if (!s->prescan_provider) s->prescan_provider = s->make_provider();
        harvest_sink sink(*s);
        s->prescan_provider->feed(bytes, n, final != 0, sink);
        if (final) { s->prescanned = true; s->prescan_provider.reset(); }
        return 0;
    } catch (const std::exception& e) { s->failed = true; try { s->error = e.what(); } catch (...) {} return -2; }
    catch (...) { s->failed = true; return -3; }
}
extern "C" int laplace_recipe_stream_feed(laplace_recipe_stream_t* s, const uint8_t* bytes, size_t n, int final) {
    if (!s || s->failed || s->final || !s->pending.empty() || s->active || s->ready) return -1;
    if (!s->table_rules.empty() && !s->prescanned) {
        s->error = "recipe identity tables were not collected before feed";
        return -1;
    }
    try {
        lowering_sink sink(*s);
        s->provider->feed(bytes, n, final != 0, sink);
        s->final = final != 0; return 0;
    } catch (const std::exception& e) { s->failed = true; try { s->error = e.what(); } catch (...) {} return -2; }
    catch (...) { s->failed = true; return -3; }
}
namespace {
// Coalesce entity, physicality and testimony tuples across records; the final
// import is one set operation, independent of semantic row count.
struct tuple_span {
    const uint8_t* bytes;
    size_t size;
};
struct tuple_selection {
    std::array<std::vector<tuple_span>, 3> spans;
    std::array<size_t, 3> byte_counts{};
    size_t row_count = 0;
};
struct tuple_batch {
    std::array<std::vector<uint8_t>, 3> buffers;
    // Keys contain the complete canonical tuple, not only its id; hash
    // equality alone never establishes identity. Testimony is never indexed.
    std::array<std::unordered_set<std::string>, 3> canonical_rows;
    size_t row_count = 0, base_bytes;
    explicit tuple_batch(size_t base) : base_bytes(base) {}
    static const uint8_t* buffer(const intent_stage_t* stage, size_t index, size_t* size) {
        return intent_stage_tuple_ptr(stage, static_cast<intent_stage_table_t>(index + 1), size);
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
                // Entity rows are (id, tier, type_id); physicality rows carry ten columns.
                const size_t length = tuple_size(bytes + offset, size - offset, i == 1 ? 10 : 3);
                std::string key(reinterpret_cast<const char*>(bytes + offset), length);
                if (canonical_rows[i].find(key) == canonical_rows[i].end() &&
                    local.emplace(std::move(key)).second) {
                    selected.spans[i].push_back({bytes + offset, length});
                    selected.byte_counts[i] += length;
                    ++selected.row_count;
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
    if (s->pending.empty() && !s->ready && !s->active && s->final && !s->file_trunk_done
        && !s->file_parts.empty()) {
        try {
            stage_ptr stage(intent_stage_new_bounded(0, maximum_bytes), intent_stage_free);
            if (!stage) throw std::bad_alloc();
            s->file_trunk_done = true;
            if (s->file_parts.size() > 1) s->compose_trunk(stage.get(), s->file_parts, "SourceFile");
            s->file_parts.clear(); s->file_parts.shrink_to_fit();
            *out = stage.release();
            return 1;
        } catch (const std::exception& e) { return fail_stream(s, e.what()); }
        catch (...) { return fail_stream(s, "unknown native recipe failure"); }
    }
    if (s->pending.empty() && !s->ready && !s->active && s->final && !s->aggregates.empty()) {
        try {
            stage_ptr stage(intent_stage_new_bounded(0, maximum_bytes), intent_stage_free);
            if (!stage) throw std::bad_alloc();
            size_t width = 0;
            if (intent_stage_tuple_payload_bound(0, 0, 0, 1, &width) != 0)
                throw std::runtime_error("invalid testimony payload bound");
            const size_t capacity = std::max<size_t>(1, std::min(maximum_rows,
                (maximum_bytes - intent_stage_memory_bytes(stage.get())) / width / 4));
            size_t emitted = 0;
            for (auto it = s->aggregates.begin(); it != s->aggregates.end() && emitted < capacity; ++emitted) {
                s->attest(stage.get(), it->second.subject, it->second);
                it = s->aggregates.erase(it);
            }
            *out = stage.release();
            return 1;
        } catch (const std::exception& e) { return fail_stream(s, e.what()); }
        catch (...) { return fail_stream(s, "unknown native recipe failure"); }
    }
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
