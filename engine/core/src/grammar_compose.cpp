#include "laplace/core/grammar_compose.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "laplace/core/attestation_engine.h"
#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/grapheme_floor.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/merkle_dedup.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/relation_law.h"
#include "laplace/core/tier_tree.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/trajectory.h"

static void hash_canonical(const char* s, hash128_t* out) {
    hash128_blake3(reinterpret_cast<const uint8_t*>(s), strlen(s), out);
}

static void node_type_entity_id(const char* modality, const char* node_type, hash128_t* out) {
    char buf[256];
    int n = snprintf(buf, sizeof(buf), "substrate/type/grammar/%s/%s/v1", modality, node_type);
    if (n <= 0 || (size_t)n >= sizeof(buf)) {
        hash128_zero(out);
        return;
    }
    hash_canonical(buf, out);
}

static int codepoint_resolver(uint32_t atom, void* ,
                              hash128_t* out_id, double out_coord[4],
                              hilbert128_t* out_hb) {
    return codepoint_table_resolve_atom(atom, out_id, out_coord, out_hb);
}

static void physicality_id_compute(hash128_t entity_id,
                                   double coord[4], const double* traj, size_t traj_n,
                                   hash128_t* out) {
    size_t traj_bytes = traj_n * sizeof(double);
    size_t total = 16 + 2 + 32 + traj_bytes;
    uint8_t* buf = (uint8_t*)malloc(total);
    if (!buf) { hash128_zero(out); return; }
    size_t o = 0;
    memcpy(buf + o, &entity_id, 16); o += 16;
    int16_t physicality_type = 1;
    memcpy(buf + o, &physicality_type, 2); o += 2;
    memcpy(buf + o, coord, 32); o += 32;
    if (traj_n > 0) {
        memcpy(buf + o, traj, traj_bytes);
        o += traj_bytes;
    }
    hash128_blake3(buf, o, out);
    free(buf);
}

typedef struct {
    hash128_t* comp_id;
    double*    comp_coord;
    uint8_t*   comp_tier;
    uint8_t*   comp_valid;
    size_t     n;
} compose_state_t;

static int push_entity(laplace_compose_result_t* r, hash128_t id, uint8_t tier,
                       hash128_t type_id);
static int compose_id_push(hash128_t** ids, size_t* count, size_t* cap, hash128_t id);
static int push_phys(laplace_compose_result_t* r, hash128_t entity_id,
                     double coord[4], hilbert128_t* hb,
                     const hash128_t* child_ids, const uint64_t* child_flags, size_t m);

static int is_json_modality(const char* modality_id) {
    return modality_id && strcmp(modality_id, "json") == 0;
}

static int json_hex_digit(uint8_t c) {
    if (c >= '0' && c <= '9') return (int)(c - '0');
    if (c >= 'a' && c <= 'f') return (int)(c - 'a' + 10);
    if (c >= 'A' && c <= 'F') return (int)(c - 'A' + 10);
    return -1;
}

static size_t utf8_encode_codepoint(uint32_t cp, uint8_t* out) {
    if (cp <= 0x7Fu) {
        out[0] = (uint8_t)cp;
        return 1;
    }
    if (cp <= 0x7FFu) {
        out[0] = (uint8_t)(0xC0u | (cp >> 6));
        out[1] = (uint8_t)(0x80u | (cp & 0x3Fu));
        return 2;
    }
    if (cp <= 0xFFFFu) {
        out[0] = (uint8_t)(0xE0u | (cp >> 12));
        out[1] = (uint8_t)(0x80u | ((cp >> 6) & 0x3Fu));
        out[2] = (uint8_t)(0x80u | (cp & 0x3Fu));
        return 3;
    }
    out[0] = (uint8_t)(0xF0u | (cp >> 18));
    out[1] = (uint8_t)(0x80u | ((cp >> 12) & 0x3Fu));
    out[2] = (uint8_t)(0x80u | ((cp >> 6) & 0x3Fu));
    out[3] = (uint8_t)(0x80u | (cp & 0x3Fu));
    return 4;
}

static int json_span_has_escapes(const uint8_t* span, size_t span_len) {
    for (size_t i = 0; i < span_len; ++i)
        if (span[i] == (uint8_t)'\\') return 1;
    return 0;
}

static int json_unescape_utf8(const uint8_t* span, size_t span_len,
                              uint8_t** out, size_t* out_len) {
    *out = NULL;
    *out_len = 0;
    if (!span || span_len == 0) return -1;
    if (!json_span_has_escapes(span, span_len)) return -1;

    uint8_t* buf = (uint8_t*)malloc(span_len);
    if (!buf) return -3;
    size_t w = 0;
    for (size_t i = 0; i < span_len; ++i) {
        uint8_t c = span[i];
        if (c != (uint8_t)'\\' || i + 1 >= span_len) {
            buf[w++] = c;
            continue;
        }
        uint8_t esc = span[++i];
        switch (esc) {
            case '"':  buf[w++] = (uint8_t)'"'; break;
            case '\\': buf[w++] = (uint8_t)'\\'; break;
            case '/':  buf[w++] = (uint8_t)'/'; break;
            case 'b':  buf[w++] = (uint8_t)'\b'; break;
            case 'f':  buf[w++] = (uint8_t)'\f'; break;
            case 'n':  buf[w++] = (uint8_t)'\n'; break;
            case 'r':  buf[w++] = (uint8_t)'\r'; break;
            case 't':  buf[w++] = (uint8_t)'\t'; break;
            case 'u':
                if (i + 4 >= span_len) { free(buf); return -1; }
                {
                    int h0 = json_hex_digit(span[i + 1]);
                    int h1 = json_hex_digit(span[i + 2]);
                    int h2 = json_hex_digit(span[i + 3]);
                    int h3 = json_hex_digit(span[i + 4]);
                    if (h0 < 0 || h1 < 0 || h2 < 0 || h3 < 0) { free(buf); return -1; }
                    uint32_t cp = (uint32_t)((h0 << 12) | (h1 << 8) | (h2 << 4) | h3);
                    i += 4;
                    uint8_t tmp[4];
                    size_t n = utf8_encode_codepoint(cp, tmp);
                    if (w + n > span_len) {
                        uint8_t* grown = (uint8_t*)realloc(buf, w + n + span_len);
                        if (!grown) { free(buf); return -3; }
                        buf = grown;
                    }
                    memcpy(buf + w, tmp, n);
                    w += n;
                }
                break;
            default:
                buf[w++] = esc;
                break;
        }
    }
    *out = buf;
    *out_len = w;
    return 0;
}

