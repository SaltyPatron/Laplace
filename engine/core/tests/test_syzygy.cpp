#include <gtest/gtest.h>

#include <cstdint>
#include <cstring>
#include <string>

#include "laplace/core/syzygy.h"

// Committed fixture set: the two smallest 3-men tables (KQvK / KRvK, WDL +
// DTZ, ~14 KB total) under test-data/syzygy — see the README there for origin
// and checksums. LAPLACE_SYZYGY_FIXTURE_DIR is injected by tests/CMakeLists.
//
// gtest_discover_tests runs every TEST as its own ctest process, so no state
// crosses tests: probe tests init through the fixture's SetUp each time.

namespace {

// Minimal FEN -> Fathom bitboard converter for the fixtures (a1=bit0..h8=63).
struct ProbePos {
    uint64_t white = 0, black = 0, kings = 0, queens = 0, rooks = 0,
             bishops = 0, knights = 0, pawns = 0;
    bool white_to_move = true;
};

ProbePos from_fen(const std::string& fen) {
    ProbePos p;
    int rank = 7, file = 0;
    size_t i = 0;
    for (; i < fen.size() && fen[i] != ' '; i++) {
        char c = fen[i];
        if (c == '/') { rank--; file = 0; continue; }
        if (c >= '1' && c <= '8') { file += c - '0'; continue; }
        uint64_t bit = 1ULL << (rank * 8 + file);
        bool is_white = (c >= 'A' && c <= 'Z');
        (is_white ? p.white : p.black) |= bit;
        switch (c | 0x20) {
            case 'k': p.kings |= bit; break;
            case 'q': p.queens |= bit; break;
            case 'r': p.rooks |= bit; break;
            case 'b': p.bishops |= bit; break;
            case 'n': p.knights |= bit; break;
            case 'p': p.pawns |= bit; break;
            default: ADD_FAILURE() << "bad FEN piece " << c;
        }
        file++;
    }
    p.white_to_move = fen[i + 1] == 'w';
    return p;
}

int probe_wdl(const ProbePos& p) {
    return laplace_syzygy_probe_wdl(
        p.white, p.black, p.kings, p.queens, p.rooks, p.bishops, p.knights,
        p.pawns, 0, p.white_to_move ? 1 : 0);
}

int probe_root(const ProbePos& p, int* wdl, int* dtz) {
    int from = -1, to = -1, promotes = -1;
    return laplace_syzygy_probe_root(
        p.white, p.black, p.kings, p.queens, p.rooks, p.bishops, p.knights,
        p.pawns, 0, p.white_to_move ? 1 : 0, wdl, dtz,
        &from, &to, &promotes);
}

class LaplaceCoreSyzygyFixture : public ::testing::Test {
protected:
    void SetUp() override {
        ASSERT_EQ(laplace_syzygy_init(LAPLACE_SYZYGY_FIXTURE_DIR), 3);
        ASSERT_EQ(laplace_syzygy_largest(), 3);
    }
    void TearDown() override { laplace_syzygy_free(); }
};

}  // namespace

TEST(LaplaceCoreSyzygy, UninitializedProbeFailsCleanly) {
    laplace_syzygy_free();
    EXPECT_EQ(laplace_syzygy_largest(), 0);
    EXPECT_EQ(probe_wdl(from_fen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1")), -1);
    EXPECT_EQ(laplace_syzygy_init(nullptr), -1);
    EXPECT_EQ(laplace_syzygy_init(""), -1);
}

TEST(LaplaceCoreSyzygy, EmptyDirYieldsZeroLargest) {
    // A path with no tables is init success with an empty set (unattested !=
    // attested-false): probes then fail per-position, never crash.
    EXPECT_EQ(laplace_syzygy_init("."), 0);
    EXPECT_EQ(laplace_syzygy_largest(), 0);
    laplace_syzygy_free();
}

TEST_F(LaplaceCoreSyzygyFixture, KQvK_WhiteToMoveWins) {
    EXPECT_EQ(probe_wdl(from_fen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1")),
              LAPLACE_SYZYGY_WIN);
}

TEST_F(LaplaceCoreSyzygyFixture, KQvK_BlackToMoveLoses) {
    EXPECT_EQ(probe_wdl(from_fen("4k3/8/8/8/8/8/8/3QK3 b - - 0 1")),
              LAPLACE_SYZYGY_LOSS);
}

TEST_F(LaplaceCoreSyzygyFixture, KRvK_HangingRookIsDraw) {
    // Black to move, in check from Rb3, captures the undefended rook -> KvK.
    EXPECT_EQ(probe_wdl(from_fen("8/8/8/8/1k6/1R6/8/K7 b - - 0 1")),
              LAPLACE_SYZYGY_DRAW);
}

TEST_F(LaplaceCoreSyzygyFixture, KRvK_WhiteToMoveWins) {
    EXPECT_EQ(probe_wdl(from_fen("4k3/8/8/8/8/8/8/R3K3 w - - 0 1")),
              LAPLACE_SYZYGY_WIN);
}

TEST_F(LaplaceCoreSyzygyFixture, RootProbeReturnsWdlAndDtz) {
    int wdl = -1, dtz = -1;
    ASSERT_EQ(probe_root(from_fen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1"),
                         &wdl, &dtz), 0);
    EXPECT_EQ(wdl, LAPLACE_SYZYGY_WIN);
    EXPECT_GE(dtz, 1);   // a zeroing (mating) sequence exists and is not instant
    EXPECT_LE(dtz, 20);  // KQvK mates well inside the 50-move horizon
}

TEST_F(LaplaceCoreSyzygyFixture, ProbeMoreMenThanTablesFails) {
    // KQvKR is 4 men; only 3-men tables are loaded -> clean per-position fail.
    EXPECT_EQ(probe_wdl(from_fen("4k3/4r3/8/8/8/8/8/3QK3 w - - 0 1")), -1);
}
