#include <gtest/gtest.h>

#include <cstdint>
#include <cstring>
#include <vector>
#include <algorithm>
#include <memory>
#include <string>
#include "laplace/core/tier_tree.h"

#include "laplace/core/intent_stage.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/trajectory.h"

namespace {

constexpr uint8_t kSig[11] = {
    'P', 'G', 'C', 'O', 'P', 'Y', '\n', 0xff, '\r', '\n', '\0'
};
constexpr size_t kHeader  = 19;
constexpr size_t kTrailer = 2;

uint16_t read_be16(const uint8_t* p) {
    return (uint16_t)((uint32_t)p[0] << 8 | (uint32_t)p[1]);
}

uint32_t read_be32(const uint8_t* p) {
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16)
         | ((uint32_t)p[2] << 8)  | (uint32_t)p[3];
}

uint64_t read_be64(const uint8_t* p) {
    uint64_t hi = read_be32(p);
    uint64_t lo = read_be32(p + 4);
    return (hi << 32) | lo;
}

double read_be_double(const uint8_t* p) {
    uint64_t bits = read_be64(p);
    double d;
    std::memcpy(&d, &bits, sizeof(d));
    return d;
}

uint32_t read_le_u32(const uint8_t* p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8)
         | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

double read_le_double(const uint8_t* p) {
    uint64_t bits = 0;
    for (int i = 0; i < 8; ++i) bits |= (uint64_t)p[i] << (i * 8);
    double d;
    std::memcpy(&d, &bits, sizeof(d));
    return d;
}

hash128_t make_hash(uint8_t fill) {
    hash128_t h;
    std::memset(&h, fill, sizeof(h));
    return h;
}

}

TEST(LaplaceCoreIntentStage, NewWithZeroCapacityIsValid) {
    intent_stage_t* s = intent_stage_new(0);
    ASSERT_NE(nullptr, s);
    EXPECT_EQ(0u, intent_stage_entity_count(s));
    EXPECT_EQ(0u, intent_stage_physicality_count(s));
    EXPECT_EQ(0u, intent_stage_attestation_count(s));
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, FreeNullIsSafe) {
    intent_stage_free(nullptr);
    SUCCEED();
}

TEST(LaplaceCoreIntentStage, ColumnListsAreStableAndCorrect) {
    EXPECT_STREQ("id, tier, type_id, first_observed_by",
                 intent_stage_copy_column_list(INTENT_STAGE_TABLE_ENTITIES));
    EXPECT_NE(nullptr, intent_stage_copy_column_list(INTENT_STAGE_TABLE_PHYSICALITIES));
    EXPECT_NE(nullptr, intent_stage_copy_column_list(INTENT_STAGE_TABLE_ATTESTATIONS));
    EXPECT_EQ(nullptr, intent_stage_copy_column_list((intent_stage_table_t)999));
}