static int emit_grapheme_floor_entities(
    laplace_compose_result_t* r,
    tier_tree_t* tree, const laplace_grapheme_floor_t* floor,
    hash128_t** emitted_entity, size_t* emitted_entity_n, size_t* emitted_entity_cap) {
    hash128_t grapheme_type;
    hash_canonical("Grapheme", &grapheme_type);
    size_t g_first = laplace_grapheme_floor_graph_first_idx(floor);
    size_t g_count = laplace_grapheme_floor_graph_count(floor);
    for (size_t g = 0; g < g_count; ++g) {
        tier_node_view_t gv;
        if (tier_tree_get_node(tree, (uint32_t)(g_first + g), &gv) != 0) continue;
        /* Grapheme-floor law: a single-codepoint cluster IS its codepoint
         * (same bytes, same id -- hash_composer copies the sole child's id).
         * It is scaffold only; emitting it would mint a wrong-tier (tier 1)
         * row for a tier-0 identity. Only multi-codepoint clusters (combining
         * stacks, ZWJ sequences, Hangul, emoji) are real tier-1 content. */
        if (gv.child_count == 1) continue;
        if (compose_id_push(emitted_entity, emitted_entity_n, emitted_entity_cap, gv.id) != 1)
            continue;
        if (push_entity(r, gv.id, 1, grapheme_type) != 0) return -3;
        uint64_t gf = laplace_vertex_flags(1, 0, 0);
        hash128_t gid = gv.id;
        if (push_phys(r, gv.id, gv.coord, &gv.hilbert, &gid, &gf, 1) != 0)
            return -3;
    }
    return 0;
}





static int json_leaf_fill_grapheme_children(
    const uint8_t* utf8, size_t len, laplace_ast_t* ast, size_t idx,
    laplace_compose_result_t* r,
    hash128_t** emitted_entity, size_t* emitted_entity_n, size_t* emitted_entity_cap,
    hash128_t** out_ids, double** out_coords, uint64_t** out_flags, size_t* out_m,
    hash128_t* out_root_id) {
    *out_ids = NULL;
    *out_coords = NULL;
    *out_flags = NULL;
    *out_m = 0;
    if (out_root_id) hash128_zero(out_root_id);

    laplace_ast_node_t node;
    if (laplace_ast_get_node(ast, idx, &node) != 0) return -1;
    if (node.end_byte <= node.start_byte || node.end_byte > len) return -1;

    const uint8_t* span = utf8 + node.start_byte;
    size_t span_len = (size_t)(node.end_byte - node.start_byte);
    const char* nt = laplace_ast_type_name(ast, node.type_id);
    if (nt && strcmp(nt, "string_content") == 0) {
        
    } else if (nt && strcmp(nt, "string") == 0 && span_len >= 2
               && span[0] == '"' && span[span_len - 1] == '"') {
        span += 1;
        span_len -= 2;
    } else if (!nt || (strcmp(nt, "number") != 0 && strcmp(nt, "true") != 0
                       && strcmp(nt, "false") != 0 && strcmp(nt, "null") != 0)) {
        return -1;
    }
    if (span_len == 0) return -1;

    const uint8_t* content_span = span;
    size_t content_len = span_len;
    uint8_t* decoded_owned = NULL;
    if (json_span_has_escapes(span, span_len)) {
        if (json_unescape_utf8(span, span_len, &decoded_owned, &content_len) != 0
            || content_len == 0) {
            free(decoded_owned);
            return -1;
        }
        content_span = decoded_owned;
    }

    if (out_root_id) {
        if (laplace_content_root_id(content_span, content_len, out_root_id) != 0)
            hash128_zero(out_root_id);
    }

    tier_tree_t* local_tree = NULL;
    laplace_grapheme_floor_t local_floor;
    memset(&local_floor, 0, sizeof(local_floor));
    if (laplace_grapheme_floor_build(content_span, content_len, &local_tree, &local_floor) != 0) {
        free(decoded_owned);
        return -1;
    }
    if (codepoint_table_is_loaded()) {
        if (hash_composer_run(local_tree, codepoint_resolver, NULL) != 0) {
            laplace_grapheme_floor_free(&local_floor);
            tier_tree_free(local_tree);
            free(decoded_owned);
            return -1;
        }
    }

    if (r && emitted_entity) {
        int erc = emit_grapheme_floor_entities(
            r, local_tree, &local_floor,
            emitted_entity, emitted_entity_n, emitted_entity_cap);
        if (erc != 0) {
            laplace_grapheme_floor_free(&local_floor);
            tier_tree_free(local_tree);
            free(decoded_owned);
            return erc;
        }
    }

    size_t g_first = laplace_grapheme_floor_graph_first_idx(&local_floor);
    size_t g_count = laplace_grapheme_floor_graph_count(&local_floor);
    hash128_t* ids = (hash128_t*)malloc(g_count * sizeof(hash128_t));
    double* coords = (double*)malloc(g_count * 4 * sizeof(double));
    uint64_t* flags = (uint64_t*)malloc(g_count * sizeof(uint64_t));
    if (!ids || !coords || !flags) {
        free(ids);
        free(coords);
        free(flags);
        laplace_grapheme_floor_free(&local_floor);
        tier_tree_free(local_tree);
        free(decoded_owned);
        return -3;
    }
    size_t w = 0;
    for (size_t g = 0; g < g_count; ++g) {
        tier_node_view_t gv;
        if (tier_tree_get_node(local_tree, (uint32_t)(g_first + g), &gv) != 0) continue;
        ids[w] = gv.id;
        memcpy(coords + w * 4, gv.coord, 4 * sizeof(double));
        flags[w] = laplace_vertex_flags(1, 0, 0);
        w++;
    }
    laplace_grapheme_floor_free(&local_floor);
    tier_tree_free(local_tree);
    if (w == 0) {
        free(ids);
        free(coords);
        free(flags);
        free(decoded_owned);
        return -1;
    }
    *out_ids = ids;
    *out_coords = coords;
    *out_flags = flags;
    *out_m = w;
    free(decoded_owned);
    return 0;
}

