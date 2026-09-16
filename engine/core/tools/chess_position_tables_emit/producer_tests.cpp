/* Native producer contract. The finite alphabet is deliberately materialized
 * here only as a small independent v1 serialization reference; production
 * never builds a corpus-sized reference vector. No PostgreSQL admission claim. */
#define LAPLACE_CHESS_PRODUCER_TESTING
#include "main.cpp"
#include <iterator>
#include <random>
#include "laplace/core/chess_position_table.h"

namespace {
namespace fs = std::filesystem;
unsigned checks = 0;
void check(bool value, const char* message) {
    ++checks;
    if (!value) throw std::runtime_error(message);
}
template<class Function> void refuses(Function function, const char* fragment = nullptr) {
    bool failed = false;
    try { function(); }
    catch (const std::exception& error) {
        failed = true;
        if (fragment) check(std::string_view(error.what()).find(fragment) != std::string_view::npos,
                            "refusal did not identify the expected cause");
    }
    check(failed, "expected producer refusal");
}
class Fixture {
public:
    Fixture() {
        std::random_device random;
        for (unsigned i = 0; i < 64; ++i) {
            root = fs::current_path() / ("laplace-chess-producer-test-" + std::to_string(random()));
            if (fs::create_directory(root)) return;
        }
        throw std::runtime_error("cannot create fixture directory");
    }
    ~Fixture() { std::error_code ignored; fs::remove_all(root, ignored); }
    fs::path root;
};
std::vector<uint8_t> read(const fs::path& path) {
    std::ifstream file(path, std::ios::binary);
    check(static_cast<bool>(file), "cannot read fixture");
    return {std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
}
void write(const fs::path& path, const std::vector<uint8_t>& bytes) {
    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    file.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    file.close(); check(static_cast<bool>(file), "cannot write fixture");
}
void write(const fs::path& path, const std::string& text) {
    write(path, std::vector<uint8_t>(text.begin(), text.end()));
}
void put(std::vector<uint8_t>& bytes, uint64_t value, unsigned width) {
    for (unsigned i = 0; i < width; ++i) bytes.push_back(static_cast<uint8_t>(value >> (8u * i)));
}
void append(std::vector<uint8_t>& bytes, const void* data, size_t count) {
    const auto* first = static_cast<const uint8_t*>(data);
    bytes.insert(bytes.end(), first, first + count);
}
hash128_t legacy_source_hash(const std::string& input) {
    hash128_t body;
    hash128_blake3(reinterpret_cast<const uint8_t*>(input.data()), input.size(), &body);
    std::vector<uint8_t> bytes;
    append(bytes, &body, sizeof(body));
    constexpr char tag[] = LAPLACE_CHESS_PERFCACHE_GENERATOR_TAG;
    constexpr char alphabet[] = "typed-board-and-move-alphabet/v3";
    constexpr char scope[] = "catalog";
    append(bytes, tag, sizeof(tag)); append(bytes, alphabet, sizeof(alphabet));
    append(bytes, scope, sizeof(scope) - 1u);
    hash128_t result; hash128_blake3(bytes.data(), bytes.size(), &result); return result;
}
std::vector<uint8_t> legacy_blob(std::vector<chess_emit::Record> records, const hash128_t& source) {
    std::sort(records.begin(), records.end(), [](const auto& a, const auto& b) {
        return hash128_compare(&a.id, &b.id) < 0;
    });
    records.erase(std::unique(records.begin(), records.end(), [](const auto& a, const auto& b) {
        if (!hash128_equals(&a.id, &b.id)) return false;
        check(std::memcmp(&a, &b, sizeof(a)) == 0, "reference fixture conflicts");
        return true;
    }), records.end());
    std::vector<uint8_t> bytes;
    put(bytes, LAPLACE_CHESS_PERFCACHE_MAGIC, 4); put(bytes, 1u, 4);
    put(bytes, records.size(), 8); put(bytes, 80u, 8); put(bytes, 128u, 8);
    append(bytes, &source, sizeof(source));
    char scope[16]{}; std::memcpy(scope, "catalog", 7);
    append(bytes, scope, sizeof(scope)); bytes.resize(128u, 0u);
    for (const auto& record : records) append(bytes, &record, sizeof(record));
    hash128_t digest; hash128_blake3(bytes.data(), bytes.size(), &digest);
    append(bytes, &digest, sizeof(digest));
    return bytes;
}
chess_emit::Record record(unsigned index) {
    chess_emit::Record result{};
    const std::string content = "controlled-record-" + std::to_string(index);
    hash128_blake3_str(content.c_str(), &result.id);
    result.n = 5; result.tier = 2;
    hilbert4d_encode(result.coord, &result.hilbert);
    return result;
}
void empty_scratch(const fs::path& path) {
    check(!fs::exists(path) || fs::is_empty(path), "owned temporary workspace was not removed");
}
void run_controls() {
    Fixture fixture;
    const fs::path input_path = fixture.root / "positions.txt";
    const fs::path output_path = fixture.root / "positions.bin";
    const fs::path scratch = fixture.root / "sort";
    const std::string a = "stm:w cr:- ep:- Ke1 ke8 Pe2";
    const std::string alias = "stm:w cr:- ep:- Pe2 ke8 Ke1";
    const std::string b = "rules:chess960 stm:b cr:AHah ep:- Ra1 Ke1 Rh1 ra8 ke8 rh8";
    const std::string input = a + "\r\n" + a + "\n" + alias + "\n" + b; // unterminated final line
    write(input_path, input);
    const auto observed = chess_emit::scan(input_path);
    check(observed.bytes == input.size() && observed.occurrences == 4, "input byte/occurrence accounting");
    const hash128_t source = legacy_source_hash(input);
    const hash128_t streamed_source = chess_emit::source_hash(observed, nullptr);
    check(hash128_equals(&source, &streamed_source), "legacy source hash changed");

    super_fibonacci(128, g_byte_coords);
    std::vector<chess_emit::Record> reference;
    check(emit_tier1_alphabet(reference) == 0 && reference.size() == 1001u, "finite atom coverage");
    check(emit_move_alphabet(reference) == 0 && reference.size() == 230377u, "finite move coverage");
    const size_t alphabet_size = reference.size();
    for (const auto& line : {a, a, alias, b}) {
        chess_emit::Record item{};
        check(compose_position(line, &item) == 0, "valid canonical surface refused");
        reference.push_back(item);
    }
    const size_t memory = chess_emit::fixed_memory_bytes + 4096u * 88u;
    const uint64_t spill = chess_emit::default_spill_bytes(reference.size());
    chess_emit::Statistics stats;
    {
        chess_emit::Producer producer(memory, spill, scratch);
        for (size_t i = reference.size(); i > 0u; --i) {
            const size_t at = i - 1u;
            const uint8_t origin = at >= alphabet_size ? chess_emit::board_origin :
                at >= 1001u ? chess_emit::move_origin : chess_emit::atom_origin;
            producer.add(reference[at], origin);
        }
        stats = producer.publish(output_path, source);
    }
    const auto expected = legacy_blob(reference, source);
    check(read(output_path) == expected, "bounded producer differs from complete legacy v1 bytes");
    check(stats.records == alphabet_size + 2u && stats.board_ids == 2u, "typed record origin counts");
    check(stats.runs_written > 64u, "fixture did not exercise leveled multi-run merging");
    check(stats.controlled_memory_bytes <= memory && stats.spill_peak_bytes <= spill, "resource ceiling violated");
    empty_scratch(scratch);
    check(chess_position_table_load(output_path.string().c_str()) == 0, "ordinary runtime loader refused output");
    uint64_t loaded = 0;
    check(chess_position_table_record_count(&loaded) == 0 && loaded == stats.records, "runtime count mismatch");
    for (size_t i = alphabet_size; i < reference.size(); ++i) {
        const auto* found = chess_position_table_lookup(&reference[i].id);
        check(found && std::memcmp(found, &reference[i], sizeof(*found)) == 0, "ordinary runtime lookup mismatch");
    }
    chess_position_table_unload();

    std::array<std::string, 6> arguments{"producer", "--surfaces", input_path.string(),
                                        "--output", output_path.string(), "--verify-existing"};
    std::array<char*, 6> argv{};
    for (size_t i = 0; i < argv.size(); ++i) argv[i] = arguments[i].data();
    Cli cli = parse_cli(static_cast<int>(argv.size()), argv.data());
    const auto timestamp = fs::last_write_time(output_path);
    check(emit(cli) == 0 && fs::last_write_time(output_path) == timestamp, "verification rewrote existing file");
    cli.verify_existing = false;
    check(emit(cli) == 0 && fs::last_write_time(output_path) == timestamp, "verified skip rewrote existing file");
    write(input_path, input + "\n" + a);
    cli.verify_existing = true;
    refuses([&] { emit(cli); }, "does not match");
    check(read(output_path) == expected, "source-mismatch verification changed destination");
    write(input_path, input);

    const auto extra_path = fixture.root / "extra.txt";
    write(extra_path, a + "\n");
    const auto extra = chess_emit::scan(extra_path);
    const auto pair_hash = chess_emit::source_hash(observed, &extra);
    check(!hash128_equals(&source, &pair_hash), "additional input was not bound");
    cli.additional_surfaces = extra_path.string();
    refuses([&] { emit(cli); }, "does not match");
    const auto empty_extra = chess_emit::scan(fs::path{});
    const auto empty_pair = chess_emit::source_hash(observed, &empty_extra);
    check(!hash128_equals(&source, &empty_pair), "present empty additional input collided with absence");

    // Exercise both streamed input callbacks through publication, using the
    // already composed finite alphabet as the independent bounded test reference.
    {
        chess_emit::Producer producer(memory, spill, scratch);
        for (size_t i = 0; i < alphabet_size; ++i)
            producer.add(reference[i], i < 1001u ? chess_emit::atom_origin : chess_emit::move_origin);
        const auto primary_replay = chess_emit::scan(input_path, compose_surface, &producer);
        const auto additional_replay = chess_emit::scan(extra_path, compose_surface, &producer);
        check(primary_replay.occurrences == 4u && additional_replay.occurrences == 1u,
              "two input streams lost their occurrence counts");
        const auto pair_stats = producer.publish(output_path, pair_hash);
        check(pair_stats.board_ids == 2u && pair_stats.records == alphabet_size + 2u,
              "additional duplicates changed unique board coverage");
    }
    check(read(output_path) == legacy_blob(reference, pair_hash), "additional input v1 content mismatch");
    check(emit(cli) == 0, "verification refused authenticated two-input output");
    write(extra_path, b + "\n");
    refuses([&] { emit(cli); }, "does not match");
    write(output_path, expected);

    // Verification authenticates the whole file, not just its matching header.
    auto corrupt = expected; corrupt[128u + 16u] ^= 1u;
    write(output_path, corrupt);
    cli.additional_surfaces.clear();
    refuses([&] { emit(cli); }, "does not match");
    check(read(output_path) == corrupt, "verification changed corrupt input");
    write(output_path, expected);
    for (size_t cut : {size_t{0}, size_t{127}, expected.size() - 1u}) {
        write(fixture.root / "truncated.bin", std::vector<uint8_t>(expected.begin(), expected.begin() + cut));
        check(!chess_emit::validate_blob(fixture.root / "truncated.bin", source), "truncated blob accepted");
    }

    const size_t tiny_memory = chess_emit::fixed_memory_bytes + 2u * 88u;
    const auto first = record(1), second = record(2), third = record(3);
    auto conflict = first; conflict.coord[0] = 0.125;
    hilbert4d_encode(conflict.coord, &conflict.hilbert);
    {
        chess_emit::Producer producer(tiny_memory, 65536u, scratch);
        producer.add(first, chess_emit::board_origin);
        refuses([&] { producer.add(conflict, chess_emit::board_origin); }, "conflicting");
    }
    empty_scratch(scratch);
    {
        chess_emit::Producer producer(tiny_memory, 65536u, scratch);
        producer.add(first, chess_emit::board_origin); producer.add(second, chess_emit::board_origin);
        producer.add(conflict, chess_emit::board_origin);
        refuses([&] { producer.add(third, chess_emit::board_origin); }, "conflicting");
    }
    empty_scratch(scratch);
    {
        chess_emit::Producer producer(tiny_memory, 1u, scratch);
        producer.add(first, chess_emit::board_origin);
        refuses([&] { producer.publish(output_path, source); }, "spill-byte grant");
    }
    empty_scratch(scratch);
    check(read(output_path) == expected, "refused work replaced prior cache");
    refuses([&] { chess_emit::Producer producer(chess_emit::fixed_memory_bytes, 65536u, scratch); }, "memory grant");
    empty_scratch(scratch);

    // Actual spill corruption must be detected before publication.
    {
        chess_emit::Producer producer(tiny_memory, 65536u, scratch);
        producer.add(first, chess_emit::board_origin); producer.add(second, chess_emit::board_origin);
        const auto workspace = *fs::directory_iterator(scratch);
        const auto run = *fs::directory_iterator(workspace.path());
        auto damaged = read(run.path()); damaged[16u] ^= 1u; write(run.path(), damaged);
        refuses([&] { producer.publish(output_path, source); }, "checksum");
    }
    empty_scratch(scratch);
    check(read(output_path) == expected, "corrupt spill replaced prior cache");

    const auto blocked = fixture.root / "blocked";
    fs::create_directory(blocked); write(blocked / "keep", std::string("owned fixture"));
    {
        chess_emit::Producer producer(tiny_memory, 65536u, scratch);
        producer.add(first, chess_emit::board_origin);
        refuses([&] { producer.publish(blocked, source); }, "replacement");
    }
    empty_scratch(scratch);
    check(read(blocked / "keep") == std::vector<uint8_t>({'o','w','n','e','d',' ','f','i','x','t','u','r','e'}),
          "failed atomic replacement damaged destination directory");

    write(extra_path, std::string(chess_emit::maximum_line_bytes + 1u, 'x'));
    refuses([&] { chess_emit::scan(extra_path); }, "maximum line");
    chess_emit::Record invalid{};
    check(compose_position("stm:w cr:- ep:- Ke1 ke8 Ke1", &invalid) != 0, "duplicate square accepted");
    check(compose_position("stm:w cr:- ep:- unexpected-token", &invalid) != 0, "unknown token ignored");
    check(compose_position("rules: stm:w cr:- ep:-", &invalid) != 0, "empty rule identity accepted");
    refuses([&] { positive_integer("18446744073709551616"); }, "representable");
    refuses([&] { positive_integer("-1"); }, "representable");
    refuses([&] { positive_integer("0"); }, "positive");
}
} // namespace

int main() {
    try {
        run_controls();
        std::fprintf(stderr, "chess position producer: %u native contract checks passed\n", checks);
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "chess position producer contract failed: %s\n", error.what());
        return 1;
    }
}