TEST(LaplaceCoreIntentStage, EmptyStreamHasHeaderAndTrailerOnly) {
    intent_stage_t* s = intent_stage_new(0);
    ASSERT_NE(nullptr, s);
    const size_t required = intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ENTITIES, nullptr, 0);
    EXPECT_EQ(kHeader + kTrailer, required);

    std::vector<uint8_t> buf(required);
    const size_t written = intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ENTITIES,
                                                        buf.data(), buf.size());
    EXPECT_EQ(required, written);
    EXPECT_EQ(0, std::memcmp(buf.data(), kSig, sizeof(kSig)));
    EXPECT_EQ(0u, read_be32(buf.data() + 11));
    EXPECT_EQ(0u, read_be32(buf.data() + 15));
    EXPECT_EQ(0xff, buf[buf.size() - 2]);
    EXPECT_EQ(0xff, buf[buf.size() - 1]);
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, AddEntityRejectsInvalidArgs) {
    intent_stage_t* s = intent_stage_new(0);
    ASSERT_NE(nullptr, s);
    hash128_t id = make_hash(0xAA);
    hash128_t type_id = make_hash(0xBB);
    EXPECT_NE(0, intent_stage_add_entity(nullptr, &id, 0, &type_id, nullptr));
    EXPECT_NE(0, intent_stage_add_entity(s, nullptr, 0, &type_id, nullptr));
    EXPECT_NE(0, intent_stage_add_entity(s, &id, 0, nullptr, nullptr));
    EXPECT_NE(0, intent_stage_add_entity(s, &id, -1, &type_id, nullptr));
    EXPECT_NE(0, intent_stage_add_entity(s, &id, 256, &type_id, nullptr));
    EXPECT_EQ(0u, intent_stage_entity_count(s));
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, AddEntityEncodesOneRowExactly) {
    intent_stage_t* s = intent_stage_new(1);
    ASSERT_NE(nullptr, s);
    hash128_t id = make_hash(0x11);
    hash128_t type_id = make_hash(0x22);
    ASSERT_EQ(0, intent_stage_add_entity(s, &id, 5, &type_id, nullptr));
    EXPECT_EQ(1u, intent_stage_entity_count(s));

    const size_t need = intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ENTITIES, nullptr, 0);
    std::vector<uint8_t> buf(need);
    ASSERT_EQ(need, intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ENTITIES,
                                                 buf.data(), buf.size()));

    ASSERT_EQ(73u, need);
    EXPECT_EQ(4, (int16_t)read_be16(buf.data() + 19));
    EXPECT_EQ(16u, read_be32(buf.data() + 21));
    for (int i = 0; i < 16; ++i) EXPECT_EQ(0x11, buf[25 + i]);
    EXPECT_EQ(2u, read_be32(buf.data() + 41));
    EXPECT_EQ(5, (int16_t)read_be16(buf.data() + 45));
    EXPECT_EQ(16u, read_be32(buf.data() + 47));
    for (int i = 0; i < 16; ++i) EXPECT_EQ(0x22, buf[51 + i]);
    EXPECT_EQ((uint32_t)-1, read_be32(buf.data() + 67));
    EXPECT_EQ(0xff, buf[71]);
    EXPECT_EQ(0xff, buf[72]);
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, AddEntityFirstObservedByPopulated) {
    intent_stage_t* s = intent_stage_new(1);
    ASSERT_NE(nullptr, s);
    hash128_t id = make_hash(0x10);
    hash128_t type_id = make_hash(0x20);
    hash128_t source = make_hash(0x30);
    ASSERT_EQ(0, intent_stage_add_entity(s, &id, 0, &type_id, &source));
    const size_t need = intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ENTITIES, nullptr, 0);
    std::vector<uint8_t> buf(need);
    ASSERT_EQ(need, intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ENTITIES,
                                                 buf.data(), buf.size()));
    EXPECT_EQ(16u, read_be32(buf.data() + 67));
    for (int i = 0; i < 16; ++i) EXPECT_EQ(0x30, buf[71 + i]);
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, BufferTooSmallReturnsRequiredCount) {
    intent_stage_t* s = intent_stage_new(0);
    ASSERT_NE(nullptr, s);
    uint8_t small[4];
    const size_t r = intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ENTITIES, small, sizeof(small));
    EXPECT_EQ(kHeader + kTrailer, r);
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, MultipleEntitiesAccumulate) {
    intent_stage_t* s = intent_stage_new(3);
    ASSERT_NE(nullptr, s);
    for (uint8_t i = 0; i < 3; ++i) {
        hash128_t id = make_hash(i);
        hash128_t t  = make_hash((uint8_t)(0x80 | i));
        ASSERT_EQ(0, intent_stage_add_entity(s, &id, (int16_t)i, &t, nullptr));
    }
    EXPECT_EQ(3u, intent_stage_entity_count(s));
    const size_t need = intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ENTITIES, nullptr, 0);
    EXPECT_EQ(177u, need);
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, AddPhysicalityRoundTripsAllFields) {
    intent_stage_t* s = intent_stage_new(1);
    ASSERT_NE(nullptr, s);
    hash128_t id{}, eid{};
    double coord[4] = { 0.25, 0.5, 0.75, 1.0 };
    hilbert128_t hb;
    for (int i = 0; i < 16; ++i) hb.bytes[i] = (uint8_t)(0x40 + i);
    const hash128_t members[] = {make_hash(0x02), make_hash(0x03)};
    double traj[8];
    ASSERT_EQ(0, trajectory_build(members, 2u, traj));
    size_t logical = 0u;
    ASSERT_EQ(0, trajectory_content_identity(traj, 2u, &eid, &logical));
    ASSERT_EQ(logical, 2u);
    laplace_physicality_id_compute(eid, 1, &id);
    ASSERT_EQ(0, intent_stage_add_physicality(
        s, &id, &eid,
        1,
        coord, &hb,
        traj, 2,
        2,
        0,
        0.0125,
        1,
        0,
        INTENT_STAGE_PG_EPOCH_UNIX_US + 1234567));

    const size_t need = intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_PHYSICALITIES,
                                                     nullptr, 0);
    std::vector<uint8_t> buf(need);
    ASSERT_EQ(need, intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_PHYSICALITIES,
                                                  buf.data(), buf.size()));

    const uint8_t* p = buf.data() + kHeader;
    EXPECT_EQ(10, (int16_t)read_be16(p)); p += 2;
    EXPECT_EQ(16u, read_be32(p)); p += 4;
    EXPECT_EQ(0, std::memcmp(&id, p, 16u)); p += 16u;
    EXPECT_EQ(16u, read_be32(p)); p += 4;
    EXPECT_EQ(0, std::memcmp(&eid, p, 16u)); p += 16u;
    EXPECT_EQ(2u, read_be32(p)); p += 4;
    EXPECT_EQ(1, (int16_t)read_be16(p)); p += 2;
    EXPECT_EQ(37u, read_be32(p)); p += 4;
    EXPECT_EQ(0x01, *p++);
    EXPECT_EQ(0xC0000001u, read_le_u32(p)); p += 4;
    EXPECT_EQ(0.25, read_le_double(p)); p += 8;
    EXPECT_EQ(0.5,  read_le_double(p)); p += 8;
    EXPECT_EQ(0.75, read_le_double(p)); p += 8;
    EXPECT_EQ(1.0,  read_le_double(p)); p += 8;
    EXPECT_EQ(16u, read_be32(p)); p += 4;
    for (int i = 0; i < 16; ++i) EXPECT_EQ((uint8_t)(0x40 + i), *p++);
    EXPECT_EQ(73u, read_be32(p)); p += 4;
    EXPECT_EQ(0x01, *p++);
    EXPECT_EQ(0xC0000002u, read_le_u32(p)); p += 4;
    EXPECT_EQ(2u, read_le_u32(p)); p += 4;
    for (int v = 0; v < 2; ++v) {
        for (int c = 0; c < 4; ++c) {
            const double expected = traj[v * 4 + c];
            EXPECT_EQ(expected, read_le_double(p));
            p += 8;
        }
    }
    EXPECT_EQ(4u, read_be32(p)); p += 4;
    EXPECT_EQ(2, (int32_t)read_be32(p)); p += 4;
    EXPECT_EQ(8u, read_be32(p)); p += 4;
    EXPECT_EQ(0.0125, read_be_double(p)); p += 8;
    EXPECT_EQ((uint32_t)-1, read_be32(p)); p += 4;
    EXPECT_EQ(8u, read_be32(p)); p += 4;
    EXPECT_EQ((int64_t)1234567, (int64_t)read_be64(p)); p += 8;

    EXPECT_EQ(0xff, *p++);
    EXPECT_EQ(0xff, *p++);
    EXPECT_EQ(buf.data() + buf.size(), p);

    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, AddPhysicalityNullTrajectoryIsValid) {
    intent_stage_t* s = intent_stage_new(1);
    ASSERT_NE(nullptr, s);
    hash128_t z = make_hash(0);
    hash128_t placement;
    laplace_physicality_id_compute(z, 1, &placement);
    double coord[4] = {0, 0, 0, 0};
    hilbert128_t hb; std::memset(&hb, 0, sizeof(hb));
    ASSERT_EQ(0, intent_stage_add_physicality(
        s, &placement, &z, 1, coord, &hb, nullptr, 0, 0,
        1, 0.0, 1, 0, 0));
    EXPECT_EQ(1u, intent_stage_physicality_count(s));
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, AddPhysicalityRejectsTrajectoryNullPointerNonZeroCount) {
    intent_stage_t* s = intent_stage_new(1);
    ASSERT_NE(nullptr, s);
    hash128_t z = make_hash(0);
    double coord[4] = {0, 0, 0, 0};
    hilbert128_t hb; std::memset(&hb, 0, sizeof(hb));
    EXPECT_NE(0, intent_stage_add_physicality(
        s, &z, &z, 1, coord, &hb, nullptr, 2, 0,
        1, 0.0, 1, 0, 0));
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, AddAttestationAllFieldsBigEndian) {
    intent_stage_t* s = intent_stage_new(1);
    ASSERT_NE(nullptr, s);
    hash128_t id  = make_hash(0xA1);
    hash128_t sub = make_hash(0xA2);
    hash128_t kid = make_hash(0xA3);
    hash128_t obj = make_hash(0xA4);
    hash128_t src = make_hash(0xA5);
    hash128_t ctx = make_hash(0xA6);
    const int16_t outcome    = 2;
    const int64_t obs_us     = INTENT_STAGE_PG_EPOCH_UNIX_US + 999;
    const int64_t obs_count  = 17;
    const int64_t sum_score  = INT64_C(17000000000);
    const int64_t opp_rd     = INT64_C(30000000000);
    // Distinct from every other field so a mis-ordered write is visible, and
    // distinct from neutral so a dropped write reads as 1500e9 rather than this.
    const int64_t opp_rating = INT64_C(1820000000000);

    ASSERT_EQ(0, intent_stage_add_attestation(
        s, &id, &sub, &kid, &obj, &src, &ctx,
        outcome, obs_us, obs_count, sum_score, opp_rd, opp_rating, NULL));

    const size_t need = intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ATTESTATIONS,
                                                     nullptr, 0);
    std::vector<uint8_t> buf(need);
    ASSERT_EQ(need, intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ATTESTATIONS,
                                                  buf.data(), buf.size()));
    const uint8_t* p = buf.data() + kHeader;
    EXPECT_EQ(14, (int16_t)read_be16(p)); p += 2;
    for (int f = 0; f < 6; ++f) {
        EXPECT_EQ(16u, read_be32(p)); p += 4;
        EXPECT_EQ((uint8_t)(0xA1 + f), *p);
        p += 16;
    }
    EXPECT_EQ(2u, read_be32(p)); p += 4;
    EXPECT_EQ(outcome, (int16_t)read_be16(p)); p += 2;
    EXPECT_EQ(8u, read_be32(p)); p += 4;
    EXPECT_EQ((int64_t)999, (int64_t)read_be64(p)); p += 8;
    EXPECT_EQ(8u, read_be32(p)); p += 4;
    EXPECT_EQ(obs_count, (int64_t)read_be64(p)); p += 8;
    EXPECT_EQ(8u, read_be32(p)); p += 4;
    EXPECT_EQ(sum_score, (int64_t)read_be64(p)); p += 8;
    EXPECT_EQ(8u, read_be32(p)); p += 4;
    EXPECT_EQ(opp_rd, (int64_t)read_be64(p)); p += 8;
    EXPECT_EQ(8u, read_be32(p)); p += 4;
    EXPECT_EQ(opp_rating, (int64_t)read_be64(p)); p += 8;
    EXPECT_EQ(1u, read_be32(p)); p += 4;
    EXPECT_EQ(1u, *p); p += 1;
    EXPECT_EQ((uint32_t)-1, read_be32(p));
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, AddAttestationNullObjectAndContext) {
    intent_stage_t* s = intent_stage_new(1);
    ASSERT_NE(nullptr, s);
    hash128_t z = make_hash(0);
    ASSERT_EQ(0, intent_stage_add_attestation(
        s, &z, &z, &z, nullptr, &z, nullptr,
        0, 0, 0, 0, 0, 0, NULL));
    const size_t need = intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ATTESTATIONS,
                                                      nullptr, 0);
    std::vector<uint8_t> buf(need);
    ASSERT_EQ(need, intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ATTESTATIONS,
                                                  buf.data(), buf.size()));
    const uint8_t* p = buf.data() + kHeader;
    p += 2;
    p += 4 + 16;
    p += 4 + 16;
    p += 4 + 16;
    EXPECT_EQ((uint32_t)-1, read_be32(p)); p += 4;
    EXPECT_EQ(16u, read_be32(p)); p += 4 + 16;
    EXPECT_EQ((uint32_t)-1, read_be32(p));
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, EachTableHasIndependentRowCount) {
    intent_stage_t* s = intent_stage_new(1);
    hash128_t z = make_hash(0);
    hash128_t placement;
    laplace_physicality_id_compute(z, 1, &placement);
    double coord[4] = {0, 0, 0, 0};
    hilbert128_t hb; std::memset(&hb, 0, sizeof(hb));
    ASSERT_EQ(0, intent_stage_add_entity(s, &z, 0, &z, nullptr));
    ASSERT_EQ(0, intent_stage_add_physicality(s, &placement, &z, 1, coord, &hb, nullptr, 0, 0, 1, 0, 1, 0, 0));
    ASSERT_EQ(0, intent_stage_add_attestation(s, &z, &z, &z, nullptr, &z, nullptr, 1, 0, 0, 0, 0, 0, NULL));
    EXPECT_EQ(1u, intent_stage_entity_count(s));
    EXPECT_EQ(1u, intent_stage_physicality_count(s));
    EXPECT_EQ(1u, intent_stage_attestation_count(s));
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, PGEpochOffsetConstantIsCorrect) {
    EXPECT_EQ(INT64_C(946684800000000), INTENT_STAGE_PG_EPOCH_UNIX_US);
}