static int compose_ast_nodes(const uint8_t* utf8, size_t len, laplace_ast_t* ast,
                             const char* modality_id,
                             const laplace_grapheme_floor_t* floor, tier_tree_t* tree,
                             laplace_compose_result_t* r,
                             hash128_t** emitted_entity, size_t* emitted_entity_n,
                             size_t* emitted_entity_cap,
                             compose_state_t* st) {
    (void)tree;
    size_t n = laplace_ast_node_count(ast);
    st->n = n;
    if (n == 0) return 0;

    st->comp_id    = (hash128_t*)calloc(n, sizeof(hash128_t));
    st->comp_coord = (double*)calloc(n * 4, sizeof(double));
    st->comp_tier  = (uint8_t*)calloc(n, 1);
    st->comp_valid = (uint8_t*)calloc(n, 1);
    if (!st->comp_id || !st->comp_coord || !st->comp_tier || !st->comp_valid)
        return -3;

    size_t graph_first = laplace_grapheme_floor_graph_first_idx(floor);

    uint32_t** children_of = (uint32_t**)calloc(n, sizeof(uint32_t*));
    uint32_t*  child_counts = (uint32_t*)calloc(n, sizeof(uint32_t));
    if (!children_of || !child_counts) {
        free(children_of);
        free(child_counts);
        return -3;
    }

    for (size_t i = 0; i < n; ++i) {
        laplace_ast_node_t nd;
        if (laplace_ast_get_node(ast, i, &nd) != 0) continue;
        if (nd.parent != LAPLACE_AST_ROOT && nd.parent < n) {
            uint32_t p = nd.parent;
            uint32_t cc = child_counts[p]++;
            uint32_t* new_ch = (uint32_t*)realloc(children_of[p], (size_t)(cc + 1) * sizeof(uint32_t));
            if (!new_ch) {
                for (size_t j = 0; j < n; ++j) free(children_of[j]);
                free(children_of);
                free(child_counts);
                return -3;
            }
            children_of[p] = new_ch;
            children_of[p][cc] = (uint32_t)i;
        }
    }

    for (size_t idx = n; idx-- > 0;) {
        laplace_ast_node_t node;
        if (laplace_ast_get_node(ast, idx, &node) != 0) continue;
        uint32_t kid_n = child_counts[idx];

        hash128_t*  child_ids    = NULL;
        double*     child_coords = NULL;
        uint64_t*   child_flags  = NULL;
        size_t      m            = 0;
        uint8_t     tier         = 0;
        hash128_t   leaf_root_id;
        hash128_zero(&leaf_root_id);

        if (kid_n > 0) {
            child_ids    = (hash128_t*)malloc(kid_n * sizeof(hash128_t));
            child_coords = (double*)malloc(kid_n * 4 * sizeof(double));
            child_flags  = (uint64_t*)malloc(kid_n * sizeof(uint64_t));
            if (!child_ids || !child_coords || !child_flags) {
                free(child_ids); free(child_coords); free(child_flags);
                continue;
            }
            uint8_t max_tier = 0;
            size_t w = 0;
            for (uint32_t j = 0; j < kid_n; ++j) {
                uint32_t c = children_of[idx][j];
                if (!st->comp_valid[c]) continue;
                child_ids[w] = st->comp_id[c];
                memcpy(child_coords + w * 4, st->comp_coord + c * 4, 4 * sizeof(double));
                uint8_t ct = st->comp_tier[c];
                child_flags[w] = laplace_vertex_flags(ct, 0, 0);
                if (ct > max_tier) max_tier = ct;
                w++;
            }
            if (w == 0) {
                free(child_ids); free(child_coords); free(child_flags);
                continue;
            }
            m = w;
            {
                const uint8_t TIER_DOCUMENT = 4;
                uint8_t next = (uint8_t)(max_tier + 1);
                tier = next < TIER_DOCUMENT ? next : TIER_DOCUMENT;
            }
        } else if (is_json_modality(modality_id)) {
            if (json_leaf_fill_grapheme_children(
                    utf8, len, ast, idx, r,
                    emitted_entity, emitted_entity_n, emitted_entity_cap,
                    &child_ids, &child_coords, &child_flags, &m,
                    &leaf_root_id) != 0)
                continue;
            tier = 2;
        } else {
            size_t g_start = 0, g_end = 0;
            if (laplace_grapheme_floor_span_to_graphemes(
                    floor, node.start_byte, node.end_byte, &g_start, &g_end) != 0) {
                continue;
            }
            m = g_end - g_start;
            child_ids    = (hash128_t*)malloc(m * sizeof(hash128_t));
            child_coords = (double*)malloc(m * 4 * sizeof(double));
            child_flags  = (uint64_t*)malloc(m * sizeof(uint64_t));
            if (!child_ids || !child_coords || !child_flags) {
                free(child_ids); free(child_coords); free(child_flags);
                continue;
            }
            size_t filled = 0;
            for (size_t g = g_start; g < g_end; ++g) {
                tier_node_view_t gv;
                uint32_t gidx = (uint32_t)(graph_first + g);
                if (tier_tree_get_node(tree, gidx, &gv) != 0) continue;
                child_ids[filled] = gv.id;
                memcpy(child_coords + filled * 4, gv.coord, 4 * sizeof(double));
                child_flags[filled] = laplace_vertex_flags(1, 0, 0);
                filled++;
            }
            m = filled;
            tier = 2;
        }

        if (m == 0) {
            free(child_ids);
            free(child_coords);
            free(child_flags);
            continue;
        }

        double out_coord[4];
        hilbert128_t hb;
        hash_composer_compose_node(tier, child_ids, child_coords, m,
                                   &st->comp_id[idx], out_coord, &hb);
        if (leaf_root_id.hi != 0 || leaf_root_id.lo != 0)
            st->comp_id[idx] = leaf_root_id;
        memcpy(st->comp_coord + idx * 4, out_coord, 4 * sizeof(double));
        st->comp_tier[idx]  = tier;
        st->comp_valid[idx] = 1;

        free(child_ids);
        free(child_coords);
        free(child_flags);
    }

    for (size_t i = 0; i < n; ++i) {
        free(children_of[i]);
    }
    free(children_of);
    free(child_counts);
    return 0;
}

static int push_entity(laplace_compose_result_t* r, hash128_t id, uint8_t tier,
                       hash128_t type_id) {
    laplace_compose_entity_t* n = (laplace_compose_entity_t*)realloc(
        r->entities, (r->entity_count + 1) * sizeof(*n));
    if (!n) return -3;
    r->entities = n;
    laplace_compose_entity_t* e = &r->entities[r->entity_count++];
    e->id = id;
    e->tier = tier;
    e->type_id = type_id;
    return 0;
}

static int compose_id_seen(const hash128_t* ids, size_t count, hash128_t id) {
    for (size_t j = 0; j < count; ++j)
        if (ids[j].hi == id.hi && ids[j].lo == id.lo) return 1;
    return 0;
}

