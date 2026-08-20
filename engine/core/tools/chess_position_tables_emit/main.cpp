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
 * Inputs:  --output (required) + optional --surfaces
 * Output:  sorted id → coord/hilbert/n/tier + BLAKE3 trailer
 *
 * Runtime load: chess_position_table_load. Not a managed catalog walker.
 * Not a Postgres testimony dump. Not Glicko.
 */

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

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

static double g_byte_coords[128 * 4];

struct Cli {
    std::string surfaces; /* optional tier-2 catalog surfaces */
    std::string output;
};

static Cli parse_cli(int argc, char** argv) {
    Cli c;
    for (int i = 1; i < argc; ++i) {
        std::string_view a = argv[i];
        auto nx = [&]() -> std::string {
            if (i + 1 >= argc) {
                std::fprintf(stderr, "%s needs value\n", argv[i]);
                std::exit(2);
            }
            return argv[++i];
        };
        if (a == "--surfaces") c.surfaces = nx();
        else if (a == "--output") c.output = nx();
        else if (a == "--from-db") {
            std::fprintf(stderr,
                "REFUSED: --from-db dumps substrate testimony geometry; that is "
                "not the compose floor (GH #822). This blob "
                "is deterministic tier-1 vocab (+ catalog tier-2 positions).\n");
            std::exit(3);
        } else {
            std::fprintf(stderr, "unknown arg %s\n", argv[i]);
            std::exit(2);
        }
    }
    if (c.output.empty()) {
        std::fprintf(stderr, "required: --output [--surfaces]\n");
        std::exit(2);
    }
    return c;
}

static bool read_file_bytes(const std::string& path, std::vector<uint8_t>& out) {
    std::ifstream f(path, std::ios::binary);
    if (!f) return false;
    out.assign(std::istreambuf_iterator<char>(f), std::istreambuf_iterator<char>());
    return true;
}

static void put_u32(std::vector<uint8_t>& b, uint32_t v) {
    for (int i = 0; i < 4; ++i) b.push_back((uint8_t)(v >> (i * 8)));
}
static void put_u64(std::vector<uint8_t>& b, uint64_t v) {
    for (int i = 0; i < 8; ++i) b.push_back((uint8_t)(v >> (i * 8)));
}
static void put_h128(std::vector<uint8_t>& b, const hash128_t& h) {
    const uint8_t* p = (const uint8_t*)&h;
    for (int i = 0; i < 16; ++i) b.push_back(p[i]);
}

static void compute_source_hash(const std::vector<uint8_t>& surfaces_bytes,
                                hash128_t* out) {
    hash128_t hs;
    hash128_blake3(surfaces_bytes.data(), surfaces_bytes.size(), &hs);
    std::vector<uint8_t> mix;
    put_h128(mix, hs);
    for (const char* t = LAPLACE_CHESS_PERFCACHE_GENERATOR_TAG; *t; ++t)
        mix.push_back((uint8_t)*t);
    mix.push_back(0);
    /* Finite tier-1 alphabet is always in the blob; tag it so skipping surfaces
     * still fingerprints a real product. */
    const char* t1 = "tier1-typed-board-atoms/v2";
    for (const char* t = t1; *t; ++t) mix.push_back((uint8_t)*t);
    mix.push_back(0);
    const char* scope = "catalog";
    for (const char* t = scope; *t; ++t) mix.push_back((uint8_t)*t);
    hash128_blake3(mix.data(), mix.size(), out);
}

struct atom_t {
    uint8_t domain{};
    uint16_t value{};
    hash128_t digest{};
    bool has_digest{};
};

static void append_encoded_byte(std::vector<uint8_t>& atoms, uint8_t b) {
    atoms.push_back((uint8_t)(0xA0u | (b >> 4)));
    atoms.push_back((uint8_t)(0xB0u | (b & 0x0Fu)));
}

static void compose_atom(const atom_t& atom, hash128_t* out_id, double out_coord[4]) {
    std::vector<uint8_t> bytes;
    bytes.reserve(atom.has_digest ? 33 : 5);
    bytes.push_back((uint8_t)(0x80u + atom.domain));
    if (atom.has_digest) {
        const uint8_t* p = (const uint8_t*)&atom.digest;
        for (size_t i = 0; i < 16; ++i) append_encoded_byte(bytes, p[i]);
    } else {
        append_encoded_byte(bytes, (uint8_t)atom.value);
        append_encoded_byte(bytes, (uint8_t)(atom.value >> 8));
    }

    std::vector<hash128_t> ids(bytes.size());
    std::vector<double> coords(bytes.size() * 4);
    for (size_t i = 0; i < bytes.size(); ++i) {
        hash128_blake3(&bytes[i], 1, &ids[i]);
        const double* c = g_byte_coords + ((bytes[i] - 0x80u) * 4u);
        std::memcpy(coords.data() + i * 4, c, 4 * sizeof(double));
    }
    hash128_merkle(kSubstructureTier, ids.data(), ids.size(), out_id);
    math4d_karcher_mean(coords.data(), ids.size(), nullptr, 1e-12, 64, out_coord);
}

