#include <gtest/gtest.h>

#include <cmath>
#include <vector>

extern "C" {
#include "laplace/core/modality_atoms.h"
#include "laplace/core/super_fibonacci.h"
}

/*
 * Tier-0 alphabets for the non-text modalities, pinned against
 * docs/invention/modality-ladder-law.md. These are ROCK-LOCK properties: the
 * law permits an operator override of the colour order before the first image
 * seed and never after, because the order IS the S3 anchor assignment — change
 * it later and every deposited image trajectory points somewhere else.
 */

TEST(ModalityAtoms, ImageOrderIsPackedRgbRockLock) {
    // "(R<<16)|(G<<8)|B", verbatim from the law.
    EXPECT_EQ(laplace_image_atom_pack(0xFF, 0x00, 0x00), 0xFF0000u);
    EXPECT_EQ(laplace_image_atom_pack(0x00, 0xFF, 0x00), 0x00FF00u);
    EXPECT_EQ(laplace_image_atom_pack(0x00, 0x00, 0xFF), 0x0000FFu);
    EXPECT_EQ(laplace_image_atom_pack(0x12, 0x34, 0x56), 0x123456u);

    uint8_t r = 0, g = 0, b = 0;
    laplace_image_atom_unpack(0x123456u, &r, &g, &b);
    EXPECT_EQ(r, 0x12); EXPECT_EQ(g, 0x34); EXPECT_EQ(b, 0x56);

    // The packed value is the rank: red dominates green dominates blue.
    uint64_t lo = 0, hi = 0;
    ASSERT_EQ(laplace_modality_atom_rank(LAPLACE_MODALITY_IMAGE,
                laplace_image_atom_pack(0, 0, 255), &lo), 0);
    ASSERT_EQ(laplace_modality_atom_rank(LAPLACE_MODALITY_IMAGE,
                laplace_image_atom_pack(0, 1, 0), &hi), 0);
    EXPECT_LT(lo, hi);
}

TEST(ModalityAtoms, AudioIsAmplitudeOrderedOver16BitPcm) {
    // "16-bit PCM sample alphabet (65,536 atoms), amplitude order".
    EXPECT_EQ(laplace_modality_alphabet_size(LAPLACE_MODALITY_AUDIO), 65536ull);

    uint64_t rank = 0;
    ASSERT_EQ(laplace_modality_atom_rank(LAPLACE_MODALITY_AUDIO, -32768, &rank), 0);
    EXPECT_EQ(rank, 0ull);
    ASSERT_EQ(laplace_modality_atom_rank(LAPLACE_MODALITY_AUDIO, 0, &rank), 0);
    EXPECT_EQ(rank, 32768ull);
    ASSERT_EQ(laplace_modality_atom_rank(LAPLACE_MODALITY_AUDIO, 32767, &rank), 0);
    EXPECT_EQ(rank, 65535ull);

    // Monotone in amplitude across the whole alphabet.
    uint64_t prev = 0;
    ASSERT_EQ(laplace_modality_atom_rank(LAPLACE_MODALITY_AUDIO, -32768, &prev), 0);
    for (int64_t s = -32767; s <= 32767; ++s) {
        uint64_t cur = 0;
        ASSERT_EQ(laplace_modality_atom_rank(LAPLACE_MODALITY_AUDIO, s, &cur), 0);
        ASSERT_EQ(cur, prev + 1) << "amplitude order broken at sample " << s;
        prev = cur;
    }
}

TEST(ModalityAtoms, RanksRoundTripAndRejectOutOfAlphabet) {
    for (int64_t s : {int64_t{-32768}, int64_t{-1}, int64_t{0}, int64_t{32767}}) {
        uint64_t rank = 0; int64_t back = 0;
        ASSERT_EQ(laplace_modality_atom_rank(LAPLACE_MODALITY_AUDIO, s, &rank), 0);
        ASSERT_EQ(laplace_modality_atom_from_rank(LAPLACE_MODALITY_AUDIO, rank, &back), 0);
        EXPECT_EQ(back, s);
    }
    uint64_t rank = 0;
    EXPECT_EQ(laplace_modality_atom_rank(LAPLACE_MODALITY_AUDIO, -32769, &rank), -1);
    EXPECT_EQ(laplace_modality_atom_rank(LAPLACE_MODALITY_AUDIO, 32768, &rank), -1);
    EXPECT_EQ(laplace_modality_atom_rank(LAPLACE_MODALITY_IMAGE, 0x1000000, &rank), -1);
    EXPECT_EQ(laplace_modality_atom_rank(LAPLACE_MODALITY_IMAGE, -1, &rank), -1);
}

TEST(ModalityAtoms, GeometryIsOnS3AndDeterministic) {
    for (int64_t s : {int64_t{-32768}, int64_t{-1234}, int64_t{0}, int64_t{32767}}) {
        double a[4], b[4];
        hilbert128_t ha, hb;
        uint64_t ra = 0, rb = 0;
        ASSERT_EQ(laplace_modality_atom_geometry(LAPLACE_MODALITY_AUDIO, s, &ra, a, &ha), 0);
        ASSERT_EQ(laplace_modality_atom_geometry(LAPLACE_MODALITY_AUDIO, s, &rb, b, &hb), 0);

        // Same atom, same anchor — every call, no state.
        EXPECT_EQ(ra, rb);
        for (int i = 0; i < 4; ++i) EXPECT_DOUBLE_EQ(a[i], b[i]);
        EXPECT_EQ(hilbert128_compare(&ha, &hb), 0);

        // On the unit 3-sphere.
        double n2 = a[0]*a[0] + a[1]*a[1] + a[2]*a[2] + a[3]*a[3];
        EXPECT_NEAR(n2, 1.0, 1e-12);
    }
}

/*
 * The lifted single-index form must be BIT-IDENTICAL to the materialised set it
 * was lifted from. This is the whole safety argument for not building a
 * 512 MiB image coordinate table: if these ever diverge, image atoms silently
 * anchor somewhere other than where the codepoint-equivalent construction puts
 * them.
 */
TEST(ModalityAtoms, SingleIndexSuperFibonacciMatchesTheMaterialisedSet) {
    const size_t n = 4096;
    std::vector<double> all(4 * n);
    super_fibonacci(n, all.data());
    for (size_t i = 0; i < n; ++i) {
        double one[4];
        super_fibonacci_point(n, i, one);
        for (int k = 0; k < 4; ++k)
            ASSERT_DOUBLE_EQ(one[k], all[4 * i + k]) << "index " << i << " component " << k;
    }
}

TEST(ModalityAtoms, ImageGeometryResolvesAcrossTheAlphabetWithoutMaterialising) {
    // 2^24 atoms: the point is that these resolve at all, cheaply, per atom.
    for (int64_t atom : {int64_t{0}, int64_t{0x7FFFFF}, int64_t{0xFF0000}, int64_t{0xFFFFFF}}) {
        double coord[4]; hilbert128_t hb; uint64_t rank = 0;
        ASSERT_EQ(laplace_modality_atom_geometry(LAPLACE_MODALITY_IMAGE, atom, &rank, coord, &hb), 0);
        EXPECT_EQ(rank, (uint64_t)atom);
        double n2 = coord[0]*coord[0] + coord[1]*coord[1] + coord[2]*coord[2] + coord[3]*coord[3];
        EXPECT_NEAR(n2, 1.0, 1e-12);
    }
}