static int compose_id_push(hash128_t** ids, size_t* count, size_t* cap, hash128_t id) {
    if (compose_id_seen(*ids, *count, id)) return 0;
    if (*count >= *cap) {
        size_t nc = *cap ? *cap * 2 : 16;
        hash128_t* p = (hash128_t*)realloc(*ids, nc * sizeof(hash128_t));
        if (!p) return -1;
        *ids = p;
        *cap = nc;
    }
    (*ids)[(*count)++] = id;
    return 1;
}

static int push_phys(laplace_compose_result_t* r, hash128_t entity_id,
                     double coord[4], hilbert128_t* hb,
                     const hash128_t* child_ids, const uint64_t* child_flags, size_t m) {
    double* traj = (double*)malloc(m * 4 * sizeof(double));
    if (!traj && m > 0) return -3;
    if (m > 0 && trajectory_build_flagged(child_ids, child_flags, m, traj) != 0) {
        free(traj);
        return -3;
    }
    hash128_t phys_id;
    physicality_id_compute(entity_id, coord, traj, m * 4, &phys_id);

    laplace_compose_physicality_t* n = (laplace_compose_physicality_t*)realloc(
        r->physicalities, (r->phys_count + 1) * sizeof(*n));
    if (!n) { free(traj); return -3; }
    r->physicalities = n;
    laplace_compose_physicality_t* p = &r->physicalities[r->phys_count++];
    p->id = phys_id;
    p->entity_id = entity_id;
    memcpy(p->coord, coord, 4 * sizeof(double));
    p->hilbert = *hb;
    p->trajectory_xyzm = traj;
    p->trajectory_n = m * 4;
    p->n_constituents = m;
    return 0;
}

static uint32_t containment_idmap_find(
    const uint32_t* slot, size_t cap,
    const laplace_compose_result_t* r, hash128_t target) {
    size_t p = (size_t)(target.lo & (cap - 1));
    for (;;) {
        uint32_t s = slot[p];
        if (s == UINT32_MAX) return UINT32_MAX;
        if (r->entities[s].id.hi == target.hi && r->entities[s].id.lo == target.lo) return s;
        p = (p + 1) & (cap - 1);
    }
}

static int grammar_compose_impl(const uint8_t* utf8, size_t len, laplace_ast_t* ast,
                                const char* modality_id, hash128_t source_id,
                                hash128_t type_meta_id, laplace_compose_result_t** out,
                                int materialize_phys);

static tier_tree_t* build_containment_tree(
    const laplace_compose_result_t* r, laplace_ast_t* ast, const compose_state_t* st) {
    size_t ec = r->entity_count;
    if (ec == 0) return NULL;
    tier_tree_t* t = tier_tree_new(ec);
    if (!t) return NULL;
    for (size_t i = 0; i < ec; ++i) {
        uint32_t idx = tier_tree_add_leaf(t, r->entities[i].tier, 0, 0, 0);
        if (idx == TIER_TREE_INVALID) { tier_tree_free(t); return NULL; }
        tier_tree_set_id(t, idx, &r->entities[i].id);
    }

    size_t cap = 1;
    while (cap < ec * 2) cap <<= 1;
    uint32_t* slot = (uint32_t*)malloc(cap * sizeof(uint32_t));
    if (!slot) return t;
    for (size_t i = 0; i < cap; ++i) slot[i] = UINT32_MAX;
    for (size_t i = 0; i < ec; ++i) {
        size_t p = (size_t)(r->entities[i].id.lo & (cap - 1));
        while (slot[p] != UINT32_MAX) {
            if (r->entities[slot[p]].id.hi == r->entities[i].id.hi
                && r->entities[slot[p]].id.lo == r->entities[i].id.lo) break;
            p = (p + 1) & (cap - 1);
        }
        if (slot[p] == UINT32_MAX) slot[p] = (uint32_t)i;
    }

    if (st && st->n > 0 && st->comp_valid && st->comp_id) {
        for (size_t a = 0; a < st->n; ++a) {
            if (!st->comp_valid[a]) continue;
            laplace_ast_node_t nd;
            if (laplace_ast_get_node(ast, a, &nd) != 0) continue;
            if (nd.parent == LAPLACE_AST_ROOT || nd.parent >= st->n) continue;
            if (!st->comp_valid[nd.parent]) continue;
            uint32_t ce = containment_idmap_find(slot, cap, r, st->comp_id[a]);
            uint32_t pe = containment_idmap_find(slot, cap, r, st->comp_id[nd.parent]);
            if (ce != UINT32_MAX && pe != UINT32_MAX && ce != pe)
                tier_tree_set_parent(t, ce, pe);
        }
    }
    free(slot);
    return t;
}

int laplace_grammar_compose(const uint8_t* utf8, size_t len, laplace_ast_t* ast,
                            const char* modality_id, hash128_t source_id,
                            hash128_t type_meta_id, laplace_compose_result_t** out) {
    return grammar_compose_impl(utf8, len, ast, modality_id, source_id, type_meta_id, out, 1);
}

int laplace_grammar_compose_probe(const uint8_t* utf8, size_t len, laplace_ast_t* ast,
                                  const char* modality_id, hash128_t source_id,
                                  hash128_t type_meta_id, laplace_compose_result_t** out) {
    return grammar_compose_impl(utf8, len, ast, modality_id, source_id, type_meta_id, out, 0);
}

static int emit_grapheme_floor_phys(laplace_compose_result_t* r,
                                    tier_tree_t* tree, const laplace_grapheme_floor_t* floor) {
    size_t g_first = laplace_grapheme_floor_graph_first_idx(floor);
    size_t g_count = laplace_grapheme_floor_graph_count(floor);
    for (size_t g = 0; g < g_count; ++g) {
        tier_node_view_t gv;
        if (tier_tree_get_node(tree, (uint32_t)(g_first + g), &gv) != 0) continue;
        /* Grapheme-floor law: single-codepoint clusters are pass-through
         * scaffold -- their identity is the tier-0 codepoint, whose
         * physicality is seeded by the tier-0 seed, not minted here. */
        if (gv.child_count == 1) continue;
        uint64_t gf = laplace_vertex_flags(1, 0, 0);
        hash128_t gid = gv.id;
        if (push_phys(r, gv.id, gv.coord, &gv.hilbert, &gid, &gf, 1) != 0) return -3;
    }
    return 0;
}

