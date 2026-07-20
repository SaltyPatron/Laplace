#include <gtest/gtest.h>

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#include "laplace/synthesis/safetensors_parser.h"

namespace {

// Build a safetensors buffer: [u64 LE json length][json]. Tensor data is never read
// by the parser, so the body is omitted.
std::vector<unsigned char> WithHeader(const std::string& json) {
    std::vector<unsigned char> buf(8 + json.size());
    uint64_t n = json.size();
    for (int i = 0; i < 8; ++i) buf[i] = (unsigned char)((n >> (8 * i)) & 0xFF);
    std::memcpy(buf.data() + 8, json.data(), json.size());
    return buf;
}

safetensors_header_t* Parse(const std::string& json) {
    auto buf = WithHeader(json);
    return safetensors_parse_header(buf.data(), buf.size());
}

}  // namespace

TEST(LaplaceSafetensorsParser, ParsesTensorsInDataOffsetOrder) {
    // Declared out of order on purpose: entries must come back in on-disk order so a
    // caller walking indices reads the file forward.
    const std::string json =
        R"({"b.weight":{"dtype":"F32","shape":[2,2],"data_offsets":[16,32]},)"
        R"("a.weight":{"dtype":"BF16","shape":[4],"data_offsets":[0,8]}})";
    auto* h = Parse(json);
    ASSERT_NE(h, nullptr);

    EXPECT_EQ(safetensors_tensor_count(h), 2);
    EXPECT_EQ(safetensors_header_bytes(h), (long long)(8 + json.size()));

    EXPECT_STREQ(safetensors_tensor_name(h, 0), "a.weight");
    EXPECT_STREQ(safetensors_tensor_dtype(h, 0), "BF16");
    EXPECT_EQ(safetensors_tensor_rank(h, 0), 1);
    EXPECT_EQ(safetensors_tensor_dim(h, 0, 0), 4);
    EXPECT_EQ(safetensors_tensor_data_start(h, 0), 0);
    EXPECT_EQ(safetensors_tensor_data_end(h, 0), 8);

    EXPECT_STREQ(safetensors_tensor_name(h, 1), "b.weight");
    EXPECT_EQ(safetensors_tensor_rank(h, 1), 2);
    EXPECT_EQ(safetensors_tensor_dim(h, 1, 0), 2);
    EXPECT_EQ(safetensors_tensor_dim(h, 1, 1), 2);

    safetensors_header_free(h);
}

TEST(LaplaceSafetensorsParser, MetadataIsSkippedButRecorded) {
    const std::string with =
        R"({"__metadata__":{"format":"pt","note":"x"},)"
        R"("w":{"dtype":"F16","shape":[1],"data_offsets":[0,2]}})";
    auto* h = Parse(with);
    ASSERT_NE(h, nullptr);
    EXPECT_EQ(safetensors_tensor_count(h), 1);   // __metadata__ is not a tensor
    EXPECT_EQ(safetensors_has_metadata(h), 1);
    EXPECT_STREQ(safetensors_tensor_name(h, 0), "w");
    safetensors_header_free(h);

    const std::string without = R"({"w":{"dtype":"F16","shape":[1],"data_offsets":[0,2]}})";
    auto* h2 = Parse(without);
    ASSERT_NE(h2, nullptr);
    EXPECT_EQ(safetensors_has_metadata(h2), 0);
    safetensors_header_free(h2);
}

TEST(LaplaceSafetensorsParser, ScalarAndEmptyShapesAreValid) {
    const std::string json = R"({"s":{"dtype":"F32","shape":[],"data_offsets":[0,4]}})";
    auto* h = Parse(json);
    ASSERT_NE(h, nullptr);
    EXPECT_EQ(safetensors_tensor_rank(h, 0), 0);
    safetensors_header_free(h);
}

TEST(LaplaceSafetensorsParser, EmptyHeaderObjectIsStructurallyValid) {
    auto* h = Parse("{}");
    ASSERT_NE(h, nullptr);
    EXPECT_EQ(safetensors_tensor_count(h), 0);
    safetensors_header_free(h);
}

TEST(LaplaceSafetensorsParser, UnknownFieldsAreIgnored) {
    const std::string json =
        R"({"w":{"dtype":"F32","extra":{"a":[1,2]},"shape":[2],"future":"x","data_offsets":[0,8]}})";
    auto* h = Parse(json);
    ASSERT_NE(h, nullptr);
    EXPECT_EQ(safetensors_tensor_count(h), 1);
    EXPECT_EQ(safetensors_tensor_data_end(h, 0), 8);
    safetensors_header_free(h);
}

