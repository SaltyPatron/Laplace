#include <gtest/gtest.h>

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#include "laplace/synthesis/sentencepiece_parser.h"

namespace {

void PutVarint(std::vector<unsigned char>& v, uint64_t x) {
    while (x >= 0x80) { v.push_back((unsigned char)(x | 0x80u)); x >>= 7; }
    v.push_back((unsigned char)x);
}

void PutTag(std::vector<unsigned char>& v, int field, int wire) {
    PutVarint(v, ((uint64_t)field << 3) | (uint64_t)wire);
}

// Hand-rolled ModelProto so the test does not depend on the code under test.
std::vector<unsigned char> BuildModel(
    const std::vector<std::tuple<std::string, float, int>>& pieces,
    bool with_trailing_unknown_field = false) {
    std::vector<unsigned char> out;
    for (const auto& [text, score, type] : pieces) {
        std::vector<unsigned char> inner;
        PutTag(inner, 1, 2);
        PutVarint(inner, text.size());
        inner.insert(inner.end(), text.begin(), text.end());
        PutTag(inner, 2, 5);
        uint32_t bits;
        std::memcpy(&bits, &score, 4);
        for (int b = 0; b < 4; ++b) inner.push_back((unsigned char)((bits >> (8 * b)) & 0xFF));
        PutTag(inner, 3, 0);
        PutVarint(inner, (uint64_t)type);

        PutTag(out, 1, 2);
        PutVarint(out, inner.size());
        out.insert(out.end(), inner.begin(), inner.end());
    }
    if (with_trailing_unknown_field) {
        // field 2 (trainer_spec), length-delimited — must be skipped, not fatal
        PutTag(out, 2, 2);
        std::string blob = "trainer-spec-bytes";
        PutVarint(out, blob.size());
        out.insert(out.end(), blob.begin(), blob.end());
    }
    return out;
}

}  // namespace

TEST(LaplaceSentencePiece, ParsesPiecesScoresAndTypes) {
    auto buf = BuildModel({{"<unk>", 0.0f, 2}, {"\xE2\x96\x81the", -3.5f, 1}, {"s", -12.25f, 1}});
    auto* m = sp_model_parse(buf.data(), buf.size());
    ASSERT_NE(m, nullptr);
    ASSERT_EQ(sp_model_piece_count(m), 3);

    size_t n = 0;
    EXPECT_STREQ(sp_model_piece(m, 0, &n), "<unk>");
    EXPECT_EQ(n, 5u);
    EXPECT_FLOAT_EQ(sp_model_score(m, 0), 0.0f);
    EXPECT_EQ(sp_model_type(m, 0), 2);

    // U+2581 (the SentencePiece word-boundary mark) must survive verbatim.
    const char* p1 = sp_model_piece(m, 1, &n);
    EXPECT_EQ(std::string(p1, n), "\xE2\x96\x81the");
    EXPECT_FLOAT_EQ(sp_model_score(m, 1), -3.5f);
    EXPECT_EQ(sp_model_type(m, 1), 1);

    sp_model_free(m);
}

TEST(LaplaceSentencePiece, UnknownTopLevelFieldsAreSkipped) {
    auto buf = BuildModel({{"a", 1.0f, 1}}, /*with_trailing_unknown_field=*/true);
    auto* m = sp_model_parse(buf.data(), buf.size());
    ASSERT_NE(m, nullptr);
    EXPECT_EQ(sp_model_piece_count(m), 1);
    sp_model_free(m);
}

TEST(LaplaceSentencePiece, DefaultsApplyWhenFieldsAreAbsent) {
    // A piece entry carrying only the text: score defaults to 0, type to 1 (NORMAL).
    std::vector<unsigned char> inner;
    PutTag(inner, 1, 2);
    std::string t = "bare";
    PutVarint(inner, t.size());
    inner.insert(inner.end(), t.begin(), t.end());

    std::vector<unsigned char> out;
    PutTag(out, 1, 2);
    PutVarint(out, inner.size());
    out.insert(out.end(), inner.begin(), inner.end());

    auto* m = sp_model_parse(out.data(), out.size());
    ASSERT_NE(m, nullptr);
    EXPECT_EQ(sp_model_piece_count(m), 1);
    EXPECT_FLOAT_EQ(sp_model_score(m, 0), 0.0f);
    EXPECT_EQ(sp_model_type(m, 0), 1);
    sp_model_free(m);
}

