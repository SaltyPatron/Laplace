#include <gtest/gtest.h>

#include <array>
#include <cstring>
#include <string>
#include <vector>

#include "laplace/core/attestation_engine.h"
#include "laplace/core/glicko2.h"
#include "laplace/core/hash128.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/source_reduce.h"

namespace {

constexpr int64_t kNeutralMu = INT64_C(1500000000000);
constexpr int64_t kInitialRd = INT64_C(350000000000);
constexpr int64_t kInitialVolatility = INT64_C(60000000);

hash128_t id_of(const char* text) {
    hash128_t h;
    hash128_blake3(reinterpret_cast<const uint8_t*>(text), std::strlen(text), &h);
    return h;
}

hash128_t id_bytes(uint8_t first) {
    hash128_t h;
    uint8_t b[16];
    for (int i = 0; i < 16; ++i) b[i] = static_cast<uint8_t>(first + i);
    std::memcpy(&h, b, 16);
    return h;
}

// Rows of a binary COPY stream, each as its fields (empty optional = NULL).
using field = std::vector<uint8_t>;
struct row { std::vector<field> fields; std::vector<bool> nulls; };

uint32_t be32(const uint8_t* p) { return (uint32_t(p[0]) << 24) | (uint32_t(p[1]) << 16) | (uint32_t(p[2]) << 8) | p[3]; }
int64_t be64(const std::vector<uint8_t>& v) {
    uint64_t x = 0;
    for (uint8_t b : v) x = (x << 8) | b;
    return static_cast<int64_t>(x);
}

std::vector<row> rows_of(const uint8_t* p, size_t n) {
    std::vector<row> out;
    EXPECT_GE(n, 21u);
    EXPECT_EQ(0, std::memcmp(p, "PGCOPY\n\377\r\n\0", 11));
    size_t at = 19;
    while (at < n) {
        const uint16_t fields = static_cast<uint16_t>((p[at] << 8) | p[at + 1]);
        at += 2;
        if (fields == 0xffff) break;
        row r;
        for (uint16_t f = 0; f < fields; ++f) {
            const int32_t len = static_cast<int32_t>(be32(p + at));
            at += 4;
            r.nulls.push_back(len < 0);
            r.fields.emplace_back(len > 0 ? p + at : p, len > 0 ? p + at + len : p);
            if (len > 0) at += static_cast<size_t>(len);
        }
        out.push_back(std::move(r));
    }
    EXPECT_EQ(at, n);
    return out;
}

std::vector<row> stream(laplace_source_reduce_t* r, laplace_source_reduce_stream_t s, uint32_t lane) {
    size_t len = 0;
    uint64_t count = 0;
    const uint8_t* p = laplace_source_reduce_stream(r, s, lane, &len, &count);
    EXPECT_NE(nullptr, p) << laplace_source_reduce_error(r);
    if (!p) return {};
    auto rows = rows_of(p, len);
    EXPECT_EQ(count, rows.size());
    return rows;
}

bool same_id(const field& f, const hash128_t& id) {
    return f.size() == 16 && std::memcmp(f.data(), &id, 16) == 0;
}

void add_claim(intent_stage_t* stage, const hash128_t& subject, const hash128_t& type, const hash128_t& object,
               const hash128_t& source, int64_t ts, int64_t games, int64_t sum, int64_t phi, int64_t opponent,
               const uint8_t* mask) {
    hash128_t id;
    ASSERT_EQ(0, laplace_attestation_id_compute(&subject, &type, &object, 0, &source, nullptr, 1, &id));
    int16_t outcome;
    ASSERT_EQ(0, laplace_attestation_outcome_from_totals_fp(games, sum, &outcome));
    ASSERT_EQ(0, intent_stage_add_attestation_mode(stage, &id, &subject, &type, &object, &source, nullptr,
        outcome, ts, games, sum, phi, opponent, 1, mask));
}

std::vector<uint8_t> prior_copy(const hash128_t& cell, const hash128_t& subject,
                                int64_t rating, int64_t rd, int64_t volatility) {
    std::vector<uint8_t> out(reinterpret_cast<const uint8_t*>("PGCOPY\n\377\r\n\0"),
                             reinterpret_cast<const uint8_t*>("PGCOPY\n\377\r\n\0") + 11);
    auto put32 = [&](uint32_t v) { for (int s = 24; s >= 0; s -= 8) out.push_back(uint8_t(v >> s)); };
    auto put64 = [&](int64_t v) { for (int s = 56; s >= 0; s -= 8) out.push_back(uint8_t(uint64_t(v) >> s)); };
    put32(0); put32(0);
    out.push_back(0); out.push_back(5);
    put32(16); out.insert(out.end(), reinterpret_cast<const uint8_t*>(&cell), reinterpret_cast<const uint8_t*>(&cell) + 16);
    put32(16); out.insert(out.end(), reinterpret_cast<const uint8_t*>(&subject), reinterpret_cast<const uint8_t*>(&subject) + 16);
    put32(8); put64(rating);
    put32(8); put64(rd);
    put32(8); put64(volatility);
    out.push_back(0xff); out.push_back(0xff);
    return out;
}

hash128_t cell_id(const hash128_t& s, const hash128_t& t, const hash128_t& o) {
    uint8_t buf[48];
    std::memcpy(buf, &s, 16);
    std::memcpy(buf + 16, &t, 16);
    std::memcpy(buf + 32, &o, 16);
    hash128_t h;
    hash128_blake3(buf, sizeof(buf), &h);
    return h;
}

}  // namespace

