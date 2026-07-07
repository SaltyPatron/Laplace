#include "laplace/core/etl_ingest.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "laplace/core/attestation_engine.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/etl_anchor.h"
#include "laplace/core/grammar_compose.h"
#include "laplace/core/grammar_decomposer.h"
#include "laplace/core/grammar_registry.h"
#include "laplace/core/hash128.h"
#include "laplace/core/merkle_dedup.h"
#include "laplace/core/tier_tree.h"

int etl_witness_conceptnet_row(intent_stage_t* stage, const laplace_etl_config_t* cfg,
                               const uint8_t* line, size_t line_len, const laplace_ast_t* ast);
int etl_witness_conceptnet_trunk_skip(intent_stage_t* stage, const laplace_etl_config_t* cfg,
                                      const uint8_t* line, size_t line_len, const laplace_ast_t* ast,
                                      laplace_etl_exist_probe_fn probe, void* probe_ctx);

#define ETL_PROBE_CHUNK 1024
#define ETL_READ_BUF    (4u << 20)

typedef struct {
    laplace_compose_result_t* compose;
    laplace_ast_t*            ast;
    uint8_t*                  line;
    size_t                    line_len;
} etl_pending_row_t;

struct laplace_etl_session {
    laplace_etl_config_t      cfg;
    laplace_etl_edge_rule_t*  owned_edge_rules;
    size_t                    owned_edge_rule_count;
    const TSLanguage*         recipe;
    laplace_grammar_row_iter_t* iter;
    const lp_ili_map_t*       ili_map;
    FILE*                     fp;
    char                      path[4096];
    size_t                    rows_left;
    laplace_etl_stats_t       total;
};

static void hash_canonical(const char* s, hash128_t* out) {
    hash128_blake3((const uint8_t*)s, strlen(s), out);
}

static hash128_t atomic_none_id(void) {
    hash128_t id;
    hash_canonical("substrate/atomic/none/v1", &id);
    return id;
}

typedef struct {
    const char* rel;
    const char* type;
} atomic_rel_t;

static const atomic_rel_t k_atomic_rels[] = {
    {"oEffect", "O_EFFECT"},       {"oReact", "O_REACT"},         {"oWant", "O_WANT"},
    {"xAttr", "X_ATTR"},           {"xEffect", "X_EFFECT"},       {"xIntent", "X_INTENT"},
    {"xNeed", "X_NEED"},           {"xReact", "X_REACT"},         {"xWant", "X_WANT"},
    {"xReason", "X_REASON"},       {"HinderedBy", "OBSTRUCTED_BY"},{"isAfter", "IS_AFTER"},
    {"isBefore", "IS_BEFORE"},     {"isFilledBy", "X_FILLED_BY"}, {"Causes", "CAUSES"},
    {"ObjectUse", "OBJECT_USE"},   {"AtLocation", "AT_LOCATION"}, {"HasSubEvent", "HAS_SUBEVENT"},
    {"CapableOf", "CAPABLE_OF"},   {"Desires", "DESIRES"},        {"HasProperty", "HAS_PROPERTY"},
    {"MadeUpOf", "MADE_UP_OF"},    {"NotDesires", "NOT_DESIRES"},
};

static const char* atomic_rel_type(const char* rel, size_t rel_len) {
    char buf[64];
    if (rel_len >= sizeof(buf)) return NULL;
    memcpy(buf, rel, rel_len);
    buf[rel_len] = '\0';
    for (size_t i = 0; i < sizeof(k_atomic_rels) / sizeof(k_atomic_rels[0]); ++i) {
        if (strcmp(buf, k_atomic_rels[i].rel) == 0) return k_atomic_rels[i].type;
    }
    return NULL;
}

static int trim_field(const uint8_t* utf8, size_t len, const uint8_t** out, size_t* out_len) {
    size_t s = 0;
    while (s < len && utf8[s] == ' ') ++s;
    size_t e = len;
    while (e > s && utf8[e - 1] == ' ') --e;
    *out = utf8 + s;
    *out_len = e - s;
    return (e > s) ? 0 : -1;
}

