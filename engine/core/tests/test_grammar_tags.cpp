#include <gtest/gtest.h>

#include <cstring>

// Seam proof: Laplace headers only — no tree_sitter/api.h.
#include "laplace/core/grammar_registry.h"
#include "laplace/core/grammar_tags.h"

namespace {

TEST(GrammarTags, PythonDefAndCallCaptures)
{
    const TSLanguage* py = laplace_grammar_lookup_by_id("python");
    ASSERT_NE(py, nullptr);

    const char* tags =
        "(function_definition name: (identifier) @name) @definition.function\n"
        "(call function: (identifier) @name) @reference.call\n";
    const char* code = "def greet():\n    pass\ngreet()\n";

    laplace_tag_t* out = nullptr;
    size_t n = 0;
    int rc = laplace_grammar_tags_run(
        py, tags, std::strlen(tags),
        reinterpret_cast<const uint8_t*>(code), std::strlen(code), &out, &n);

    ASSERT_EQ(rc, 0);
    ASSERT_NE(out, nullptr);
    ASSERT_GT(n, 0u);

    bool defFn = false, refCall = false, name = false;
    for (size_t i = 0; i < n; ++i) {
        switch (out[i].capture_kind) {
            case LAPLACE_TAG_DEF_FUNCTION: defFn = true; break;
            case LAPLACE_TAG_REF_CALL:     refCall = true; break;
            case LAPLACE_TAG_NAME:         name = true; break;
            default: break;
        }
        EXPECT_LE(out[i].start_byte, out[i].end_byte);
    }
    EXPECT_TRUE(defFn)   << "definition.function not captured";
    EXPECT_TRUE(refCall) << "reference.call not captured";
    EXPECT_TRUE(name)    << "@name not captured";

    laplace_grammar_tags_free(out);
}

TEST(GrammarTags, InvalidQueryIsRejected)
{
    const TSLanguage* py = laplace_grammar_lookup_by_id("python");
    ASSERT_NE(py, nullptr);
    const char* bad = "(this is not a valid query";
    const char* code = "x = 1\n";
    laplace_tag_t* out = nullptr;
    size_t n = 0;
    int rc = laplace_grammar_tags_run(
        py, bad, std::strlen(bad),
        reinterpret_cast<const uint8_t*>(code), std::strlen(code), &out, &n);
    EXPECT_EQ(rc, -2);
    EXPECT_EQ(out, nullptr);
}

}  // namespace
