/*
 * Build-time emit for laplace_chess_position_perfcache.bin (GH #822 / spec 33).
 *
 * Peer of laplace_ucd_tables_emit in ROLE (declared inputs → deterministic blob).
 * Tier 0 remains CODEPOINTS only (t0 blob) — this tool LOADS t0 and composes up.
 *
 * Always emits the finite tier-1 piece×square alphabet (deterministic lossless
 * chess vocabulary). Optionally composes additional tier-2 board surfaces from
 * a catalog file (openings / Chess960 / … as "sentences" selecting coverage).
 *
 * Inputs:  --t0 (required) + --output (required) + optional --surfaces
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
#include "laplace/core/codepoint_table.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/math4d.h"
#include "laplace/core/perfcache_format.h"
#include "laplace/core/utf8.h"

static const uint8_t kSubstructureTier = 1;
static const uint8_t kPositionTier = 2;

struct Cli {
    std::string t0;
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
        if (a == "--t0") c.t0 = nx();
        else if (a == "--surfaces") c.surfaces = nx();
        else if (a == "--output") c.output = nx();
        else if (a == "--from-db") {
            std::fprintf(stderr,
                "REFUSED: --from-db dumps substrate testimony geometry; that is "
                "not the compose floor (GH #822). Tier-0 is codepoints; this blob "
                "is deterministic tier-1 vocab (+ catalog tier-2 positions).\n");
            std::exit(3);
        } else {
            std::fprintf(stderr, "unknown arg %s\n", argv[i]);
            std::exit(2);
        }
    }
    if (c.t0.empty() || c.output.empty()) {
        std::fprintf(stderr, "required: --t0 --output [--surfaces]\n");
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
                                const hash128_t& t0_fingerprint,
                                hash128_t* out) {
    hash128_t hs;
    hash128_blake3(surfaces_bytes.data(), surfaces_bytes.size(), &hs);
    std::vector<uint8_t> mix;
    put_h128(mix, hs);
    put_h128(mix, t0_fingerprint);
    for (const char* t = LAPLACE_CHESS_PERFCACHE_GENERATOR_TAG; *t; ++t)
        mix.push_back((uint8_t)*t);
    mix.push_back(0);
    /* Finite tier-1 alphabet is always in the blob; tag it so skipping surfaces
     * still fingerprints a real product. */
    const char* t1 = "tier1-piece-square/v1";
    for (const char* t = t1; *t; ++t) mix.push_back((uint8_t)*t);
    mix.push_back(0);
    const char* scope = "catalog";
    for (const char* t = scope; *t; ++t) mix.push_back((uint8_t)*t);
    hash128_blake3(mix.data(), mix.size(), out);
}

/* Compose one token (tier 1) from its UTF-8 bytes via the codepoint floor. */
static int compose_token(const uint8_t* s, size_t len,
                         hash128_t* out_id, double out_coord[4]) {
    if (len == 0) return -1;
    std::vector<hash128_t> ids;
    std::vector<double> coords;
    size_t off = 0;
    while (off < len) {
        uint32_t cp = 0;
        size_t consumed = 0;
        if (laplace_utf8_decode(s + off, len - off, &cp, &consumed) != 0)
            return -1;
        const codepoint_entry_t* e = codepoint_table_lookup(cp);
        if (!e) return -1;
        ids.push_back(e->hash);
        coords.push_back(e->coord[0]);
        coords.push_back(e->coord[1]);
        coords.push_back(e->coord[2]);
        coords.push_back(e->coord[3]);
        off += consumed;
    }
    hash128_merkle(kSubstructureTier, ids.data(), ids.size(), out_id);
    math4d_centroid(coords.data(), ids.size(), out_coord);
    return 0;
}

/* Board surface → tier-2 position record fields. */
static int compose_position(const std::string& surface,
                            laplace_chess_perfcache_record_t* out) {
    std::vector<hash128_t> tok_ids;
    std::vector<double> tok_coords;
    const char* p = surface.c_str();
    size_t n = surface.size();
    size_t i = 0;
    while (i < n) {
        while (i < n && p[i] == ' ') ++i;
        if (i >= n) break;
        size_t j = i;
        while (j < n && p[j] != ' ') ++j;
        hash128_t tid;
        double tc[4];
        if (compose_token((const uint8_t*)(p + i), j - i, &tid, tc) != 0)
            return -1;
        tok_ids.push_back(tid);
        tok_coords.push_back(tc[0]);
        tok_coords.push_back(tc[1]);
        tok_coords.push_back(tc[2]);
        tok_coords.push_back(tc[3]);
        i = j;
    }
    if (tok_ids.empty()) return -1;

    std::memset(out, 0, sizeof(*out));
    hash128_merkle(kPositionTier, tok_ids.data(), tok_ids.size(), &out->id);
    math4d_centroid(tok_coords.data(), tok_ids.size(), out->coord);
    hilbert4d_encode(out->coord, &out->hilbert);
    out->n = (uint32_t)tok_ids.size();
    out->tier = kPositionTier;
    return 0;
}

/* Finite chess tier-1 alphabet: 12 piece chars × 64 squares. Enumerated in
 * native code (peer of enumerating codepoints from UCD) — not a TSV walk. */
static int emit_tier1_alphabet(std::vector<laplace_chess_perfcache_record_t>& out) {
    static const char* kPieces = "PNBRQKpnbrqk";
    char tok[4] = {0, 0, 0, 0};
    for (const char* pc = kPieces; *pc; ++pc) {
        tok[0] = *pc;
        for (char file = 'a'; file <= 'h'; ++file) {
            tok[1] = file;
            for (char rank = '1'; rank <= '8'; ++rank) {
                tok[2] = rank;
                laplace_chess_perfcache_record_t rec{};
                double coord[4];
                if (compose_token((const uint8_t*)tok, 3, &rec.id, coord) != 0)
                    return -1;
                rec.coord[0] = coord[0];
                rec.coord[1] = coord[1];
                rec.coord[2] = coord[2];
                rec.coord[3] = coord[3];
                hilbert4d_encode(rec.coord, &rec.hilbert);
                rec.n = 3; /* three ASCII codepoints in "Pe2" */
                rec.tier = kSubstructureTier;
                out.push_back(rec);
            }
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

    if (codepoint_table_load_perfcache(cli.t0.c_str()) != 0) {
        std::fprintf(stderr, "cannot load t0 perfcache %s\n", cli.t0.c_str());
        return 4;
    }

    std::vector<uint8_t> surfaces_bytes;
    if (!cli.surfaces.empty()) {
        if (!read_file_bytes(cli.surfaces, surfaces_bytes)) {
            std::fprintf(stderr, "cannot open surfaces %s\n", cli.surfaces.c_str());
            return 4;
        }
    }

    hash128_t t0_fp{};
    {
        std::ifstream tf(cli.t0, std::ios::binary);
        laplace_perfcache_header_t th{};
        tf.read((char*)&th, sizeof(th));
        if (!tf || th.magic != LAPLACE_PERFCACHE_MAGIC) {
            std::fprintf(stderr, "bad t0 header\n");
            return 4;
        }
        t0_fp = th.ucd_hash;
    }

    hash128_t source_hash;
    compute_source_hash(surfaces_bytes, t0_fp, &source_hash);

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
    records.reserve(768 + 8192);
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