static int field_spans(const laplace_ast_t* ast, uint32_t* starts, uint32_t* ends, size_t cap,
                       size_t* out_n) {
    *out_n = 0;
    size_t n = laplace_ast_node_count(ast);
    for (size_t i = 0; i < n && *out_n < cap; ++i) {
        laplace_ast_node_t nd;
        if (laplace_ast_get_node(ast, i, &nd) != 0) continue;
        const char* tn = laplace_ast_type_name(ast, nd.type_id);
        if (!tn || strcmp(tn, "field") != 0) continue;
        starts[*out_n] = nd.start_byte;
        ends[*out_n] = nd.end_byte;
        ++(*out_n);
    }
    return 0;
}

static int content_id_for_span(intent_stage_t* stage, const hash128_t* source_id,
                               const uint8_t* utf8, size_t len, hash128_t* out_id) {
    hash128_t root;
    if (content_witness_batch_add(stage, utf8, len, source_id, &root) != 0) return -1;
    *out_id = root;
    return 0;
}

static int is_none_tail(const uint8_t* t, size_t tlen) {
    if (tlen == 0) return 1;
    if (tlen != 4) return 0;
    return ((t[0] | 32) == 'n' && (t[1] | 32) == 'o' && (t[2] | 32) == 'n' && (t[3] | 32) == 'e');
}

static int witness_atomic2020(intent_stage_t* stage, const laplace_etl_config_t* cfg,
                              const uint8_t* line, size_t line_len, const laplace_ast_t* ast) {
    uint32_t starts[16], ends[16];
    size_t nf = 0;
    field_spans(ast, starts, ends, 16, &nf);
    if (nf < 3) return 0;

    const uint8_t *h, *r, *t;
    size_t hlen, rlen, tlen;
    if (trim_field(line + starts[0], ends[0] - starts[0], &h, &hlen) != 0) return 0;
    if (trim_field(line + starts[1], ends[1] - starts[1], &r, &rlen) != 0) return 0;
    if (trim_field(line + starts[2], ends[2] - starts[2], &t, &tlen) != 0) tlen = 0;

    const char* type_name = atomic_rel_type((const char*)r, rlen);
    if (!type_name) return 0;

    hash128_t head_id;
    if (content_id_for_span(stage, &cfg->source_id, h, hlen, &head_id) != 0) return -1;

    hash128_t tail_id;
    if (is_none_tail(t, tlen)) {
        tail_id = atomic_none_id();
    } else if (content_id_for_span(stage, &cfg->source_id, t, tlen, &tail_id) != 0) {
        return -1;
    }

    const hash128_t* ctx = cfg->context_is_null ? NULL : &cfg->context_id;
    return laplace_attestation_categorical_add(
        stage, type_name, &head_id, &tail_id, 0, &cfg->source_id, ctx, cfg->context_is_null,
        cfg->trust_weight, 1, 1);
}

static int resolve_edge_field(intent_stage_t* stage, const laplace_etl_config_t* cfg,
                              const lp_ili_map_t* ili_map, uint8_t kind,
                              const uint8_t* field, size_t flen, hash128_t* out) {
    if (kind == LAPLACE_ETL_ANCHOR_ILI_SYNSET) {
        if (!ili_map || !lp_resolve_synset_anchor(ili_map, (const char*)field, flen, out)) return 1;
        return 0;
    }
    return content_id_for_span(stage, &cfg->source_id, field, flen, out) == 0 ? 0 : -1;
}

static int witness_field_edges(intent_stage_t* stage, const laplace_etl_config_t* cfg,
                               const lp_ili_map_t* ili_map,
                               const uint8_t* line, size_t line_len, const laplace_ast_t* ast) {
    uint32_t starts[32], ends[32];
    size_t nf = 0;
    field_spans(ast, starts, ends, 32, &nf);

    for (size_t ri = 0; ri < cfg->edge_rule_count; ++ri) {
        const laplace_etl_edge_rule_t* rule = &cfg->edge_rules[ri];
        if (rule->subject_field >= nf || rule->object_field >= nf) continue;

        const uint8_t *subj, *obj;
        size_t slen, olen;
        if (trim_field(line + starts[rule->subject_field], ends[rule->subject_field] - starts[rule->subject_field],
                       &subj, &slen) != 0)
            continue;
        if (trim_field(line + starts[rule->object_field], ends[rule->object_field] - starts[rule->object_field],
                       &obj, &olen) != 0)
            continue;

        hash128_t subject_id, object_id;
        int sr = resolve_edge_field(stage, cfg, ili_map, rule->subject_kind, subj, slen, &subject_id);
        if (sr < 0) return -1;
        if (sr == 1) continue;
        int or_ = resolve_edge_field(stage, cfg, ili_map, rule->object_kind, obj, olen, &object_id);
        if (or_ < 0) return -1;
        if (or_ == 1) continue;

        const hash128_t* ctx = cfg->context_is_null ? NULL : &cfg->context_id;
        if (laplace_attestation_categorical_add(
                stage, rule->relation_surface, &subject_id, &object_id, 0, &cfg->source_id, ctx,
                cfg->context_is_null, cfg->trust_weight, 1, 1) != 0)
            return -1;
    }
    return 0;
}