TEST(LaplaceCoreIntentStage, AttestationGrowthFromZeroHintLargeBatchesNoCorruption) {
    for (int batch = 0; batch < 4; ++batch) {
        intent_stage_t* s = intent_stage_new(0);
        ASSERT_NE(nullptr, s);
        const size_t kRows = 250000;
        for (size_t i = 0; i < kRows; ++i) {
            hash128_t id  = make_hash((uint8_t)(i));
            hash128_t sub = make_hash((uint8_t)(i >> 8));
            hash128_t kid = make_hash((uint8_t)(i >> 16));
            hash128_t obj = make_hash((uint8_t)(i + 1));
            hash128_t src = make_hash(0x5A);
            hash128_t ctx = make_hash((uint8_t)(i + 2));
            ASSERT_EQ(0, intent_stage_add_attestation(
                s, &id, &sub, &kid, &obj, &src, &ctx,
                (int16_t)(i % 3), INTENT_STAGE_PG_EPOCH_UNIX_US + (int64_t)i,
                (int64_t)(i % 100),
                (int64_t)(i % 100) * INT64_C(500000000), INT64_C(30000000000), 0, NULL));
        }
        ASSERT_EQ(kRows, intent_stage_attestation_count(s));
        const size_t need = intent_stage_emit_copy_binary(
            s, INTENT_STAGE_TABLE_ATTESTATIONS, nullptr, 0);
        ASSERT_GT(need, kHeader + kTrailer);
        std::vector<uint8_t> buf(need);
        ASSERT_EQ(need, intent_stage_emit_copy_binary(
            s, INTENT_STAGE_TABLE_ATTESTATIONS, buf.data(), buf.size()));
        EXPECT_EQ(0xff, buf[buf.size() - 2]);
        EXPECT_EQ(0xff, buf[buf.size() - 1]);
        intent_stage_free(s);
    }
    SUCCEED();
}











TEST(LaplaceCoreIntentStage, UdBatchShapeEntitiesSurviveAttestationGrowth) {
    const size_t kEntities     = 8527;
    const size_t kPhysicalities = 8500;
    const size_t kAttestations  = 100000;

    
    intent_stage_t* s = intent_stage_new(kAttestations);
    ASSERT_NE(nullptr, s);

    
    
    for (size_t i = 0; i < kEntities; ++i) {
        hash128_t id   = make_hash((uint8_t)(i));
        hash128_t type = make_hash((uint8_t)(i >> 8));
        hash128_t fob  = make_hash((uint8_t)(i >> 4));
        const hash128_t* fobp = (i & 1) ? &fob : nullptr;
        ASSERT_EQ(0, intent_stage_add_entity(
            s, &id, (int16_t)(i % 7), &type, fobp))
            << "entity add failed at i=" << i;
    }
    ASSERT_EQ(kEntities, intent_stage_entity_count(s));

    
    
    
    
    {
        std::vector<double> traj;
        hilbert128_t hb; std::memset(&hb, 0, sizeof(hb));
        double coord[4] = {1.0, 2.0, 3.0, 4.0};
        for (size_t i = 0; i < kPhysicalities; ++i) {
            const uint32_t verts = (uint32_t)(1 + (i % 50));
            traj.resize((size_t)verts * 4);
            std::vector<hash128_t> members(verts);
            for (uint32_t v = 0; v < verts; ++v) members[v] = make_hash((uint8_t)(i + v));
            ASSERT_EQ(0, trajectory_build(members.data(), verts, traj.data()));
            hash128_t id{}, ent{};
            size_t logical = 0u;
            ASSERT_EQ(0, trajectory_content_identity(traj.data(), verts, &ent, &logical));
            ASSERT_EQ(logical, verts);
            const int16_t type = (int16_t)(1 + i % 3);
            laplace_physicality_id_compute(ent, type, &id);
            ASSERT_EQ(0, intent_stage_add_physicality(
                s, &id, &ent, type, coord, &hb,
                traj.data(), verts, (int32_t)verts,
                0, 0.5, 0, (int32_t)(1 + i % 16),
                INTENT_STAGE_PG_EPOCH_UNIX_US + (int64_t)i))
                << "physicality add failed at i=" << i;
        }
        ASSERT_EQ(kPhysicalities, intent_stage_physicality_count(s));
    }

    
    
    
    for (size_t i = 0; i < kAttestations; ++i) {
        hash128_t id  = make_hash((uint8_t)(i));
        hash128_t sub = make_hash((uint8_t)(i >> 8));
        hash128_t kid = make_hash((uint8_t)(i >> 16));
        hash128_t obj = make_hash((uint8_t)(i + 1));
        hash128_t src = make_hash(0x5A);
        hash128_t ctx = make_hash((uint8_t)(i + 2));
        const hash128_t* objp = (i & 1) ? &obj : nullptr;
        const hash128_t* ctxp = (i & 2) ? &ctx : nullptr;
        ASSERT_EQ(0, intent_stage_add_attestation(
            s, &id, &sub, &kid, objp, &src, ctxp,
            (int16_t)(i % 3), INTENT_STAGE_PG_EPOCH_UNIX_US + (int64_t)i,
            (int64_t)(i % 100),
            (int64_t)(i % 100) * INT64_C(500000000), INT64_C(30000000000), 0, NULL))
            << "attestation add failed at i=" << i;
    }
    ASSERT_EQ(kAttestations, intent_stage_attestation_count(s));

    
    
    const size_t need = intent_stage_emit_copy_binary(
        s, INTENT_STAGE_TABLE_ENTITIES, nullptr, 0);
    std::vector<uint8_t> buf(need);
    ASSERT_EQ(need, intent_stage_emit_copy_binary(
        s, INTENT_STAGE_TABLE_ENTITIES, buf.data(), buf.size()));

    EXPECT_EQ(0, std::memcmp(buf.data(), kSig, sizeof(kSig)));
    const uint8_t* p   = buf.data() + kHeader;
    const uint8_t* end = buf.data() + buf.size() - kTrailer;
    for (size_t row = 0; row < kEntities; ++row) {
        ASSERT_LE(p + 2, end) << "ran off blob at row " << row;
        const int16_t fields = (int16_t)read_be16(p);
        ASSERT_EQ(4, fields)
            << "ENTITIES corruption at row " << row
            << " byte offset " << (size_t)(p - buf.data());
        p += 2;
        
        ASSERT_EQ(16u, read_be32(p)); p += 4 + 16;   
        ASSERT_EQ(2u,  read_be32(p)); p += 4 + 2;     
        ASSERT_EQ(16u, read_be32(p)); p += 4 + 16;    
        
        const uint32_t fob_len = read_be32(p); p += 4;
        if ((row & 1) == 0) {
            ASSERT_EQ((uint32_t)-1, fob_len) << "row " << row;
        } else {
            ASSERT_EQ(16u, fob_len) << "row " << row;
            p += 16;
        }
    }
    
    EXPECT_EQ(end, p) << "entity rows did not consume the blob exactly";
    EXPECT_EQ(0xff, buf[buf.size() - 2]);
    EXPECT_EQ(0xff, buf[buf.size() - 1]);

    intent_stage_free(s);
}


