#include <gtest/gtest.h>

#include <cstdint>
#include <cstring>

#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"

namespace {

// Regression guard for the forged-duplicate-physicality bug: composing the same
// content once forged 319 chess-move physicality rows with identical coords but
// float-divergent trajectories, because a compose path hashed the centroid coord
// and trajectory INTO the physicality id. Identity is (entity_id,
// physicality_type) ONLY -- geometry is payload and MUST NOT enter the id. The
// fix gave the one shared laplace_physicality_id_compute no geometry parameter;
// these tests pin that contract so a future edit that re-introduces geometry into
// the id fails here, loudly, instead of silently minting duplicates on re-ingest.

TEST(LaplacePhysicalityId, IsHashOfEntityIdAndTypeOnly) {
    hash128_t entity = { 0x0123456789abcdefULL, 0xfedcba9876543210ULL };
    hash128_t got;
    laplace_physicality_id_compute(entity, 1, &got);

    // The exact, geometry-free serialization: blake3(entity_id[16] || type[2]).
    uint8_t buf[18];
    std::memcpy(buf, &entity, 16);
    int16_t type = 1;
    std::memcpy(buf + 16, &type, 2);
    hash128_t expected;
    hash128_blake3(buf, sizeof(buf), &expected);

    EXPECT_TRUE(hash128_equals(&got, &expected));
}

TEST(LaplacePhysicalityId, DeterministicAcrossCalls) {
    hash128_t entity = { 42u, 99u };
    hash128_t a, b;
    laplace_physicality_id_compute(entity, 1, &a);
    laplace_physicality_id_compute(entity, 1, &b);
    EXPECT_TRUE(hash128_equals(&a, &b));
}

TEST(LaplacePhysicalityId, SameEntityAndTypeCollide_DifferentTypeDoesNot) {
    // Any compose path reaching the same (entity id, type) mints the SAME
    // physicality id -- that collision IS the cross-path dedup the geometry-
    // hashing bug broke. A different physicality_type must still diverge.
    hash128_t entity = { 7u, 7u };
    hash128_t t1a, t1b, t2;
    laplace_physicality_id_compute(entity, 1, &t1a);
    laplace_physicality_id_compute(entity, 1, &t1b);
    laplace_physicality_id_compute(entity, 2, &t2);
    EXPECT_TRUE(hash128_equals(&t1a, &t1b));
    EXPECT_FALSE(hash128_equals(&t1a, &t2));
}

}  // namespace
