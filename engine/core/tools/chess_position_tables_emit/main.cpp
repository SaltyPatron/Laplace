/*
 * Build-time emit for laplace_chess_position_perfcache.bin (GH #822 / spec 33).
 *
 * Peer of laplace_ucd_tables_emit in ROLE (declared inputs → deterministic blob), but
 * chess identity is typed binary structure and has no dependency on the text/codepoint floor.
 *
 * Always emits the finite typed board-state atom alphabet. Optionally composes
 * additional tier-2 boards from catalog interchange surfaces; the surface itself
 * never participates in identity.
 *
 * Inputs:  --output + optional --surfaces/--additional-surfaces; bounded memory/spill
 * Verification: --verify-existing checks the same inputs and complete blob without writing
 * Output:  sorted id → coord/hilbert/n/tier + BLAKE3 trailer
 *
 * Runtime load: chess_position_table_load. Not a managed catalog walker.
 * Not a Postgres testimony dump. Not Glicko.
 */

#include <algorithm>
#include <array>
#include <filesystem>
#include <limits>
#include <sstream>
#include <stdexcept>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <string>
#include <string_view>
#include <vector>

#include "producer.hpp"
#include "laplace/core/chess_perfcache_format.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/math4d.h"
#include "laplace/core/super_fibonacci.h"

static const uint8_t kSubstructureTier = 1;
static const uint8_t kPositionTier = 2;
static const uint8_t kSideDomain = 1;
static const uint8_t kCastlingDomain = 2;
static const uint8_t kEnPassantDomain = 3;
static const uint8_t kPieceSquareDomain = 4;
static const uint8_t kRulesDomain = 5;
static const uint8_t kCastlingRookOverrideDomain = 6;
static const uint8_t kMovePieceDomain = 16;
static const uint8_t kMoveFromDomain = 17;
static const uint8_t kMoveToDomain = 18;
static const uint8_t kMoveFlagsDomain = 19;
static const uint8_t kMovePromotionDomain = 20;

static const uint16_t kMoveDoublePush = 1;
static const uint16_t kMoveEnPassant = 2;
static const uint16_t kMoveCastle = 4;
static const uint16_t kMovePromotion = 8;

static double g_byte_coords[128 * 4];

struct Cli {
    std::string surfaces;
    std::string additional_surfaces;
    std::string output;
    std::string scratch;
    bool verify_existing = false;
    size_t memory_bytes = 64u * 1024u * 1024u;
    uint64_t maximum_spill_bytes = 0; // Unspecified: derive a finite bound from observed input occurrences.
};
struct CliError : std::runtime_error {
    int code;
    CliError(int value, const std::string& message) : std::runtime_error(message), code(value) {}
};
static uint64_t positive_integer(std::string_view value) {
    if (value.empty()) throw CliError(2, "resource bound needs a positive decimal integer");
    uint64_t result = 0;
    for (char digit : value) {
        if (digit < '0' || digit > '9' || result > (UINT64_MAX - static_cast<unsigned>(digit - '0')) / 10u)
            throw CliError(2, "resource bound is not a representable positive decimal integer");
        result = result * 10u + static_cast<unsigned>(digit - '0');
    }
    if (!result) throw CliError(2, "resource bound must be positive");
    return result;
}
static Cli parse_cli(int argc, char** argv) {
    Cli c;
    for (int i = 1; i < argc; ++i) {
        std::string_view a = argv[i];
        auto nx = [&]() -> std::string {
            if (i + 1 >= argc) throw CliError(2, std::string(argv[i]) + " needs value");
            return argv[++i];
        };
        if (a == "--surfaces") c.surfaces = nx();
        else if (a == "--additional-surfaces") c.additional_surfaces = nx();
        else if (a == "--output") c.output = nx();
        else if (a == "--scratch-dir") c.scratch = nx();
        else if (a == "--verify-existing") c.verify_existing = true;
        else if (a == "--memory-bytes") {
            const uint64_t value = positive_integer(nx());
            if (value > SIZE_MAX) throw CliError(2, "memory bound exceeds this native address window");
            c.memory_bytes = static_cast<size_t>(value);
        } else if (a == "--maximum-spill-bytes") c.maximum_spill_bytes = positive_integer(nx());
        else if (a == "--from-db")
            throw CliError(3, "--from-db testimony geometry is not the deterministic typed compose floor");
        else throw CliError(2, "unknown arg " + std::string(a));
    }
    if (c.output.empty()) throw CliError(2, "required: --output [--surfaces] [--additional-surfaces]");
    if (c.memory_bytes < chess_emit::fixed_memory_bytes + 176u)
        throw CliError(2, "memory grant must hold fixed producer buffers and at least two sort records");
    return c;
}