/* Collect the low 8 bytes of a 16-byte field (1-based index) from every row. */
static void collect_field_lo(const intent_stage_t* s, intent_stage_table_t table,
                             uint16_t field_1based, std::vector<uint64_t>& out) {
    const size_t need = intent_stage_emit_copy_binary(s, table, nullptr, 0);
    std::vector<uint8_t> buf(need);
    EXPECT_EQ(need, intent_stage_emit_copy_binary(s, table, buf.data(), buf.size()));
    const uint8_t* p   = buf.data() + kHeader;
    const uint8_t* end = buf.data() + buf.size() - kTrailer;
    while (p + 2 <= end) {
        const uint16_t cols = read_be16(p);
        if (cols == 0xffff) break;
        p += 2;
        for (uint16_t c = 1; c <= cols; ++c) {
            const int32_t flen = (int32_t)read_be32(p); p += 4;
            if (c == field_1based) {
                ASSERT_EQ(16, flen);
                uint64_t lo = 0;
                std::memcpy(&lo, p + 8, 8);
                out.push_back(lo);
            }
            if (flen > 0) p += (size_t)flen;
        }
    }
}

TEST(LaplaceCoreIntentStage, PartitionRoutesEveryRowDisjointByIdLo) {
    intent_stage_t* s = intent_stage_new(0);
    ASSERT_NE(nullptr, s);

    const size_t kN = 4;
    const size_t kEnt = 500, kPhys = 300, kAtt = 700;
    hilbert128_t hb; std::memset(&hb, 0, sizeof(hb));
    double coord[4] = {1.0, 2.0, 3.0, 4.0};

    for (size_t i = 0; i < kEnt; ++i) {
        hash128_t id; id.hi = 0x1111; id.lo = i * 2654435761ULL + 7;
        hash128_t t = make_hash(0x22);
        ASSERT_EQ(0, intent_stage_add_entity(s, &id, (int16_t)(i % 7), &t, nullptr));
    }
    for (size_t i = 0; i < kPhys; ++i) {
        hash128_t id;
        hash128_t e; e.hi = 0x33; e.lo = i * 6700417ULL + 11;
        laplace_physicality_id_compute(e, 1, &id);
        ASSERT_EQ(0, intent_stage_add_physicality(
            s, &id, &e, 1, coord, &hb, nullptr, 0, 0, 1, 0.0, 1, 0,
            INTENT_STAGE_PG_EPOCH_UNIX_US));
    }
    for (size_t i = 0; i < kAtt; ++i) {
        hash128_t id; id.hi = 0x3333; id.lo = i * 2246822519ULL + 29;
        hash128_t sub; sub.hi = 0x44; sub.lo = i * 7919ULL + 3;
        hash128_t kid = make_hash(0x45), src = make_hash(0x46);
        ASSERT_EQ(0, intent_stage_add_attestation(
            s, &id, &sub, &kid, nullptr, &src, nullptr, 1,
            INTENT_STAGE_PG_EPOCH_UNIX_US, 1,
            INT64_C(500000000), INT64_C(30000000000), 0, NULL));
    }

    intent_stage_t* parts[kN];
    ASSERT_EQ(0, intent_stage_partition(s, kN, parts));

    size_t total_ent = 0, total_phys = 0, total_att = 0;
    for (size_t k = 0; k < kN; ++k) {
        ASSERT_NE(nullptr, parts[k]);
        total_ent  += intent_stage_entity_count(parts[k]);
        total_phys += intent_stage_physicality_count(parts[k]);
        total_att  += intent_stage_attestation_count(parts[k]);

        /* Referential-locality contract (see partition_row): entities route
         * by their own id; physicalities/attestations route by the entity
         * they reference (entity_id / subject_id — field 2), so a row and
         * the entity it's about always share a partition/transaction. */
        std::vector<uint64_t> los;
        collect_field_lo(parts[k], INTENT_STAGE_TABLE_ENTITIES, 1, los);
        for (uint64_t lo : los)
            EXPECT_EQ(k, (size_t)(lo % kN)) << "entity landed in wrong partition";
        los.clear();
        collect_field_lo(parts[k], INTENT_STAGE_TABLE_PHYSICALITIES, 2, los);
        for (uint64_t lo : los)
            EXPECT_EQ(k, (size_t)(lo % kN)) << "physicality not co-located with its entity";
        los.clear();
        collect_field_lo(parts[k], INTENT_STAGE_TABLE_ATTESTATIONS, 2, los);
        for (uint64_t lo : los)
            EXPECT_EQ(k, (size_t)(lo % kN)) << "attestation not co-located with its subject";
    }
    
    EXPECT_EQ(kEnt,  total_ent);
    EXPECT_EQ(kPhys, total_phys);
    EXPECT_EQ(kAtt,  total_att);

    for (size_t k = 0; k < kN; ++k) intent_stage_free(parts[k]);
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, PartitionCountOnePreservesAllRows) {
    intent_stage_t* s = intent_stage_new(0);
    ASSERT_NE(nullptr, s);
    for (size_t i = 0; i < 50; ++i) {
        hash128_t id; id.hi = 1; id.lo = i;
        hash128_t t = make_hash(0x22);
        ASSERT_EQ(0, intent_stage_add_entity(s, &id, 0, &t, nullptr));
    }
    intent_stage_t* parts[1];
    ASSERT_EQ(0, intent_stage_partition(s, 1, parts));
    EXPECT_EQ(50u, intent_stage_entity_count(parts[0]));
    intent_stage_free(parts[0]);
    intent_stage_free(s);
}

