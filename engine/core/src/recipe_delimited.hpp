#pragma once

#include <cstdint>
#include <map>
#include <regex>
#include <memory>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

// Adapted from Laplace-Refactor's decomposition_delimited.cpp separator/record
// recovery. Streaming retains an unfinished record across feeds. This provider
// recovers syntax only; recipe_stream owns all shared semantic lowering.
// One declared in-group reference: a column whose value names another row of
// the same group by its key column. The provider replaces the pointer with the
// referenced row's target value; the source's pointer numbers are never content.
// A value equal to root_value names the group trunk itself. Pair columns carry
// several "key<pair_value_separator>value" items; only the key is a reference.
struct recipe_delimited_reference {
    std::string column, key_column, target_column, root_value;
    std::string pair_separator, pair_value_separator;
};

// Resolved reference values travel to the recipe VM with in-memory markers that
// cannot occur in source text lines.
inline constexpr char recipe_group_trunk_value[] = "\x01";
inline constexpr char recipe_pair_item_separator = '\x1e';
inline constexpr char recipe_pair_value_separator = '\x1f';

// Source structure recovered alongside the keyed cells: the raw values of one
// line in column order, and for the last row of a group the group's lines in
// source order (comment key/value lines, rows, and key-skipped rows).
struct recipe_delimited_structure {
    std::vector<std::string> cells;
    std::shared_ptr<const std::vector<std::vector<std::string>>> group;
};

struct recipe_delimited_config {
    std::string record_name, namespace_uri, separator, comment_prefix;
    // Grouped records (blank-line blocks such as CoNLL-U sentences).
    bool group_blank_lines = false;
    std::string group_attribute_separator;
    std::string skip_key_column, skip_key_characters;
    std::vector<recipe_delimited_reference> references;
    std::map<std::string, std::string> constants;
    std::string directive_prefix, directive_record_name;
    std::string range_column, range_separator, range_first_field, range_last_field;
    bool trim_fields = true;
    uint32_t minimum_columns = 0; // Zero requires the complete data-column schema.
    uint32_t header_lines = 0;    // leading lines naming the columns, not records
    bool allow_trailing_empty_column = false;
    std::vector<std::string> columns, directive_columns;
    // A line whose first field is a key here is read with that key's columns.
    std::map<std::string, std::vector<std::string>> keyed_columns;
    // Data the source keeps in comments (UTS 51 emoji-test): a record's trailing comment
    // as a column, fields captured from it by a pattern, and comment lines "key: value"
    // whose value holds for the records that follow.
    std::string comment_column, comment_pattern, state_separator;
    std::vector<std::string> comment_groups, state_keys;
};