static int emit_ast_node_physicalities(
    laplace_compose_result_t* r, const uint8_t* utf8, size_t len, laplace_ast_t* ast,
    const laplace_grapheme_floor_t* floor, tier_tree_t* tree,
    const compose_state_t* st, size_t g_first, int json_mod) {
    size_t n = st->n;
    for (size_t idx = n; idx-- > 0;) {
        if (!st->comp_valid[idx]) continue;
        hash128_t id = st->comp_id[idx];

        laplace_ast_node_t node;
        if (laplace_ast_get_node(ast, idx, &node) != 0) continue;

        hilbert128_t hb;
        hilbert4d_encode(st->comp_coord + idx * 4, &hb);

        uint32_t kid_n = 0;
        for (size_t i = 0; i < n; ++i) {
            laplace_ast_node_t nd;
            if (laplace_ast_get_node(ast, i, &nd) != 0) continue;
            if (nd.parent == (uint32_t)idx) kid_n++;
        }
        hash128_t* child_ids = NULL;
        uint64_t*  child_flags = NULL;
        size_t m = 0;
        if (kid_n > 0) {
            child_ids = (hash128_t*)malloc(kid_n * sizeof(hash128_t));
            child_flags = (uint64_t*)malloc(kid_n * sizeof(uint64_t));
            if (child_ids && child_flags) {
                size_t w = 0;
                for (size_t i = 0; i < n; ++i) {
                    laplace_ast_node_t nd;
                    if (laplace_ast_get_node(ast, i, &nd) != 0) continue;
                    if (nd.parent != (uint32_t)idx || !st->comp_valid[i]) continue;
                    child_ids[w] = st->comp_id[i];
                    child_flags[w] = laplace_vertex_flags(st->comp_tier[i], 0, 0);
                    w++;
                }
                m = w;
            } else {
                free(child_ids); free(child_flags);
                child_ids = NULL;
            }
        } else if (json_mod) {
            double* jcoords = NULL;
            if (json_leaf_fill_grapheme_children(
                    utf8, len, ast, idx, NULL,
                    NULL, NULL, NULL,
                    &child_ids, &jcoords, &child_flags, &m, NULL) == 0) {
                free(jcoords);
            } else {
                child_ids = NULL;
                child_flags = NULL;
                m = 0;
            }
        } else {
            size_t g_start = 0, g_end = 0;
            if (laplace_grapheme_floor_span_to_graphemes(
                    floor, node.start_byte, node.end_byte, &g_start, &g_end) == 0) {
                size_t cap = g_end - g_start;
                child_ids = (hash128_t*)malloc(cap * sizeof(hash128_t));
                child_flags = (uint64_t*)malloc(cap * sizeof(uint64_t));
                if (child_ids && child_flags) {
                    for (size_t g = g_start; g < g_end; ++g) {
                        tier_node_view_t gv;
                        if (tier_tree_get_node(tree, (uint32_t)(g_first + g), &gv) != 0) continue;
                        child_ids[m] = gv.id;
                        child_flags[m] = laplace_vertex_flags(1, 0, 0);
                        m++;
                    }
                } else {
                    free(child_ids); free(child_flags);
                    child_ids = NULL;
                }
            }
        }
        if (m > 0 && child_ids) {
            if (push_phys(r, id, st->comp_coord + idx * 4, &hb,
                          child_ids, child_flags, m) != 0) {
                free(child_ids); free(child_flags);
                return -3;
            }
        }
        free(child_ids);
        free(child_flags);
        (void)id;
    }
    return 0;
}

int laplace_grammar_compose_materialize_phys(laplace_compose_result_t* r,
                                             const uint8_t* utf8, size_t len,
                                             laplace_ast_t* ast, const char* modality_id) {
    if (!r || !utf8 || !ast || !modality_id) return -1;
    if (r->phys_count > 0) return 0;

    compose_state_t st{};
    tier_tree_t* tree = NULL;
    laplace_grapheme_floor_t floor;
    memset(&floor, 0, sizeof(floor));
    const int json_mod = is_json_modality(modality_id);
    size_t g_first = 0;
    int rc = 0;

    if (!json_mod) {
        rc = laplace_grapheme_floor_build(utf8, len, &tree, &floor);
        if (rc != 0) return rc;
        if (codepoint_table_is_loaded()) {
            rc = hash_composer_run(tree, codepoint_resolver, NULL);
            if (rc != 0) goto done;
        }
        g_first = laplace_grapheme_floor_graph_first_idx(&floor);
        rc = emit_grapheme_floor_phys(r, tree, &floor);
        if (rc != 0) goto done;
    }

    rc = compose_ast_nodes(utf8, len, ast, modality_id, &floor, tree, NULL,
                           NULL, NULL, NULL, &st);
    if (rc != 0) goto done_st;

    rc = emit_ast_node_physicalities(r, utf8, len, ast, &floor, tree, &st,
                                     g_first, json_mod);

done_st:
    free(st.comp_id);
    free(st.comp_coord);
    free(st.comp_tier);
    free(st.comp_valid);
done:
    laplace_grapheme_floor_free(&floor);
    tier_tree_free(tree);
    return rc;
}