// Repro harness for the CILI COPY-blob corruption (attestations row truncated to
// one field short, so the next row's be16 field-count is read as a giant field
// length). Stage a CILI-shaped attestation load — mixed NULL object/context,
// 32-byte masks, and enough rows to force several arena reallocs past the initial
// reserve — then emit the COPY binary and walk it asserting EVERY row carries
// exactly 12 fields and the walk consumes the body EXACTLY. A truncated row trips
// an assertion here; a heap overrun in the arena trips ASan at the write.
TEST(LaplaceCoreIntentStage, AttestationStagingAtCiliScaleStaysAligned) {
    const size_t N = 1400000;  // ~260 MB of rows -> multiple doubling reallocs
    intent_stage_t* s = intent_stage_new(N);
    ASSERT_NE(nullptr, s);

    uint8_t mask[32];
    for (int i = 0; i < 32; ++i) mask[i] = (uint8_t)(i * 7 + 1);

    for (size_t i = 0; i < N; ++i) {
        hash128_t id, subj, type, obj, src, ctx;
        std::memset(&id,   (uint8_t)(i),       sizeof(id));
        std::memset(&subj, (uint8_t)(i >> 8),  sizeof(subj));
        std::memset(&type, (uint8_t)(i >> 16), sizeof(type));
        std::memset(&obj,  (uint8_t)(i * 3),   sizeof(obj));
        std::memset(&src,  0xAB,               sizeof(src));
        std::memset(&ctx,  (uint8_t)(i * 5),   sizeof(ctx));
        // CILI shape: some unary (NULL object), some contextless — variable row width.
        const hash128_t* obj_ptr = (i % 3 == 0) ? nullptr : &obj;
        const hash128_t* ctx_ptr = (i % 4 == 0) ? nullptr : &ctx;
        ASSERT_EQ(0, intent_stage_add_attestation(
            s, &id, &subj, &type, obj_ptr, &src, ctx_ptr,
            (int16_t)(i % 3), (int64_t)i, (int64_t)(i % 100 + 1),
            (int64_t)(i % 100 + 1) * INT64_C(500000000), INT64_C(30000000000), 0, mask));
    }
    ASSERT_EQ(N, intent_stage_attestation_count(s));

    const size_t need =
        intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ATTESTATIONS, nullptr, 0);
    std::vector<uint8_t> buf(need);
    ASSERT_EQ(need, intent_stage_emit_copy_binary(
                        s, INTENT_STAGE_TABLE_ATTESTATIONS, buf.data(), buf.size()));

    size_t off = kHeader;
    const size_t body_end = buf.size() - kTrailer;
    size_t rows = 0;
    while (off < body_end) {
        ASSERT_LE(off + 2, body_end);
        uint16_t cols = read_be16(buf.data() + off);
        ASSERT_EQ(14u, cols) << "row " << rows << " at byte " << off << " has " << cols << " cols";
        off += 2;
        for (int c = 0; c < 14; ++c) {
            ASSERT_LE(off + 4, body_end) << "row " << rows << " field " << c << " len overruns";
            int32_t flen = (int32_t)read_be32(buf.data() + off);
            off += 4;
            if (flen < 0) continue;  // SQL NULL
            ASSERT_LE(off + (size_t)flen, body_end)
                << "row " << rows << " field " << c << " data (len " << flen << ") overruns";
            off += (size_t)flen;
        }
        ++rows;
    }
    EXPECT_EQ(N, rows);
    EXPECT_EQ(body_end, off) << "walk did not consume the body exactly — a row is mis-sized";

    // The real apply validates the PARTITIONED buffers, not the source stage.
    // Partition into 12 (as the seed does) and walk each partition's emit the
    // same way — this covers intent_stage_partition / partition_one_buf / the
    // per-row copy, which is where a mis-cut boundary would land.
    const size_t P = 12;
    std::vector<intent_stage_t*> parts(P, nullptr);
    ASSERT_EQ(0, intent_stage_partition(s, P, parts.data()));
    size_t partitioned_rows = 0;
    for (size_t p = 0; p < P; ++p) {
        ASSERT_NE(nullptr, parts[p]);
        const size_t pneed =
            intent_stage_emit_copy_binary(parts[p], INTENT_STAGE_TABLE_ATTESTATIONS, nullptr, 0);
        std::vector<uint8_t> pbuf(pneed);
        ASSERT_EQ(pneed, intent_stage_emit_copy_binary(
                             parts[p], INTENT_STAGE_TABLE_ATTESTATIONS, pbuf.data(), pbuf.size()));
        size_t poff = kHeader;
        const size_t pend = pbuf.size() - kTrailer;
        while (poff < pend) {
            ASSERT_LE(poff + 2, pend);
            uint16_t pc = read_be16(pbuf.data() + poff);
            ASSERT_EQ(14u, pc) << "partition " << p << " row " << partitioned_rows
                               << " at byte " << poff << " has " << pc << " cols";
            poff += 2;
            for (int c = 0; c < 14; ++c) {
                ASSERT_LE(poff + 4, pend);
                int32_t fl = (int32_t)read_be32(pbuf.data() + poff);
                poff += 4;
                if (fl < 0) continue;
                ASSERT_LE(poff + (size_t)fl, pend)
                    << "partition " << p << " row " << partitioned_rows
                    << " field " << c << " (len " << fl << ") overruns";
                poff += (size_t)fl;
            }
            ++partitioned_rows;
        }
        EXPECT_EQ(pend, poff) << "partition " << p << " body not consumed exactly";
        intent_stage_free(parts[p]);
    }
    EXPECT_EQ(N, partitioned_rows) << "partitioning lost or duplicated rows";
    intent_stage_free(s);
}


TEST(LaplaceCoreIntentStage, SemanticDigestIgnoresOrderPartitionAndObservationClock) {
    auto* first = intent_stage_new(0);
    auto* second = intent_stage_new(0);
    auto* split = intent_stage_new(0);
    ASSERT_NE(nullptr, first); ASSERT_NE(nullptr, second); ASSERT_NE(nullptr, split);
    hash128_t a = make_hash(11), b = make_hash(12), type = make_hash(13);
    ASSERT_EQ(0, intent_stage_add_entity(first, &a, 1, &type, nullptr));
    ASSERT_EQ(0, intent_stage_add_entity(first, &b, 2, &type, nullptr));
    ASSERT_EQ(0, intent_stage_add_entity(second, &b, 2, &type, nullptr));
    ASSERT_EQ(0, intent_stage_add_entity(split, &a, 1, &type, nullptr));
    double coord[4] = {1, 0, 0, 0}; hilbert128_t hilbert{};
    hash128_t placement;
    laplace_physicality_id_compute(a, 1, &placement);
    ASSERT_EQ(0, intent_stage_add_physicality(first, &placement, &a, 1, coord, &hilbert,
        nullptr, 0, 0, 1, 0, 1, 0, 1000000));
    ASSERT_EQ(0, intent_stage_add_physicality(second, &placement, &a, 1, coord, &hilbert,
        nullptr, 0, 0, 1, 0, 1, 0, 2000000));
    ASSERT_EQ(0, intent_stage_add_attestation(first, &b, &a, &type, &b, &a,
        nullptr, 2, 1000000, 1, 1000000000, 100000000, 1500000000, nullptr));
    ASSERT_EQ(0, intent_stage_add_attestation(second, &b, &a, &type, &b, &a,
        nullptr, 2, 2000000, 1, 1000000000, 100000000, 1500000000, nullptr));
    hash128_t one{}, two{};
    const intent_stage_t* parts[] = {split, second};
    ASSERT_EQ(0, intent_stage_semantic_digest(first, &one));
    ASSERT_EQ(0, intent_stage_semantic_digest_batch(parts, 2, &two));
    EXPECT_EQ(0, std::memcmp(&one, &two, sizeof(one)));
    ASSERT_EQ(1, intent_stage_lower_entity_tier(first, &b, 1));
    ASSERT_EQ(0, intent_stage_semantic_digest(first, &one));
    EXPECT_NE(0, std::memcmp(&one, &two, sizeof(one)));
    intent_stage_free(first); intent_stage_free(second); intent_stage_free(split);
}