// A lane owns whole PostgreSQL HASH partitions: the partition of a bytea key is
// PostgreSQL's (hash_bytes_extended seeded with HASH_PARTITION_SEED, hash_combine64,
// modulo 64). The expected remainders are satisfies_hash_partition's answers.
TEST(LaplaceSourceReduce, RoutesRowsToTheirPostgresHashPartition) {
    const std::array<std::pair<uint8_t, uint32_t>, 3> expected{{{0x00, 47}, {0x10, 4}, {0x50, 44}}};
    intent_stage_t* stage = intent_stage_new(8);
    ASSERT_NE(nullptr, stage);
    const hash128_t type = id_of("source-reduce/type");
    for (auto [first, partition] : expected) {
        const hash128_t id = id_bytes(first);
        ASSERT_EQ(0, intent_stage_add_entity(stage, &id, 1, &type));
    }
    laplace_source_reduce_t* r = laplace_source_reduce_new();
    ASSERT_EQ(0, laplace_source_reduce_add_stage(r, stage));
    ASSERT_EQ(0, laplace_source_reduce_partitions(r, 64, 64, 64));
    ASSERT_EQ(0, laplace_source_reduce_route(r, 64));
    for (auto [first, partition] : expected) {
        auto rows = stream(r, LAPLACE_SOURCE_REDUCE_ENTITIES, partition);
        ASSERT_EQ(1u, rows.size()) << "partition " << partition;
        EXPECT_TRUE(same_id(rows[0].fields[0], id_bytes(first)));
        ASSERT_EQ(5u, rows[0].fields.size());
        EXPECT_EQ(0, rows[0].fields[3][0]) << "no physicality realizes the entity";
    }
    laplace_source_reduce_free(r);
    intent_stage_free(stage);
}