static int grammar_compose_impl(const uint8_t* utf8, size_t len, laplace_ast_t* ast,
                            const char* modality_id, hash128_t source_id,
                            hash128_t type_meta_id, laplace_compose_result_t** out,
                            int materialize_phys) {
    if (!utf8 || !ast || !modality_id || !out) return -1;
    *out = NULL;
    (void)source_id;
    if (len == 0 || laplace_ast_node_count(ast) == 0) return 0;

    laplace_compose_result_t* r = (laplace_compose_result_t*)calloc(1, sizeof(*r));
    if (!r) return -3;

    compose_state_t st{};
    size_t n = 0;
    hash128_t* emitted_entity = NULL;
    hash128_t* emitted_type   = NULL;
    size_t emitted_entity_n = 0, emitted_entity_cap = 0;
    size_t emitted_type_n   = 0, emitted_type_cap   = 0;
    int rc = 0;
    uint32_t** children_of  = NULL;
    uint32_t*  child_counts = NULL;

    tier_tree_t* tree = NULL;
    laplace_grapheme_floor_t floor;
    memset(&floor, 0, sizeof(floor));
    const int json_mod = is_json_modality(modality_id);
    size_t g_first = 0;

    if (!json_mod) {
        rc = laplace_grapheme_floor_build(utf8, len, &tree, &floor);
        if (rc != 0) { free(r); return rc; }

        if (codepoint_table_is_loaded()) {
            rc = hash_composer_run(tree, codepoint_resolver, NULL);
            if (rc != 0) {
                laplace_grapheme_floor_free(&floor);
                tier_tree_free(tree);
                free(r);
                return rc;
            }
        }

        hash128_t grapheme_type;
        hash_canonical("Grapheme", &grapheme_type);

        size_t g_first = laplace_grapheme_floor_graph_first_idx(&floor);
        size_t g_count = laplace_grapheme_floor_graph_count(&floor);
        for (size_t g = 0; g < g_count; ++g) {
            tier_node_view_t gv;
            if (tier_tree_get_node(tree, (uint32_t)(g_first + g), &gv) != 0) continue;
            /* Grapheme-floor law: single-codepoint clusters are pass-through
             * scaffold (id == codepoint id); never emit them at tier 1. */
            if (gv.child_count == 1) continue;
            if (push_entity(r, gv.id, 1, grapheme_type) != 0) { rc = -3; goto fail; }
            if (materialize_phys) {
                uint64_t gf = laplace_vertex_flags(1, 0, 0);
                hash128_t gid = gv.id;
                if (push_phys(r, gv.id, gv.coord, &gv.hilbert,
                              &gid, &gf, 1) != 0) { rc = -3; goto fail; }
            }
        }
    }

    if (!json_mod)
        g_first = laplace_grapheme_floor_graph_first_idx(&floor);

    rc = compose_ast_nodes(utf8, len, ast, modality_id, &floor, tree, r,
                           &emitted_entity, &emitted_entity_n, &emitted_entity_cap, &st);
    if (rc != 0) goto fail_st;

    n = st.n;

    /* Node->children adjacency, built ONCE and reused by the emit loop below.
       The loop used to rescan all n nodes twice per node to re-derive each
       node's children (O(n^2) on fat JSON records with thousands of nodes);
       this is the same adjacency compose_ast_nodes builds internally, in the
       same node order, so the composed ids stay byte-identical. */
    children_of  = (uint32_t**)calloc(n ? n : 1, sizeof(uint32_t*));
    child_counts = (uint32_t*)calloc(n ? n : 1, sizeof(uint32_t));
    if (!children_of || !child_counts) { rc = -3; goto fail_emit; }
    for (size_t i = 0; i < n; ++i) {
        laplace_ast_node_t nd;
        if (laplace_ast_get_node(ast, i, &nd) != 0) continue;
        if (nd.parent != LAPLACE_AST_ROOT && nd.parent < n) {
            uint32_t p = nd.parent;
            uint32_t cc = child_counts[p]++;
            uint32_t* new_ch = (uint32_t*)realloc(children_of[p], (size_t)(cc + 1) * sizeof(uint32_t));
            if (!new_ch) { rc = -3; goto fail_emit; }
            children_of[p] = new_ch;
            children_of[p][cc] = (uint32_t)i;
        }
    }

    for (size_t idx = n; idx-- > 0;) {
        if (!st.comp_valid[idx]) continue;
        hash128_t id = st.comp_id[idx];
        if (compose_id_push(&emitted_entity, &emitted_entity_n, &emitted_entity_cap, id) != 1)
            continue;

        laplace_ast_node_t node;
        if (laplace_ast_get_node(ast, idx, &node) != 0) continue;
        const char* node_type = laplace_ast_type_name(ast, node.type_id);
        if (!node_type) node_type = "unknown";
        hash128_t grammar_type_id;
        node_type_entity_id(modality_id, node_type, &grammar_type_id);
        if (compose_id_push(&emitted_type, &emitted_type_n, &emitted_type_cap, grammar_type_id) == 1) {
            if (push_entity(r, grammar_type_id, 0, type_meta_id) != 0) { rc = -3; goto fail_emit; }
        }
        if (push_entity(r, id, st.comp_tier[idx], grammar_type_id) != 0) { rc = -3; goto fail_emit; }

        hilbert128_t hb;
        hilbert4d_encode(st.comp_coord + idx * 4, &hb);

        uint32_t kid_n = child_counts[idx];
        hash128_t* child_ids = NULL;
        uint64_t*  child_flags = NULL;
        size_t m = 0;
        if (kid_n > 0) {
            child_ids = (hash128_t*)malloc(kid_n * sizeof(hash128_t));
            child_flags = (uint64_t*)malloc(kid_n * sizeof(uint64_t));
            if (child_ids && child_flags) {
                size_t w = 0;
                for (uint32_t j = 0; j < kid_n; ++j) {
                    uint32_t c = children_of[idx][j];
                    if (!st.comp_valid[c]) continue;
                    child_ids[w] = st.comp_id[c];
                    child_flags[w] = laplace_vertex_flags(st.comp_tier[c], 0, 0);
                    w++;
                }
                m = w;
            } else {
                free(child_ids); free(child_flags);
                child_ids = NULL;
            }
        } else if (json_mod) {
            double* jcoords = NULL;
            if (json_leaf_fill_grapheme_children(
                    utf8, len, ast, idx, NULL,
                    NULL, NULL, NULL,
                    &child_ids, &jcoords, &child_flags, &m, NULL) == 0) {
                free(jcoords);
            } else {
                child_ids = NULL;
                child_flags = NULL;
                m = 0;
            }
        } else {
            size_t g_start = 0, g_end = 0;
            if (laplace_grapheme_floor_span_to_graphemes(
                    &floor, node.start_byte, node.end_byte, &g_start, &g_end) == 0) {
                size_t cap = g_end - g_start;
                child_ids = (hash128_t*)malloc(cap * sizeof(hash128_t));
                child_flags = (uint64_t*)malloc(cap * sizeof(uint64_t));
                if (child_ids && child_flags) {
                    for (size_t g = g_start; g < g_end; ++g) {
                        tier_node_view_t gv;
                        if (tier_tree_get_node(tree, (uint32_t)(g_first + g), &gv) != 0) continue;
                        child_ids[m] = gv.id;
                        child_flags[m] = laplace_vertex_flags(1, 0, 0);
                        m++;
                    }
                } else {
                    free(child_ids); free(child_flags);
                    child_ids = NULL;
                }
            }
        }
        if (m > 0 && child_ids && materialize_phys) {
            if (push_phys(r, id, st.comp_coord + idx * 4, &hb,
                          child_ids, child_flags, m) != 0) {
                free(child_ids); free(child_flags);
                rc = -3; goto fail_emit;
            }
        }
        free(child_ids);
        free(child_flags);

        laplace_compose_span_t* sp = (laplace_compose_span_t*)realloc(
            r->spans, (r->span_count + 1) * sizeof(*sp));
        if (!sp) { rc = -3; goto fail_emit; }
        r->spans = sp;
        r->spans[r->span_count].start_byte = node.start_byte;
        r->spans[r->span_count].end_byte   = node.end_byte;
        r->spans[r->span_count].entity_id  = id;
        r->span_count++;
    }

    if (st.comp_valid[0])
        r->root_id = st.comp_id[0];

    for (size_t i = 0; i < n; ++i) free(children_of[i]);
    free(children_of);  children_of = NULL;
    free(child_counts); child_counts = NULL;

    /* Sibling-order PRECEDES. JSON containers emit none: member/array
       order there is dump-format plumbing, and attesting it duplicated
       ordering the physicality trajectories already carry losslessly.
       Text modalities keep word-order evidence. Single pass with a
       per-parent last-child cursor plus an open-addressing dedup table
       (the old shape rescanned all n nodes per parent and linearly
       scanned the precedes array per pair — O(n^2) on fat records). */
    if (!json_mod && n > 0) {
        uint32_t* last_child = (uint32_t*)malloc(n * sizeof(uint32_t));
        size_t    tab_cap    = 64;
        while (tab_cap < n * 2) tab_cap <<= 1;
        uint32_t* table    = (uint32_t*)malloc(tab_cap * sizeof(uint32_t));
        size_t    prec_cap = r->precedes_count;
        if (!last_child || !table) {
            free(last_child); free(table);
            rc = -3; goto fail_emit;
        }
        memset(last_child, 0xFF, n * sizeof(uint32_t));
        memset(table, 0xFF, tab_cap * sizeof(uint32_t));
        for (size_t i = 0; i < n; ++i) {
            laplace_ast_node_t nd;
            if (laplace_ast_get_node(ast, i, &nd) != 0) continue;
            if (!st.comp_valid[i]) continue;
            if (nd.parent >= n) continue;
            uint32_t prev = last_child[nd.parent];
            last_child[nd.parent] = (uint32_t)i;
            if (prev == UINT32_MAX) continue;
            hash128_t a = st.comp_id[prev], b = st.comp_id[i];
            uint64_t h = (a.hi ^ (a.lo * 0x9E3779B97F4A7C15ULL))
                       ^ ((b.hi + 0xC2B2AE3D27D4EB4FULL) * 0x165667B19E3779F9ULL) ^ b.lo;
            size_t s = (size_t)h & (tab_cap - 1);
            uint32_t found = UINT32_MAX;
            for (;;) {
                uint32_t k = table[s];
                if (k == UINT32_MAX) break;
                if (r->precedes[k].subject_id.hi == a.hi && r->precedes[k].subject_id.lo == a.lo
                    && r->precedes[k].object_id.hi == b.hi && r->precedes[k].object_id.lo == b.lo) {
                    found = k; break;
                }
                s = (s + 1) & (tab_cap - 1);
            }
            if (found != UINT32_MAX) {
                r->precedes[found].games++;
                continue;
            }
            if (r->precedes_count == prec_cap) {
                size_t ncap = prec_cap ? prec_cap * 2 : 64;
                laplace_compose_precedes_t* pr = (laplace_compose_precedes_t*)realloc(
                    r->precedes, ncap * sizeof(*pr));
                if (!pr) { free(last_child); free(table); rc = -3; goto fail_emit; }
                r->precedes = pr;
                prec_cap = ncap;
            }
            r->precedes[r->precedes_count].subject_id = a;
            r->precedes[r->precedes_count].object_id = b;
            r->precedes[r->precedes_count].games = 1;
            table[s] = (uint32_t)r->precedes_count;
            r->precedes_count++;
        }
        free(last_child);
        free(table);
    }

    r->tree = build_containment_tree(r, ast, &st);

    free(emitted_entity);
    free(emitted_type);
    free(st.comp_id);
    free(st.comp_coord);
    free(st.comp_tier);
    free(st.comp_valid);
    laplace_grapheme_floor_free(&floor);
    tier_tree_free(tree);
    *out = r;
    return 0;

fail_emit:
    if (children_of) { for (size_t i = 0; i < n; ++i) free(children_of[i]); free(children_of); }
    free(child_counts);
    free(emitted_entity);
    free(emitted_type);
fail_st:
    free(st.comp_id);
    free(st.comp_coord);
    free(st.comp_tier);
    free(st.comp_valid);
fail:
    laplace_grapheme_floor_free(&floor);
    tier_tree_free(tree);
    laplace_compose_result_free(r);
    return rc;
}

