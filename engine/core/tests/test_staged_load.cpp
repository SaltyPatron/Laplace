#include <gtest/gtest.h>

#include <cstring>
#include <vector>

#include "laplace/core/glicko2.h"
#include "laplace/core/hash128.h"
#include "laplace/core/staged_load.h"

namespace {

struct Copy {
    std::vector<uint8_t> b;
    Copy() {
        const uint8_t sig[11] = {'P', 'G', 'C', 'O', 'P', 'Y', '\n', 0xff, '\r', '\n', 0};
        b.insert(b.end(), sig, sig + 11);
        put32(0);
        put32(0);
    }
    void put16(uint16_t v) { b.push_back(uint8_t(v >> 8)); b.push_back(uint8_t(v)); }
    void put32(uint32_t v) { for (int s = 24; s >= 0; s -= 8) b.push_back(uint8_t(v >> s)); }
    void row(uint16_t n) { put16(n); }
    void id(uint8_t fill) { put32(16); for (int i = 0; i < 16; ++i) b.push_back(fill); }
    void null() { put32(0xffffffffu); }
    void i2(int16_t v) { put32(2); put16(uint16_t(v)); }
    void i8(int64_t v) { put32(8); for (int s = 56; s >= 0; s -= 8) b.push_back(uint8_t(uint64_t(v) >> s)); }
    void boolean(bool v) { put32(1); b.push_back(v ? 1 : 0); }
    void mask(uint8_t first) { put32(32); b.push_back(first); for (int i = 1; i < 32; ++i) b.push_back(0); }
    void end() { put16(0xffff); }
};

void claim(Copy& c, uint8_t id, int64_t ts, int64_t games, int64_t sum, uint8_t mask) {
    c.row(14);
    c.id(id); c.id(0x10); c.id(0x20); c.id(0x30); c.id(0x40); c.null();
    c.i2(2); c.i8(ts); c.i8(games); c.i8(sum); c.i8(30000000000); c.i8(1500000000000);
    c.boolean(true); c.mask(mask);
}

// Rows of a COPY stream: counts rows and returns the field bytes of every row.
std::vector<std::vector<std::vector<uint8_t>>> rows_of(const std::vector<uint8_t>& s) {
    std::vector<std::vector<std::vector<uint8_t>>> out;
    size_t at = 11 + 4;
    const uint32_t ext = (uint32_t(s[at]) << 24) | (uint32_t(s[at + 1]) << 16) | (uint32_t(s[at + 2]) << 8) | s[at + 3];
    at += 4 + ext;
    while (at + 2 <= s.size()) {
        const uint16_t n = uint16_t((s[at] << 8) | s[at + 1]);
        at += 2;
        if (n == 0xffff) break;
        std::vector<std::vector<uint8_t>> fields;
        for (uint16_t f = 0; f < n; ++f) {
            const int32_t len = int32_t((uint32_t(s[at]) << 24) | (uint32_t(s[at + 1]) << 16) | (uint32_t(s[at + 2]) << 8) | s[at + 3]);
            at += 4;
            if (len < 0) { fields.emplace_back(); continue; }
            fields.emplace_back(s.begin() + at, s.begin() + at + len);
            at += size_t(len);
        }
        out.push_back(std::move(fields));
    }
    return out;
}

int64_t be64(const std::vector<uint8_t>& v) {
    int64_t x = 0;
    for (uint8_t c : v) x = (x << 8) | c;
    return x;
}

}  // namespace

// Every observation of one claim in the source becomes one claim: games and sums add,
// qualifiers OR, the latest observation's row is kept. Slices of one byte prove that
// rows split across reads are reassembled.
TEST(StagedLoad, ClaimsSortedByIdMergePerIdAcrossAnySlicing) {
    Copy in;
    claim(in, 0x01, 100, 2, 2000000000, 0x01);
    claim(in, 0x01, 300, 1, 0, 0x04);
    claim(in, 0x02, 200, 1, 1000000000, 0x02);
    in.end();

    laplace_staged_claims_t* m = laplace_staged_claims_new();
    ASSERT_NE(nullptr, m);
    std::vector<uint8_t> out;
    for (size_t i = 0; i < in.b.size(); ++i) {
        ASSERT_EQ(0, laplace_staged_claims_feed(m, &in.b[i], 1, i + 1 == in.b.size()))
            << laplace_staged_claims_error(m);
        size_t len = 0;
        const uint8_t* p = laplace_staged_claims_output(m, &len);
        out.insert(out.end(), p, p + len);
        laplace_staged_claims_consume(m);
    }
    uint64_t rows_in = 0, rows_out = 0;
    laplace_staged_claims_counts(m, &rows_in, &rows_out);
    EXPECT_EQ(3u, rows_in);
    EXPECT_EQ(2u, rows_out);
    laplace_staged_claims_free(m);

    auto rows = rows_of(out);
    ASSERT_EQ(2u, rows.size());
    EXPECT_EQ(3, be64(rows[0][8]));            // games 2 + 1
    EXPECT_EQ(2000000000, be64(rows[0][9]));   // sums added
    EXPECT_EQ(300, be64(rows[0][7]));          // latest observation
    EXPECT_EQ(0x05, rows[0][13][0]);           // qualifiers OR-ed
    EXPECT_EQ(1, be64(rows[1][8]));
}

