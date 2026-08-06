/*
 * Build-time emit for laplace_modality_number_perfcache.bin
 * (spec 33 / modality-ladder-law codepoint floor).
 *
 * Peer of laplace_chess_position_tables_emit in ROLE: load t0 → compose → blob.
 * Tier 0 remains CODEPOINTS only. This packs the dense channel-byte number
 * table (0..255) whose ids are text content roots of decimal digit strings —
 * the same ScalarId / word_id law, not packed-RGBA or PCM alphabets.
 *
 * Inputs:  --t0 (required) + --output (required)
 * Output:  dense value → id/coord/hilbert/n/tier + BLAKE3 trailer
 *
 * Runtime load: modality_number_table_load (O(1) index). No corpus required.
 */

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <string>
#include <string_view>
#include <vector>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/modality_number_perfcache_format.h"
#include "laplace/core/perfcache_format.h"
#include "laplace/core/tier_tree.h"

struct Cli {
    std::string t0;
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
        else if (a == "--output") c.output = nx();
        else if (a == "--from-db") {
            std::fprintf(stderr,
                "REFUSED: --from-db dumps substrate testimony; that is not the "
                "modality number compose floor. Tier-0 is codepoints; this blob "
                "is deterministic decimal digit → number ROM (0..255).\n");
            std::exit(3);
        } else {
            std::fprintf(stderr, "unknown arg %s\n", argv[i]);
            std::exit(2);
        }
    }
    if (c.t0.empty() || c.output.empty()) {
        std::fprintf(stderr, "required: --t0 --output\n");
        std::exit(2);
    }
    return c;
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

static void compute_source_hash(const hash128_t& t0_fingerprint, hash128_t* out) {
    std::vector<uint8_t> mix;
    put_h128(mix, t0_fingerprint);
    for (const char* t = LAPLACE_MODALITY_NUMBER_PERFCACHE_GENERATOR_TAG; *t; ++t)
        mix.push_back((uint8_t)*t);
    mix.push_back(0);
    for (const char* t = LAPLACE_MODALITY_NUMBER_PERFCACHE_SCOPE; *t; ++t)
        mix.push_back((uint8_t)*t);
    mix.push_back(0);
    /* Pin dense cardinality so a future scope bump cannot silently reuse v1. */
    put_u32(mix, LAPLACE_MODALITY_NUMBER_PERFCACHE_VALUE_COUNT);
    hash128_blake3(mix.data(), mix.size(), out);
}

/* Decimal digit string → text content natural-unit root (ScalarId / word_id). */
static int compose_number(uint32_t value,
                          laplace_modality_number_perfcache_record_t* out) {
    char digits[4];
    int n = std::snprintf(digits, sizeof(digits), "%u", value);
    if (n <= 0 || n >= (int)sizeof(digits)) return -1;

    hash128_t root_id{};
    if (laplace_content_root_id((const uint8_t*)digits, (size_t)n, &root_id) != 0)
        return -1;

    tier_tree_t* tree = nullptr;
    if (content_witness_tree_build((const uint8_t*)digits, (size_t)n, &tree) != 0
        || !tree)
        return -1;

    /* Geometry from the natural-unit node (id matches laplace_content_root_id). */
    tier_node_view_t root{};
    int found = 0;
    size_t nc = tier_tree_node_count(tree);
    for (uint32_t i = 0; i < (uint32_t)nc; ++i) {
        tier_node_view_t node;
        if (tier_tree_get_node(tree, i, &node) != 0) continue;
        if (hash128_equals(&node.id, &root_id)) {
            root = node;
            found = 1;
            break;
        }
    }
    tier_tree_free(tree);
    if (!found) return -1;

    std::memset(out, 0, sizeof(*out));
    out->id = root_id;
    out->coord[0] = root.coord[0];
    out->coord[1] = root.coord[1];
    out->coord[2] = root.coord[2];
    out->coord[3] = root.coord[3];
    out->hilbert = root.hilbert;
    out->value = value;
    out->n = (uint32_t)n;
    out->tier = root.tier;
    return 0;
}

int main(int argc, char** argv) {
    Cli cli = parse_cli(argc, argv);

    if (codepoint_table_load_perfcache(cli.t0.c_str()) != 0) {
        std::fprintf(stderr, "cannot load t0 perfcache %s\n", cli.t0.c_str());
        return 4;
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
    compute_source_hash(t0_fp, &source_hash);

    {
        std::ifstream prev(cli.output, std::ios::binary);
        if (prev) {
            laplace_modality_number_perfcache_header_t hdr{};
            prev.read((char*)&hdr, sizeof(hdr));
            if (prev.gcount() == (std::streamsize)sizeof(hdr)
                && hdr.magic == LAPLACE_MODALITY_NUMBER_PERFCACHE_MAGIC
                && hdr.format_version == LAPLACE_MODALITY_NUMBER_PERFCACHE_VERSION
                && std::memcmp(&hdr.source_hash, &source_hash, sizeof(hash128_t)) == 0) {
                std::fprintf(stderr,
                    "modality_number_perfcache: sources unchanged — emit skipped\n");
                return 0;
            }
        }
    }

    std::vector<laplace_modality_number_perfcache_record_t> records(
        LAPLACE_MODALITY_NUMBER_PERFCACHE_VALUE_COUNT);
    for (uint32_t v = 0; v < LAPLACE_MODALITY_NUMBER_PERFCACHE_VALUE_COUNT; ++v) {
        if (compose_number(v, &records[v]) != 0) {
            std::fprintf(stderr, "compose failed for value %u\n", v);
            return 4;
        }
    }

    std::vector<uint8_t> blob;
    blob.reserve(LAPLACE_MODALITY_NUMBER_PERFCACHE_HEADER_SIZE
                 + records.size() * LAPLACE_MODALITY_NUMBER_PERFCACHE_RECORD_SIZE
                 + LAPLACE_MODALITY_NUMBER_PERFCACHE_TRAILER_BYTES);

    put_u32(blob, LAPLACE_MODALITY_NUMBER_PERFCACHE_MAGIC);
    put_u32(blob, LAPLACE_MODALITY_NUMBER_PERFCACHE_VERSION);
    put_u64(blob, records.size());
    put_u64(blob, LAPLACE_MODALITY_NUMBER_PERFCACHE_RECORD_SIZE);
    put_u64(blob, LAPLACE_MODALITY_NUMBER_PERFCACHE_HEADER_SIZE);
    put_h128(blob, source_hash);
    {
        char scope[16] = {0};
        std::memcpy(scope, LAPLACE_MODALITY_NUMBER_PERFCACHE_SCOPE,
                    std::strlen(LAPLACE_MODALITY_NUMBER_PERFCACHE_SCOPE));
        for (int i = 0; i < 16; ++i) blob.push_back((uint8_t)scope[i]);
    }
    for (int i = 0; i < 64; ++i) blob.push_back(0);

    if (blob.size() != LAPLACE_MODALITY_NUMBER_PERFCACHE_HEADER_SIZE) {
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
        "modality_number_perfcache: values=0..%u unique_slots=%zu -> %s (%.1f KiB)\n",
        LAPLACE_MODALITY_NUMBER_PERFCACHE_VALUE_COUNT - 1, records.size(),
        cli.output.c_str(), blob.size() / 1024.0);
    return 0;
}