TEST(LaplaceSafetensorsParser, EscapedNamesDecodeExactly) {
    // A mangled tensor name would mint a different entity, so escapes must be exact.
    const std::string json =
        R"({"a\/bA\t":{"dtype":"F32","shape":[1],"data_offsets":[0,4]}})";
    auto* h = Parse(json);
    ASSERT_NE(h, nullptr);
    EXPECT_STREQ(safetensors_tensor_name(h, 0), "a/bA\t");
    safetensors_header_free(h);
}

// Refusal cases: a header that does not describe its own bytes must not parse.
TEST(LaplaceSafetensorsParser, MalformedHeadersAreRefused) {
    EXPECT_EQ(safetensors_parse_header(nullptr, 100), nullptr);

    unsigned char tiny[4] = {1, 0, 0, 0};
    EXPECT_EQ(safetensors_parse_header(tiny, sizeof tiny), nullptr);  // shorter than the length field

    EXPECT_EQ(Parse("[]"), nullptr);                                    // not an object
    EXPECT_EQ(Parse(R"({"w":{"shape":[1],"data_offsets":[0,4]}})"), nullptr);        // no dtype
    EXPECT_EQ(Parse(R"({"w":{"dtype":"F32","data_offsets":[0,4]}})"), nullptr);      // no shape
    EXPECT_EQ(Parse(R"({"w":{"dtype":"F32","shape":[1]}})"), nullptr);               // no offsets
    EXPECT_EQ(Parse(R"({"w":{"dtype":"F32","shape":[1],"data_offsets":[8,4]}})"), nullptr);  // reversed
    EXPECT_EQ(Parse(R"({"w":{"dtype":"F32","shape":[-1],"data_offsets":[0,4]}})"), nullptr); // negative dim
    EXPECT_EQ(Parse(R"({"w":{"dtype":"F32","shape":[1.5],"data_offsets":[0,4]}})"), nullptr);// non-integer dim
    EXPECT_EQ(Parse(R"({"w":{"dtype":"F32","shape":[1],"data_offsets":[0,4]})"), nullptr);   // unterminated
}

TEST(LaplaceSafetensorsParser, DeclaredLengthLongerThanBufferIsRefused) {
    const std::string json = R"({"w":{"dtype":"F32","shape":[1],"data_offsets":[0,4]}})";
    auto buf = WithHeader(json);
    buf.resize(buf.size() - 3);  // truncate mid-JSON, length field still claims more
    EXPECT_EQ(safetensors_parse_header(buf.data(), buf.size()), nullptr);
}

TEST(LaplaceSafetensorsParser, ImplausibleLengthIsRefused) {
    std::vector<unsigned char> buf(16, 0);
    uint64_t huge = (uint64_t)512 * 1024 * 1024;  // beyond the 256 MiB bound
    for (int i = 0; i < 8; ++i) buf[i] = (unsigned char)((huge >> (8 * i)) & 0xFF);
    EXPECT_EQ(safetensors_parse_header(buf.data(), buf.size()), nullptr);

    std::vector<unsigned char> zero(16, 0);  // zero-length header
    EXPECT_EQ(safetensors_parse_header(zero.data(), zero.size()), nullptr);
}

TEST(LaplaceSafetensorsParser, OutOfRangeAccessorsReturnSentinels) {
    auto* h = Parse(R"({"w":{"dtype":"F32","shape":[2],"data_offsets":[0,8]}})");
    ASSERT_NE(h, nullptr);
    EXPECT_EQ(safetensors_tensor_name(h, 5), nullptr);
    EXPECT_EQ(safetensors_tensor_dtype(h, -1), nullptr);
    EXPECT_EQ(safetensors_tensor_rank(h, 5), -1);
    EXPECT_EQ(safetensors_tensor_dim(h, 0, 9), -1);
    EXPECT_EQ(safetensors_tensor_data_start(h, 9), -1);
    EXPECT_EQ(safetensors_tensor_count(nullptr), -1);
    EXPECT_EQ(safetensors_header_bytes(nullptr), -1);
    safetensors_header_free(h);
    safetensors_header_free(nullptr);  // must not crash
}