struct atom_t {
    uint8_t domain{};
    uint16_t value{};
    hash128_t digest{};
    bool has_digest{};
};

static void compose_atom(const atom_t& atom, hash128_t* out_id, double out_coord[4]) {
    std::array<uint8_t, 33> bytes{};
    size_t count = 0;
    bytes[count++] = static_cast<uint8_t>(0x80u + atom.domain);
    auto append = [&](uint8_t value) {
        bytes[count++] = static_cast<uint8_t>(0xA0u | (value >> 4));
        bytes[count++] = static_cast<uint8_t>(0xB0u | (value & 0x0Fu));
    };
    if (atom.has_digest) {
        const auto* data = reinterpret_cast<const uint8_t*>(&atom.digest);
        for (size_t i = 0; i < sizeof(atom.digest); ++i) append(data[i]);
    } else {
        append(static_cast<uint8_t>(atom.value));
        append(static_cast<uint8_t>(atom.value >> 8));
    }
    std::array<hash128_t, 33> ids{};
    std::array<double, 33 * 4> coords{};
    for (size_t i = 0; i < count; ++i) {
        hash128_blake3(&bytes[i], 1, &ids[i]);
        const double* coord = g_byte_coords + ((bytes[i] - 0x80u) * 4u);
        std::memcpy(coords.data() + i * 4, coord, 4 * sizeof(double));
    }
    hash128_merkle(kSubstructureTier, ids.data(), count, out_id);
    math4d_karcher_mean(coords.data(), count, nullptr, 1e-12, 64, out_coord);
}

static int piece_ordinal(char p) {
    const char* found = std::strchr("PNBRQKpnbrqk", p);
    return found && *found ? (int)(found - "PNBRQKpnbrqk") : -1;
}

static int square_bit(char file, char rank) {
    if (file < 'a' || file > 'h' || rank < '1' || rank > '8') return -1;
    return (rank - '1') * 8 + (file - 'a');
}

/* Interchange surface -> typed binary state-atom trajectory. The surface is parsed input,
 * never hashed or admitted as chess content. */
