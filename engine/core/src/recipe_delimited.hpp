#pragma once

#include <cstdint>
#include <map>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

// Adapted from Laplace-Refactor's decomposition_delimited.cpp separator/record
// recovery. Streaming retains an unfinished record across feeds. This provider
// recovers syntax only; recipe_stream owns all shared semantic lowering.
struct recipe_delimited_config {
    std::string record_name, namespace_uri, separator, comment_prefix;
    std::string directive_prefix, directive_record_name;
    std::string range_column, range_separator, range_first_field, range_last_field;
    bool trim_fields = true;
    uint32_t minimum_columns = 0; // Zero requires the complete data-column schema.
    bool allow_trailing_empty_column = false;
    std::vector<std::string> columns, directive_columns;
};

class recipe_delimited_stream {
    std::string pending_;
    uint64_t line_ = 0;
    bool final_ = false;

    static std::string_view trim(std::string_view text) {
        auto first = text.find_first_not_of(" \t\r");
        if (first == std::string_view::npos) return {};
        return text.substr(first, text.find_last_not_of(" \t\r") - first + 1);
    }
    [[noreturn]] void fail(const std::string& reason) const {
        throw std::runtime_error("delimited provider line " + std::to_string(line_) + ": " + reason);
    }
    template<class Emit> void record(std::string_view text, Emit& emit) {
        ++line_;
        if (line_ == 1 && text.size() >= 3 && text.substr(0, 3) == "\xef\xbb\xbf") text.remove_prefix(3);
        if (!text.empty() && text.back() == '\r') text.remove_suffix(1);
        auto nonspace = trim(text);
        if (nonspace.empty()) return;
        bool directive = false;
        if (!config.comment_prefix.empty() && nonspace.find(config.comment_prefix) == 0) {
            auto body = trim(nonspace.substr(config.comment_prefix.size()));
            if (!config.directive_prefix.empty() && body.find(config.directive_prefix) == 0) {
                directive = true;
                text = body.substr(config.directive_prefix.size());
            } else {
                // Data-bearing directives cannot disappear into the comment lane.
                if (!body.empty() && body.front() == '@') fail("undeclared directive " + std::string(body));
                return;
            }
        } else if (!config.directive_prefix.empty() && nonspace.find(config.directive_prefix) == 0) {
            directive = true;
            text = nonspace.substr(config.directive_prefix.size());
        }
        if (!config.comment_prefix.empty()) {
            auto comment = text.find(config.comment_prefix);
            if (comment != std::string_view::npos) text = text.substr(0, comment);
        }
        const auto& columns = directive ? config.directive_columns : config.columns;
        std::map<std::string, std::string> values;
        size_t start = 0, column = 0;
        for (;;) {
            auto end = text.find(config.separator, start);
            auto value = text.substr(start, end == std::string_view::npos ? end : end - start);
            if (config.trim_fields) value = trim(value);
            if (column == columns.size()) {
                // A declared terminator-like trailing separator may recover one
                // additional empty column. It cannot hide another data value or
                // an arbitrary suffix of empty columns.
                if (!config.allow_trailing_empty_column || end != std::string_view::npos || !value.empty())
                    fail("more fields than the declared column schema");
                break;
            }
            if (!values.emplace(columns[column++], std::string(value)).second) fail("duplicate column name");
            if (end == std::string_view::npos) break;
            start = end + config.separator.size();
        }
        const size_t minimum = config.minimum_columns == 0
            ? columns.size() : config.minimum_columns;
        if (column < minimum) fail("expected at least " + std::to_string(minimum)
            + " fields, recovered " + std::to_string(column));
        while (column < columns.size())
            if (!values.emplace(columns[column++], std::string{}).second) fail("duplicate column name");
        if (!config.range_column.empty()) {
            auto range = values.find(config.range_column);
            if (range != values.end()) {
                auto separator = range->second.find(config.range_separator);
                if (separator != std::string::npos) {
                    std::string first(trim(std::string_view(range->second).substr(0, separator)));
                    std::string last(trim(std::string_view(range->second).substr(separator + config.range_separator.size())));
                    if (first.empty() || last.empty() || last.find(config.range_separator) != std::string::npos)
                        fail("malformed declared range");
                    values.erase(range);
                    if (!values.emplace(config.range_first_field, std::move(first)).second
                        || !values.emplace(config.range_last_field, std::move(last)).second)
                        fail("range endpoint collides with a declared column");
                }
            }
        }
        emit(directive ? config.directive_record_name : config.record_name,
             config.namespace_uri, std::move(values));
    }

public:
    recipe_delimited_config config;
    explicit recipe_delimited_stream(recipe_delimited_config value) : config(std::move(value)) {
        if (config.record_name.empty() || config.separator.empty() || config.columns.empty()
            || config.separator.find_first_of("\r\n") != std::string::npos
            || config.minimum_columns > config.columns.size())
            throw std::runtime_error("invalid delimited syntax declaration");
        if (!config.directive_prefix.empty() && (config.directive_record_name.empty() || config.directive_columns.empty()))
            throw std::runtime_error("directive requires its own record route and columns");
        if (!config.range_column.empty() && (config.range_separator.empty()
            || config.range_first_field.empty() || config.range_last_field.empty()))
            throw std::runtime_error("range requires separator and endpoint fields");
    }
    template<class Emit> void feed(const uint8_t* bytes, size_t size, bool final, Emit emit) {
        if (final_) throw std::runtime_error("delimited feed after final input");
        if (size && !bytes) throw std::runtime_error("missing delimited input bytes");
        if (size) pending_.append(reinterpret_cast<const char*>(bytes), size);
        size_t start = 0;
        for (;;) {
            auto end = pending_.find('\n', start);
            if (end == std::string::npos) break;
            record(std::string_view(pending_).substr(start, end - start), emit);
            start = end + 1;
        }
        if (start) pending_.erase(0, start);
        if (final) {
            if (!pending_.empty()) record(pending_, emit);
            pending_.clear();
            final_ = true;
        }
    }
};