TEST(StagedLoad, UnsortedClaimsAreRejected) {
    Copy in;
    claim(in, 0x02, 100, 1, 0, 0);
    claim(in, 0x01, 100, 1, 0, 0);
    in.end();
    laplace_staged_claims_t* m = laplace_staged_claims_new();
    EXPECT_NE(0, laplace_staged_claims_feed(m, in.b.data(), in.b.size(), 1));
    EXPECT_NE(nullptr, std::strstr(laplace_staged_claims_error(m), "sorted"));
    laplace_staged_claims_free(m);
}

// One witness is one rating period on the cell's prior standing; a novel cell starts
// from the neutral prior. The result equals the Glicko-2 kernel applied directly.
TEST(StagedLoad, ScoreFoldsOnePeriodPerWitnessOnPriorStanding) {
    const int64_t rating = 1500000000000, rd = 350000000000, vol = 60000000;
    const int64_t opp = 1500000000000, phi = 30000000000;
    Copy in;
    // Cell A (novel): one witness, two groups with equal opponent state merge.
    for (int k = 0; k < 2; ++k) {
        in.row(12);
        in.id(0x10); in.id(0x20); in.id(0x30); in.id(0x40);
        in.i8(opp); in.i8(phi); in.i8(1); in.i8(1000000000); in.i8(50 + k);
        in.null(); in.null(); in.null();
    }
    // Cell B (prior standing).
    in.row(12);
    in.id(0x11); in.id(0x20); in.null(); in.id(0x40);
    in.i8(opp); in.i8(phi); in.i8(3); in.i8(3000000000); in.i8(70);
    in.i8(1600000000000); in.i8(100000000000); in.i8(vol);
    in.end();

    laplace_staged_score_t* s = laplace_staged_score_new();
    ASSERT_EQ(0, laplace_staged_score_feed(s, in.b.data(), in.b.size(), 1)) << laplace_staged_score_error(s);
    size_t nl = 0, sl = 0;
    const uint8_t* np = laplace_staged_score_output(s, 0, &nl);
    std::vector<uint8_t> novel(np, np + nl);
    const uint8_t* sp = laplace_staged_score_output(s, 1, &sl);
    std::vector<uint8_t> standing(sp, sp + sl);
    uint64_t cells = 0, games = 0;
    laplace_staged_score_counts(s, &cells, &games);
    laplace_staged_score_free(s);
    EXPECT_EQ(2u, cells);
    EXPECT_EQ(5u, games);

    glicko2_state_t a;
    glicko2_init(&a, rating, rd, vol);
    ASSERT_EQ(0, glicko2_fold_uniform_period(&a, opp, phi, 2, 2000000000, LAPLACE_GLICKO2_DEFAULT_TAU, 0));
    auto n = rows_of(novel);
    ASSERT_EQ(1u, n.size());
    EXPECT_EQ(a.rating, be64(n[0][4]));
    EXPECT_EQ(a.rd, be64(n[0][5]));
    EXPECT_EQ(2, be64(n[0][7]));
    EXPECT_EQ(51, be64(n[0][8]));

    glicko2_state_t b;
    glicko2_init(&b, 1600000000000, 100000000000, vol);
    ASSERT_EQ(0, glicko2_fold_uniform_period(&b, opp, phi, 3, 3000000000, LAPLACE_GLICKO2_DEFAULT_TAU, 0));
    auto p = rows_of(standing);
    ASSERT_EQ(1u, p.size());
    uint8_t key[48];
    std::memset(key, 0x11, 16);
    std::memset(key + 16, 0x20, 16);
    std::memset(key + 32, 0, 16);
    hash128_t cell;
    hash128_blake3(key, sizeof(key), &cell);
    EXPECT_EQ(0, std::memcmp(p[0][0].data(), &cell, 16));
    EXPECT_EQ(b.rating, be64(p[0][4]));
    EXPECT_TRUE(p[0][3].empty());
}