static int compose_position(std::string_view surface,
                            laplace_chess_perfcache_record_t* out) {
    std::array<std::string_view, 68> tokens{};
    size_t token_count = 0;
    size_t i = 0;
    while (i < surface.size()) {
        while (i < surface.size() && surface[i] == ' ') ++i;
        size_t j = i;
        while (j < surface.size() && surface[j] != ' ') ++j;
        if (j > i) {
            if (token_count == tokens.size()) return -1;
            tokens[token_count++] = surface.substr(i, j - i);
        }
        i = j;
    }
    if (token_count < 3) return -1;

    size_t at = 0;
    std::vector<atom_t> atoms;
    atoms.reserve(69u);
    if (tokens[at].starts_with("rules:")) {
        std::string_view rules = tokens[at++].substr(6);
        if (rules.empty()) return -1;
        atom_t a{}; a.domain = kRulesDomain; a.has_digest = true;
        hash128_blake3(reinterpret_cast<const uint8_t*>(rules.data()), rules.size(), &a.digest);
        atoms.push_back(a);
    }
    if (at + 3 > token_count || !tokens[at].starts_with("stm:")
        || !tokens[at + 1].starts_with("cr:") || !tokens[at + 2].starts_with("ep:"))
        return -1;

    std::string_view stm = tokens[at++].substr(4);
    std::string_view castle = tokens[at++].substr(3);
    std::string_view ep = tokens[at++].substr(3);
    if ((stm != "w" && stm != "b") || castle.empty()) return -1;

    char board[64]{};
    struct piece_at_t { uint16_t packed; int bit; };
    std::vector<piece_at_t> pieces;
    pieces.reserve(64u);
    for (; at < token_count; ++at) {
        std::string_view t = tokens[at];
        if (t.size() != 3) return -1;
        int po = piece_ordinal(t[0]);
        int bit = square_bit(t[1], t[2]);
        if (po < 0 || bit < 0 || board[bit] != 0) return -1;
        board[bit] = t[0];
        pieces.push_back({(uint16_t)((po << 6) | bit), bit});
    }
    std::sort(pieces.begin(), pieces.end(),
              [](const piece_at_t& a, const piece_at_t& b) { return a.bit < b.bit; });

    atoms.push_back(atom_t{kSideDomain, (uint16_t)(stm == "w"), {}, false});

    uint8_t rights = 0;
    int designated[4] = {-1, -1, -1, -1};
    int wk = -1, bk = -1;
    for (int bit = 0; bit < 64; ++bit) {
        if (board[bit] == 'K') wk = bit & 7;
        if (board[bit] == 'k') bk = bit & 7;
    }
    if (castle != "-") for (char c : castle) {
        int slot = -1, file = -1;
        if (c == 'K') { slot = 0; file = 7; }
        else if (c == 'Q') { slot = 1; file = 0; }
        else if (c == 'k') { slot = 2; file = 7; }
        else if (c == 'q') { slot = 3; file = 0; }
        else if (c >= 'A' && c <= 'H') { file = c - 'A'; slot = file > wk ? 0 : 1; }
        else if (c >= 'a' && c <= 'h') { file = c - 'a'; slot = file > bk ? 2 : 3; }
        else return -1;
        rights |= (uint8_t)(1u << slot);
        designated[slot] = file;
    }
    atoms.push_back(atom_t{kCastlingDomain, rights, {}, false});

    uint16_t rook_override = 0;
    bool needs_override = false;
    for (int slot = 0; slot < 4; ++slot) {
        if ((rights & (1u << slot)) == 0) continue;
        bool white = slot < 2, king_side = (slot & 1) == 0;
        int king_file = white ? wk : bk;
        int rank = white ? 0 : 7;
        char rook = white ? 'R' : 'r';
        rook_override |= (uint16_t)(1u << ((white ? 0 : 8) + designated[slot]));
        int count = 0, only = -1;
        for (int file = 0; file < 8; ++file) {
            if ((king_side ? file <= king_file : file >= king_file)) continue;
            if (board[rank * 8 + file] == rook) { ++count; only = file; }
        }
        if (count != 1 || only != designated[slot]) {
            needs_override = true;
        }
    }
    if (needs_override)
        atoms.push_back(atom_t{kCastlingRookOverrideDomain, rook_override, {}, false});

    uint16_t ep_value = 64;
    if (ep != "-") {
        if (ep.size() != 2) return -1;
        int bit = square_bit(ep[0], ep[1]);
        if (bit < 0) return -1;
        ep_value = (uint16_t)bit;
    }
    atoms.push_back(atom_t{kEnPassantDomain, ep_value, {}, false});
    for (const auto& p : pieces)
        atoms.push_back(atom_t{kPieceSquareDomain, p.packed, {}, false});

    std::vector<hash128_t> ids(atoms.size());
    std::vector<double> coords(atoms.size() * 4);
    for (size_t k = 0; k < atoms.size(); ++k)
        compose_atom(atoms[k], &ids[k], coords.data() + k * 4);

    std::memset(out, 0, sizeof(*out));
    hash128_merkle(kPositionTier, ids.data(), ids.size(), &out->id);
    math4d_karcher_mean(coords.data(), ids.size(), nullptr, 1e-12, 64, out->coord);
    hilbert4d_encode(out->coord, &out->hilbert);
    out->n = (uint32_t)ids.size();
    out->tier = kPositionTier;
    return 0;
}

template<class Sink>
static void emit_scalar_atom(Sink& out,
                             uint8_t domain, uint16_t value) {
    atom_t atom{domain, value, {}, false};
    laplace_chess_perfcache_record_t rec{};
    compose_atom(atom, &rec.id, rec.coord);
    hilbert4d_encode(rec.coord, &rec.hilbert);
    rec.n = 5;
    rec.tier = kSubstructureTier;
    out.push_back(rec);
}