TEST(LaplaceCoreIntentStage, SemanticDigestDetectsChangedNativeEvidenceAndMultiplicity) {
    auto* first = intent_stage_new(0); auto* second = intent_stage_new(0);
    hash128_t id = make_hash(21), subject = make_hash(22), type = make_hash(23);
    ASSERT_EQ(0, intent_stage_add_attestation(first, &id, &subject, &type, &id, &subject,
        nullptr, 2, 1000000, 1, 1000000000, 100000000, 1500000000, nullptr));
    ASSERT_EQ(0, intent_stage_add_attestation(second, &id, &subject, &type, &id, &subject,
        nullptr, 0, 1000000, 1, 0, 100000000, 1500000000, nullptr));
    hash128_t one{}, two{}, repeated{};
    ASSERT_EQ(0, intent_stage_semantic_digest(first, &one));
    ASSERT_EQ(0, intent_stage_semantic_digest(second, &two));
    EXPECT_NE(0, std::memcmp(&one, &two, sizeof(one)));
    const intent_stage_t* twice[] = {first, first};
    ASSERT_EQ(0, intent_stage_semantic_digest_batch(twice, 2, &repeated));
    EXPECT_NE(0, std::memcmp(&one, &repeated, sizeof(one)));
    intent_stage_free(first); intent_stage_free(second);
}

TEST(LaplaceCoreIntentStage, SemanticDigestBindsFoldReplayDisposition) {
    auto* replayable = intent_stage_new(0);
    auto* transient = intent_stage_new(0);
    ASSERT_NE(nullptr, replayable); ASSERT_NE(nullptr, transient);
    hash128_t id = make_hash(31), subject = make_hash(32), type = make_hash(33);
    ASSERT_EQ(0, intent_stage_add_attestation_mode(
        replayable, &id, &subject, &type, &id, &subject, nullptr,
        2, 1000000, 1, 1000000000, 100000000, 1500000000, 1, nullptr));
    ASSERT_EQ(0, intent_stage_add_attestation_mode(
        transient, &id, &subject, &type, &id, &subject, nullptr,
        2, 1000000, 1, 1000000000, 100000000, 1500000000, 0, nullptr));
    hash128_t one{}, two{};
    ASSERT_EQ(0, intent_stage_semantic_digest(replayable, &one));
    ASSERT_EQ(0, intent_stage_semantic_digest(transient, &two));
    EXPECT_NE(0, std::memcmp(&one, &two, sizeof(one)));
    intent_stage_free(replayable); intent_stage_free(transient);
}

namespace {
using InterpretationStage = std::unique_ptr<intent_stage_t, decltype(&intent_stage_free)>;

std::vector<uint8_t> stage_tuple_bytes(const intent_stage_t* stage, intent_stage_table_t table) {
    size_t bytes=0;
    const auto* data=intent_stage_tuple_ptr(stage,table,&bytes);
    return bytes ? std::vector<uint8_t>(data,data+bytes) : std::vector<uint8_t>();
}

std::vector<uint8_t> interpretation_bytes(const intent_stage_t* stage) {
    size_t bytes=0;
    const auto* data=intent_stage_entity_interpretation_tuple_ptr(stage,&bytes);
    return bytes ? std::vector<uint8_t>(data,data+bytes) : std::vector<uint8_t>();
}

std::vector<std::vector<uint8_t>> wire_rows(const std::vector<uint8_t>& bytes, unsigned columns) {
    std::vector<std::vector<uint8_t>> rows;
    size_t at=0;
    while(at<bytes.size()) {
        const size_t start=at;
        if(bytes.size()-at<2 || read_be16(bytes.data()+at)!=columns) { ADD_FAILURE(); return {}; }
        at+=2;
        for(unsigned field=0;field<columns;++field) {
            if(bytes.size()-at<4) { ADD_FAILURE(); return {}; }
            const uint32_t size=read_be32(bytes.data()+at);at+=4;
            if(size==UINT32_MAX) continue;
            if(size>bytes.size()-at) { ADD_FAILURE(); return {}; }
            at+=size;
        }
        rows.emplace_back(bytes.begin()+start,bytes.begin()+at);
    }
    std::sort(rows.begin(),rows.end());
    return rows;
}

std::vector<std::vector<uint8_t>> interpretation_rows(const intent_stage_t* stage) {
    return wire_rows(interpretation_bytes(stage),4);
}
}

TEST(LaplaceCoreIntentStage, InterpretationsRetainOriginalPairsWithoutChangingLegacySemanticBytes) {
    InterpretationStage stage(intent_stage_new(0),intent_stage_free);
    ASSERT_NE(nullptr,stage.get());
    const auto id=make_hash(51), type_old=make_hash(52), type_new=make_hash(53), source=make_hash(54);
    ASSERT_EQ(0,intent_stage_add_entity(stage.get(),&id,4,&type_old,&source));
    ASSERT_EQ(1,intent_stage_lower_entity_tier(stage.get(),&id,1));
    const auto entity=stage_tuple_bytes(stage.get(),INTENT_STAGE_TABLE_ENTITIES);
    ASSERT_EQ(68u,entity.size());
    EXPECT_EQ(1u,read_be16(entity.data()+26));
    EXPECT_EQ(0,std::memcmp(entity.data()+32,&type_old,16)); // historical compatibility bytes
    hash128_t before{},after{};
    ASSERT_EQ(0,intent_stage_semantic_digest(stage.get(),&before));
    ASSERT_EQ(0,intent_stage_add_entity_interpretation(stage.get(),&id,1,&type_new,&source));
    EXPECT_EQ(1u,intent_stage_entity_count(stage.get()));
    EXPECT_EQ(entity,stage_tuple_bytes(stage.get(),INTENT_STAGE_TABLE_ENTITIES));
    ASSERT_EQ(0,intent_stage_semantic_digest(stage.get(),&after));
    EXPECT_EQ(0,hash128_compare(&before,&after));
    const auto facets=interpretation_bytes(stage.get());
    ASSERT_EQ(136u,facets.size());
    EXPECT_EQ(4u,read_be16(facets.data()+26));
    EXPECT_EQ(0,std::memcmp(facets.data()+32,&type_old,16));
    EXPECT_EQ(1u,read_be16(facets.data()+68+26));
    EXPECT_EQ(0,std::memcmp(facets.data()+68+32,&type_new,16));
    EXPECT_TRUE(intent_stage_entity_interpretations_complete(stage.get()));
}

