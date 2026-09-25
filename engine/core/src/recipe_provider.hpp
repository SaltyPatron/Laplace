#pragma once

#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <stdexcept>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "laplace/core/xml_stream.h"
#include "recipe_delimited.hpp"
#include "recipe_turtle.hpp"

// A syntax provider recovers one standard format's records and nothing else. The
// recipe names which provider reads its source; recipe_stream owns every semantic
// decision (routing, inheritance, coverage, identity, lowering). A new standard
// format is a new provider, never a new decomposer.

// One recovered source record.
struct recipe_node {
    std::string name, ns;
    std::map<std::string, std::string> attributes;
    std::vector<recipe_node> children;
    // Element character data, exactly as the source wrote it.
    std::string text;
    // Recovered source structure in source order: a delimited line's raw cells,
    // or an XML element's own (name, value) attributes. `group` is carried by the
    // last row of a delimited group and lists the group's lines in order.
    std::vector<std::string> cells;
    std::vector<std::pair<std::string, std::string>> own;
    std::shared_ptr<const std::vector<std::vector<std::string>>> group;
    // The self-description of the witness whose observations this record carries
    // (a WN-LMF Lexicon's [id, version]); empty = the generation's witness.
    std::vector<std::string> witness_parts;
    static recipe_node named(std::string name, std::string ns) {
        recipe_node n; n.name = std::move(name); n.ns = std::move(ns); return n;
    }
    // "Child/@attr" reads a child element's attribute (a WN-LMF entry is named by its
    // Lemma's writtenForm, not by its packaging id).
    std::string get(const std::string& key) const {
        const size_t at = key.find("/@");
        if (at != std::string::npos) {
            const std::string child = key.substr(0, at);
            for (const auto& c : children)
                if (c.name == child) return c.get(key.substr(at + 2));
            return "";
        }
        auto i = attributes.find(key); return i == attributes.end() ? "" : i->second;
    }
};

inline bool recipe_has_text(std::string_view text) {
    return text.find_first_not_of(" \r\n\t") != std::string_view::npos;
}

// What a provider reports. Hierarchical syntaxes also report the scope one level
// above the record depth (a WN-LMF Lexicon, a UCD group): opened once its own
// attributes are complete, closed after its last record. Values arrive as written.
struct recipe_record_sink {
    virtual ~recipe_record_sink() = default;
    virtual void scope_open(recipe_node&& scope) = 0;
    virtual void record(recipe_node&& record) = 0;
    virtual void scope_close() = 0;
};

struct recipe_syntax_provider {
    virtual ~recipe_syntax_provider() = default;
    virtual void feed(const uint8_t* bytes, size_t size, bool final, recipe_record_sink& sink) = 0;
};

// Declared by the recipe; each call yields an independent reader (the identity-table
// prescan reads the source once before the lowering read).
using recipe_provider_factory = std::function<std::unique_ptr<recipe_syntax_provider>()>;

// XML: records are the elements at the recipe's record depth, with their subtrees.
class recipe_xml_provider final : public recipe_syntax_provider {
    laplace_xml_stream_t* parser_ = nullptr;
    int depth_;
    std::vector<recipe_node> stack_;
    recipe_node scope_;
    bool scope_active_ = false, scope_reported_ = false;

    // A namespaced attribute is addressed by the source's own qualified name ("dc:type").
    static std::string attribute_key(const laplace_xml_event_t& e) {
        if (!e.namespace_uri || !*e.namespace_uri) return e.name;
        if (!e.prefix || !*e.prefix)
            throw std::runtime_error("namespaced attribute has no prefix: " + std::string(e.name));
        return std::string(e.prefix) + ":" + e.name;
    }
    void report_scope(recipe_record_sink& sink) {
        if (!scope_active_ || scope_reported_) return;
        scope_reported_ = true;
        sink.scope_open(std::move(scope_));
        scope_ = {};
    }

public:
    explicit recipe_xml_provider(int depth) : depth_(depth) {
        if (laplace_xml_stream_new(&parser_) != 0 || !parser_)
            throw std::runtime_error("XML provider creation failed");
    }
    ~recipe_xml_provider() override { laplace_xml_stream_free(parser_); }
    recipe_xml_provider(const recipe_xml_provider&) = delete;
    recipe_xml_provider& operator=(const recipe_xml_provider&) = delete;