template<class Sink>
static void emit_move(Sink& out,
                      uint16_t piece, uint16_t from, uint16_t to,
                      uint16_t flags, uint16_t promotion) {
    atom_t atoms[5] = {
        {kMovePieceDomain, piece, {}, false},
        {kMoveFromDomain, from, {}, false},
        {kMoveToDomain, to, {}, false},
        {kMoveFlagsDomain, flags, {}, false},
        {kMovePromotionDomain, promotion, {}, false},
    };
    hash128_t ids[5];
    double coords[5 * 4];
    for (size_t i = 0; i < 5; ++i)
        compose_atom(atoms[i], &ids[i], coords + i * 4);
    laplace_chess_perfcache_record_t rec{};
    hash128_merkle(kPositionTier, ids, 5, &rec.id);
    math4d_karcher_mean(coords, 5, nullptr, 1e-12, 64, rec.coord);
    hilbert4d_encode(rec.coord, &rec.hilbert);
    rec.n = 5;
    rec.tier = kPositionTier;
    out.push_back(rec);
}

/* Finite typed state atoms. Rare ambiguous-rook overrides and rule digests are composed
 * on demand; the ordinary board alphabet is closed and belongs in ROM. */
template<class Sink>
static int emit_tier1_alphabet(Sink& out) {
    for (uint16_t side = 0; side < 2; ++side)
        emit_scalar_atom(out, kSideDomain, side);
    for (uint16_t rights = 0; rights < 16; ++rights)
        emit_scalar_atom(out, kCastlingDomain, rights);
    for (uint16_t ep = 0; ep <= 64; ++ep)
        emit_scalar_atom(out, kEnPassantDomain, ep);
    for (uint16_t piece = 0; piece < 12; ++piece)
        emit_scalar_atom(out, kMovePieceDomain, piece);
    for (uint16_t sq = 0; sq < 64; ++sq) {
        emit_scalar_atom(out, kMoveFromDomain, sq);
        emit_scalar_atom(out, kMoveToDomain, sq);
    }
    for (uint16_t flags : {uint16_t{0}, kMoveDoublePush, kMoveEnPassant,
                           kMoveCastle, kMovePromotion})
        emit_scalar_atom(out, kMoveFlagsDomain, flags);
    for (uint16_t promotion = 0; promotion <= 4; ++promotion)
        emit_scalar_atom(out, kMovePromotionDomain, promotion);
    for (int piece = 0; piece < 12; ++piece) {
        for (int bit = 0; bit < 64; ++bit) {
            emit_scalar_atom(out, kPieceSquareDomain,
                             (uint16_t)((piece << 6) | bit));
        }
    }
    return 0;
}

template<class Sink>
static int emit_move_alphabet(Sink& out) {
    static constexpr uint16_t ordinary_flags[] = {
        0, kMoveDoublePush, kMoveEnPassant, kMoveCastle
    };
    for (uint16_t piece = 0; piece < 12; ++piece) {
        for (uint16_t from = 0; from < 64; ++from) {
            for (uint16_t to = 0; to < 64; ++to) {
                for (uint16_t flags : ordinary_flags)
                    emit_move(out, piece, from, to, flags, 0);
                if (piece == 0 || piece == 6)
                    for (uint16_t promotion = 1; promotion <= 4; ++promotion)
                        emit_move(out, piece, from, to, kMovePromotion, promotion);
            }
        }
    }
    return 0;
}

