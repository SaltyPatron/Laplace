#pragma once

#include <cctype>
#include <cstdint>
#include <map>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

#include "recipe_delimited.hpp"

// RDF 1.1 Turtle syntax recovery for the recipe VM. One record per statement's
// subject (a nested [ ... ] blank node is its own record). The provider recovers
// syntax only; recipe_stream owns all semantic lowering. Record attributes:
//   about            the subject
//   a                rdf:type objects
//   <predicate>      objects of the predicate, keyed by its compact name in the
//                    file ("skos:definition") or its full IRI when no prefix covers it
//   <key>#ns         for IRIs: the namespace IRI the value is local to. The value is
//                    the local part, so <i1> under @base and ili:i1 under a prefix
//                    both read "i1" in namespace http://globalwordnet.org/cili/
//   <key>#lang       a literal's language tag, lower case
//   <key>#type       a typed literal's datatype IRI
// A predicate stated several times carries its values in source order separated by
// recipe_pair_item_separator; the #ns/#lang/#type companions stay aligned with them.
struct recipe_turtle_config {
    std::string record_name, namespace_uri;
    // rdf:type IRIs (expanded) whose resources describe the file itself (a
    // voaf:Vocabulary header, owl:Class declarations): declared packaging, not records.
    std::vector<std::string> exclude_types;
};

class recipe_turtle_stream {
    static constexpr const char* rdf_type = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
    static constexpr const char* xsd = "http://www.w3.org/2001/XMLSchema#";

    struct term {
        enum kind_t { iri, blank, literal } kind = iri;
        std::string value, lang, datatype;
        std::vector<term> items; // a collection ( ... )
        bool collection = false;
    };
    using attributes_t = std::map<std::string, std::string>;

    std::string pending_;
    uint64_t statements_ = 0, blank_ = 0;
    bool final_ = false;
    std::map<std::string, std::string> prefixes_;
    std::string base_;

    [[noreturn]] void fail(const std::string& reason) const {
        throw std::runtime_error("turtle provider statement " + std::to_string(statements_ + 1) + ": " + reason);
    }
    static bool space(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }
    static bool delimiter(char c) { return space(c) || c == ',' || c == ';' || c == ')' || c == ']' || c == '#'; }

    // Offset one past a complete statement starting at `from`, or npos when the buffer
    // does not yet hold one. Strings, IRIs and comments cannot end a statement.
    size_t statement_end(std::string_view t, size_t from) const {
        size_t i = from;
        auto keyword = [&](std::string_view word) {
            if (t.size() - i < word.size()) return false;
            for (size_t k = 0; k < word.size(); ++k)
                if ((t[i + k] | 0x20) != (word[k] | 0x20)) return false;
            return t.size() > i + word.size() && space(t[i + word.size()]);
        };
        // SPARQL-style PREFIX/BASE end at their IRI, without a '.'.
        if (keyword("PREFIX") || keyword("BASE")) {
            const size_t close = t.find('>', i);
            return close == std::string_view::npos ? close : close + 1;
        }
        int depth = 0;
        while (i < t.size()) {
            const char c = t[i];
            if (c == '#') {
                const size_t eol = t.find('\n', i);
                if (eol == std::string_view::npos) return eol;
                i = eol + 1; continue;
            }
            if (c == '<') {
                const size_t close = t.find('>', i + 1);
                if (close == std::string_view::npos) return close;
                i = close + 1; continue;
            }
            if (c == '"' || c == '\'') {
                const bool triple = t.size() - i >= 3 && t[i + 1] == c && t[i + 2] == c;
                size_t k = i + (triple ? 3 : 1);
                for (;;) {
                    if (k >= t.size()) return std::string_view::npos;
                    if (t[k] == '\\') { k += 2; continue; }
                    if (t[k] == c && (!triple || (t.size() - k >= 3 && t[k + 1] == c && t[k + 2] == c))) {
                        k += triple ? 3 : 1; break;
                    }
                    if (!triple && t[k] == '\n') fail("unterminated string");
                    ++k;
                }
                i = k; continue;
            }
            if (c == '[' || c == '(') ++depth;
            else if (c == ']' || c == ')') --depth;
            else if (c == '.' && depth == 0) {
                if (i + 1 == t.size()) return final_ ? i + 1 : std::string_view::npos;
                if (space(t[i + 1]) || t[i + 1] == '#') return i + 1;
            }
            ++i;
        }
        return std::string_view::npos;
    }