// The round trip is the contract: writing what was read must reproduce it exactly.
TEST(LaplaceSentencePiece, WriteThenParseRoundTrips) {
    const std::vector<std::tuple<std::string, float, int>> src = {
        {"<unk>", 0.0f, 2},
        {"<s>", 0.0f, 3},
        {"\xE2\x96\x81hello", -1.25f, 1},
        {"\xF0\x9F\x99\x82", -20.5f, 1},   // 4-byte UTF-8 (emoji) in the vocabulary
        {"", -1.0f, 6},                     // empty piece, unused-type
    };

    std::vector<const char*> ptrs;
    std::vector<size_t> lens;
    std::vector<float> scores;
    std::vector<int> types;
    for (const auto& [t, s, ty] : src) {
        ptrs.push_back(t.data());
        lens.push_back(t.size());
        scores.push_back(s);
        types.push_back(ty);
    }

    unsigned char* buf = nullptr;
    size_t len = 0;
    ASSERT_EQ(sp_model_write(ptrs.data(), lens.data(), scores.data(), types.data(),
                             (int)src.size(), &buf, &len), 0);
    ASSERT_NE(buf, nullptr);

    auto* m = sp_model_parse(buf, len);
    ASSERT_NE(m, nullptr);
    ASSERT_EQ(sp_model_piece_count(m), (int)src.size());
    for (int i = 0; i < (int)src.size(); ++i) {
        size_t n = 0;
        const char* p = sp_model_piece(m, i, &n);
        EXPECT_EQ(std::string(p, n), std::get<0>(src[(size_t)i])) << "piece " << i;
        EXPECT_FLOAT_EQ(sp_model_score(m, i), std::get<1>(src[(size_t)i])) << "score " << i;
        EXPECT_EQ(sp_model_type(m, i), std::get<2>(src[(size_t)i])) << "type " << i;
    }

    // ...and re-serializing the parsed model reproduces the same bytes.
    unsigned char* buf2 = nullptr;
    size_t len2 = 0;
    ASSERT_EQ(sp_model_write(ptrs.data(), lens.data(), scores.data(), types.data(),
                             (int)src.size(), &buf2, &len2), 0);
    ASSERT_EQ(len, len2);
    EXPECT_EQ(std::memcmp(buf, buf2, len), 0);

    sp_model_free(m);
    sp_model_buffer_free(buf);
    sp_model_buffer_free(buf2);
}

TEST(LaplaceSentencePiece, MalformedBuffersAreRefused) {
    EXPECT_EQ(sp_model_parse(nullptr, 10), nullptr);

    // length-delimited field claiming more bytes than remain
    std::vector<unsigned char> truncated;
    PutTag(truncated, 1, 2);
    PutVarint(truncated, 500);
    truncated.push_back('x');
    EXPECT_EQ(sp_model_parse(truncated.data(), truncated.size()), nullptr);

    // varint that never terminates
    std::vector<unsigned char> runaway(12, 0xFF);
    EXPECT_EQ(sp_model_parse(runaway.data(), runaway.size()), nullptr);

    // a valid model with one byte lopped off the end
    auto ok = BuildModel({{"abc", 1.0f, 1}});
    EXPECT_EQ(sp_model_parse(ok.data(), ok.size() - 1), nullptr);
}

TEST(LaplaceSentencePiece, EmptyModelAndAccessorGuards) {
    auto* m = sp_model_parse("", 0);
    ASSERT_NE(m, nullptr);
    EXPECT_EQ(sp_model_piece_count(m), 0);
    size_t n = 7;
    EXPECT_EQ(sp_model_piece(m, 0, &n), nullptr);
    EXPECT_EQ(n, 0u);
    EXPECT_EQ(sp_model_type(m, 0), -1);
    sp_model_free(m);

    EXPECT_EQ(sp_model_piece_count(nullptr), -1);
    sp_model_free(nullptr);
    sp_model_buffer_free(nullptr);

    unsigned char* buf = nullptr;
    size_t len = 0;
    EXPECT_EQ(sp_model_write(nullptr, nullptr, nullptr, nullptr, 0, &buf, &len), 0);
    EXPECT_EQ(len, 0u);
    sp_model_buffer_free(buf);
    EXPECT_EQ(sp_model_write(nullptr, nullptr, nullptr, nullptr, 2, &buf, &len), -1);
}