    void feed(const uint8_t* bytes, size_t size, bool final, recipe_record_sink& sink) override {
        const laplace_xml_event_t* events = nullptr; size_t count = 0;
        if (laplace_xml_stream_feed(parser_, bytes, size, final ? 1 : 0, &events, &count) != 0)
            throw std::runtime_error(laplace_xml_stream_error(parser_));
        for (size_t i = 0; i < count; ++i) {
            const auto& e = events[i];
            if (e.depth + 1 == depth_) {
                if (e.kind == 1) {
                    scope_ = recipe_node::named(e.name, e.namespace_uri ? e.namespace_uri : "");
                    scope_active_ = true; scope_reported_ = false;
                } else if (e.kind == 4 && scope_active_ && !scope_reported_) {
                    const std::string key = attribute_key(e);
                    std::string value(e.value, e.value_len);
                    if (!scope_.attributes.emplace(key, value).second)
                        throw std::runtime_error("duplicate parent record attribute");
                    scope_.own.emplace_back(key, std::move(value));
                } else if (e.kind == 2) {
                    if (scope_active_) { report_scope(sink); sink.scope_close(); }
                    scope_ = {};
                    scope_active_ = false;
                }
                continue;
            }
            if (e.depth < depth_) continue;
            if (e.kind == 1) {
                report_scope(sink);
                stack_.push_back(recipe_node::named(e.name, e.namespace_uri ? e.namespace_uri : ""));
            } else if (e.kind == 4) {
                if (stack_.empty()) throw std::runtime_error("attribute outside record");
                const std::string key = attribute_key(e);
                std::string value(e.value, e.value_len);
                if (!stack_.back().attributes.emplace(key, value).second)
                    throw std::runtime_error("duplicate record attribute");
                stack_.back().own.emplace_back(key, std::move(value));
            } else if (e.kind == 2) {
                if (stack_.empty()) throw std::runtime_error("unbalanced record");
                recipe_node done = std::move(stack_.back()); stack_.pop_back();
                if (stack_.empty()) sink.record(std::move(done));
                else stack_.back().children.push_back(std::move(done));
            } else if (e.kind == 3) {
                // Character data belongs to its element; the recipe decides its disposition.
                if (!stack_.empty()) stack_.back().text.append(e.value, e.value_len);
                else if (recipe_has_text(std::string_view(e.value, e.value_len)))
                    throw std::runtime_error("recipe has no text-node disposition");
            }
        }
        if (final && !stack_.empty()) throw std::runtime_error("XML source ended inside a record");
    }
};

// Line and statement syntaxes (delimited tables, Turtle) report flat records.
template<class Reader>
class recipe_flat_provider final : public recipe_syntax_provider {
    Reader reader_;
public:
    template<class Config> explicit recipe_flat_provider(Config config) : reader_(std::move(config)) {}
    const Reader& reader() const { return reader_; }
    void feed(const uint8_t* bytes, size_t size, bool final, recipe_record_sink& sink) override {
        reader_.feed(bytes, size, final, [&](const std::string& name, const std::string& ns,
            std::map<std::string, std::string>&& attributes, recipe_delimited_structure&& structure) {
            recipe_node n = recipe_node::named(name, ns);
            n.attributes = std::move(attributes);
            n.cells = std::move(structure.cells);
            n.group = std::move(structure.group);
            sink.record(std::move(n));
        });
    }
};
using recipe_delimited_provider = recipe_flat_provider<recipe_delimited_stream>;
using recipe_turtle_provider = recipe_flat_provider<recipe_turtle_stream>;