static int witness_row(intent_stage_t* stage, const laplace_etl_config_t* cfg,
                       const lp_ili_map_t* ili_map,
                       const uint8_t* line, size_t line_len, const laplace_ast_t* ast) {
    switch (cfg->witness_kind) {
        case LAPLACE_ETL_WITNESS_ATOMIC2020:
            return witness_atomic2020(stage, cfg, line, line_len, ast);
        case LAPLACE_ETL_WITNESS_FIELD_EDGES:
            return witness_field_edges(stage, cfg, ili_map, line, line_len, ast);
        case LAPLACE_ETL_WITNESS_CONCEPTNET:
            return etl_witness_conceptnet_row(stage, cfg, line, line_len, ast);
        default:
            return 0;
    }
}

static int probe_one_row(const laplace_compose_result_t* compose,
                         laplace_etl_exist_probe_fn probe, void* probe_ctx,
                         uint8_t** out_bm, size_t* out_bits) {
    size_t ec = laplace_compose_entity_count(compose);
    if (ec == 0) {
        *out_bm = NULL;
        *out_bits = 0;
        return 0;
    }

    tier_tree_t* tree = laplace_compose_get_tier_tree(compose);
    size_t nc = tree ? tier_tree_node_count(tree) : 0;
    if (!tree || nc != ec) {
        hash128_t* ids = (hash128_t*)malloc(ec * sizeof(hash128_t));
        if (!ids) return -1;
        for (size_t j = 0; j < ec; ++j) {
            laplace_compose_entity_t e;
            if (laplace_compose_get_entity(compose, j, &e) != 0) { free(ids); return -1; }
            ids[j] = e.id;
        }
        uint8_t* bm = (uint8_t*)calloc((ec + 7) / 8, 1);
        if (!bm) { free(ids); return -1; }
        int rc = probe(probe_ctx, ids, NULL, ec, bm, ec);
        free(ids);
        if (rc != 0) { free(bm); return rc; }
        *out_bm = bm;
        *out_bits = ec;
        return 0;
    }

    const hash128_t* id_arr = tier_tree_id_array(tree);
    const uint8_t*   tier_arr = tier_tree_tier_array(tree);
    const uint32_t*  parent_arr = tier_tree_parent_idx_array(tree);
    if (!id_arr || !tier_arr) return -1;

    int32_t* tree_to_flat = (int32_t*)malloc(nc * sizeof(int32_t));
    if (!tree_to_flat) return -1;
    for (size_t j = 0; j < nc; ++j) tree_to_flat[j] = -1;

    size_t trunk_cap = nc;
    hash128_t* trunk_ids = (hash128_t*)malloc(trunk_cap * sizeof(hash128_t));
    int32_t*   trunk_parents = (int32_t*)malloc(trunk_cap * sizeof(int32_t));
    if (!trunk_ids || !trunk_parents) {
        free(tree_to_flat); free(trunk_ids); free(trunk_parents);
        return -1;
    }
    size_t n_trunks = 0;
    for (size_t j = 0; j < nc; ++j) {
        if (tier_arr[j] < 2) continue;
        tree_to_flat[j] = (int32_t)n_trunks;
        trunk_ids[n_trunks] = id_arr[j];
        trunk_parents[n_trunks] = -1;
        if (parent_arr) {
            uint32_t p = parent_arr[j];
            while (p != TIER_TREE_INVALID && p < (uint32_t)nc) {
                if (tree_to_flat[p] >= 0) {
                    trunk_parents[n_trunks] = tree_to_flat[p];
                    break;
                }
                p = parent_arr ? parent_arr[p] : TIER_TREE_INVALID;
            }
        }
        ++n_trunks;
    }

    uint8_t* node_bm = (uint8_t*)calloc((nc + 7) / 8, 1);
    if (!node_bm) {
        free(tree_to_flat); free(trunk_ids); free(trunk_parents);
        return -1;
    }

    if (n_trunks > 0) {
        uint8_t* trunk_bm = (uint8_t*)calloc((n_trunks + 7) / 8, 1);
        if (!trunk_bm) {
            free(node_bm); free(tree_to_flat); free(trunk_ids); free(trunk_parents);
            return -1;
        }
        if (probe(probe_ctx, trunk_ids, trunk_parents, n_trunks, trunk_bm, n_trunks) != 0) {
            free(trunk_bm); free(node_bm); free(tree_to_flat);
            free(trunk_ids); free(trunk_parents);
            return -1;
        }
        size_t g = 0;
        for (size_t j = 0; j < nc; ++j) {
            if (tier_arr[j] < 2) continue;
            if (g < n_trunks && (trunk_bm[g >> 3] & (1u << (g & 7u))) != 0)
                node_bm[j >> 3] |= (uint8_t)(1u << (j & 7u));
            ++g;
        }
        free(trunk_bm);
    }

    hash128_t* tier01_ids = (hash128_t*)malloc(nc * sizeof(hash128_t));
    uint32_t*  tier01_idx = (uint32_t*)malloc(nc * sizeof(uint32_t));
    if (!tier01_ids || !tier01_idx) {
        free(tier01_ids); free(tier01_idx);
        free(tree_to_flat); free(trunk_ids); free(trunk_parents); free(node_bm);
        return -1;
    }
    size_t n_tier01 = 0;
    for (size_t j = 0; j < nc; ++j) {
        if (tier_arr[j] >= 2) continue;
        tier01_ids[n_tier01] = id_arr[j];
        tier01_idx[n_tier01] = (uint32_t)j;
        ++n_tier01;
    }
    if (n_tier01 > 0) {
        uint8_t* tier01_bm = (uint8_t*)calloc((n_tier01 + 7) / 8, 1);
        if (!tier01_bm) {
            free(tier01_ids); free(tier01_idx);
            free(tree_to_flat); free(trunk_ids); free(trunk_parents); free(node_bm);
            return -1;
        }
        if (probe(probe_ctx, tier01_ids, NULL, n_tier01, tier01_bm, n_tier01) != 0) {
            free(tier01_bm); free(tier01_ids); free(tier01_idx);
            free(tree_to_flat); free(trunk_ids); free(trunk_parents); free(node_bm);
            return -1;
        }
        for (size_t t = 0; t < n_tier01; ++t) {
            if ((tier01_bm[t >> 3] & (1u << (t & 7u))) != 0)
                node_bm[tier01_idx[t] >> 3] |= (uint8_t)(1u << (tier01_idx[t] & 7u));
        }
        free(tier01_bm);
    }

    free(tier01_ids);
    free(tier01_idx);
    free(tree_to_flat);
    free(trunk_ids);
    free(trunk_parents);
    *out_bm = node_bm;
    *out_bits = nc;
    return 0;
}

