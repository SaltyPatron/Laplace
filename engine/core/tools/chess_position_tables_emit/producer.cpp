#include "producer.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cerrno>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <limits>
#include <random>
#include <stdexcept>
#include <string>
#include <system_error>
#include "blake3.h"
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <io.h>
#else
#include <unistd.h>
#endif

namespace chess_emit {
namespace fs = std::filesystem;
namespace {
static_assert(std::endian::native == std::endian::little,
              "the v1 native chess record layout requires a little-endian target");
constexpr size_t io_bytes = 8192u;
constexpr uint64_t trailer_bytes = sizeof(hash128_t);
constexpr size_t levels = 64u;

void require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}
uint64_t checked_add(uint64_t a, uint64_t b) {
    require(b <= UINT64_MAX - a, "producer byte/count overflow");
    return a + b;
}
uint64_t multiply(uint64_t a, uint64_t b) {
    require(a == 0u || b <= UINT64_MAX / a, "producer byte/count overflow");
    return a * b;
}
struct Digest {
    blake3_hasher state;
    Digest() { blake3_hasher_init(&state); }
    void update(const void* bytes, size_t count) {
        if (count) blake3_hasher_update(&state, bytes, count);
    }
    hash128_t finish() const {
        hash128_t result;
        blake3_hasher_finalize(&state, reinterpret_cast<uint8_t*>(&result), sizeof(result));
        return result;
    }
};
class File {
public:
    File(const fs::path& path, const char* mode) {
#ifdef _WIN32
        const std::wstring wide_mode(mode, mode + std::strlen(mode));
        file_ = _wfopen(path.c_str(), wide_mode.c_str());
#else
        file_ = std::fopen(path.c_str(), mode);
#endif
        require(file_ != nullptr, "cannot open producer input or temporary file");
        if (std::setvbuf(file_, buffer_.data(), _IOFBF, buffer_.size()) != 0) {
            std::fclose(file_); file_ = nullptr;
            throw std::runtime_error("cannot set bounded file buffer");
        }
    }
    ~File() { if (file_) std::fclose(file_); }
    File(const File&) = delete;
    File& operator=(const File&) = delete;
    size_t read(void* data, size_t count) {
        const size_t got = std::fread(data, 1u, count, file_);
        require(!std::ferror(file_), "producer input read failed");
        return got;
    }
    void exact(void* data, size_t count) {
        require(read(data, count) == count, "truncated producer file");
    }
    void write(const void* data, size_t count) {
        require(std::fwrite(data, 1u, count, file_) == count, "producer output write failed");
    }
    void close(bool durable = false) {
        require(std::fflush(file_) == 0, "producer output flush failed");
        if (durable) {
#ifdef _WIN32
            require(_commit(_fileno(file_)) == 0, "producer output sync failed");
#else
            require(fsync(fileno(file_)) == 0, "producer output sync failed");
#endif
        }
        FILE* closing = file_; file_ = nullptr;
        require(std::fclose(closing) == 0, "producer output close failed");
    }
private:
    FILE* file_ = nullptr;
    std::array<char, io_bytes> buffer_{};
};
class Directory {
public:
    explicit Directory(const fs::path& parent) {
        require(parent.native().size() <= 4096u, "producer path exceeds its bounded envelope");
        fs::create_directories(parent);
        std::random_device random;
        for (unsigned attempt = 0; attempt < 64u; ++attempt) {
            path_ = parent / (".laplace-chess-" + std::to_string(random()) + "-" + std::to_string(random()));
            if (fs::create_directory(path_)) {
                std::error_code error;
                fs::permissions(path_, fs::perms::owner_all, fs::perm_options::replace, error);
                if (error) { fs::remove(path_); throw std::runtime_error("cannot restrict private producer workspace"); }
                return;
            }
        }
        throw std::runtime_error("cannot create unique producer workspace");
    }
    ~Directory() { std::error_code ignored; if (!path_.empty()) fs::remove_all(path_, ignored); }
    const fs::path& path() const { return path_; }
    void remove_empty() {
        require(fs::remove(path_), "cannot remove completed producer workspace");
        path_.clear();
    }
private:
    fs::path path_;
};
struct Ledger {
    uint64_t limit, live = 0, peak = 0;
    void reserve(uint64_t bytes) {
        require(bytes <= limit - live, "producer spill-byte grant exhausted");
        live += bytes; peak = std::max(peak, live);
    }
    void release(uint64_t bytes) {
        require(bytes <= live, "producer spill accounting mismatch");
        live -= bytes;
    }
};
struct Entry {
    Record record{};
    uint8_t origins = 0;
    uint8_t reserved[7]{};
};
static_assert(sizeof(Entry) == 88u);
bool before(const Entry& a, const Entry& b) { return hash128_compare(&a.record.id, &b.record.id) < 0; }
void combine(Entry& a, const Entry& b) {
    require(std::memcmp(&a.record, &b.record, sizeof(Record)) == 0,
            "same chess identity has conflicting full record content");
    a.origins |= b.origins;
}
bool valid_record(const Record& record) {
    if ((record.tier != 1u && record.tier != 2u) || record.n == 0u) return false;
    for (double value : record.coord) if (!std::isfinite(value)) return false;
    for (uint8_t value : record._pad) if (value) return false;
    for (uint8_t value : record.reserved) if (value) return false;
    return true;
}
struct Run {
    uint64_t serial = 0, count = 0;
    bool present = false;
    uint64_t bytes() const { return checked_add(multiply(count, sizeof(Entry)), trailer_bytes); }
};
fs::path run_path(const Directory& directory, const Run& run) {
    return directory.path() / ("run-" + std::to_string(run.serial));
}
class RunWriter {
public:
    RunWriter(const fs::path& path, Ledger& ledger) : file_(path, "wb"), ledger_(ledger) {}
    void append(const Entry& entry) {
        ledger_.reserve(sizeof(entry));
        file_.write(&entry, sizeof(entry)); digest_.update(&entry, sizeof(entry));
        count_ = checked_add(count_, 1u);
    }
    uint64_t finish() {
        const hash128_t hash = digest_.finish();
        ledger_.reserve(sizeof(hash)); file_.write(&hash, sizeof(hash)); file_.close();
        return count_;
    }
private:
    File file_;
    Ledger& ledger_;
    Digest digest_;
    uint64_t count_ = 0;
};
class RunReader {
public:
    RunReader(const fs::path& path, const Run& run) : file_(path, "rb"), left_(run.count) {
        require(fs::file_size(path) == run.bytes(), "sort run length differs from its retained count");
    }
    bool next(Entry& entry) {
        if (!left_) {
            if (!verified_) {
                hash128_t stored;
                file_.exact(&stored, sizeof(stored));
                const auto computed = digest_.finish();
                require(hash128_equals(&computed, &stored), "sort run checksum mismatch");
                uint8_t extra;
                require(file_.read(&extra, 1u) == 0u, "sort run has trailing bytes");
                verified_ = true;
            }
            return false;
        }
        file_.exact(&entry, sizeof(entry)); digest_.update(&entry, sizeof(entry));
        require(valid_record(entry.record) && entry.origins && !(entry.origins & ~7u),
                "sort run contains invalid record metadata");
        for (uint8_t byte : entry.reserved) require(byte == 0u, "sort run padding is not canonical");
        require(!have_previous_ || hash128_compare(&previous_, &entry.record.id) < 0,
                "sort run is not strictly ordered");
        previous_ = entry.record.id; have_previous_ = true; --left_;
        return true;
    }
private:
    File file_;
    Digest digest_;
    hash128_t previous_{};
    uint64_t left_;
    bool have_previous_ = false, verified_ = false;
};
laplace_chess_perfcache_header_t header(const hash128_t& source, uint64_t count) {
    laplace_chess_perfcache_header_t value{};
    value.magic = LAPLACE_CHESS_PERFCACHE_MAGIC;
    value.format_version = LAPLACE_CHESS_PERFCACHE_VERSION;
    value.record_count = count;
    value.record_size = sizeof(Record);
    value.records_offset = sizeof(value);
    value.source_hash = source;
    std::memcpy(value.scope, "catalog", 7u);
    return value;
}
void replace(const fs::path& temporary, const fs::path& output) {
#ifdef _WIN32
    require(MoveFileExW(temporary.c_str(), output.c_str(),
                       MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != 0,
            "atomic chess cache replacement failed");
#else
    require(::rename(temporary.c_str(), output.c_str()) == 0, "atomic chess cache replacement failed");
#endif
}
} // namespace

Input scan(const fs::path& path, LineVisitor visitor, void* context) {
    Input result;
    Digest digest;
    if (path.empty()) { result.hash = digest.finish(); return result; }
    require(path.native().size() <= 4096u, "surface path exceeds its bounded envelope");
    File input(path, "rb");
    std::array<char, maximum_line_bytes> line{};
    std::array<char, 65536u> block{};
    size_t used = 0;
    auto emit = [&]() {
        size_t length = used;
        if (length && line[length - 1u] == '\r') --length;
        if (length) {
            result.occurrences = checked_add(result.occurrences, 1u);
            if (visitor) visitor(std::string_view(line.data(), length), context);
        }
        used = 0;
    };
    for (;;) {
        const size_t count = input.read(block.data(), block.size());
        if (!count) break;
        digest.update(block.data(), count); result.bytes = checked_add(result.bytes, count);
        for (size_t i = 0; i < count; ++i) {
            const char value = block[i];
            if (value == '\n') emit();
            else {
                require(value != '\0', "surface contains a NUL byte");
                require(used < line.size(), "surface exceeds maximum line bytes");
                line[used++] = value;
            }
        }
    }
    if (used) emit();
    result.hash = digest.finish();
    return result;
}

hash128_t source_hash(const Input& primary, const Input* additional) {
    Digest original;
    original.update(&primary.hash, sizeof(primary.hash));
    constexpr char tag[] = LAPLACE_CHESS_PERFCACHE_GENERATOR_TAG;
    constexpr char alphabet[] = "typed-board-and-move-alphabet/v3";
    constexpr char scope[] = "catalog";
    original.update(tag, sizeof(tag)); original.update(alphabet, sizeof(alphabet));
    original.update(scope, sizeof(scope) - 1u);
    const hash128_t old = original.finish();
    if (!additional) return old;
    Digest combined;
    constexpr char domain[] = "chess_position_perfcache/additional-surfaces/v1";
    combined.update(domain, sizeof(domain));
    combined.update(&old, sizeof(old));
    combined.update(&primary.bytes, sizeof(primary.bytes));
    combined.update(&additional->hash, sizeof(additional->hash));
    combined.update(&additional->bytes, sizeof(additional->bytes));
    return combined.finish();
}

bool validate_blob(const fs::path& path, const hash128_t& source, uint64_t* count) {
    if (count) *count = 0u;
    try {
        const uint64_t bytes = fs::file_size(path);
        if (bytes < sizeof(laplace_chess_perfcache_header_t) + trailer_bytes) return false;
        File input(path, "rb");
        laplace_chess_perfcache_header_t observed;
        input.exact(&observed, sizeof(observed));
        const auto expected = header(source, observed.record_count);
        if (std::memcmp(&observed, &expected, sizeof(expected)) != 0) return false;
        const uint64_t payload = bytes - sizeof(observed) - trailer_bytes;
        if (payload % sizeof(Record) || observed.record_count != payload / sizeof(Record)) return false;
        Digest digest; digest.update(&observed, sizeof(observed));
        hash128_t previous{};
        for (uint64_t i = 0; i < observed.record_count; ++i) {
            Record record;
            input.exact(&record, sizeof(record)); digest.update(&record, sizeof(record));
            if (!valid_record(record) || (i && hash128_compare(&previous, &record.id) >= 0)) return false;
            previous = record.id;
        }
        hash128_t stored;
        input.exact(&stored, sizeof(stored));
        const auto computed = digest.finish();
        uint8_t extra;
        if (!hash128_equals(&computed, &stored) || input.read(&extra, 1u)) return false;
        if (count) *count = observed.record_count;
        return true;
    } catch (const std::exception&) { return false; }
}

uint64_t default_spill_bytes(uint64_t records) {
    return checked_add(multiply(multiply(records, sizeof(Entry)), 3u), 4096u);
}

class Producer::Impl {
public:
    Impl(size_t memory, uint64_t spill, const fs::path& parent)
        : workspace(parent), ledger{spill} {
        require(memory >= fixed_memory_bytes + 2u * sizeof(Entry), "producer memory grant is too small");
        require(spill > 0u, "producer spill grant must be positive");
        capacity = (memory - fixed_memory_bytes) / sizeof(Entry);
        buffer = std::make_unique<Entry[]>(capacity);
        stats.controlled_memory_bytes = fixed_memory_bytes + capacity * sizeof(Entry);
    }
    void push(const Record& record, uint8_t origin) {
        require(!finished && valid_record(record) && origin && !(origin & ~7u), "invalid producer record");
        buffer[used] = Entry{}; buffer[used].record = record; buffer[used].origins = origin;
        if (++used == capacity) flush();
    }
    Run next_run() {
        require(serial != UINT64_MAX, "sort run identifier overflow");
        return Run{++serial, 0u, true};
    }
    void erase(const Run& run) {
        require(fs::remove(run_path(workspace, run)), "cannot retire consumed sort run");
        ledger.release(run.bytes());
    }
    Run merge(const Run& a, const Run& b) {
        Run output = next_run();
        {
            RunReader left(run_path(workspace, a), a), right(run_path(workspace, b), b);
            RunWriter writer(run_path(workspace, output), ledger);
            Entry x{}, y{};
            bool have_x = left.next(x), have_y = right.next(y);
            while (have_x || have_y) {
                if (!have_y || (have_x && before(x, y))) {
                    writer.append(x); have_x = left.next(x);
                } else if (!have_x || before(y, x)) {
                    writer.append(y); have_y = right.next(y);
                } else {
                    combine(x, y); writer.append(x);
                    have_x = left.next(x); have_y = right.next(y);
                }
            }
            output.count = writer.finish();
        }
        ++stats.runs_written;
        erase(a); erase(b);
        return output;
    }
    void flush() {
        if (!used) return;
        std::sort(buffer.get(), buffer.get() + used, before);
        size_t unique = 0u;
        for (size_t i = 0; i < used; ++i) {
            if (unique && hash128_equals(&buffer[unique - 1u].record.id, &buffer[i].record.id))
                combine(buffer[unique - 1u], buffer[i]);
            else buffer[unique++] = buffer[i];
        }
        Run run = next_run();
        {
            RunWriter writer(run_path(workspace, run), ledger);
            for (size_t i = 0; i < unique; ++i) writer.append(buffer[i]);
            run.count = writer.finish();
        }
        ++stats.runs_written; used = 0;
        for (size_t level = 0; level < slots.size(); ++level) {
            if (!slots[level].present) { slots[level] = run; return; }
            run = merge(slots[level], run); slots[level] = Run{};
        }
        throw std::runtime_error("producer run count exceeds uint64 envelope");
    }
    Statistics publish(const fs::path& destination, const hash128_t& source) {
        require(!finished, "producer already published");
        require(destination.native().size() <= 4096u, "output path exceeds its bounded envelope");
        flush();
        Run final;
        for (auto& slot : slots) if (slot.present) {
            final = final.present ? merge(final, slot) : slot;
            slot = Run{};
        }
        const fs::path parent = destination.has_parent_path() ? destination.parent_path() : fs::path(".");
        Directory publication(parent);
        const fs::path temporary = publication.path() / "cache.bin";
        const auto prefix = header(source, final.count);
        {
            File output(temporary, "wb");
            Digest digest;
            auto write = [&](const void* bytes, size_t size) {
                ledger.reserve(size); output.write(bytes, size); digest.update(bytes, size);
            };
            write(&prefix, sizeof(prefix));
            if (final.present) {
                RunReader reader(run_path(workspace, final), final);
                Entry entry{};
                while (reader.next(entry)) {
                    write(&entry.record, sizeof(entry.record));
                    if (entry.origins & board_origin) stats.board_ids = checked_add(stats.board_ids, 1u);
                }
            }
            const hash128_t checksum = digest.finish();
            ledger.reserve(sizeof(checksum)); output.write(&checksum, sizeof(checksum));
            output.close(true);
        }
        uint64_t verified_count = 0;
        require(validate_blob(temporary, source, &verified_count) && verified_count == final.count,
                "completed chess blob failed full validation");
        if (final.present) erase(final);
        workspace.remove_empty();
        stats.records = final.count; stats.spill_peak_bytes = ledger.peak;
        replace(temporary, destination);
        ledger.release(checked_add(sizeof(prefix) + trailer_bytes, multiply(final.count, sizeof(Record))));
        require(ledger.live == 0u, "producer retained unexpected scratch bytes");
        finished = true;
        return stats;
    }
    Directory workspace;
    Ledger ledger;
    std::unique_ptr<Entry[]> buffer;
    std::array<Run, levels> slots{};
    size_t used = 0, capacity = 0;
    uint64_t serial = 0;
    bool finished = false;
    Statistics stats{};
};

Producer::Producer(size_t memory, uint64_t spill, const fs::path& scratch)
    : impl_(std::make_unique<Impl>(memory, spill, scratch)) {}
Producer::~Producer() = default;
void Producer::add(const Record& record, uint8_t origin) { impl_->push(record, origin); }
Statistics Producer::publish(const fs::path& output, const hash128_t& source) {
    return impl_->publish(output, source);
}
} // namespace chess_emit
