#include <gtest/gtest.h>
#include "laplace/synthesis/format_writer.h"
#include "laplace/synthesis/safetensors_parser.h"
#include <chrono>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <vector>

namespace {
struct ExportDirectory {
    std::filesystem::path path = std::filesystem::temp_directory_path()
        / (std::string("laplace-safetensors-") + ::testing::UnitTest::GetInstance()->current_test_info()->name()
           + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
    ~ExportDirectory() { std::filesystem::remove_all(path); }
};
std::vector<char> read(const std::filesystem::path& path) {
    std::ifstream input(path, std::ios::binary);
    return {std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>()};
}
}

TEST(LaplaceFormatWriter, RoundTripsNamesShapesAndExactTensorBytes) {
    ExportDirectory dir;
    auto* writer = format_writer_create("safetensors", dir.path.string().c_str());
    ASSERT_NE(writer, nullptr);
    const std::string name = "犬.\"quoted\\tensor\n" + std::string(1500, 'x');
    const float matrix[] = {1.25f, -3.5f, 0.f, 42.f};
    const size_t shape[] = {2, 2};
    ASSERT_EQ(format_writer_add_tensor(writer, name.c_str(), 0, shape, 2, matrix, sizeof(matrix)), 0);
    const uint16_t scalar = 0x3c00;
    ASSERT_EQ(format_writer_add_tensor(writer, "scalar", 1, nullptr, 0, &scalar, sizeof(scalar)), 0);
    const size_t empty[] = {0, 7};
    ASSERT_EQ(format_writer_add_tensor(writer, "empty", 2, empty, 2, nullptr, 0), 0);
    ASSERT_EQ(format_writer_set_config(writer, "{}", 2), 0);
    ASSERT_EQ(format_writer_finalize(writer), 0);
    EXPECT_EQ(format_writer_add_tensor(writer, "late", 1, nullptr, 0, &scalar, sizeof(scalar)), -1);
    format_writer_free(writer);

    auto bytes = read(dir.path / "model.safetensors");
    auto* header = safetensors_parse_header(bytes.data(), bytes.size());
    ASSERT_NE(header, nullptr);
    EXPECT_EQ(safetensors_tensor_count(header), 3);
    EXPECT_STREQ(safetensors_tensor_name(header, 0), name.c_str());
    EXPECT_STREQ(safetensors_tensor_dtype(header, 0), "F32");
    EXPECT_EQ(safetensors_tensor_dim(header, 0, 0), 2);
    EXPECT_EQ(safetensors_tensor_dim(header, 0, 1), 2);
    size_t start = safetensors_header_bytes(header);
    ASSERT_EQ(bytes.size(), start + sizeof(matrix) + sizeof(scalar));
    EXPECT_EQ(start % 8, 0u);
    EXPECT_EQ(std::memcmp(bytes.data() + start, matrix, sizeof(matrix)), 0);
    EXPECT_EQ(std::memcmp(bytes.data() + start + sizeof(matrix), &scalar, sizeof(scalar)), 0);
    EXPECT_EQ(safetensors_tensor_rank(header, 1), 0);
    EXPECT_EQ(safetensors_tensor_data_end(header, 2) - safetensors_tensor_data_start(header, 2), 0);
    EXPECT_EQ(read(dir.path / "config.json"), (std::vector<char>{'{', '}'}));
    EXPECT_FALSE(std::filesystem::exists(dir.path / "model.safetensors.partial"));
    safetensors_header_free(header);
}

TEST(LaplaceFormatWriter, RejectsMalformedTensorMetadataWithoutCorruptingAcceptedPayload) {
    ExportDirectory dir;
    auto* writer = format_writer_create("safetensors", dir.path.string().c_str());
    ASSERT_NE(writer, nullptr);
    const float value = 9;
    const size_t huge[] = {std::numeric_limits<size_t>::max(), 2};
    EXPECT_EQ(format_writer_add_tensor(writer, "overflow", 0, huge, 2, &value, 4), -1);
    EXPECT_EQ(format_writer_add_tensor(writer, "unknown", 99, nullptr, 0, &value, 4), -1);
    EXPECT_EQ(format_writer_add_tensor(writer, "__metadata__", 0, nullptr, 0, &value, 4), -1);
    EXPECT_EQ(format_writer_add_tensor(writer, "short", 0, nullptr, 0, &value, 2), -1);
    EXPECT_EQ(format_writer_add_tensor(writer, "valid", 0, nullptr, 0, &value, 4), 0);
    EXPECT_EQ(format_writer_add_tensor(writer, "valid", 0, nullptr, 0, &value, 4), -1);
    ASSERT_EQ(format_writer_finalize(writer), 0);
    format_writer_free(writer);
    auto bytes = read(dir.path / "model.safetensors");
    auto* header = safetensors_parse_header(bytes.data(), bytes.size());
    ASSERT_NE(header, nullptr);
    EXPECT_EQ(safetensors_tensor_count(header), 1);
    EXPECT_EQ(std::memcmp(bytes.data() + safetensors_header_bytes(header), &value, 4), 0);
    safetensors_header_free(header);
}

TEST(LaplaceFormatWriter, SidecarWriteFailureDoesNotPublishASuccessfulShard) {
    ExportDirectory dir;
    auto* writer = format_writer_create("safetensors", dir.path.string().c_str());
    ASSERT_NE(writer, nullptr);
    std::filesystem::create_directory(dir.path / "config.json");
    ASSERT_EQ(format_writer_set_config(writer, "{}", 2), 0);
    EXPECT_EQ(format_writer_finalize(writer), -1);
    EXPECT_FALSE(std::filesystem::exists(dir.path / "model.safetensors"));
    format_writer_free(writer);
}