static int bitmap_all_absent(const uint8_t* bm, size_t bits) {
    if (!bm || bits == 0) return 1;
    size_t nbytes = (bits + 7) / 8;
    for (size_t i = 0; i < nbytes; ++i)
        if (bm[i] != 0) return 0;
    return 1;
}

static int root_gate_and_compose(laplace_etl_session_t* sess, intent_stage_t* stage,
                                 etl_pending_row_t* pending, size_t* pending_n,
                                 laplace_etl_exist_probe_fn probe, void* probe_ctx,
                                 size_t* rows_emitted) {
    size_t n = *pending_n;
    if (n == 0 || !probe) return 0;

    hash128_t* roots = (hash128_t*)malloc(n * sizeof(hash128_t));
    int32_t*   root_slot = (int32_t*)malloc(n * sizeof(int32_t));
    if (!roots || !root_slot) {
        free(roots);
        free(root_slot);
        return -1;
    }
    size_t n_roots = 0;
    for (size_t i = 0; i < n; ++i) {
        root_slot[i] = -1;
        hash128_t root_id;
        uint8_t   tier = 0;
        if (laplace_grammar_compose_row_root(pending[i].line, pending[i].line_len, pending[i].ast,
                                             sess->cfg.modality_id, &root_id, &tier) != 0)
            continue;
        root_slot[i] = (int32_t)n_roots;
        roots[n_roots++] = root_id;
    }

    uint8_t* root_bm = NULL;
    if (n_roots > 0) {
        root_bm = (uint8_t*)calloc((n_roots + 7) / 8, 1);
        if (!root_bm) {
            free(roots);
            free(root_slot);
            return -1;
        }
        if (probe(probe_ctx, roots, NULL, n_roots, root_bm, n_roots) != 0) {
            free(root_bm);
            free(roots);
            free(root_slot);
            return -1;
        }
    }

    size_t write = 0;
    for (size_t i = 0; i < n; ++i) {
        int present = 0;
        if (root_slot[i] >= 0) {
            int32_t k = root_slot[i];
            if (k >= 0 && (size_t)k < n_roots
                && (root_bm[k >> 3] & (1u << (k & 7u))) != 0)
                present = 1;
        }
        if (present) {
            if (witness_row(stage, &sess->cfg, sess->ili_map, pending[i].line, pending[i].line_len,
                            pending[i].ast) != 0) {
                free(root_bm);
                free(roots);
                free(root_slot);
                return -1;
            }
            free(pending[i].line);
            laplace_ast_free(pending[i].ast);
            pending[i].line = NULL;
            pending[i].ast = NULL;
            pending[i].compose = NULL;
            (*rows_emitted)++;
            sess->total.rows_compose_skipped++;
            sess->total.rows_emitted++;
            continue;
        }

        if (pending[i].compose == NULL) {
            laplace_compose_result_t* compose = NULL;
            if (laplace_grammar_compose_probe(
                    pending[i].line, pending[i].line_len, pending[i].ast, sess->cfg.modality_id,
                    sess->cfg.source_id, sess->cfg.type_meta_id, &compose) != 0
                || !compose) {
                free(pending[i].line);
                laplace_ast_free(pending[i].ast);
                pending[i].line = NULL;
                pending[i].ast = NULL;
                continue;
            }
            pending[i].compose = compose;
        }

        if (write != i) pending[write] = pending[i];
        ++write;
    }

    free(root_bm);
    free(roots);
    free(root_slot);
    *pending_n = write;
    return 0;
}

