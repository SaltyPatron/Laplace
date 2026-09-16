#pragma once

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <string_view>
#include "laplace/core/chess_perfcache_format.h"

namespace chess_emit {
using Record = laplace_chess_perfcache_record_t;
inline constexpr size_t maximum_line_bytes = 65536u;
/* Bounds controlled input/stdio buffers, composition scratch, merge heads,
 * hash states and fixed run metadata. Allocator/OS bookkeeping is not RSS. */
inline constexpr size_t fixed_memory_bytes = 256u * 1024u;
inline constexpr uint8_t atom_origin = 1u, move_origin = 2u, board_origin = 4u;

struct Input {
    hash128_t hash{};
    uint64_t bytes = 0;
    uint64_t occurrences = 0;
};
using LineVisitor = void (*)(std::string_view, void*);
Input scan(const std::filesystem::path& path, LineVisitor visitor = nullptr, void* context = nullptr);
hash128_t source_hash(const Input& primary, const Input* additional);
bool validate_blob(const std::filesystem::path& path, const hash128_t& source, uint64_t* count = nullptr);
uint64_t default_spill_bytes(uint64_t records);
struct Statistics {
    uint64_t records = 0, board_ids = 0, spill_peak_bytes = 0, runs_written = 0;
    size_t controlled_memory_bytes = 0;
};

/* Chunk memory and run inventory are bounded independently of corpus size.
 * Two-way leveled merges retain at most 64 run descriptors. Private workspaces
 * are removed on refusal; only a fully verified file replaces the destination. */
class Producer {
public:
    Producer(size_t memory_bytes, uint64_t maximum_spill_bytes,
             const std::filesystem::path& scratch_parent);
    ~Producer();
    Producer(const Producer&) = delete;
    Producer& operator=(const Producer&) = delete;
    void add(const Record& record, uint8_t origin);
    Statistics publish(const std::filesystem::path& output, const hash128_t& source);
private:
    class Impl;
    std::unique_ptr<Impl> impl_;
};
} // namespace chess_emit