TEST(LaplaceCoreIntentStage, InterpretationImportIsExplicitCompleteValidatedAndNondestructive) {
    InterpretationStage original(intent_stage_new(0),intent_stage_free);
    const auto id=make_hash(61), type=make_hash(62), source=make_hash(63);
    ASSERT_EQ(0,intent_stage_add_entity(original.get(),&id,2,&type,&source));
    const auto entities=stage_tuple_bytes(original.get(),INTENT_STAGE_TABLE_ENTITIES);
    intent_stage_t* raw=nullptr;
    ASSERT_EQ(0,intent_stage_from_tuple_bytes(entities.data(),entities.size(),nullptr,0,nullptr,0,SIZE_MAX,&raw));
    InterpretationStage imported(raw,intent_stage_free);
    EXPECT_FALSE(intent_stage_entity_interpretations_complete(imported.get()));
    EXPECT_EQ(0u,intent_stage_entity_interpretation_count(imported.get()));
    const auto good=interpretation_bytes(original.get());
    ASSERT_EQ(0,intent_stage_import_entity_interpretations(imported.get(),good.data(),good.size()));
    EXPECT_TRUE(intent_stage_entity_interpretations_complete(imported.get()));
    EXPECT_EQ(good,interpretation_bytes(imported.get()));
    // Legacy import validates framing only. Promotion must validate the old
    // entity fields before claiming a complete interpretation stream.
    const auto reject_malformed_legacy_promotion = [&](const std::vector<uint8_t>& bad) {
        intent_stage_t* raw_legacy=nullptr;
        ASSERT_EQ(0,intent_stage_from_tuple_bytes(
            bad.data(),bad.size(),nullptr,0,nullptr,0,SIZE_MAX,&raw_legacy));
        InterpretationStage malformed(raw_legacy,intent_stage_free);
        const auto allocated=intent_stage_memory_bytes(malformed.get());
        ASSERT_FALSE(intent_stage_entity_interpretations_complete(malformed.get()));
        ASSERT_EQ(0u,intent_stage_entity_interpretation_count(malformed.get()));
        EXPECT_EQ(-1,intent_stage_add_entity_interpretation(malformed.get(),&id,3,&type,&source));
        EXPECT_EQ(-1,intent_stage_add_entity(malformed.get(),&id,3,&type,&source));
        EXPECT_FALSE(intent_stage_entity_interpretations_complete(malformed.get()));
        EXPECT_EQ(0u,intent_stage_entity_interpretation_count(malformed.get()));
        EXPECT_TRUE(interpretation_bytes(malformed.get()).empty());
        EXPECT_EQ(bad,stage_tuple_bytes(malformed.get(),INTENT_STAGE_TABLE_ENTITIES));
        EXPECT_EQ(1u,intent_stage_entity_count(malformed.get()));
        EXPECT_EQ(allocated,intent_stage_memory_bytes(malformed.get()));
    };
    // Well-framed but invalid fields must fail before replacing prior metadata.
    for(unsigned field : {0u,1u,2u,3u}) {
        auto bad=good;
        const size_t prefix[]={2,22,28,48};
        const size_t width[]={16,2,16,16};
        bad.erase(bad.begin()+prefix[field]+4,bad.begin()+prefix[field]+4+width[field]);
        bad[prefix[field]+3]=0; // zero-length payload, valid overall framing
        reject_malformed_legacy_promotion(bad);
        EXPECT_EQ(-1,intent_stage_import_entity_interpretations(imported.get(),bad.data(),bad.size()));
        EXPECT_EQ(good,interpretation_bytes(imported.get()));
        EXPECT_TRUE(intent_stage_entity_interpretations_complete(imported.get()));
    }
    auto bad_tier=good;bad_tier[26]=1;bad_tier[27]=0; // 256
    reject_malformed_legacy_promotion(bad_tier);
    EXPECT_EQ(-1,intent_stage_import_entity_interpretations(imported.get(),bad_tier.data(),bad_tier.size()));
    EXPECT_EQ(good,interpretation_bytes(imported.get()));
    EXPECT_EQ(-1,intent_stage_import_entity_interpretations(imported.get(),good.data(),good.size()-1));
    EXPECT_EQ(good,interpretation_bytes(imported.get()));
    // Aliased input is copied before releasing the old buffer.
    size_t length=0;const auto* alias=intent_stage_entity_interpretation_tuple_ptr(imported.get(),&length);
    ASSERT_EQ(0,intent_stage_import_entity_interpretations(imported.get(),alias,length));
    EXPECT_EQ(good,interpretation_bytes(imported.get()));
    InterpretationStage empty(intent_stage_new(0),intent_stage_free);
    ASSERT_EQ(0,intent_stage_import_entity_interpretations(empty.get(),nullptr,0));
    EXPECT_TRUE(intent_stage_entity_interpretations_complete(empty.get()));
    EXPECT_EQ(0u,intent_stage_entity_interpretation_count(empty.get()));
    // A new observation appended to a legacy stage first retains its old E interpretation.
    raw=nullptr;
    ASSERT_EQ(0,intent_stage_from_tuple_bytes(entities.data(),entities.size(),nullptr,0,nullptr,0,SIZE_MAX,&raw));
    InterpretationStage legacy(raw,intent_stage_free);
    ASSERT_EQ(0,intent_stage_add_entity_interpretation(legacy.get(),&id,3,&type,nullptr));
    EXPECT_TRUE(intent_stage_entity_interpretations_complete(legacy.get()));
    EXPECT_EQ(2u,intent_stage_entity_interpretation_count(legacy.get()));
    EXPECT_EQ(entities,stage_tuple_bytes(legacy.get(),INTENT_STAGE_TABLE_ENTITIES));
}

TEST(LaplaceCoreIntentStage, NullableInterpretationProvenanceSurvivesImportPromotionAndPartition) {
    const auto id=make_hash(91),other=make_hash(92),type=make_hash(93),source=make_hash(94);
    InterpretationStage original(intent_stage_new(0),intent_stage_free);
    ASSERT_NE(nullptr,original.get());
    ASSERT_EQ(0,intent_stage_add_entity(original.get(),&id,2,&type,nullptr));
    ASSERT_EQ(0,intent_stage_add_entity(original.get(),&other,3,&type,&source));
    const auto entities=stage_tuple_bytes(original.get(),INTENT_STAGE_TABLE_ENTITIES);
    const auto facets=interpretation_bytes(original.get());
    ASSERT_EQ(120u,facets.size()); // one NULL source row (52), one sourced row (68)
    EXPECT_EQ(UINT32_MAX,read_be32(facets.data()+48));
    hash128_t before{},after{};
    ASSERT_EQ(0,intent_stage_semantic_digest(original.get(),&before));

    intent_stage_t* raw=nullptr;
    ASSERT_EQ(0,intent_stage_from_tuple_bytes(
        entities.data(),entities.size(),nullptr,0,nullptr,0,SIZE_MAX,&raw));
    InterpretationStage imported(raw,intent_stage_free);
    ASSERT_EQ(0,intent_stage_import_entity_interpretations(imported.get(),facets.data(),facets.size()));
    EXPECT_TRUE(intent_stage_entity_interpretations_complete(imported.get()));
    EXPECT_EQ(2u,intent_stage_entity_interpretation_count(imported.get()));
    EXPECT_EQ(facets,interpretation_bytes(imported.get()));
    EXPECT_EQ(entities,stage_tuple_bytes(imported.get(),INTENT_STAGE_TABLE_ENTITIES));
    ASSERT_EQ(0,intent_stage_semantic_digest(imported.get(),&after));
    EXPECT_EQ(0,hash128_compare(&before,&after));

    raw=nullptr;
    ASSERT_EQ(0,intent_stage_from_tuple_bytes(
        entities.data(),entities.size(),nullptr,0,nullptr,0,SIZE_MAX,&raw));
    InterpretationStage legacy(raw,intent_stage_free);
    ASSERT_FALSE(intent_stage_entity_interpretations_complete(legacy.get()));
    ASSERT_EQ(0,intent_stage_add_entity_interpretation(legacy.get(),&id,4,&type,nullptr));
    EXPECT_TRUE(intent_stage_entity_interpretations_complete(legacy.get()));
    EXPECT_EQ(3u,intent_stage_entity_interpretation_count(legacy.get()));
    EXPECT_EQ(entities,stage_tuple_bytes(legacy.get(),INTENT_STAGE_TABLE_ENTITIES));
    const auto promoted=interpretation_bytes(legacy.get());
    ASSERT_EQ(172u,promoted.size());
    EXPECT_TRUE(std::equal(facets.begin(),facets.end(),promoted.begin()));
    ASSERT_EQ(0,intent_stage_semantic_digest(legacy.get(),&after));
    EXPECT_EQ(0,hash128_compare(&before,&after));

    intent_stage_t* raw_parts[2]={};
    ASSERT_EQ(0,intent_stage_partition(legacy.get(),2,raw_parts));
    std::vector<std::vector<uint8_t>> combined;
    for(auto* part:raw_parts) {
        InterpretationStage owner(part,intent_stage_free);
        ASSERT_TRUE(intent_stage_entity_interpretations_complete(owner.get()));
        const auto bytes=interpretation_bytes(owner.get());
        InterpretationStage roundtrip(intent_stage_new(0),intent_stage_free);
        ASSERT_EQ(0,intent_stage_import_entity_interpretations(roundtrip.get(),bytes.data(),bytes.size()));
        EXPECT_EQ(bytes,interpretation_bytes(roundtrip.get()));
        auto rows=interpretation_rows(roundtrip.get());
        combined.insert(combined.end(),rows.begin(),rows.end());
    }
    std::sort(combined.begin(),combined.end());
    EXPECT_EQ(interpretation_rows(legacy.get()),combined);

    // Only provenance may be NULL; malformed lengths and required NULL fields
    // must preserve the previously accepted mixed-source stream.
    auto bad=facets;bad[51]=0xfe; // -2 is not the COPY null marker.
    EXPECT_EQ(-1,intent_stage_import_entity_interpretations(imported.get(),bad.data(),bad.size()));
    for(unsigned field=0;field<3;++field) {
        const size_t prefix[]={2,22,28},width[]={16,2,16};
        bad=facets;
        bad.erase(bad.begin()+prefix[field]+4,bad.begin()+prefix[field]+4+width[field]);
        std::fill(bad.begin()+prefix[field],bad.begin()+prefix[field]+4,0xff);
        EXPECT_EQ(-1,intent_stage_import_entity_interpretations(imported.get(),bad.data(),bad.size()));
        EXPECT_EQ(facets,interpretation_bytes(imported.get()));
        EXPECT_TRUE(intent_stage_entity_interpretations_complete(imported.get()));
        EXPECT_EQ(2u,intent_stage_entity_interpretation_count(imported.get()));
    }
}