int laplace_grammar_compose_node_id(const uint8_t* utf8, size_t len, laplace_ast_t* ast,
                                    const char* modality_id, size_t ast_node_index,
                                    hash128_t* out_id, uint8_t* out_tier) {
    if (!utf8 || !ast || !modality_id || !out_id) return -1;
    if (ast_node_index >= laplace_ast_node_count(ast)) return -1;

    tier_tree_t* tree = NULL;
    laplace_grapheme_floor_t floor;
    int rc = laplace_grapheme_floor_build(utf8, len, &tree, &floor);
    if (rc != 0) return rc;
    if (codepoint_table_is_loaded())
        hash_composer_run(tree, codepoint_resolver, NULL);

    compose_state_t st{};
    rc = compose_ast_nodes(utf8, len, ast, modality_id, &floor, tree, NULL,
                           NULL, NULL, NULL, &st);
    if (rc == 0 && st.comp_valid[ast_node_index]) {
        *out_id = st.comp_id[ast_node_index];
        if (out_tier) *out_tier = st.comp_tier[ast_node_index];
    } else {
        rc = -1;
    }
    free(st.comp_id);
    free(st.comp_coord);
    free(st.comp_tier);
    free(st.comp_valid);
    laplace_grapheme_floor_free(&floor);
    tier_tree_free(tree);
    return rc;
}

int laplace_grammar_compose_row_root(const uint8_t* utf8, size_t len, laplace_ast_t* ast,
                                     const char* modality_id, hash128_t* out_id, uint8_t* out_tier) {
    return laplace_grammar_compose_node_id(utf8, len, ast, modality_id, 0, out_id, out_tier);
}

int laplace_compose_span_lookup(const laplace_compose_result_t* r,
                                uint32_t start_byte, uint32_t end_byte,
                                hash128_t* out_id) {
    if (!r || !out_id) return -1;
    for (size_t i = 0; i < r->span_count; ++i) {
        if (r->spans[i].start_byte == start_byte && r->spans[i].end_byte == end_byte) {
            *out_id = r->spans[i].entity_id;
            return 0;
        }
    }
    return -1;
}

size_t laplace_compose_entity_count(const laplace_compose_result_t* r) {
    return r ? r->entity_count : 0;
}
size_t laplace_compose_physicality_count(const laplace_compose_result_t* r) {
    return r ? r->phys_count : 0;
}
size_t laplace_compose_precedes_count(const laplace_compose_result_t* r) {
    return r ? r->precedes_count : 0;
}
hash128_t laplace_compose_root_id(const laplace_compose_result_t* r) {
    hash128_t z;
    hash128_zero(&z);
    return r ? r->root_id : z;
}