    // --- statement parsing over one complete statement ---
    std::string_view s_;
    size_t p_ = 0;
    std::vector<std::pair<std::string, attributes_t>> records_;

    void skip() {
        while (p_ < s_.size()) {
            if (space(s_[p_])) ++p_;
            else if (s_[p_] == '#') { while (p_ < s_.size() && s_[p_] != '\n') ++p_; }
            else break;
        }
    }
    bool at(char c) { skip(); return p_ < s_.size() && s_[p_] == c; }
    void expect(char c) {
        if (!at(c)) fail(std::string("expected '") + c + "'");
        ++p_;
    }
    static void utf8(std::string& out, uint32_t cp) {
        if (cp < 0x80) out += char(cp);
        else if (cp < 0x800) { out += char(0xC0 | (cp >> 6)); out += char(0x80 | (cp & 0x3F)); }
        else if (cp < 0x10000) { out += char(0xE0 | (cp >> 12)); out += char(0x80 | ((cp >> 6) & 0x3F)); out += char(0x80 | (cp & 0x3F)); }
        else { out += char(0xF0 | (cp >> 18)); out += char(0x80 | ((cp >> 12) & 0x3F)); out += char(0x80 | ((cp >> 6) & 0x3F)); out += char(0x80 | (cp & 0x3F)); }
    }
    uint32_t hex(size_t digits) {
        if (s_.size() - p_ < digits) fail("truncated escape");
        uint32_t v = 0;
        for (size_t k = 0; k < digits; ++k) {
            const char c = s_[p_++];
            v <<= 4;
            if (c >= '0' && c <= '9') v |= uint32_t(c - '0');
            else if ((c | 0x20) >= 'a' && (c | 0x20) <= 'f') v |= uint32_t((c | 0x20) - 'a' + 10);
            else fail("invalid hex escape");
        }
        return v;
    }
    std::string resolve(const std::string& ref) const {
        const size_t colon = ref.find(':');
        if (colon != std::string::npos && ref.find_first_of("/?#") > colon) return ref; // absolute
        if (base_.empty()) return ref;
        if (ref.empty()) return base_;
        if (ref[0] == '#') return base_.substr(0, base_.find('#')) + ref;
        if (ref[0] == '/') {
            const size_t authority = base_.find("//");
            const size_t path = authority == std::string::npos ? std::string::npos : base_.find('/', authority + 2);
            return (path == std::string::npos ? base_ : base_.substr(0, path)) + ref;
        }
        return base_.substr(0, base_.rfind('/') + 1) + ref;
    }
    std::string iriref() {
        expect('<');
        std::string out;
        while (p_ < s_.size() && s_[p_] != '>') {
            if (s_[p_] == '\\') {
                ++p_;
                const char e = p_ < s_.size() ? s_[p_++] : '\0';
                if (e == 'u') utf8(out, hex(4));
                else if (e == 'U') utf8(out, hex(8));
                else fail("invalid IRI escape");
            } else out += s_[p_++];
        }
        if (p_ >= s_.size()) fail("unterminated IRI");
        ++p_;
        return resolve(out);
    }
    std::string pname() {
        const size_t start = p_;
        while (p_ < s_.size() && s_[p_] != ':' && !delimiter(s_[p_])) ++p_;
        if (p_ >= s_.size() || s_[p_] != ':') fail("expected a prefixed name");
        const std::string prefix(s_.substr(start, p_ - start));
        ++p_;
        std::string local;
        while (p_ < s_.size() && !delimiter(s_[p_])) {
            if (s_[p_] == '\\' && p_ + 1 < s_.size()) { local += s_[p_ + 1]; p_ += 2; continue; }
            local += s_[p_++];
        }
        // A local name never ends with '.': that dot terminates the statement.
        while (!local.empty() && local.back() == '.') { local.pop_back(); --p_; }
        const auto ns = prefixes_.find(prefix);
        if (ns == prefixes_.end()) fail("undeclared prefix " + prefix + ":");
        return ns->second + local;
    }
    term literal() {
        term t; t.kind = term::literal;
        const char q = s_[p_];
        const bool triple = s_.size() - p_ >= 3 && s_[p_ + 1] == q && s_[p_ + 2] == q;
        p_ += triple ? 3 : 1;
        for (;;) {
            if (p_ >= s_.size()) fail("unterminated string");
            const char c = s_[p_];
            if (c == '\\') {
                ++p_;
                const char e = p_ < s_.size() ? s_[p_++] : '\0';
                switch (e) {
                case 't': t.value += '\t'; break; case 'b': t.value += '\b'; break;
                case 'n': t.value += '\n'; break; case 'r': t.value += '\r'; break;
                case 'f': t.value += '\f'; break; case '"': t.value += '"'; break;
                case '\'': t.value += '\''; break; case '\\': t.value += '\\'; break;
                case 'u': utf8(t.value, hex(4)); break; case 'U': utf8(t.value, hex(8)); break;
                default: fail("invalid string escape");
                }
                continue;
            }
            if (c == q && (!triple || (s_.size() - p_ >= 3 && s_[p_ + 1] == q && s_[p_ + 2] == q))) {
                p_ += triple ? 3 : 1; break;
            }
            t.value += c; ++p_;
        }
        if (p_ < s_.size() && s_[p_] == '@') {
            ++p_;
            while (p_ < s_.size() && (std::isalnum(static_cast<unsigned char>(s_[p_])) || s_[p_] == '-'))
                t.lang += char(std::tolower(static_cast<unsigned char>(s_[p_++])));
        } else if (s_.size() - p_ >= 2 && s_[p_] == '^' && s_[p_ + 1] == '^') {
            p_ += 2;
            t.datatype = s_[p_] == '<' ? iriref() : pname();
        }
        return t;
    }
    term object() {
        skip();
        if (p_ >= s_.size()) fail("expected an object");
        const char c = s_[p_];
        term t;
        if (c == '<') { t.value = iriref(); return t; }
        if (c == '"' || c == '\'') return literal();
        if (c == '[') { t.kind = term::blank; t.value = property_list_node(); return t; }
        if (c == '(') {
            ++p_; t.collection = true;
            while (!at(')')) t.items.push_back(object());
            ++p_;
            return t;
        }
        if (c == '_' && p_ + 1 < s_.size() && s_[p_ + 1] == ':') {
            const size_t start = p_;
            p_ += 2;
            while (p_ < s_.size() && !delimiter(s_[p_])) ++p_;
            while (s_[p_ - 1] == '.') --p_;
            t.kind = term::blank; t.value = std::string(s_.substr(start, p_ - start));
            return t;
        }
        if (std::isdigit(static_cast<unsigned char>(c)) || c == '+' || c == '-' || c == '.') {
            const size_t start = p_;
            while (p_ < s_.size() && !delimiter(s_[p_])) ++p_;
            while (p_ > start + 1 && s_[p_ - 1] == '.') --p_;
            t.kind = term::literal; t.value = std::string(s_.substr(start, p_ - start));
            t.datatype = std::string(xsd) + (t.value.find_first_of("eE") != std::string::npos ? "double"
                : t.value.find('.') != std::string::npos ? "decimal" : "integer");
            return t;
        }
        for (const char* word : {"true", "false"}) {
            const size_t n = std::char_traits<char>::length(word);
            if (s_.substr(p_, n) == word && (p_ + n == s_.size() || delimiter(s_[p_ + n]) || s_[p_ + n] == '.')) {
                p_ += n; t.kind = term::literal; t.value = word; t.datatype = std::string(xsd) + "boolean";
                return t;
            }
        }
        t.value = pname();
        return t;
    }
    std::string predicate() {
        skip();
        if (p_ < s_.size() && s_[p_] == 'a' && (p_ + 1 == s_.size() || space(s_[p_ + 1]) || s_[p_ + 1] == '<')) {
            ++p_; return rdf_type;
        }
        return s_[p_] == '<' ? iriref() : pname();
    }