static int piece_ordinal(char p) {
    const char* found = std::strchr("PNBRQKpnbrqk", p);
    return found ? (int)(found - "PNBRQKpnbrqk") : -1;
}

static int square_bit(char file, char rank) {
    if (file < 'a' || file > 'h' || rank < '1' || rank > '8') return -1;
    return (rank - '1') * 8 + (file - 'a');
}

/* Interchange surface -> typed binary state-atom trajectory. The surface is parsed input,
 * never hashed or admitted as chess content. */
static int compose_position(const std::string& surface,
                            laplace_chess_perfcache_record_t* out) {
    std::vector<std::string_view> tokens;
    size_t i = 0;
    while (i < surface.size()) {
        while (i < surface.size() && surface[i] == ' ') ++i;
        size_t j = i;
        while (j < surface.size() && surface[j] != ' ') ++j;
        if (j > i) tokens.emplace_back(surface.data() + i, j - i);
        i = j;
    }
    if (tokens.size() < 3) return -1;

    size_t at = 0;
    std::vector<atom_t> atoms;
    if (tokens[at].starts_with("rules:")) {
        std::string_view rules = tokens[at++].substr(6);
        atom_t a{}; a.domain = kRulesDomain; a.has_digest = true;
        hash128_blake3(reinterpret_cast<const uint8_t*>(rules.data()), rules.size(), &a.digest);
        atoms.push_back(a);
    }
    if (at + 3 > tokens.size() || !tokens[at].starts_with("stm:")
        || !tokens[at + 1].starts_with("cr:") || !tokens[at + 2].starts_with("ep:"))
        return -1;

    std::string_view stm = tokens[at++].substr(4);
    std::string_view castle = tokens[at++].substr(3);
    std::string_view ep = tokens[at++].substr(3);
    if (stm != "w" && stm != "b") return -1;

    char board[64]{};
    struct piece_at_t { uint16_t packed; int bit; };
    std::vector<piece_at_t> pieces;
    for (; at < tokens.size(); ++at) {
        std::string_view t = tokens[at];
        if (t.size() != 3) continue;
        int po = piece_ordinal(t[0]);
        int bit = square_bit(t[1], t[2]);
        if (po < 0 || bit < 0) return -1;
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
        int count = 0, only = -1;
        for (int file = 0; file < 8; ++file) {
            if ((king_side ? file <= king_file : file >= king_file)) continue;
            if (board[rank * 8 + file] == rook) { ++count; only = file; }
        }
        if (count != 1 || only != designated[slot]) {
            needs_override = true;
            rook_override |= (uint16_t)(1u << ((white ? 0 : 8) + designated[slot]));
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

static void emit_scalar_atom(std::vector<laplace_chess_perfcache_record_t>& out,
                             uint8_t domain, uint16_t value) {
    atom_t atom{domain, value, {}, false};
    laplace_chess_perfcache_record_t rec{};
    compose_atom(atom, &rec.id, rec.coord);
    hilbert4d_encode(rec.coord, &rec.hilbert);
    rec.n = 5;
    rec.tier = kSubstructureTier;
    out.push_back(rec);
}

/* Finite typed state atoms. Rare ambiguous-rook overrides and rule digests are composed
 * on demand; the ordinary board alphabet is closed and belongs in ROM. */
static int emit_tier1_alphabet(std::vector<laplace_chess_perfcache_record_t>& out) {
    for (uint16_t side = 0; side < 2; ++side)
        emit_scalar_atom(out, kSideDomain, side);
    for (uint16_t rights = 0; rights < 16; ++rights)
        emit_scalar_atom(out, kCastlingDomain, rights);
    for (uint16_t ep = 0; ep <= 64; ++ep)
        emit_scalar_atom(out, kEnPassantDomain, ep);
    for (int piece = 0; piece < 12; ++piece) {
        for (int bit = 0; bit < 64; ++bit) {
            emit_scalar_atom(out, kPieceSquareDomain,
                             (uint16_t)((piece << 6) | bit));
        }
    }
    return 0;
}

static int id_less(const laplace_chess_perfcache_record_t& a,
                   const laplace_chess_perfcache_record_t& b) {
    return hash128_compare(&a.id, &b.id) < 0;
}

int main(int argc, char** argv) {
    Cli cli = parse_cli(argc, argv);

    super_fibonacci(128, g_byte_coords);

    std::vector<uint8_t> surfaces_bytes;
    if (!cli.surfaces.empty()) {
        if (!read_file_bytes(cli.surfaces, surfaces_bytes)) {
            std::fprintf(stderr, "cannot open surfaces %s\n", cli.surfaces.c_str());
            return 4;
        }
    }

    hash128_t source_hash;
    compute_source_hash(surfaces_bytes, &source_hash);

    {
        std::ifstream prev(cli.output, std::ios::binary);
        if (prev) {
            laplace_chess_perfcache_header_t hdr{};
            prev.read((char*)&hdr, sizeof(hdr));
            if (prev.gcount() == (std::streamsize)sizeof(hdr)
                && hdr.magic == LAPLACE_CHESS_PERFCACHE_MAGIC
                && hdr.format_version == LAPLACE_CHESS_PERFCACHE_VERSION
                && std::memcmp(&hdr.source_hash, &source_hash, sizeof(hash128_t)) == 0) {
                std::fprintf(stderr,
                    "chess_position_perfcache: sources unchanged — emit skipped\n");
                return 0;
            }
        }
    }

    std::vector<laplace_chess_perfcache_record_t> records;
    records.reserve(851 + 8192);
    if (emit_tier1_alphabet(records) != 0) {
        std::fprintf(stderr, "tier-1 alphabet compose failed\n");
        return 4;
    }
    const size_t tier1_n = records.size();

    size_t surface_n = 0;
    if (!surfaces_bytes.empty()) {
        std::unordered_map<std::string, laplace_chess_perfcache_record_t> by_surface;
        std::string line;
        std::string text(surfaces_bytes.begin(), surfaces_bytes.end());
        size_t start = 0;
        while (start <= text.size()) {
            size_t nl = text.find('\n', start);
            if (nl == std::string::npos) nl = text.size();
            line = text.substr(start, nl - start);
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (!line.empty() && by_surface.find(line) == by_surface.end()) {
                laplace_chess_perfcache_record_t rec{};
                if (compose_position(line, &rec) != 0) {
                    std::fprintf(stderr, "compose failed: %s\n", line.c_str());
                    return 4;
                }
                by_surface.emplace(line, rec);
            }
            if (nl == text.size()) break;
            start = nl + 1;
        }
        surface_n = by_surface.size();
        for (auto& kv : by_surface) records.push_back(kv.second);
    }

    std::sort(records.begin(), records.end(), id_less);
    records.erase(std::unique(records.begin(), records.end(),
                              [](const laplace_chess_perfcache_record_t& a,
                                 const laplace_chess_perfcache_record_t& b) {
                                  return hash128_equals(&a.id, &b.id) != 0;
                              }),
                  records.end());

    std::vector<uint8_t> blob;
    blob.reserve(LAPLACE_CHESS_PERFCACHE_HEADER_SIZE
                 + records.size() * LAPLACE_CHESS_PERFCACHE_RECORD_SIZE
                 + LAPLACE_CHESS_PERFCACHE_TRAILER_BYTES);

    put_u32(blob, LAPLACE_CHESS_PERFCACHE_MAGIC);
    put_u32(blob, LAPLACE_CHESS_PERFCACHE_VERSION);
    put_u64(blob, records.size());
    put_u64(blob, LAPLACE_CHESS_PERFCACHE_RECORD_SIZE);
    put_u64(blob, LAPLACE_CHESS_PERFCACHE_HEADER_SIZE);
    put_h128(blob, source_hash);
    {
        char scope[16] = {0};
        std::memcpy(scope, "catalog", 7);
        for (int i = 0; i < 16; ++i) blob.push_back((uint8_t)scope[i]);
    }
    for (int i = 0; i < 64; ++i) blob.push_back(0);

    if (blob.size() != LAPLACE_CHESS_PERFCACHE_HEADER_SIZE) {
        std::fprintf(stderr, "header size bug: %zu\n", blob.size());
        return 5;
    }

    for (const auto& r : records) {
        const uint8_t* p = (const uint8_t*)&r;
        for (size_t i = 0; i < sizeof(r); ++i) blob.push_back(p[i]);
    }

    hash128_t crc;
    hash128_blake3(blob.data(), blob.size(), &crc);
    put_h128(blob, crc);

    std::ofstream out(cli.output, std::ios::binary);
    if (!out) {
        std::fprintf(stderr, "cannot write %s\n", cli.output.c_str());
        return 5;
    }
    out.write((const char*)blob.data(), (std::streamsize)blob.size());
    out.close();

    std::fprintf(stderr,
        "chess_position_perfcache: tier1=%zu catalog_surfaces=%zu unique_ids=%zu "
        "-> %s (%.1f KiB)\n",
        tier1_n, surface_n, records.size(), cli.output.c_str(),
        blob.size() / 1024.0);
    return 0;
}
