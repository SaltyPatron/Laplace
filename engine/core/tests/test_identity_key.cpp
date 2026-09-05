#include <gtest/gtest.h>

#include <cstdlib>
#include <string>

#include "laplace/core/identity_key.h"

namespace {

static std::string normalize(const std::string& input) {
    uint8_t* output = nullptr;
    size_t output_len = 0;
    int rc = laplace_identity_key_normalize_utf8(
        reinterpret_cast<const uint8_t*>(input.data()), input.size(), &output, &output_len);
    EXPECT_EQ(0, rc);
    std::string result(reinterpret_cast<const char*>(output), output_len);
    std::free(output);
    return result;
}

}

TEST(LaplaceCoreIdentityKey, TrimsCollapsesFullCaseFoldsAndNormalizesNfc) {
    EXPECT_EQ("newton principia", normalize("\xE2\x80\x83Newton\t  Principia\n"));
    EXPECT_EQ("poincar\xC3\xA9", normalize("  Poincare\xCC\x81  "));
    EXPECT_EQ(normalize("Stra\xC3\x9F" "e"), normalize("STRASSE"));
}

TEST(LaplaceCoreIdentityKey, IsIdempotent) {
    const std::string once = normalize("  Passages\nfrom\tLife  ");
    EXPECT_EQ(once, normalize(once));
}
