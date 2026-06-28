#include <gtest/gtest.h>

#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <system_error>

#include "laplace/core/etl_anchor.h"

namespace {

// Golden values mirror SourceEntityIdConventions.ParseMcrSynsetKey exactly — the native ETL must parse
// keys to the same (offset, ss_type) as the C# path so the resolved anchor ids are bit-identical.
TEST(EtlAnchor, ParseMcrSynset_MatchesCSharp) {
    struct Case { const char* key; bool ok; int64_t off; char ss; } cases[] = {
        {"30-02244956-v",        true,  2244956, 'v'},   // MCR with version prefix
        {"ili-30-02244956-v",    true,  2244956, 'v'},   // ili- prefix stripped
        {"mcr:ili-30-02244956-v", true, 2244956, 'v'},   // namespace stripped
        {"00009147-v",           true,  9147,    'v'},   // OMW bare offset-pos (no inner dash)
        {"02164298-a",           true,  2164298, 'a'},   // head adjective
        {"02164298-s",           true,  2164298, 's'},   // satellite adjective
        {"NULL",                 false, 0,       0},
        {"",                     false, 0,       0},
        {"abc",                  false, 0,       0},      // no dash
        {"30-02244956-x",        false, 0,       0},      // x is not a valid ss_type
    };
    for (const auto& c : cases) {
        int64_t off = -1;
        char ss = 0;
        int rc = lp_parse_mcr_synset(c.key, std::strlen(c.key), &off, &ss);
        EXPECT_EQ(rc, c.ok ? 1 : 0) << "key=" << c.key;
        if (c.ok) {
            EXPECT_EQ(off, c.off) << "key=" << c.key;
            EXPECT_EQ(ss, c.ss) << "key=" << c.key;
        }
    }
}

// Mirrors SourceEntityIdConventions.ParseMapNetSynsetKey (pos#offset, '$' terminator tolerated).
TEST(EtlAnchor, ParseMapNetSynset_MatchesCSharp) {
    struct Case { const char* key; bool ok; int64_t off; char ss; } cases[] = {
        {"a#00057580",  true,  57580,   'a'},
        {"a#00057580$", true,  57580,   'a'},   // trailing '$' terminator ignored
        {"v#01142646",  true,  1142646, 'v'},
        {"n#00057580",  true,  57580,   'n'},
        {"x#00057580",  false, 0,       0},      // x is not a valid ss_type
        {"#00057580",   false, 0,       0},      // no pos char before '#'
        {"a#",          false, 0,       0},      // nothing after '#'
        {"a#abc",       false, 0,       0},      // no digits
        {"NULL",        false, 0,       0},
    };
    for (const auto& c : cases) {
        int64_t off = -1;
        char ss = 0;
        int rc = lp_parse_mapnet_synset(c.key, std::strlen(c.key), &off, &ss);
        EXPECT_EQ(rc, c.ok ? 1 : 0) << "key=" << c.key;
        if (c.ok) {
            EXPECT_EQ(off, c.off) << "key=" << c.key;
            EXPECT_EQ(ss, c.ss) << "key=" << c.key;
        }
    }
}

// The native ILI map mirrors C# IliMap: a/s collapse (OMW's '-a' satellite query resolves the pwn '-s'
// entry) and the 3-column older-map form (confidence column ignored). Builds a tiny temp map, asserts.
TEST(EtlAnchor, IliMapLoadResolve_SatelliteCollapse) {
    const auto path = std::filesystem::temp_directory_path() / "laplace_ili_map_test.tab";
    {
        std::ofstream f(path, std::ios::binary);
        f << "i100\t00000001-a\n"            // head adjective
          << "i200\t00000002-s\n"            // satellite adjective (stored -s)
          << "i300\t00000003-n\n"            // noun
          << "i4026\t00006263-a\t0.352\n";   // 3-col older-map form — confidence ignored
    }

    lp_ili_map_t* m = lp_ili_map_load(path.string().c_str());
    ASSERT_NE(m, nullptr);
    EXPECT_EQ(lp_ili_map_count(m), 4u);

    EXPECT_STREQ(lp_ili_map_resolve(m, 2, 's'), "i200");   // as stored
    EXPECT_STREQ(lp_ili_map_resolve(m, 2, 'a'), "i200");   // OMW's '-a' satellite query resolves it
    EXPECT_STREQ(lp_ili_map_resolve(m, 1, 'a'), "i100");
    EXPECT_STREQ(lp_ili_map_resolve(m, 1, 's'), "i100");   // collapse is symmetric
    EXPECT_STREQ(lp_ili_map_resolve(m, 3, 'n'), "i300");
    EXPECT_STREQ(lp_ili_map_resolve(m, 6263, 'a'), "i4026");  // 3-col parsed, confidence dropped
    EXPECT_EQ(lp_ili_map_resolve(m, 999, 'n'), nullptr);
    EXPECT_EQ(lp_ili_map_resolve(m, 3, 'v'), nullptr);     // distinct pos stays distinct

    lp_ili_map_free(m);
    std::error_code ec;
    std::filesystem::remove(path, ec);
}

}  // namespace