tier_tree_t* laplace_compose_get_tier_tree(const laplace_compose_result_t* r) {
    return r ? r->tree : NULL;
}

int laplace_compose_get_entity(const laplace_compose_result_t* r, size_t i,
                               laplace_compose_entity_t* out) {
    if (!r || !out || i >= r->entity_count) return -1;
    *out = r->entities[i];
    return 0;
}
int laplace_compose_get_physicality(const laplace_compose_result_t* r, size_t i,
                                    laplace_compose_physicality_t* out) {
    if (!r || !out || i >= r->phys_count) return -1;
    *out = r->physicalities[i];
    return 0;
}
int laplace_compose_get_precedes(const laplace_compose_result_t* r, size_t i,
                                 laplace_compose_precedes_t* out) {
    if (!r || !out || i >= r->precedes_count) return -1;
    *out = r->precedes[i];
    return 0;
}

typedef struct {
    uint8_t  emit_all;
    uint8_t* novel_entity;
    size_t   entity_n;
    hash128_t* novel_ids;
    size_t   novel_id_n;
} compose_emit_filter_t;

static compose_emit_filter_t make_emit_filter(
    const laplace_compose_result_t* r,
    const uint8_t*                  existing_bitmap,
    size_t                          bitmap_bits) {
    compose_emit_filter_t f = {1, NULL, 0, NULL, 0};
    if (!r || !existing_bitmap || existing_bitmap == NULL || bitmap_bits == 0) return f;
    tier_tree_t* tree = r->tree;
    if (!tree) return f;
    const size_t node_count = tier_tree_node_count(tree);
    if (node_count == 0 || node_count != r->entity_count || bitmap_bits < node_count) return f;

    uint32_t* novel_idx = (uint32_t*)malloc(node_count * sizeof(uint32_t));
    if (!novel_idx) return f;
    size_t novel_count = 0;
    if (merkle_dedup_trunk_shortcircuit(tree, existing_bitmap, bitmap_bits,
                                        novel_idx, &novel_count) != 0) {
        free(novel_idx);
        return f;
    }

    f.emit_all = 0;
    f.novel_entity = (uint8_t*)calloc(node_count, 1);
    f.entity_n = node_count;
    if (!f.novel_entity) { free(novel_idx); return f; }
    for (size_t i = 0; i < novel_count; ++i)
        f.novel_entity[novel_idx[i]] = 1;

    f.novel_ids = (hash128_t*)malloc(novel_count * sizeof(hash128_t));
    f.novel_id_n = 0;
    if (f.novel_ids) {
        for (size_t i = 0; i < r->entity_count; ++i) {
            if (!f.novel_entity[i]) continue;
            f.novel_ids[f.novel_id_n++] = r->entities[i].id;
        }
    }
    free(novel_idx);
    return f;
}

static void free_emit_filter(compose_emit_filter_t* f) {
    if (!f) return;
    free(f->novel_entity);
    free(f->novel_ids);
    f->novel_entity = NULL;
    f->novel_ids = NULL;
}

static int entity_novel(const compose_emit_filter_t* f, size_t idx) {
    return f->emit_all || (f->novel_entity && idx < f->entity_n && f->novel_entity[idx]);
}

static int phys_novel(const compose_emit_filter_t* f, const hash128_t* entity_id) {
    if (f->emit_all || !f->novel_ids) return 1;
    for (size_t i = 0; i < f->novel_id_n; ++i) {
        if (hash128_equals(entity_id, &f->novel_ids[i])) return 1;
    }
    return 0;
}

int laplace_compose_drain_into_stage(
    const laplace_compose_result_t* r,
    intent_stage_t*                 stage,
    const hash128_t*                source_id,
    int64_t                         now_unix_us,
    double                          witness_weight,
    const uint8_t*                  existing_bitmap,
    size_t                          bitmap_bits) {
    if (!r || !stage || !source_id) return -1;

    compose_emit_filter_t filter = make_emit_filter(r, existing_bitmap, bitmap_bits);

    for (size_t i = 0; i < r->entity_count; ++i) {
        if (!entity_novel(&filter, i)) continue;
        const laplace_compose_entity_t* e = &r->entities[i];
        if (intent_stage_witness_seen(stage, &e->id)) continue;
        if (intent_stage_add_entity(stage, &e->id, (int16_t)e->tier, &e->type_id, source_id) != 0) {
            free_emit_filter(&filter);
            return -1;
        }
        intent_stage_witness_record(stage, &e->id);
    }

    for (size_t i = 0; i < r->phys_count; ++i) {
        const laplace_compose_physicality_t* ph = &r->physicalities[i];
        if (!phys_novel(&filter, &ph->entity_id)) continue;
        if (intent_stage_witness_seen(stage, &ph->id)) continue;
        if (intent_stage_add_physicality(
                stage, &ph->id, &ph->entity_id, 1, ph->coord, &ph->hilbert,
                ph->trajectory_xyzm, (uint32_t)(ph->trajectory_n / 4), (int32_t)ph->n_constituents,
                1, 0.0, 1, 0, now_unix_us) != 0) {
            free_emit_filter(&filter);
            return -1;
        }
        intent_stage_witness_record(stage, &ph->id);
    }

    hash128_t precedes_type;
    if (laplace_relation_resolve("PRECEDES", &precedes_type) != 0) {
        free_emit_filter(&filter);
        return -1;
    }

    for (size_t i = 0; i < r->precedes_count; ++i) {
        const laplace_compose_precedes_t* pr = &r->precedes[i];
        int64_t sum_score = pr->games * LAPLACE_GLICKO2_FP_SCALE;
        if (laplace_attestation_aggregated_add(
                stage, &pr->subject_id, &precedes_type, &pr->object_id, 0,
                source_id, NULL, 1, witness_weight, pr->games, sum_score, now_unix_us) != 0) {
            free_emit_filter(&filter);
            return -1;
        }
    }

    free_emit_filter(&filter);
    return 0;
}

void laplace_compose_result_free(laplace_compose_result_t* r) {
    if (!r) return;
    free(r->entities);
    if (r->physicalities) {
        for (size_t i = 0; i < r->phys_count; ++i)
            free(r->physicalities[i].trajectory_xyzm);
    }
    free(r->physicalities);
    free(r->precedes);
    free(r->spans);
    tier_tree_free(r->tree);
    free(r);
}
