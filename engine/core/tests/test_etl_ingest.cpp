#include <gtest/gtest.h>

#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>

#include "laplace/core/etl_ingest.h"
#include "laplace/core/hash128.h"
#include "laplace/core/intent_stage.h"

namespace {

// A NON-NULL probe is the only requirement to make the ETL take the two-phase probe_pending /
// collect_entity_ids path (the over-write site) instead of the direct drain. Reports "nothing exists".
int probe_none(void*, const hash128_t*, size_t n, uint8_t* out_bitmap, size_t) {
    std::memset(out_bitmap, 0, (n + 7) / 8);
    return 0;
}

// #7 regression: probe_pending must size its id buffer to the EXACT sum of per-row entity counts, not a
// fixed n*64 guess. ONE row with ~100 DISTINCT words composes into far more than 64 entities (dedup
// would collapse repeated chars, so distinct words are used), so with the old guess (cap = 1*64)
// collect_entity_ids wrote past the allocation. Under -fsanitize=address this fails at the file:line if
// the buffer is undersized; with the exact-sizing fix it passes.
TEST(EtlIngest, ProbePathSizesIdBufferExactly) {
    // The native ETL field-compose dedups to DISTINCT codepoints (it doesn't tokenize words), so the
    // field must carry >64 distinct characters to make ONE row's compose exceed the old 64-slot guess.
    // Use printable ASCII 0x21..0x7E minus the TSV/grammar-special bytes (tab/newline aren't in range).
    std::string field;
    for (int c = 0x21; c <= 0x7E; ++c) {
        if (c == '"' || c == '#' || c == '\\') continue;
        field += static_cast<char>(c);
    }
    const std::string row = "k\t" + field + "\n";   // ~91 distinct codepoints -> >64 composed entities

    const auto path = std::filesystem::temp_directory_path() / "laplace_etl_probe_overwrite.tab";
    {
        std::ofstream f(path, std::ios::binary);
        f << row;
    }

    laplace_etl_config_t cfg;
    std::memset(&cfg, 0, sizeof(cfg));
    cfg.modality_id = "tsv";
    hash128_blake3(reinterpret_cast<const uint8_t*>("etl/probe"), 9, &cfg.source_id);
    hash128_blake3(reinterpret_cast<const uint8_t*>("substrate/type/Meta/v1"), 22, &cfg.type_meta_id);
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

    // Non-null probe → the containment-dedup path runs; flush_pending at EOF probes the accumulated row.
    while (laplace_etl_session_feed_file(
               sess, path.string().c_str(), 1u << 20, 0, stage,
               probe_none, nullptr, nullptr, nullptr, &stats) == 1) { /* drain */ }

    EXPECT_GT(intent_stage_entity_count(stage), 64u)
        << "row must exceed the old 64-slot per-row guess to actually exercise the over-write";

    laplace_etl_session_close(sess);
    intent_stage_free(stage);
    std::error_code ec;
    std::filesystem::remove(path, ec);
}

}  // namespace