static int probe_pending(etl_pending_row_t* pending, size_t n, laplace_etl_exist_probe_fn probe,
                         void* probe_ctx, uint8_t** per_row_bitmaps) {
    if (!probe || n == 0) return 0;

    for (size_t i = 0; i < n; ++i) {
        size_t bits = 0;
        if (probe_one_row(pending[i].compose, probe, probe_ctx, &per_row_bitmaps[i], &bits) != 0)
            return -1;
    }
    return 0;
}

static void free_pending(etl_pending_row_t* p, size_t n) {
    for (size_t i = 0; i < n; ++i) {
        if (p[i].compose) laplace_compose_result_free(p[i].compose);
        if (p[i].ast) laplace_ast_free(p[i].ast);
        free(p[i].line);
    }
}

static int drain_pending_row(intent_stage_t* stage, const laplace_etl_config_t* cfg,
                             const lp_ili_map_t* ili_map,
                             etl_pending_row_t* pr, const uint8_t* bitmap, size_t bitmap_bits) {
    int all_present = 0;
    if (bitmap && bitmap_bits > 0) {
        tier_tree_t* tree = laplace_compose_get_tier_tree(pr->compose);
        size_t ec = laplace_compose_entity_count(pr->compose);
        size_t nc = tree ? tier_tree_node_count(tree) : 0;
        if (tree && nc > 0 && nc == ec && bitmap_bits >= nc) {
            uint32_t* novel_idx = (uint32_t*)malloc(nc * sizeof(uint32_t));
            if (novel_idx) {
                size_t out_n = 0;
                if (merkle_dedup_trunk_shortcircuit(tree, bitmap, bitmap_bits, novel_idx, &out_n) == 0
                    && out_n == 0)
                    all_present = 1;
                free(novel_idx);
            }
        }
    }
    if (!all_present) {
        if (bitmap_all_absent(bitmap, bitmap_bits)) {
            if (pr->compose) laplace_compose_result_free(pr->compose);
            pr->compose = NULL;
            if (laplace_grammar_compose(pr->line, pr->line_len, pr->ast, cfg->modality_id,
                                        cfg->source_id, cfg->type_meta_id, &pr->compose) != 0
                || !pr->compose)
                return -1;
        } else if (laplace_grammar_compose_materialize_phys(
                       pr->compose, pr->line, pr->line_len, pr->ast, cfg->modality_id) != 0)
            return -1;
    }
    if (laplace_compose_drain_into_stage(
            pr->compose, stage, &cfg->source_id, cfg->now_unix_us, cfg->witness_weight, bitmap,
            bitmap_bits) != 0)
        return -1;
    return witness_row(stage, cfg, ili_map, pr->line, pr->line_len, pr->ast);
}