// One row per identity: the first entity row, and one claim per attestation id with its
// games and score added, its qualifiers OR-ed and its outcome classified from the totals.
TEST(LaplaceSourceReduce, ReducesRepeatedIdentities) {
    const hash128_t subject = id_of("source-reduce/subject");
    const hash128_t object = id_of("source-reduce/object");
    const hash128_t type = id_of("source-reduce/relation");
    const hash128_t source = id_of("source-reduce/witness");
    uint8_t reference[32] = {}, print[32] = {};
    reference[4] = 0x04;
    print[4] = 0x08;
    intent_stage_t* a = intent_stage_new(4);
    intent_stage_t* b = intent_stage_new(4);
    ASSERT_EQ(0, intent_stage_add_entity(a, &subject, 2, &type));
    ASSERT_EQ(0, intent_stage_add_entity(b, &subject, 3, &type));
    add_claim(a, subject, type, object, source, 100, 1, 1000000000, 7, 1600000000000, reference);
    add_claim(b, subject, type, object, source, 200, 1, 0, 7, 1600000000000, print);

    laplace_source_reduce_t* r = laplace_source_reduce_new();
    ASSERT_EQ(0, laplace_source_reduce_add_stage(r, a));
    ASSERT_EQ(0, laplace_source_reduce_add_stage(r, b));
    ASSERT_EQ(0, laplace_source_reduce_route(r, 1));
    laplace_source_reduce_stats_t stats;
    laplace_source_reduce_stats(r, &stats);
    EXPECT_EQ(2u, stats.staged_entities);
    EXPECT_EQ(1u, stats.entities);
    EXPECT_EQ(1u, stats.attestations);
    EXPECT_EQ(1u, stats.merged_attestations);

    auto entities = stream(r, LAPLACE_SOURCE_REDUCE_ENTITIES, 0);
    ASSERT_EQ(1u, entities.size());
    EXPECT_EQ(2, (entities[0].fields[1][0] << 8) | entities[0].fields[1][1]) << "first occurrence wins";

    auto claims = stream(r, LAPLACE_SOURCE_REDUCE_ATTESTATIONS, 0);
    ASSERT_EQ(1u, claims.size());
    const row& claim = claims[0];
    EXPECT_EQ(LAPLACE_ATTESTATION_OUTCOME_DRAW, (claim.fields[6][0] << 8) | claim.fields[6][1]);
    EXPECT_EQ(200 - INTENT_STAGE_PG_EPOCH_UNIX_US, be64(claim.fields[7])) << "the latest observation";
    EXPECT_EQ(2, be64(claim.fields[8]));
    EXPECT_EQ(1000000000, be64(claim.fields[9]));
    ASSERT_EQ(32u, claim.fields[13].size());
    EXPECT_EQ(0x0c, claim.fields[13][4]) << "qualifiers are OR-ed";
    laplace_source_reduce_free(r);
    intent_stage_free(a);
    intent_stage_free(b);
}

// A cell folds one Glicko-2 rating period per witness from its prior standing, exactly
// as glicko2_fold_uniform_period defines the period; witnesses fold in id order.
TEST(LaplaceSourceReduce, FoldsOnePeriodPerWitnessFromPriorStanding) {
    const hash128_t subject = id_of("source-reduce/fold/subject");
    const hash128_t object = id_of("source-reduce/fold/object");
    const hash128_t type = id_of("source-reduce/fold/relation");
    hash128_t w1 = id_bytes(0x10), w2 = id_bytes(0x20);
    const int64_t phi = 120000000000, opponent = 1700000000000;
    intent_stage_t* stage = intent_stage_new(4);
    add_claim(stage, subject, type, object, w2, 5, 1, 0, phi, opponent, nullptr);
    add_claim(stage, subject, type, object, w1, 5, 3, 3000000000, phi, opponent, nullptr);

    laplace_source_reduce_t* r = laplace_source_reduce_new();
    ASSERT_EQ(0, laplace_source_reduce_add_stage(r, stage));
    ASSERT_EQ(0, laplace_source_reduce_route(r, 1));
    ASSERT_EQ(0, laplace_source_reduce_admit(r, 0, nullptr, 0));
    uint64_t cells = 0;
    ASSERT_EQ(0, laplace_source_reduce_cells(r, 0, &cells));
    ASSERT_EQ(1u, cells);

    // Novel: the neutral prior, then w1's period, then w2's.
    ASSERT_EQ(0, laplace_source_reduce_fold(r, 0, nullptr, 0));
    glicko2_state_t expected;
    glicko2_init(&expected, kNeutralMu, kInitialRd, kInitialVolatility);
    ASSERT_EQ(0, glicko2_fold_uniform_period(&expected, opponent, phi, 3, 3000000000, LAPLACE_GLICKO2_DEFAULT_TAU, 0));
    ASSERT_EQ(0, glicko2_fold_uniform_period(&expected, opponent, phi, 1, 0, LAPLACE_GLICKO2_DEFAULT_TAU, 0));
    auto novel = stream(r, LAPLACE_SOURCE_REDUCE_CONSENSUS, 0);
    ASSERT_EQ(1u, novel.size());
    EXPECT_TRUE(same_id(novel[0].fields[0], cell_id(subject, type, object)));
    EXPECT_EQ(expected.rating, be64(novel[0].fields[4]));
    EXPECT_EQ(expected.rd, be64(novel[0].fields[5]));
    EXPECT_EQ(expected.volatility, be64(novel[0].fields[6]));
    EXPECT_EQ(4, be64(novel[0].fields[7]));
    EXPECT_TRUE(stream(r, LAPLACE_SOURCE_REDUCE_STANDING, 0).empty());

    // Matched: the same periods on the stored standing.
    const int64_t rating = 1650000000000, rd = 90000000000, volatility = 59000000;
    auto priors = prior_copy(cell_id(subject, type, object), subject, rating, rd, volatility);
    ASSERT_EQ(0, laplace_source_reduce_fold(r, 0, priors.data(), priors.size()));
    glicko2_init(&expected, rating, rd, volatility);
    ASSERT_EQ(0, glicko2_fold_uniform_period(&expected, opponent, phi, 3, 3000000000, LAPLACE_GLICKO2_DEFAULT_TAU, 0));
    ASSERT_EQ(0, glicko2_fold_uniform_period(&expected, opponent, phi, 1, 0, LAPLACE_GLICKO2_DEFAULT_TAU, 0));
    EXPECT_TRUE(stream(r, LAPLACE_SOURCE_REDUCE_CONSENSUS, 0).empty());
    auto standing = stream(r, LAPLACE_SOURCE_REDUCE_STANDING, 0);
    ASSERT_EQ(1u, standing.size());
    EXPECT_EQ(expected.rating, be64(standing[0].fields[4]));
    EXPECT_EQ(expected.rd, be64(standing[0].fields[5]));
    EXPECT_EQ(expected.volatility, be64(standing[0].fields[6]));
    EXPECT_EQ(4, be64(standing[0].fields[7])) << "this source's games; the stored count adds in SQL";

    laplace_source_reduce_stats_t stats;
    laplace_source_reduce_stats(r, &stats);
    EXPECT_EQ(1u, stats.cells) << "a refold replaces the lane's previous fold";
    EXPECT_EQ(4u, stats.observations);
    laplace_source_reduce_free(r);
    intent_stage_free(stage);
}

