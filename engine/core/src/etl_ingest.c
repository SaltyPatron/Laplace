#include "laplace/core/etl_ingest.h"

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
    const lp_ili_map_t*       ili_map; /* loaded from LAPLACE_CILI_DIR for anchor-kind edge fields; may be NULL */
    FILE*                     fp;
    char                      path[4096];
    size_t                    rows_left; /* 0 = unlimited */
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

/*
 * Resolve one edge field to its entity id by kind: a content field is tree-composed (witnessed here);
 * an ILI-synset anchor field resolves its WN key through the session map to the existing ILI entity id
 * (referenced, not witnessed — that entity is emitted by the OMW/WordNet path). Returns 0 = resolved
 * (*out set), 1 = skip this edge (anchor didn't resolve / no map), -1 = fatal.
 */
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
        if (sr == 1) continue; /* anchor didn't resolve — drop the edge (a miss, not a fabricated id) */
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

static size_t collect_entity_ids(const etl_pending_row_t* pending, size_t n, hash128_t* out,
                                 size_t* per_row_counts) {
    size_t total = 0;
    for (size_t i = 0; i < n; ++i) {
        size_t ec = laplace_compose_entity_count(pending[i].compose);
        per_row_counts[i] = ec;
        for (size_t j = 0; j < ec; ++j) {
            laplace_compose_entity_t e;
            if (laplace_compose_get_entity(pending[i].compose, j, &e) == 0)
                out[total++] = e.id;
        }
    }
    return total;
}

static int probe_pending(etl_pending_row_t* pending, size_t n, laplace_etl_exist_probe_fn probe,
                         void* probe_ctx, uint8_t** per_row_bitmaps) {
    if (!probe || n == 0) return 0;

    size_t* counts = (size_t*)calloc(n, sizeof(size_t));
    if (!counts) return -1;

    // Size the id buffer EXACTLY (the sum of per-row entity counts), not a fixed n*64 guess. A single
    // text row decomposes into one entity per codepoint/grapheme/word/sentence — far past 64 — so the
    // old guess let collect_entity_ids write past the allocation on real OMW/Wiktionary/Tatoeba data.
    size_t cap = 0;
    for (size_t i = 0; i < n; ++i) cap += laplace_compose_entity_count(pending[i].compose);
    if (cap == 0) { free(counts); return 0; }
    hash128_t* ids = (hash128_t*)malloc(cap * sizeof(hash128_t));
    if (!ids) { free(counts); return -1; }

    size_t total = collect_entity_ids(pending, n, ids, counts);
    if (total == 0) { free(ids); free(counts); return 0; }

    uint8_t* combined = (uint8_t*)calloc((total + 7) / 8, 1);
    if (!combined) { free(ids); free(counts); return -1; }

    if (probe(probe_ctx, ids, total, combined, total) != 0) {
        free(combined); free(ids); free(counts);
        return -1;
    }

    size_t off = 0;
    for (size_t i = 0; i < n; ++i) {
        size_t ec = counts[i];
        size_t bm_bytes = (ec + 7) / 8;
        per_row_bitmaps[i] = (uint8_t*)calloc(bm_bytes, 1);
        if (!per_row_bitmaps[i] && ec > 0) {
            free(combined); free(ids); free(counts);
            return -1;
        }
        for (size_t j = 0; j < ec; ++j) {
            size_t gi = off + j;
            if (gi < total && (combined[gi >> 3] & (1u << (gi & 7u))) != 0)
                per_row_bitmaps[i][j >> 3] |= (uint8_t)(1u << (j & 7u));
        }
        off += ec;
    }

    free(combined);
    free(ids);
    free(counts);
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

    int rc = probe_pending(pending, *pending_n, probe, probe_ctx, bitmaps);
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
    if (laplace_grammar_compose(line, line_len, ast, sess->cfg.modality_id, sess->cfg.source_id,
                                sess->cfg.type_meta_id, &compose) != 0 || !compose) {
        laplace_ast_free(ast);
        return 0;
    }

    if (probe) {
        etl_pending_row_t* pr = &pending[*pending_n];
        pr->compose = compose;
        pr->ast = ast;
        pr->line = (uint8_t*)malloc(line_len);
        if (!pr->line) {
            laplace_compose_result_free(compose);
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
    // Own modality_id for the session: process_row passes sess->cfg.modality_id to compose on EVERY row,
    // so the caller's (marshalled) string can't be relied on to outlive session_open. strdup + free at close,
    // symmetric with relation_surface above.
    s->cfg.modality_id = strdup(cfg->modality_id);
    if (!s->cfg.modality_id) {
        laplace_grammar_row_iter_free(s->iter);
        free(s->owned_edge_rules);
        free(s);
        return -3;
    }
    // Load the ILI offset map for anchor-kind edge fields from the same place C# does (LAPLACE_CILI_DIR).
    // Best-effort: if the env/file is absent the map stays NULL and anchor edges simply drop (a miss),
    // exactly as the C# resolver counts a miss rather than fabricating an id.
    {
        const char* cili = getenv("LAPLACE_CILI_DIR");
        if (cili && cili[0]) {
            char mp[4096];
            int wn = snprintf(mp, sizeof(mp), "%s/ili-map-pwn30.tab", cili);
            if (wn > 0 && (size_t)wn < sizeof(mp)) s->ili_map = lp_ili_map_load(mp);
        }
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
            /* Flush a trailing record with no terminating newline. */
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
