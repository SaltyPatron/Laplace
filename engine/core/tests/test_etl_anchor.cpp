#include <gtest/gtest.h>

#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <system_error>

#include "laplace/core/etl_anchor.h"

namespace {



TEST(EtlAnchor, ParseMcrSynset_MatchesCSharp) {
    struct Case { const char* key; bool ok; int64_t off; char ss; } cases[] = {
        {"30-02244956-v",        true,  2244956, 'v'},   
        {"ili-30-02244956-v",    true,  2244956, 'v'},   
        {"mcr:ili-30-02244956-v", true, 2244956, 'v'},   
        {"00009147-v",           true,  9147,    'v'},   
        {"02164298-a",           true,  2164298, 'a'},   
        {"02164298-s",           true,  2164298, 's'},   
        {"NULL",                 false, 0,       0},
        {"",                     false, 0,       0},
        {"abc",                  false, 0,       0},      
        {"30-02244956-x",        false, 0,       0},      
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


TEST(EtlAnchor, ParseMapNetSynset_MatchesCSharp) {
    struct Case { const char* key; bool ok; int64_t off; char ss; } cases[] = {
        {"a#00057580",  true,  57580,   'a'},
        {"a#00057580$", true,  57580,   'a'},   
        {"v#01142646",  true,  1142646, 'v'},
        {"n#00057580",  true,  57580,   'n'},
        {"x#00057580",  false, 0,       0},      
        {"#00057580",   false, 0,       0},      
        {"a#",          false, 0,       0},      
        {"a#abc",       false, 0,       0},      
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



TEST(EtlAnchor, IliMapLoadResolve_SatelliteCollapse) {
    const auto path = std::filesystem::temp_directory_path() / "laplace_ili_map_test.tab";
    {
        std::ofstream f(path, std::ios::binary);
        f << "i100\t00000001-a\n"            
          << "i200\t00000002-s\n"            
          << "i300\t00000003-n\n"            
          << "i4026\t00006263-a\t0.352\n";   
    }

    lp_ili_map_t* m = lp_ili_map_load(path.string().c_str());
    ASSERT_NE(m, nullptr);
    EXPECT_EQ(lp_ili_map_count(m), 4u);

    EXPECT_STREQ(lp_ili_map_resolve(m, 2, 's'), "i200");   
    EXPECT_STREQ(lp_ili_map_resolve(m, 2, 'a'), "i200");   
    EXPECT_STREQ(lp_ili_map_resolve(m, 1, 'a'), "i100");
    EXPECT_STREQ(lp_ili_map_resolve(m, 1, 's'), "i100");   
    EXPECT_STREQ(lp_ili_map_resolve(m, 3, 'n'), "i300");
    EXPECT_STREQ(lp_ili_map_resolve(m, 6263, 'a'), "i4026");  
    EXPECT_EQ(lp_ili_map_resolve(m, 999, 'n'), nullptr);
    EXPECT_EQ(lp_ili_map_resolve(m, 3, 'v'), nullptr);     

    lp_ili_map_free(m);
    std::error_code ec;
    std::filesystem::remove(path, ec);
}

}  