static int flush_pending(laplace_etl_session_t* sess, intent_stage_t* stage,
                         etl_pending_row_t* pending, size_t* pending_n,
                         laplace_etl_exist_probe_fn probe, void* probe_ctx,
                         size_t* rows_emitted) {
    if (*pending_n == 0) return 0;

    uint8_t** bitmaps = (uint8_t**)calloc(*pending_n, sizeof(uint8_t*));
    if (!bitmaps) return -1;

    int rc = root_gate_and_compose(sess, stage, pending, pending_n, probe, probe_ctx, rows_emitted);
    if (rc == 0 && *pending_n > 0)
        rc = probe_pending(pending, *pending_n, probe, probe_ctx, bitmaps);
    if (rc == 0) {
        for (size_t i = 0; i < *pending_n; ++i) {
            size_t ec = laplace_compose_entity_count(pending[i].compose);
            size_t bits = ec;
            rc = drain_pending_row(stage, &sess->cfg, sess->ili_map, &pending[i], bitmaps[i], bits);
            if (rc != 0) break;
            (*rows_emitted)++;
            sess->total.rows_emitted++;
        }
    }

    for (size_t i = 0; i < *pending_n; ++i) free(bitmaps[i]);
    free(bitmaps);
    free_pending(pending, *pending_n);
    *pending_n = 0;
    return rc;
}

static int accept_line(const laplace_etl_config_t* cfg, const uint8_t* line, size_t len) {
    if (len == 0) return 0;
    if (cfg->skip_comment_rows && line[0] == '#') return 0;
    return 1;
}

static int process_row(laplace_etl_session_t* sess, intent_stage_t* stage,
                       const uint8_t* line, size_t line_len, laplace_grammar_row_iter_t* iter,
                       etl_pending_row_t* pending, size_t* pending_n, size_t batch_cap,
                       laplace_etl_exist_probe_fn probe, void* probe_ctx,
                       laplace_etl_accept_row_fn accept, void* accept_ctx, size_t* batch_rows) {
    if (!accept_line(&sess->cfg, line, line_len)) return 0;
    if (accept && accept(accept_ctx, line, line_len) == 0) return 0;

    laplace_ast_t* ast = NULL;
    if (laplace_grammar_row_iter_parse_row(iter, line, line_len, &ast) != 0 || !ast) return 0;
    sess->total.rows_parsed++;

    if (probe && sess->cfg.witness_kind == LAPLACE_ETL_WITNESS_CONCEPTNET) {
        int skipped = etl_witness_conceptnet_trunk_skip(
            stage, &sess->cfg, line, line_len, ast, probe, probe_ctx);
        if (skipped == 1) {
            laplace_ast_free(ast);
            (*batch_rows)++;
            sess->total.rows_compose_skipped++;
            sess->total.rows_emitted++;
            return 0;
        }
        if (skipped < 0) {
            laplace_ast_free(ast);
            return -1;
        }
    }

    laplace_compose_result_t* compose = NULL;
    if (probe) {
        etl_pending_row_t* pr = &pending[*pending_n];
        pr->compose = NULL;
        pr->ast = ast;
        pr->line = (uint8_t*)malloc(line_len);
        if (!pr->line) {
            laplace_ast_free(ast);
            return -1;
        }
        memcpy(pr->line, line, line_len);
        pr->line_len = line_len;
        ++(*pending_n);

        if (*pending_n >= ETL_PROBE_CHUNK) {
            size_t emitted = 0;
            int rc = flush_pending(sess, stage, pending, pending_n, probe, probe_ctx, &emitted);
            *batch_rows += emitted;
            if (rc != 0) return rc;
        }
        return 0;
    }

    if (laplace_grammar_compose_probe(line, line_len, ast, sess->cfg.modality_id, sess->cfg.source_id,
                                      sess->cfg.type_meta_id, &compose) != 0 || !compose) {
        laplace_ast_free(ast);
        return 0;
    }

    if (laplace_grammar_compose_materialize_phys(compose, line, line_len, ast, sess->cfg.modality_id)
        != 0) {
        laplace_compose_result_free(compose);
        laplace_ast_free(ast);
        return -1;
    }
    size_t bits = 0;
    if (laplace_compose_drain_into_stage(compose, stage, &sess->cfg.source_id, sess->cfg.now_unix_us,
                                         sess->cfg.witness_weight, NULL, bits) != 0) {
        laplace_compose_result_free(compose);
        laplace_ast_free(ast);
        return -1;
    }
    if (witness_row(stage, &sess->cfg, sess->ili_map, line, line_len, ast) != 0) {
        laplace_compose_result_free(compose);
        laplace_ast_free(ast);
        return -1;
    }
    laplace_compose_result_free(compose);
    laplace_ast_free(ast);
    (*batch_rows)++;
    sess->total.rows_emitted++;
    return 0;
}

