#include <gtest/gtest.h>

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>

#include "laplace/core/etl_ingest.h"
#include "laplace/core/hash128.h"
#include "laplace/core/intent_stage.h"

namespace {

static void set_cili_dir(const std::string& d) {
#ifdef _WIN32
    _putenv_s("LAPLACE_CILI_DIR", d.c_str());
#else
    setenv("LAPLACE_CILI_DIR", d.c_str(), 1);
#endif
}



int probe_none(void*, const hash128_t*, const int32_t*, size_t n, uint8_t* out_bitmap, size_t) {
    std::memset(out_bitmap, 0, (n + 7) / 8);
    return 0;
}






TEST(EtlIngest, ProbePathSizesIdBufferExactly) {
    
    
    
    std::string field;
    for (int c = 0x21; c <= 0x7E; ++c) {
        if (c == '"' || c == '#' || c == '\\') continue;
        field += static_cast<char>(c);
    }
    const std::string row = "k\t" + field + "\n";   

    const auto path = std::filesystem::temp_directory_path() / "laplace_etl_probe_overwrite.tab";
    {
        std::ofstream f(path, std::ios::binary);
        f << row;
    }

    laplace_etl_config_t cfg;
    std::memset(&cfg, 0, sizeof(cfg));
    cfg.modality_id = "tsv";
    hash128_blake3(reinterpret_cast<const uint8_t*>("etl/probe"), 9, &cfg.source_id);
    hash128_blake3(reinterpret_cast<const uint8_t*>("Type"), 4, &cfg.type_meta_id);
    cfg.witness_weight  = 1.0;
    cfg.trust_weight    = 1.0;
    cfg.context_is_null = 1;

    laplace_etl_session_t* sess = nullptr;
    ASSERT_EQ(laplace_etl_session_open(&cfg, &sess), 0);
    ASSERT_NE(sess, nullptr);

    intent_stage_t* stage = intent_stage_new(1u << 16);
    ASSERT_NE(stage, nullptr);

    laplace_etl_stats_t stats;
    std::memset(&stats, 0, sizeof(stats));

    
    while (laplace_etl_session_feed_file(
               sess, path.string().c_str(), 1u << 20, 0, stage,
               probe_none, nullptr, nullptr, nullptr, &stats) == 1) {  }

    EXPECT_GT(intent_stage_entity_count(stage), 64u)
        << "row must exceed the old 64-slot per-row guess to actually exercise the over-write";

    laplace_etl_session_close(sess);
    intent_stage_free(stage);
    std::error_code ec;
    std::filesystem::remove(path, ec);
}



static size_t feed_anchor_field_edge(const std::string& cili_dir) {
    set_cili_dir(cili_dir);

    const std::string row = "30-02244956-v\tdog\n";   
    const auto path = std::filesystem::temp_directory_path() / "laplace_etl_anchor_edge.tab";
    {
        std::ofstream f(path, std::ios::binary);
        f << row;
    }

    laplace_etl_edge_rule_t rule;
    std::memset(&rule, 0, sizeof(rule));
    rule.subject_field = 0;
    rule.object_field  = 1;
    rule.subject_kind  = LAPLACE_ETL_ANCHOR_ILI_SYNSET;
    rule.object_kind   = LAPLACE_ETL_ANCHOR_NONE;
    rule.relation_surface = "SENSE_OF";

    laplace_etl_config_t cfg;
    std::memset(&cfg, 0, sizeof(cfg));
    cfg.modality_id = "tsv";
    hash128_blake3(reinterpret_cast<const uint8_t*>("etl/anchor"), 10, &cfg.source_id);
    hash128_blake3(reinterpret_cast<const uint8_t*>("Type"), 4, &cfg.type_meta_id);
    cfg.witness_weight  = 1.0;
    cfg.trust_weight    = 1.0;
    cfg.context_is_null = 1;
    cfg.witness_kind    = LAPLACE_ETL_WITNESS_FIELD_EDGES;
    cfg.edge_rules      = &rule;
    cfg.edge_rule_count = 1;

    laplace_etl_session_t* sess = nullptr;
    EXPECT_EQ(laplace_etl_session_open(&cfg, &sess), 0);
    intent_stage_t* stage = intent_stage_new(1u << 16);

    while (laplace_etl_session_feed_file(
               sess, path.string().c_str(), 1u << 20, 0, stage,
               nullptr, nullptr, nullptr, nullptr, nullptr) == 1) {  }

    size_t count = intent_stage_attestation_count(stage);

    laplace_etl_session_close(sess);
    intent_stage_free(stage);
    std::error_code ec;
    std::filesystem::remove(path, ec);
    return count;
}





TEST(EtlIngest, FieldEdgeAnchorResolvesViaSessionMap) {
    const auto base = std::filesystem::temp_directory_path();
    const auto map_dir   = base / "laplace_etl_anchor_map";
    const auto empty_dir = base / "laplace_etl_anchor_nomap";
    std::filesystem::create_directories(map_dir);
    std::filesystem::create_directories(empty_dir);
    {
        std::ofstream f(map_dir / "ili-map-pwn30.tab", std::ios::binary);
        f << "i23456\t02244956-v\n";   
    }

    size_t resolved   = feed_anchor_field_edge(map_dir.string());
    size_t unresolved = feed_anchor_field_edge(empty_dir.string());

    EXPECT_GT(resolved, unresolved)
        << "the ILI-synset anchor edge must be staged only when the session map resolves the key";

    set_cili_dir("");  
    std::error_code ec;
    std::filesystem::remove_all(map_dir, ec);
    std::filesystem::remove_all(empty_dir, ec);
}

}  