    // The namespace an IRI is local to: the longest declared prefix IRI, else the
    // base's directory. The value then names the resource the same way in every file.
    std::pair<std::string, std::string> split(const std::string& iri) const {
        std::string best;
        for (const auto& prefix : prefixes_)
            if (prefix.second.size() > best.size() && iri.compare(0, prefix.second.size(), prefix.second) == 0
                && iri.size() > prefix.second.size())
                best = prefix.second;
        const std::string directory = base_.substr(0, base_.rfind('/') + 1);
        if (!directory.empty() && directory.size() > best.size() && iri.compare(0, directory.size(), directory) == 0
            && iri.size() > directory.size())
            best = directory;
        if (best.empty()) return {std::string{}, iri};
        return {best, iri.substr(best.size())};
    }
    std::string key(const std::string& iri) const {
        if (iri == rdf_type) return "a";
        std::string best_prefix, best_ns;
        for (const auto& prefix : prefixes_)
            if (prefix.second.size() > best_ns.size() && iri.compare(0, prefix.second.size(), prefix.second) == 0) {
                best_prefix = prefix.first; best_ns = prefix.second;
            }
        return best_ns.empty() ? iri : best_prefix + ":" + iri.substr(best_ns.size());
    }
    static void append(attributes_t& a, const std::string& key, const std::string& value) {
        auto [slot, inserted] = a.emplace(key, value);
        if (!inserted) { slot->second += recipe_pair_item_separator; slot->second += value; }
    }
    void put(attributes_t& a, const std::string& key, const term& t) {
        if (t.collection) { for (const auto& item : t.items) put(a, key, item); return; }
        const bool first = a.find(key) == a.end();
        auto companion = [&](const char* suffix, const std::string& value) {
            // Companions exist once any value of the key carries one, aligned by position.
            const std::string name = key + suffix;
            auto slot = a.find(name);
            if (slot == a.end() && value.empty()) return;
            if (slot == a.end() && !first) {
                size_t prior = 1;
                for (char c : a[key]) prior += c == recipe_pair_item_separator;
                a[name] = std::string(prior - 1, recipe_pair_item_separator);
            }
            if (slot == a.end() && first) { a[name] = value; return; }
            append(a, name, value);
        };
        std::string value = t.value, ns;
        if (t.kind == term::iri) { auto parts = split(t.value); ns = parts.first; value = parts.second; }
        companion("#ns", ns);
        companion("#lang", t.lang);
        companion("#type", t.datatype);
        append(a, key, value);
    }
    void predicate_objects(attributes_t& a, char close) {
        for (;;) {
            skip();
            if (p_ >= s_.size() || s_[p_] == close || s_[p_] == '.') return;
            const std::string pred = predicate();
            const std::string k = key(pred);
            for (;;) {
                term o = object();
                if (pred == rdf_type) a["#types"] += std::string(1, recipe_pair_item_separator) + o.value;
                put(a, k, o);
                if (!at(',')) break;
                ++p_;
            }
            if (!at(';')) return;
            while (at(';')) ++p_;
        }
    }
    std::string property_list_node() {
        expect('[');
        const std::string id = "_:b" + std::to_string(++blank_);
        attributes_t a;
        a["about"] = id;
        predicate_objects(a, ']');
        expect(']');
        records_.emplace_back(id, std::move(a));
        return id;
    }
    void directive() {
        skip();
        const bool sparql = s_[p_] != '@';
        if (!sparql) ++p_;
        const size_t word = p_;
        while (p_ < s_.size() && !space(s_[p_])) ++p_;
        std::string name(s_.substr(word, p_ - word));
        for (auto& c : name) c = char(std::tolower(static_cast<unsigned char>(c)));
        if (name == "prefix") {
            skip();
            const size_t start = p_;
            while (p_ < s_.size() && s_[p_] != ':') ++p_;
            const std::string prefix(s_.substr(start, p_ - start));
            ++p_;
            skip();
            prefixes_[prefix] = iriref();
        } else if (name == "base") {
            skip();
            base_ = iriref();
        } else fail("unknown directive " + name);
        if (!sparql) expect('.');
    }
    template<class Emit> void statement(std::string_view text, Emit& emit) {
        s_ = text; p_ = 0;
        skip();
        if (p_ >= s_.size()) return;
        const char c = s_[p_];
        auto word = [&](std::string_view w) {
            if (s_.size() - p_ <= w.size()) return false;
            for (size_t k = 0; k < w.size(); ++k) if ((s_[p_ + k] | 0x20) != (w[k] | 0x20)) return false;
            return space(s_[p_ + w.size()]);
        };
        if (c == '@' || word("PREFIX") || word("BASE")) { directive(); ++statements_; return; }
        records_.clear();
        attributes_t a;
        if (c == '[') {
            // [ ... ] as subject: its properties and any that follow describe one node.
            const std::string id = property_list_node();
            a = std::move(records_.back().second);
            records_.pop_back();
            (void)id;
        } else {
            term subject = object();
            if (subject.kind == term::literal || subject.collection) fail("a literal cannot be a subject");
            put(a, "about", subject);
        }
        predicate_objects(a, '.');
        expect('.');
        ++statements_;
        records_.emplace_back(a["about"], std::move(a));
        for (auto& record : records_) {
            auto types = record.second.find("#types");
            bool excluded = false;
            if (types != record.second.end()) {
                for (const auto& type : config.exclude_types)
                    if ((types->second + recipe_pair_item_separator).find(
                            std::string(1, recipe_pair_item_separator) + type + recipe_pair_item_separator) != std::string::npos)
                        excluded = true;
                record.second.erase(types);
            }
            if (excluded) continue;
            emit(config.record_name, config.namespace_uri, std::move(record.second),
                 recipe_delimited_structure{{}, nullptr});
        }
        records_.clear();
    }

public:
    recipe_turtle_config config;
    explicit recipe_turtle_stream(recipe_turtle_config value) : config(std::move(value)) {
        if (config.record_name.empty()) throw std::runtime_error("invalid turtle syntax declaration");
    }
    template<class Emit> void feed(const uint8_t* bytes, size_t size, bool final, Emit emit) {
        if (final_) throw std::runtime_error("turtle feed after final input");
        if (size && !bytes) throw std::runtime_error("missing turtle input bytes");
        if (size) pending_.append(reinterpret_cast<const char*>(bytes), size);
        if (final) final_ = true;
        size_t start = 0;
        if (statements_ == 0 && pending_.compare(0, 3, "\xef\xbb\xbf") == 0) start = 3;
        for (;;) {
            // Leading space and comments belong to no statement.
            std::string_view view(pending_);
            while (start < view.size()) {
                if (space(view[start])) ++start;
                else if (view[start] == '#') {
                    const size_t eol = view.find('\n', start);
                    if (eol == std::string_view::npos) { if (final_) start = view.size(); break; }
                    start = eol + 1;
                } else break;
            }
            if (start >= view.size()) break;
            const size_t end = statement_end(view, start);
            if (end == std::string_view::npos) {
                if (final_) fail("input ends inside a statement");
                break;
            }
            statement(view.substr(start, end - start), emit);
            start = end;
        }
        if (start) pending_.erase(0, start);
    }
};