struct RecordSink {
    chess_emit::Producer& producer;
    uint8_t origin;
    uint64_t count = 0;
    void push_back(const chess_emit::Record& record) {
        if (count == UINT64_MAX) throw std::runtime_error("emitted record count overflow");
        producer.add(record, origin); ++count;
    }
};
static void compose_surface(std::string_view line, void* context) {
    laplace_chess_perfcache_record_t record{};
    if (compose_position(line, &record) != 0) throw std::runtime_error("malformed chess position surface");
    static_cast<chess_emit::Producer*>(context)->add(record, chess_emit::board_origin);
}
static std::string hex_hash(const hash128_t& value) {
    constexpr char digits[] = "0123456789abcdef";
    const auto* bytes = reinterpret_cast<const uint8_t*>(&value);
    std::string result(32u, '0');
    for (size_t i = 0; i < sizeof(value); ++i) {
        result[2u * i] = digits[bytes[i] >> 4];
        result[2u * i + 1u] = digits[bytes[i] & 15u];
    }
    return result;
}
static void report(const chess_emit::Input& primary, const chess_emit::Input& additional,
                   const hash128_t& source, bool skipped, const chess_emit::Statistics& stats,
                   size_t memory, uint64_t spill, bool verification_only) {
    std::ostringstream json;
    json << "{\"schema\":\"laplace.chess-position-producer/v1\",\"artifact_verified\":true"
         << ",\"verification_only\":" << (verification_only ? "true" : "false")
         << ",\"source_hash\":\"" << hex_hash(source) << "\",\"skipped\":" << (skipped ? "true" : "false")
         << ",\"primary_bytes\":" << primary.bytes << ",\"additional_bytes\":" << additional.bytes
         << ",\"primary_occurrences\":" << primary.occurrences
         << ",\"additional_occurrences\":" << additional.occurrences
         << ",\"finite_atoms\":1001,\"finite_moves\":229376"
         << ",\"distinct_board_ids\":";
    if (skipped) json << "null"; else json << stats.board_ids;
    json << ",\"total_records\":" << stats.records << ",\"memory_limit_bytes\":" << memory
         << ",\"controlled_memory_bytes\":" << stats.controlled_memory_bytes
         << ",\"maximum_line_bytes\":" << chess_emit::maximum_line_bytes
         << ",\"spill_limit_bytes\":" << spill << ",\"spill_peak_bytes\":" << stats.spill_peak_bytes
         << ",\"runs_written\":" << stats.runs_written << "}";
    std::fprintf(stderr, "%s\n", json.str().c_str());
}
static int emit(const Cli& cli) {
    namespace fs = std::filesystem;
    const auto primary = chess_emit::scan(cli.surfaces);
    const auto additional = chess_emit::scan(cli.additional_surfaces);
    const auto source = chess_emit::source_hash(primary, cli.additional_surfaces.empty() ? nullptr : &additional);
    if (additional.occurrences > UINT64_MAX - 230377u ||
        primary.occurrences > UINT64_MAX - 230377u - additional.occurrences)
        throw std::runtime_error("source occurrence count exceeds producer window");
    const uint64_t spill = cli.maximum_spill_bytes ? cli.maximum_spill_bytes :
        chess_emit::default_spill_bytes(primary.occurrences + additional.occurrences + 230377u);
    chess_emit::Statistics stats;
    if (chess_emit::validate_blob(cli.output, source, &stats.records)) {
        stats.controlled_memory_bytes = chess_emit::fixed_memory_bytes;
        report(primary, additional, source, true, stats, cli.memory_bytes, spill, cli.verify_existing);
        return 0;
    }
    if (cli.verify_existing)
        throw std::runtime_error("existing chess blob does not match the selected source inputs and full checksum");
    const fs::path output(cli.output);
    const fs::path scratch = cli.scratch.empty() ?
        (output.has_parent_path() ? output.parent_path() : fs::path(".")) : fs::path(cli.scratch);
    chess_emit::Producer producer(cli.memory_bytes, spill, scratch);
    super_fibonacci(128, g_byte_coords);
    RecordSink atoms{producer, chess_emit::atom_origin}, moves{producer, chess_emit::move_origin};
    if (emit_tier1_alphabet(atoms) != 0 || emit_move_alphabet(moves) != 0)
        throw std::runtime_error("finite chess alphabet compose failed");
    if (atoms.count != 1001u || moves.count != 229376u)
        throw std::runtime_error("finite chess alphabet count differs from producer contract");
    const auto repeated_primary = chess_emit::scan(cli.surfaces, compose_surface, &producer);
    const auto repeated_additional = chess_emit::scan(cli.additional_surfaces, compose_surface, &producer);
    auto same = [](const chess_emit::Input& a, const chess_emit::Input& b) {
        return a.bytes == b.bytes && a.occurrences == b.occurrences && hash128_equals(&a.hash, &b.hash);
    };
    if (!same(primary, repeated_primary) || !same(additional, repeated_additional))
        throw std::runtime_error("surface input changed between fingerprint and composition");
    stats = producer.publish(output, source);
    report(primary, additional, source, false, stats, cli.memory_bytes, spill, false);
    return 0;
}

#ifndef LAPLACE_CHESS_PRODUCER_TESTING
int main(int argc, char** argv) {
    try { return emit(parse_cli(argc, argv)); }
    catch (const CliError& error) {
        std::fprintf(stderr, "chess_position_perfcache: %s\n", error.what()); return error.code;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "chess_position_perfcache: %s\n", error.what()); return 4;
    }
}
#endif