class recipe_delimited_stream {
    std::string pending_;
    uint64_t line_ = 0;
    bool final_ = false;
    std::map<std::string, std::string> group_attributes_;
    std::vector<std::map<std::string, std::string>> group_rows_;
    // Rows skipped by key (multiword ranges, empty nodes) are not records, but
    // other rows may still point at them.
    std::vector<std::map<std::string, std::string>> group_skipped_;
    std::vector<std::vector<std::string>> group_lines_;
    std::vector<std::vector<std::string>> group_row_cells_;
    std::map<std::string, std::string> state_;       // comment-set values for later records
    std::unique_ptr<std::regex> comment_regex_;

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
        if (line_ <= config.header_lines) return;
        if (!text.empty() && text.back() == '\r') text.remove_suffix(1);
        auto nonspace = trim(text);
        if (nonspace.empty()) {
            if (config.group_blank_lines) flush_group(emit);
            return;
        }
        bool directive = false;
        if (!config.comment_prefix.empty() && nonspace.find(config.comment_prefix) == 0) {
            auto body = trim(nonspace.substr(config.comment_prefix.size()));
            if (config.group_blank_lines && !config.group_attribute_separator.empty()) {
                const auto split_at = body.find(config.group_attribute_separator);
                if (split_at != std::string_view::npos) {
                    auto key = trim(body.substr(0, split_at));
                    auto value = trim(body.substr(split_at + config.group_attribute_separator.size()));
                    if (!key.empty())
                        group_attributes_["group:" + std::string(key)] = std::string(value);
                    group_lines_.push_back({std::string(key), std::string(value)});
                } else if (!body.empty()) {
                    group_lines_.push_back({std::string(body)});
                }
                return;
            }
            if (!config.state_keys.empty()) {
                const auto split_at = body.find(config.state_separator);
                if (split_at != std::string_view::npos) {
                    const std::string key(trim(body.substr(0, split_at)));
                    for (const auto& declared : config.state_keys)
                        if (declared == key) {
                            state_[key] = std::string(trim(body.substr(split_at + config.state_separator.size())));
                            return;
                        }
                }
            }
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
        std::string comment_text;
        if (!config.comment_prefix.empty() && !config.group_blank_lines) {
            auto comment = text.find(config.comment_prefix);
            if (comment != std::string_view::npos) {
                comment_text = std::string(trim(text.substr(comment + config.comment_prefix.size())));
                text = text.substr(0, comment);
            }
        }
        const std::vector<std::string>* layout = directive ? &config.directive_columns : &config.columns;
        if (!directive && !config.keyed_columns.empty()) {
            const auto key = trim(text.substr(0, text.find(config.separator)));
            const auto keyed = config.keyed_columns.find(std::string(key));
            if (keyed != config.keyed_columns.end()) layout = &keyed->second;
        }
        const auto& columns = *layout;
        std::map<std::string, std::string> values;
        std::vector<std::string> cells;
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
            cells.emplace_back(value);
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
                } else if (!range->second.empty()) {
                    // One code point (or one sequence) is a range of itself.
                    if (!values.emplace(config.range_first_field, range->second).second
                        || !values.emplace(config.range_last_field, range->second).second)
                        fail("range endpoint collides with a declared column");
                }
            }
        }
        for (const auto& constant : config.constants)
            if (!values.emplace(constant.first, constant.second).second)
                fail("constant collides with a declared column: " + constant.first);
        if (!directive) {
            if (!config.comment_column.empty() && !comment_text.empty()
                && !values.emplace(config.comment_column, comment_text).second)
                fail("comment column collides with a declared column");
            if (!config.comment_pattern.empty() && !comment_text.empty()) {
                if (!comment_regex_) comment_regex_ = std::make_unique<std::regex>(config.comment_pattern);
                std::smatch match;
                if (std::regex_match(comment_text, match, *comment_regex_))
                    for (size_t g = 0; g < config.comment_groups.size() && g + 1 < match.size(); ++g)
                        if (match[g + 1].matched && !values.emplace(config.comment_groups[g], match[g + 1].str()).second)
                            fail("comment field collides with a declared column: " + config.comment_groups[g]);
            }
            for (const auto& state : state_)
                if (!values.emplace(state.first, state.second).second)
                    fail("comment state collides with a declared column: " + state.first);
        }
        if (config.group_blank_lines && !directive) {
            if (!config.skip_key_column.empty()) {
                const auto key = values.find(config.skip_key_column);
                if (key != values.end()
                    && key->second.find_first_of(config.skip_key_characters) != std::string::npos) {
                    group_skipped_.push_back(std::move(values));
                    group_lines_.push_back(std::move(cells));
                    return;
                }
            }
            group_rows_.push_back(std::move(values));
            group_lines_.push_back(cells);
            group_row_cells_.push_back(std::move(cells));
            return;
        }
        emit(directive ? config.directive_record_name : config.record_name,
             config.namespace_uri, std::move(values),
             recipe_delimited_structure{std::move(cells), nullptr});
    }

    std::string resolve_reference(const recipe_delimited_reference& ref,
        const std::map<std::string, const std::map<std::string, std::string>*>& by_key,
        const std::string& pointer) const {
        if (pointer == ref.root_value) return recipe_group_trunk_value;
        const auto row = by_key.find(pointer);
        if (row == by_key.end()) fail("in-group reference " + ref.column + "=" + pointer + " names no row");
        const auto target = row->second->find(ref.target_column);
        // A pointer at a row without surface content (an empty node) carries no
        // endpoint; the caller drops that item.
        if (target == row->second->end() || target->second.empty() || target->second == "_")
            return {};
        return target->second;
    }

    template<class Emit> void flush_group(Emit& emit) {
        if (group_rows_.empty()) {
            group_attributes_.clear(); group_skipped_.clear();
            group_lines_.clear(); group_row_cells_.clear();
            return;
        }
        auto group = std::make_shared<const std::vector<std::vector<std::string>>>(std::move(group_lines_));
        std::map<std::string, const std::map<std::string, std::string>*> by_key;
        for (const auto& ref : config.references) {
            for (const auto& row : group_rows_) {
                const auto key = row.find(ref.key_column);
                if (key != row.end()) by_key.emplace(key->second, &row);
            }
            for (const auto& row : group_skipped_) {
                const auto key = row.find(ref.key_column);
                if (key != row.end()) by_key.emplace(key->second, &row);
            }
        }
        std::vector<std::map<std::string, std::string>> resolved = group_rows_;
        for (size_t i = 0; i < resolved.size(); ++i) {
            auto& row = resolved[i];
            for (const auto& ref : config.references) {
                auto cell = row.find(ref.column);
                if (cell == row.end() || cell->second.empty() || cell->second == "_") continue;
                if (ref.pair_separator.empty()) {
                    // A head at a row without surface content has no endpoint, so
                    // the reference carries no testimony, exactly as a dropped pair item.
                    cell->second = resolve_reference(ref, by_key, cell->second);
                    continue;
                }
                std::string rebuilt;
                size_t start = 0;
                for (;;) {
                    const size_t end = cell->second.find(ref.pair_separator, start);
                    std::string item = cell->second.substr(start, end == std::string::npos ? end : end - start);
                    const size_t split_at = item.find(ref.pair_value_separator);
                    if (split_at == std::string::npos || split_at == 0 || split_at + ref.pair_value_separator.size() >= item.size())
                        fail("malformed reference pair in " + ref.column + ": " + item);
                    std::string target = resolve_reference(ref, by_key, item.substr(0, split_at));
                    if (!target.empty()) {
                        if (!rebuilt.empty()) rebuilt += recipe_pair_item_separator;
                        rebuilt += target;
                        rebuilt += recipe_pair_value_separator;
                        rebuilt += item.substr(split_at + ref.pair_value_separator.size());
                    }
                    if (end == std::string::npos) break;
                    start = end + ref.pair_separator.size();
                }
                cell->second = std::move(rebuilt);
            }
            for (const auto& attribute : group_attributes_) row.emplace(attribute.first, attribute.second);
            row["group:first"] = i == 0 ? "1" : "0";
            emit(config.record_name, config.namespace_uri, std::move(row),
                 recipe_delimited_structure{std::move(group_row_cells_[i]),
                     i + 1 == resolved.size() ? group : nullptr});
        }
        group_rows_.clear();
        group_skipped_.clear();
        group_attributes_.clear();
        group_lines_.clear();
        group_row_cells_.clear();
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
            if (config.group_blank_lines) flush_group(emit);
            final_ = true;
        }
    }
};
