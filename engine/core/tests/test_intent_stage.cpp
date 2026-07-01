#include <gtest/gtest.h>

#include <cstdint>
#include <cstring>
#include <vector>

#include "laplace/core/intent_stage.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"

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
    hash128_t id = make_hash(0x01);
    hash128_t eid = make_hash(0x02);
    hash128_t sid = make_hash(0x03);
    double coord[4] = { 0.25, 0.5, 0.75, 1.0 };
    hilbert128_t hb;
    for (int i = 0; i < 16; ++i) hb.bytes[i] = (uint8_t)(0x40 + i);
    double traj[8] = { 1.0, 2.0, 3.0, 4.0,  5.0, 6.0, 7.0, 8.0 };

    (void)sid;
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
    for (int i = 0; i < 16; ++i) EXPECT_EQ(0x01, *p++);
    EXPECT_EQ(16u, read_be32(p)); p += 4;
    for (int i = 0; i < 16; ++i) EXPECT_EQ(0x02, *p++);
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
            const double expected = (double)(v * 4 + c + 1);
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
    double coord[4] = {0, 0, 0, 0};
    hilbert128_t hb; std::memset(&hb, 0, sizeof(hb));
    ASSERT_EQ(0, intent_stage_add_physicality(
        s, &z, &z, 1, coord, &hb, nullptr, 0, 0,
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

    ASSERT_EQ(0, intent_stage_add_attestation(
        s, &id, &sub, &kid, &obj, &src, &ctx,
        outcome, obs_us, obs_count, NULL));

    const size_t need = intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ATTESTATIONS,
                                                     nullptr, 0);
    std::vector<uint8_t> buf(need);
    ASSERT_EQ(need, intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ATTESTATIONS,
                                                  buf.data(), buf.size()));
    const uint8_t* p = buf.data() + kHeader;
    EXPECT_EQ(10, (int16_t)read_be16(p)); p += 2;
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
    EXPECT_EQ((uint32_t)-1, read_be32(p));
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, AddAttestationNullObjectAndContext) {
    intent_stage_t* s = intent_stage_new(1);
    ASSERT_NE(nullptr, s);
    hash128_t z = make_hash(0);
    ASSERT_EQ(0, intent_stage_add_attestation(
        s, &z, &z, &z, nullptr, &z, nullptr,
        0, 0, 0, NULL));
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
    double coord[4] = {0, 0, 0, 0};
    hilbert128_t hb; std::memset(&hb, 0, sizeof(hb));
    ASSERT_EQ(0, intent_stage_add_entity(s, &z, 0, &z, nullptr));
    ASSERT_EQ(0, intent_stage_add_physicality(s, &z, &z, 1, coord, &hb, nullptr, 0, 0, 1, 0, 1, 0, 0));
    ASSERT_EQ(0, intent_stage_add_attestation(s, &z, &z, &z, nullptr, &z, nullptr, 1, 0, 0, NULL));
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
                (int64_t)(i % 100), NULL));
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
            for (uint32_t v = 0; v < verts; ++v) {
                traj[(size_t)v * 4 + 0] = (double)v;
                traj[(size_t)v * 4 + 1] = (double)(v + 1);
                traj[(size_t)v * 4 + 2] = (double)(v + 2);
                traj[(size_t)v * 4 + 3] = (double)(v + 3);
            }
            hash128_t id  = make_hash((uint8_t)(i));
            hash128_t ent = make_hash((uint8_t)(i % kEntities));
            ASSERT_EQ(0, intent_stage_add_physicality(
                s, &id, &ent, (int16_t)(i % 3), coord, &hb,
                traj.data(), verts, (int32_t)verts,
                0, 0.5, 0, (int32_t)(i % 16),
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
            (int64_t)(i % 100), NULL))
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


static void collect_ids_lo(const intent_stage_t* s, intent_stage_table_t table,
                           std::vector<uint64_t>& out) {
    const size_t need = intent_stage_emit_copy_binary(s, table, nullptr, 0);
    std::vector<uint8_t> buf(need);
    EXPECT_EQ(need, intent_stage_emit_copy_binary(s, table, buf.data(), buf.size()));
    const uint8_t* p   = buf.data() + kHeader;
    const uint8_t* end = buf.data() + buf.size() - kTrailer;
    while (p + 2 <= end) {
        const uint16_t cols = read_be16(p);
        if (cols == 0xffff) break;     
        p += 2;
        
        const uint32_t f1 = read_be32(p); p += 4;
        ASSERT_EQ(16u, f1);
        uint64_t lo = 0;
        std::memcpy(&lo, p + 8, 8);     
        out.push_back(lo);
        p += 16;
        for (uint16_t c = 1; c < cols; ++c) {
            const int32_t flen = (int32_t)read_be32(p); p += 4;
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
        hash128_t id; id.hi = 0x2222; id.lo = i * 40503ULL + 13;
        hash128_t e = make_hash(0x33);
        ASSERT_EQ(0, intent_stage_add_physicality(
            s, &id, &e, 1, coord, &hb, nullptr, 0, 0, 1, 0.0, 1, 0,
            INTENT_STAGE_PG_EPOCH_UNIX_US));
    }
    for (size_t i = 0; i < kAtt; ++i) {
        hash128_t id; id.hi = 0x3333; id.lo = i * 2246822519ULL + 29;
        hash128_t sub = make_hash(0x44), kid = make_hash(0x45), src = make_hash(0x46);
        ASSERT_EQ(0, intent_stage_add_attestation(
            s, &id, &sub, &kid, nullptr, &src, nullptr, 1,
            INTENT_STAGE_PG_EPOCH_UNIX_US, 1, NULL));
    }

    intent_stage_t* parts[kN];
    ASSERT_EQ(0, intent_stage_partition(s, kN, parts));

    size_t total_ent = 0, total_phys = 0, total_att = 0;
    for (size_t k = 0; k < kN; ++k) {
        ASSERT_NE(nullptr, parts[k]);
        total_ent  += intent_stage_entity_count(parts[k]);
        total_phys += intent_stage_physicality_count(parts[k]);
        total_att  += intent_stage_attestation_count(parts[k]);

        
        for (auto table : {INTENT_STAGE_TABLE_ENTITIES,
                           INTENT_STAGE_TABLE_ATTESTATIONS}) {
            std::vector<uint64_t> los;
            collect_ids_lo(parts[k], table, los);
            for (uint64_t lo : los)
                EXPECT_EQ(k, (size_t)(lo % kN)) << "row landed in wrong partition";
        }
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