int laplace_etl_session_open(const laplace_etl_config_t* cfg, laplace_etl_session_t** out) {
    if (!cfg || !out || !cfg->modality_id) return -1;
    const TSLanguage* recipe = laplace_grammar_lookup_by_id(cfg->modality_id);
    if (!recipe) return -2;

    laplace_etl_session_t* s = (laplace_etl_session_t*)calloc(1, sizeof(*s));
    if (!s) return -3;
    s->cfg = *cfg;
    s->owned_edge_rule_count = cfg->edge_rule_count;
    if (cfg->edge_rule_count > 0 && cfg->edge_rules) {
        s->owned_edge_rules = (laplace_etl_edge_rule_t*)calloc(
            cfg->edge_rule_count, sizeof(laplace_etl_edge_rule_t));
        if (!s->owned_edge_rules) {
            free(s);
            return -3;
        }
        for (size_t i = 0; i < cfg->edge_rule_count; ++i) {
            s->owned_edge_rules[i] = cfg->edge_rules[i];
            if (cfg->edge_rules[i].relation_surface) {
                s->owned_edge_rules[i].relation_surface =
                    strdup(cfg->edge_rules[i].relation_surface);
                if (!s->owned_edge_rules[i].relation_surface) {
                    for (size_t j = 0; j < i; ++j) free((void*)s->owned_edge_rules[j].relation_surface);
                    free(s->owned_edge_rules);
                    free(s);
                    return -3;
                }
            }
        }
        s->cfg.edge_rules = s->owned_edge_rules;
        s->cfg.edge_rule_count = s->owned_edge_rule_count;
    }
    s->recipe = recipe;
    if (laplace_grammar_row_iter_new(recipe, &s->iter) != 0) {
        free(s->owned_edge_rules);
        free(s);
        return -3;
    }
    if (cfg->line_framed)
        laplace_grammar_row_iter_set_line_framed(s->iter, 1);
    s->cfg.modality_id = strdup(cfg->modality_id);
    if (!s->cfg.modality_id) {
        laplace_grammar_row_iter_free(s->iter);
        free(s->owned_edge_rules);
        free(s);
        return -3;
    }
    {
        const char* ili_path = cfg->ili_map_path;
        if (ili_path && ili_path[0])
            s->ili_map = lp_ili_map_load(ili_path);
    }

    memset(&s->total, 0, sizeof(s->total));
    *out = s;
    return 0;
}

void laplace_etl_session_close(laplace_etl_session_t* sess) {
    if (!sess) return;
    if (sess->fp) fclose(sess->fp);
    if (sess->ili_map) lp_ili_map_free((lp_ili_map_t*)sess->ili_map);
    if (sess->iter) laplace_grammar_row_iter_free(sess->iter);
    if (sess->owned_edge_rules) {
        for (size_t i = 0; i < sess->owned_edge_rule_count; ++i)
            free((void*)sess->owned_edge_rules[i].relation_surface);
        free(sess->owned_edge_rules);
    }
    free((void*)sess->cfg.modality_id);
    free(sess);
}

