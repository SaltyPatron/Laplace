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

} // namespace

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
    /* flags = 0 */
    EXPECT_EQ(0u, read_be32(buf.data() + 11));
    /* hdr_ext_length = 0 */
    EXPECT_EQ(0u, read_be32(buf.data() + 15));
    /* trailer = 0xFFFF */
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

    /* Layout:
     *   [0..11)   signature
     *   [11..15)  flags=0
     *   [15..19)  hdr ext=0
     *   [19..21)  field_count=4 BE
     *   [21..25)  field_len=16 BE
     *   [25..41)  16 bytes of id (all 0x11)
     *   [41..45)  field_len=2 BE
     *   [45..47)  tier=5 BE (int16)
     *   [47..51)  field_len=16 BE
     *   [51..67)  16 bytes of type_id (all 0x22)
     *   [67..71)  field_len=-1 (NULL first_observed_by)
     *   [71..73)  trailer 0xFFFF
     */
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
    /* Last field: len=16 + 16 bytes of 0x30 */
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
    /* Nothing should be written; we don't easily check that, but the
     * contract says nothing is written when buf_capacity < required. */
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
    /* Each entity row is 2 (field_count) + 4+16 + 4+2 + 4+16 + 4 = 52 bytes.
     * 3 rows = 156 bytes + header(19) + trailer(2) = 177. */
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
    double traj[8] = { 1.0, 2.0, 3.0, 4.0,  5.0, 6.0, 7.0, 8.0 }; /* 2 vertices */

    ASSERT_EQ(0, intent_stage_add_physicality(
        s, &id, &eid, &sid,
        /*kind*/ 1,
        coord, &hb,
        traj, /*n_vertices*/ 2,
        /*n_constituents*/ 2,
        /*alignment_residual_is_null*/ 0,
        /*alignment_residual*/ 0.0125,
        /*source_dim_is_null*/ 1,
        /*source_dim*/ 0,
        /*observed_at_unix_us*/ INTENT_STAGE_PG_EPOCH_UNIX_US + 1234567));

    const size_t need = intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_PHYSICALITIES,
                                                     nullptr, 0);
    std::vector<uint8_t> buf(need);
    ASSERT_EQ(need, intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_PHYSICALITIES,
                                                  buf.data(), buf.size()));

    const uint8_t* p = buf.data() + kHeader;
    EXPECT_EQ(11, (int16_t)read_be16(p)); p += 2;
    /* id */
    EXPECT_EQ(16u, read_be32(p)); p += 4;
    for (int i = 0; i < 16; ++i) EXPECT_EQ(0x01, *p++);
    /* entity_id */
    EXPECT_EQ(16u, read_be32(p)); p += 4;
    for (int i = 0; i < 16; ++i) EXPECT_EQ(0x02, *p++);
    /* source_id */
    EXPECT_EQ(16u, read_be32(p)); p += 4;
    for (int i = 0; i < 16; ++i) EXPECT_EQ(0x03, *p++);
    /* kind=1 int2 */
    EXPECT_EQ(2u, read_be32(p)); p += 4;
    EXPECT_EQ(1, (int16_t)read_be16(p)); p += 2;
    /* coord — PointZM EWKB, 37 bytes */
    EXPECT_EQ(37u, read_be32(p)); p += 4;
    EXPECT_EQ(0x01, *p++); /* NDR */
    EXPECT_EQ(0xC0000001u, read_le_u32(p)); p += 4;  /* POINT | Z | M */
    EXPECT_EQ(0.25, read_le_double(p)); p += 8;
    EXPECT_EQ(0.5,  read_le_double(p)); p += 8;
    EXPECT_EQ(0.75, read_le_double(p)); p += 8;
    EXPECT_EQ(1.0,  read_le_double(p)); p += 8;
    /* hilbert_index — 16 bytes raw */
    EXPECT_EQ(16u, read_be32(p)); p += 4;
    for (int i = 0; i < 16; ++i) EXPECT_EQ((uint8_t)(0x40 + i), *p++);
    /* trajectory — LineStringZM EWKB, 1+4+4+32*2 = 73 bytes */
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
    /* n_constituents = 2 int4 */
    EXPECT_EQ(4u, read_be32(p)); p += 4;
    EXPECT_EQ(2, (int32_t)read_be32(p)); p += 4;
    /* alignment_residual = 0.0125 float8 */
    EXPECT_EQ(8u, read_be32(p)); p += 4;
    EXPECT_EQ(0.0125, read_be_double(p)); p += 8;
    /* source_dim NULL */
    EXPECT_EQ((uint32_t)-1, read_be32(p)); p += 4;
    /* observed_at: 1_234_567 µs after PG epoch */
    EXPECT_EQ(8u, read_be32(p)); p += 4;
    EXPECT_EQ((int64_t)1234567, (int64_t)read_be64(p)); p += 8;

    /* trailer */
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
        s, &z, &z, &z, 1, coord, &hb, nullptr, 0, 0,
        /*ar_null*/ 1, 0.0, /*sd_null*/ 1, 0, 0));
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
        s, &z, &z, &z, 1, coord, &hb, nullptr, 2, 0,
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
    /* Glicko-2 fixed-point ×1e9 */
    const int64_t rating     = INT64_C(1500000000000);  /* mu=1500.0 */
    const int64_t rd         = INT64_C(350000000000);   /* rd=350.0 */
    const int64_t volatility = INT64_C(60000000);       /* vol=0.06 */
    const int64_t obs_us     = INTENT_STAGE_PG_EPOCH_UNIX_US + 999;
    const int64_t obs_count  = 17;

    ASSERT_EQ(0, intent_stage_add_attestation(
        s, &id, &sub, &kid, &obj, &src, &ctx,
        rating, rd, volatility, obs_us, obs_count));

    const size_t need = intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ATTESTATIONS,
                                                     nullptr, 0);
    std::vector<uint8_t> buf(need);
    ASSERT_EQ(need, intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ATTESTATIONS,
                                                  buf.data(), buf.size()));
    const uint8_t* p = buf.data() + kHeader;
    EXPECT_EQ(11, (int16_t)read_be16(p)); p += 2;
    /* skip 5 hash128 fields and 1 nullable hash128 — verify field lengths only */
    for (int f = 0; f < 6; ++f) {
        EXPECT_EQ(16u, read_be32(p)); p += 4;
        EXPECT_EQ((uint8_t)(0xA1 + f), *p);  /* leading byte of each */
        p += 16;
    }
    /* rating int8 */
    EXPECT_EQ(8u, read_be32(p)); p += 4;
    EXPECT_EQ(rating, (int64_t)read_be64(p)); p += 8;
    /* rd */
    EXPECT_EQ(8u, read_be32(p)); p += 4;
    EXPECT_EQ(rd, (int64_t)read_be64(p)); p += 8;
    /* volatility */
    EXPECT_EQ(8u, read_be32(p)); p += 4;
    EXPECT_EQ(volatility, (int64_t)read_be64(p)); p += 8;
    /* last_observed_at: 999 µs after PG epoch */
    EXPECT_EQ(8u, read_be32(p)); p += 4;
    EXPECT_EQ((int64_t)999, (int64_t)read_be64(p)); p += 8;
    /* observation_count */
    EXPECT_EQ(8u, read_be32(p)); p += 4;
    EXPECT_EQ(obs_count, (int64_t)read_be64(p));
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, AddAttestationNullObjectAndContext) {
    intent_stage_t* s = intent_stage_new(1);
    ASSERT_NE(nullptr, s);
    hash128_t z = make_hash(0);
    ASSERT_EQ(0, intent_stage_add_attestation(
        s, &z, &z, &z, /*object*/nullptr, &z, /*context*/nullptr,
        0, 1, 1, 0, 0));
    const size_t need = intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ATTESTATIONS,
                                                      nullptr, 0);
    std::vector<uint8_t> buf(need);
    ASSERT_EQ(need, intent_stage_emit_copy_binary(s, INTENT_STAGE_TABLE_ATTESTATIONS,
                                                  buf.data(), buf.size()));
    const uint8_t* p = buf.data() + kHeader;
    p += 2;            /* field_count */
    p += 4 + 16;       /* id */
    p += 4 + 16;       /* subject */
    p += 4 + 16;       /* kind */
    /* object_id == NULL: length = -1 */
    EXPECT_EQ((uint32_t)-1, read_be32(p)); p += 4;
    /* source_id */
    EXPECT_EQ(16u, read_be32(p)); p += 4 + 16;
    /* context_id == NULL */
    EXPECT_EQ((uint32_t)-1, read_be32(p));
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, EachTableHasIndependentRowCount) {
    intent_stage_t* s = intent_stage_new(1);
    hash128_t z = make_hash(0);
    double coord[4] = {0, 0, 0, 0};
    hilbert128_t hb; std::memset(&hb, 0, sizeof(hb));
    ASSERT_EQ(0, intent_stage_add_entity(s, &z, 0, &z, nullptr));
    ASSERT_EQ(0, intent_stage_add_physicality(s, &z, &z, &z, 1, coord, &hb, nullptr, 0, 0, 1, 0, 1, 0, 0));
    ASSERT_EQ(0, intent_stage_add_attestation(s, &z, &z, &z, nullptr, &z, nullptr, 0, 1, 1, 0, 0));
    EXPECT_EQ(1u, intent_stage_entity_count(s));
    EXPECT_EQ(1u, intent_stage_physicality_count(s));
    EXPECT_EQ(1u, intent_stage_attestation_count(s));
    intent_stage_free(s);
}

TEST(LaplaceCoreIntentStage, PGEpochOffsetConstantIsCorrect) {
    /* 946684800000000 µs is 2000-01-01T00:00:00Z in Unix-µs.
     * Spot-check: 30 years * ~365.25 d/y * 86400 s/d ≈ 9.467e8 s. */
    EXPECT_EQ(INT64_C(946684800000000), INTENT_STAGE_PG_EPOCH_UNIX_US);
}