// Only admitted claims fold: a claim PostgreSQL already stored is a replay.
TEST(LaplaceSourceReduce, FoldsOnlyAdmittedClaims) {
    const hash128_t subject = id_of("source-reduce/admit/subject");
    const hash128_t type = id_of("source-reduce/admit/relation");
    const hash128_t source = id_of("source-reduce/admit/witness");
    const hash128_t kept = id_of("source-reduce/admit/kept"), replayed = id_of("source-reduce/admit/replayed");
    intent_stage_t* stage = intent_stage_new(4);
    add_claim(stage, subject, type, kept, source, 1, 1, 1000000000, 7, 1600000000000, nullptr);
    add_claim(stage, subject, type, replayed, source, 1, 1, 1000000000, 7, 1600000000000, nullptr);
    laplace_source_reduce_t* r = laplace_source_reduce_new();
    ASSERT_EQ(0, laplace_source_reduce_add_stage(r, stage));
    ASSERT_EQ(0, laplace_source_reduce_route(r, 1));

    hash128_t kept_id;
    ASSERT_EQ(0, laplace_attestation_id_compute(&subject, &type, &kept, 0, &source, nullptr, 1, &kept_id));
    std::vector<uint8_t> admitted(reinterpret_cast<const uint8_t*>("PGCOPY\n\377\r\n\0"),
                                  reinterpret_cast<const uint8_t*>("PGCOPY\n\377\r\n\0") + 11);
    for (int i = 0; i < 8; ++i) admitted.push_back(0);
    admitted.insert(admitted.end(), {0, 1, 0, 0, 0, 16});
    admitted.insert(admitted.end(), reinterpret_cast<const uint8_t*>(&kept_id), reinterpret_cast<const uint8_t*>(&kept_id) + 16);
    admitted.insert(admitted.end(), {0xff, 0xff});
    ASSERT_EQ(0, laplace_source_reduce_admit(r, 0, admitted.data(), admitted.size()));
    uint64_t cells = 0;
    ASSERT_EQ(0, laplace_source_reduce_cells(r, 0, &cells));
    EXPECT_EQ(1u, cells);
    ASSERT_EQ(0, laplace_source_reduce_admit(r, 0, nullptr, 0));
    ASSERT_EQ(0, laplace_source_reduce_cells(r, 0, &cells));
    EXPECT_EQ(2u, cells);

    // Without a loaded Highway registry every claimed relation is a dynamic pair.
    const hash128_t* pairs = nullptr;
    size_t unresolved = 0;
    ASSERT_EQ(0, laplace_source_reduce_masks(r, &pairs, &unresolved));
    EXPECT_EQ(3u, unresolved) << "subject once, each object once";
    laplace_source_reduce_free(r);
    intent_stage_free(stage);
}