int laplace_etl_session_feed_file(laplace_etl_session_t* sess, const char* path,
                                size_t batch_row_cap, size_t max_rows, intent_stage_t* stage,
                                laplace_etl_exist_probe_fn probe, void* probe_ctx,
                                laplace_etl_accept_row_fn accept, void* accept_ctx,
                                laplace_etl_stats_t* stats) {
    if (!sess || !path || !stage || batch_row_cap == 0) return -1;

    if (!sess->fp || strcmp(sess->path, path) != 0) {
        if (sess->fp) fclose(sess->fp);
        sess->fp = fopen(path, "rb");
        if (!sess->fp) return -1;
        strncpy(sess->path, path, sizeof(sess->path) - 1);
        sess->path[sizeof(sess->path) - 1] = '\0';
        sess->rows_left = max_rows;
    }

    laplace_etl_stats_t snap = sess->total;

    etl_pending_row_t pending[ETL_PROBE_CHUNK];
    size_t pending_n = 0;
    size_t batch_rows = 0;

    uint8_t* buf = (uint8_t*)malloc(ETL_READ_BUF);
    if (!buf) return -1;

    int more = 0;
    int rc = 0;

    while (batch_rows < batch_row_cap) {
        if (max_rows > 0 && sess->rows_left == 0) break;

        size_t nread = fread(buf, 1, ETL_READ_BUF, sess->fp);
        int at_eof = feof(sess->fp) ? 1 : 0;
        if (nread == 0 && !at_eof) break;

        laplace_raw_row_t* rows = NULL;
        size_t row_count = 0;
        if (laplace_grammar_row_iter_feed_lines(sess->iter, buf, nread, &rows, &row_count) != 0) {
            rc = -1;
            break;
        }

        for (size_t ri = 0; ri < row_count; ++ri) {
            sess->total.rows_read++;
            if (max_rows > 0) {
                if (sess->rows_left == 0) break;
                --sess->rows_left;
            }

            rc = process_row(sess, stage, rows[ri].row_utf8, rows[ri].row_len, sess->iter, pending,
                             &pending_n, batch_row_cap, probe, probe_ctx, accept, accept_ctx,
                             &batch_rows);
            if (rc != 0) break;
            if (batch_rows >= batch_row_cap) break;
        }

        laplace_grammar_row_iter_free_lines(rows, row_count);
        if (rc != 0) break;
        if (batch_rows >= batch_row_cap) {
            more = !at_eof;
            break;
        }
        if (at_eof) {
            rows = NULL;
            row_count = 0;
            if (laplace_grammar_row_iter_feed_lines(sess->iter, buf, 0, &rows, &row_count) == 0) {
                for (size_t ri = 0; ri < row_count; ++ri) {
                    sess->total.rows_read++;
                    if (max_rows > 0) {
                        if (sess->rows_left == 0) break;
                        --sess->rows_left;
                    }
                    rc = process_row(sess, stage, rows[ri].row_utf8, rows[ri].row_len, sess->iter,
                                     pending, &pending_n, batch_row_cap, probe, probe_ctx, accept,
                                     accept_ctx, &batch_rows);
                    if (rc != 0) break;
                    if (batch_rows >= batch_row_cap) break;
                }
                laplace_grammar_row_iter_free_lines(rows, row_count);
            }
            more = 0;
            break;
        }
        more = 1;
    }

    if (rc == 0 && pending_n > 0) {
        size_t emitted = 0;
        rc = flush_pending(sess, stage, pending, &pending_n, probe, probe_ctx, &emitted);
        batch_rows += emitted;
    }

    free(buf);

    if (stats) {
        stats->rows_read            += sess->total.rows_read - snap.rows_read;
        stats->rows_parsed          += sess->total.rows_parsed - snap.rows_parsed;
        stats->rows_compose_skipped += sess->total.rows_compose_skipped - snap.rows_compose_skipped;
        stats->rows_emitted         += sess->total.rows_emitted - snap.rows_emitted;
    }

    if (rc != 0) return -1;
    if (more && batch_rows >= batch_row_cap) return 1;
    if (!more) return 0;
    return batch_rows > 0 ? 1 : 0;
}
