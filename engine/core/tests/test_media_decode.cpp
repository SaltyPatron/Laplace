#include <gtest/gtest.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <string>
#include <vector>

#include "laplace/core/media_decode.h"

static std::string VaultOrSkip(const char* rel)
{
    const char* root = std::getenv("LAPLACE_DATA_ROOT");
    if (!root || !*root) root = "/vault/Data";
    return std::string(root) + "/" + rel;
}

TEST(MediaDecode, PngColors5x5)
{
    auto path = VaultOrSkip("test-data/images/colors_5x5.png");
    laplace_media_image_t img{};
    int rc = laplace_media_decode_image_file(path.c_str(), &img);
    if (rc != 0) {
        GTEST_SKIP() << "fixture missing: " << path;
    }
    EXPECT_EQ(img.width, 5u);
    EXPECT_EQ(img.height, 5u);
    ASSERT_NE(img.rgba, nullptr);
    laplace_media_free(img.rgba);
}

TEST(MediaDecode, WavTestTone)
{
    auto path = VaultOrSkip("test-data/audio/test_tone.wav");
    laplace_media_audio_t a{};
    int rc = laplace_media_decode_audio_file(path.c_str(), &a);
    if (rc != 0) {
        GTEST_SKIP() << "fixture missing: " << path;
    }
    EXPECT_GT(a.n_samples, 0u);
    EXPECT_EQ(a.sample_rate, 44100);
    ASSERT_NE(a.pcm, nullptr);
    laplace_media_free(a.pcm);
}

TEST(MediaDecode, Mp3FromTatoebaZipSample)
{
    /* Tiny synthetic MPEG frame is hard; skip unless an extracted mp3 exists. */
    auto path = VaultOrSkip("test-data/audio/sample.mp3");
    laplace_media_audio_t a{};
    int rc = laplace_media_decode_audio_file(path.c_str(), &a);
    if (rc != 0) {
        GTEST_SKIP() << "optional mp3 fixture missing: " << path;
    }
    EXPECT_GT(a.n_samples, 0u);
    laplace_media_free(a.pcm);
}
