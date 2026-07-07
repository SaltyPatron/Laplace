#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/intent_stage.h"

#ifdef __cplusplus
extern "C" {
#endif

#define LAPLACE_ETL_WITNESS_NONE       0
#define LAPLACE_ETL_WITNESS_ATOMIC2020 1
#define LAPLACE_ETL_WITNESS_FIELD_EDGES 2
#define LAPLACE_ETL_WITNESS_CONCEPTNET  3

typedef int (*laplace_etl_accept_row_fn)(
    void*            ctx,
    const uint8_t*   line,
    size_t           len);

#define LAPLACE_ETL_ANCHOR_NONE       0
#define LAPLACE_ETL_ANCHOR_ILI_SYNSET 1

typedef struct {
    uint16_t subject_field;
    uint16_t object_field;
    uint8_t  subject_kind;
    uint8_t  object_kind;
    const char* relation_surface;
} laplace_etl_edge_rule_t;

typedef struct {
    const char* modality_id;
    hash128_t   source_id;
    hash128_t   type_meta_id;
    double      witness_weight;
    double      trust_weight;
    int64_t     now_unix_us;
    int         witness_kind;
    const laplace_etl_edge_rule_t* edge_rules;
    size_t      edge_rule_count;
    hash128_t   context_id;
    uint8_t     context_is_null;
    uint8_t     skip_comment_rows;
    uint8_t     line_framed;
    uint8_t     _pad_cfg[2];
    const char* ili_map_path;
} laplace_etl_config_t;

typedef struct laplace_etl_session laplace_etl_session_t;

typedef struct {
    uint64_t rows_read;
    uint64_t rows_parsed;
    uint64_t rows_compose_skipped;
    uint64_t rows_emitted;
} laplace_etl_stats_t;

typedef int (*laplace_etl_exist_probe_fn)(
    void*            ctx,
    const hash128_t* ids,
    const int32_t*   parents,
    size_t           n,
    uint8_t*         out_bitmap,
    size_t           bitmap_bits);

int laplace_etl_session_open(
    const laplace_etl_config_t* cfg,
    laplace_etl_session_t**     out);

void laplace_etl_session_close(laplace_etl_session_t* sess);

int laplace_etl_session_feed_file(
    laplace_etl_session_t*      sess,
    const char*                 path,
    size_t                      batch_row_cap,
    size_t                      max_rows,
    intent_stage_t*             stage,
    laplace_etl_exist_probe_fn  probe,
    void*                       probe_ctx,
    laplace_etl_accept_row_fn   accept,
    void*                       accept_ctx,
    laplace_etl_stats_t*        stats);

#ifdef __cplusplus
}
#endif