TEST(LaplaceCoreIntentStage, InterpretationPartitionBudgetAndRetentionFollowStageOwnership) {
    InterpretationStage stage(intent_stage_new(0),intent_stage_free);
    const auto type=make_hash(71),source=make_hash(72);
    for(uint8_t i=1;i<=3;++i) {
        auto id=make_hash(i);
        ASSERT_EQ(0,intent_stage_add_entity(stage.get(),&id,2,&type,&source));
        ASSERT_EQ(0,intent_stage_add_entity_interpretation(stage.get(),&id,3,&type,nullptr));
    }
    intent_stage_t* raw_parts[3]={};
    ASSERT_EQ(0,intent_stage_partition(stage.get(),3,raw_parts));
    std::vector<std::vector<uint8_t>> combined;
    for(size_t part=0;part<3;++part) {
        InterpretationStage owner(raw_parts[part],intent_stage_free);
        EXPECT_TRUE(intent_stage_entity_interpretations_complete(owner.get()));
        auto rows=interpretation_rows(owner.get());
        for(const auto& row:rows) {
            uint64_t lo=0;std::memcpy(&lo,row.data()+14,sizeof(lo));
            EXPECT_EQ(part,lo%3);
            combined.push_back(row);
        }
    }
    std::sort(combined.begin(),combined.end());
    EXPECT_EQ(interpretation_rows(stage.get()),combined);
    InterpretationStage empty(intent_stage_new(0),intent_stage_free);
    const size_t base=intent_stage_memory_bytes(empty.get());
    InterpretationStage bounded(intent_stage_new_bounded(0,base+256),intent_stage_free);
    ASSERT_NE(nullptr,bounded.get());
    auto id=make_hash(73);
    ASSERT_EQ(0,intent_stage_add_entity_interpretation(bounded.get(),&id,2,&type,&source));
    const auto retained=interpretation_bytes(bounded.get());
    EXPECT_EQ(-2,intent_stage_import_entity_interpretations(bounded.get(),retained.data(),retained.size()));
    EXPECT_EQ(retained,interpretation_bytes(bounded.get()));
    EXPECT_TRUE(intent_stage_entity_interpretations_complete(bounded.get()));
    EXPECT_LE(intent_stage_memory_peak_bytes(bounded.get()),base+256);
    EXPECT_EQ(256u,intent_stage_retain_physicalities(bounded.get()));
    EXPECT_EQ(base,intent_stage_memory_bytes(bounded.get()));
    EXPECT_EQ(0u,intent_stage_entity_interpretation_count(bounded.get()));
    EXPECT_EQ(0u,intent_stage_retain_physicalities(bounded.get()));
}

TEST(LaplaceCoreIntentStage, PresentContentRetainsInterpretationsAndExactPhysicalityObservations) {
    const uint8_t text[]={'a','b',' ','a','b'};
    tier_tree_t* raw_tree=nullptr;
    ASSERT_EQ(0,content_witness_tree_build(text,sizeof(text),&raw_tree));
    std::unique_ptr<tier_tree_t,decltype(&tier_tree_free)> tree(raw_tree,tier_tree_free);
    InterpretationStage full(intent_stage_new(0),intent_stage_free),known(intent_stage_new(0),intent_stage_free);
    const auto source=make_hash(81);
    hash128_t root{},present_root{};
    const size_t nodes=tier_tree_node_count(tree.get());
    std::vector<uint8_t> present((nodes+7)/8,255);
    ASSERT_EQ(0,content_witness_emit_tree(full.get(),tree.get(),&source,nullptr,0,&root));
    ASSERT_EQ(0,content_witness_emit_tree(known.get(),tree.get(),&source,present.data(),nodes,&present_root));
    ASSERT_GT(intent_stage_entity_count(full.get()),0u);
    EXPECT_EQ(0u,intent_stage_entity_count(known.get()));
    EXPECT_EQ(0,hash128_compare(&root,&present_root));
    EXPECT_EQ(interpretation_rows(full.get()),interpretation_rows(known.get()));
    ASSERT_GT(intent_stage_entity_interpretation_count(known.get()),0u);
    EXPECT_EQ(wire_rows(stage_tuple_bytes(full.get(),INTENT_STAGE_TABLE_PHYSICALITIES),10),
              wire_rows(stage_tuple_bytes(known.get(),INTENT_STAGE_TABLE_PHYSICALITIES),10));
    auto rows=interpretation_rows(known.get());
    ASSERT_EQ(0,content_witness_emit_tree(known.get(),tree.get(),&source,present.data(),nodes,&present_root));
    auto doubled=rows;doubled.insert(doubled.end(),rows.begin(),rows.end());std::sort(doubled.begin(),doubled.end());
    EXPECT_EQ(doubled,interpretation_rows(known.get()));
    // Cache-backed atomic roots keep their existing P-only contract.
    InterpretationStage atom(intent_stage_new(0),intent_stage_free);
    const uint8_t atomic[]={'A'};
    ASSERT_EQ(0,content_witness_batch_add(atom.get(),atomic,sizeof(atomic),&source,&root));
    EXPECT_EQ(0u,intent_stage_entity_count(atom.get()));
    EXPECT_EQ(0u,intent_stage_entity_interpretation_count(atom.get()));
    EXPECT_EQ(1u,intent_stage_physicality_count(atom.get()));
}
