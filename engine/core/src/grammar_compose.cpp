#include "laplace/core/grammar_compose.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/grapheme_floor.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/mantissa.h"
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

static void physicality_id_compute(hash128_t entity_id, hash128_t source_id,
                                   double coord[4], const double* traj, size_t traj_n,
                                   hash128_t* out) {
    size_t traj_bytes = traj_n * sizeof(double);
    size_t total = 16 + 16 + 2 + 32 + traj_bytes;
    uint8_t* buf = (uint8_t*)malloc(total);
    if (!buf) { hash128_zero(out); return; }
    size_t o = 0;
    memcpy(buf + o, &entity_id, 16); o += 16;
    memcpy(buf + o, &source_id, 16); o += 16;
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
static int push_phys(laplace_compose_result_t* r, hash128_t entity_id, hash128_t source_id,
                     double coord[4], hilbert128_t* hb,
                     const hash128_t* child_ids, const uint64_t* child_flags, size_t m);

static int is_json_modality(const char* modality_id) {
    return modality_id && strcmp(modality_id, "json") == 0;
}

static int emit_grapheme_floor_entities(
    laplace_compose_result_t* r, hash128_t source_id,
    tier_tree_t* tree, const laplace_grapheme_floor_t* floor,
    hash128_t** emitted_entity, size_t* emitted_entity_n, size_t* emitted_entity_cap) {
    hash128_t grapheme_type;
    hash_canonical("substrate/type/Grapheme/v1", &grapheme_type);
    size_t g_first = laplace_grapheme_floor_graph_first_idx(floor);
    size_t g_count = laplace_grapheme_floor_graph_count(floor);
    for (size_t g = 0; g < g_count; ++g) {
        tier_node_view_t gv;
        if (tier_tree_get_node(tree, (uint32_t)(g_first + g), &gv) != 0) continue;
        if (compose_id_push(emitted_entity, emitted_entity_n, emitted_entity_cap, gv.id) != 1)
            continue;
        if (push_entity(r, gv.id, 1, grapheme_type) != 0) return -3;
        uint64_t gf = laplace_vertex_flags(1, 0, 0);
        hash128_t gid = gv.id;
        if (push_phys(r, gv.id, source_id, gv.coord, &gv.hilbert, &gid, &gf, 1) != 0)
            return -3;
    }
    return 0;
}





static int json_leaf_fill_grapheme_children(
    const uint8_t* utf8, size_t len, laplace_ast_t* ast, size_t idx,
    laplace_compose_result_t* r, hash128_t source_id,
    hash128_t** emitted_entity, size_t* emitted_entity_n, size_t* emitted_entity_cap,
    hash128_t** out_ids, double** out_coords, uint64_t** out_flags, size_t* out_m) {
    *out_ids = NULL;
    *out_coords = NULL;
    *out_flags = NULL;
    *out_m = 0;

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

    tier_tree_t* local_tree = NULL;
    laplace_grapheme_floor_t local_floor;
    memset(&local_floor, 0, sizeof(local_floor));
    if (laplace_grapheme_floor_build(span, span_len, &local_tree, &local_floor) != 0)
        return -1;
    if (codepoint_table_is_loaded()) {
        if (hash_composer_run(local_tree, codepoint_resolver, NULL) != 0) {
            laplace_grapheme_floor_free(&local_floor);
            tier_tree_free(local_tree);
            return -1;
        }
    }

    if (r && emitted_entity) {
        int erc = emit_grapheme_floor_entities(
            r, source_id, local_tree, &local_floor,
            emitted_entity, emitted_entity_n, emitted_entity_cap);
        if (erc != 0) {
            laplace_grapheme_floor_free(&local_floor);
            tier_tree_free(local_tree);
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
        return -1;
    }
    *out_ids = ids;
    *out_coords = coords;
    *out_flags = flags;
    *out_m = w;
    return 0;
}

static int compose_ast_nodes(const uint8_t* utf8, size_t len, laplace_ast_t* ast,
                             const char* modality_id,
                             const laplace_grapheme_floor_t* floor, tier_tree_t* tree,
                             laplace_compose_result_t* r, hash128_t source_id,
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
    if (!children_of || !child_counts) return -3;

    for (size_t i = 0; i < n; ++i) {
        laplace_ast_node_t nd;
        if (laplace_ast_get_node(ast, i, &nd) != 0) continue;
        if (nd.parent != LAPLACE_AST_ROOT && nd.parent < n) {
            uint32_t p = nd.parent;
            uint32_t cc = child_counts[p]++;
            children_of[p] = (uint32_t*)realloc(children_of[p], (size_t)(cc + 1) * sizeof(uint32_t));
            if (!children_of[p]) return -3;
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
            tier = (uint8_t)((max_tier + 1) < 255 ? (max_tier + 1) : 255);
        } else if (is_json_modality(modality_id)) {
            if (json_leaf_fill_grapheme_children(
                    utf8, len, ast, idx, r, source_id,
                    emitted_entity, emitted_entity_n, emitted_entity_cap,
                    &child_ids, &child_coords, &child_flags, &m) != 0)
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

static int push_phys(laplace_compose_result_t* r, hash128_t entity_id, hash128_t source_id,
                     double coord[4], hilbert128_t* hb,
                     const hash128_t* child_ids, const uint64_t* child_flags, size_t m) {
    double* traj = (double*)malloc(m * 4 * sizeof(double));
    if (!traj && m > 0) return -3;
    if (m > 0 && trajectory_build_flagged(child_ids, child_flags, m, traj) != 0) {
        free(traj);
        return -3;
    }
    hash128_t phys_id;
    physicality_id_compute(entity_id, source_id, coord, traj, m * 4, &phys_id);

    laplace_compose_physicality_t* n = (laplace_compose_physicality_t*)realloc(
        r->physicalities, (r->phys_count + 1) * sizeof(*n));
    if (!n) { free(traj); return -3; }
    r->physicalities = n;
    laplace_compose_physicality_t* p = &r->physicalities[r->phys_count++];
    p->id = phys_id;
    p->entity_id = entity_id;
    p->source_id = source_id;
    memcpy(p->coord, coord, 4 * sizeof(double));
    p->hilbert = *hb;
    p->trajectory_xyzm = traj;
    p->trajectory_n = m * 4;
    p->n_constituents = m;
    return 0;
}

int laplace_grammar_compose(const uint8_t* utf8, size_t len, laplace_ast_t* ast,
                            const char* modality_id, hash128_t source_id,
                            hash128_t type_meta_id, laplace_compose_result_t** out) {
    if (!utf8 || !ast || !modality_id || !out) return -1;
    *out = NULL;
    if (len == 0 || laplace_ast_node_count(ast) == 0) return 0;

    laplace_compose_result_t* r = (laplace_compose_result_t*)calloc(1, sizeof(*r));
    if (!r) return -3;

    compose_state_t st = {0};
    size_t n = 0;
    hash128_t* emitted_entity = NULL;
    hash128_t* emitted_type   = NULL;
    size_t emitted_entity_n = 0, emitted_entity_cap = 0;
    size_t emitted_type_n   = 0, emitted_type_cap   = 0;
    int rc = 0;

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
        hash_canonical("substrate/type/Grapheme/v1", &grapheme_type);

        size_t g_first = laplace_grapheme_floor_graph_first_idx(&floor);
        size_t g_count = laplace_grapheme_floor_graph_count(&floor);
        for (size_t g = 0; g < g_count; ++g) {
            tier_node_view_t gv;
            if (tier_tree_get_node(tree, (uint32_t)(g_first + g), &gv) != 0) continue;
            if (push_entity(r, gv.id, 1, grapheme_type) != 0) { rc = -3; goto fail; }
            {
                uint64_t gf = laplace_vertex_flags(1, 0, 0);
                hash128_t gid = gv.id;
                if (push_phys(r, gv.id, source_id, gv.coord, &gv.hilbert,
                              &gid, &gf, 1) != 0) { rc = -3; goto fail; }
            }
        }
    }

    if (!json_mod)
        g_first = laplace_grapheme_floor_graph_first_idx(&floor);

    rc = compose_ast_nodes(utf8, len, ast, modality_id, &floor, tree, r, source_id,
                           &emitted_entity, &emitted_entity_n, &emitted_entity_cap, &st);
    if (rc != 0) goto fail_st;

    n = st.n;
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
                    if (nd.parent != (uint32_t)idx || !st.comp_valid[i]) continue;
                    child_ids[w] = st.comp_id[i];
                    child_flags[w] = laplace_vertex_flags(st.comp_tier[i], 0, 0);
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
                    utf8, len, ast, idx, NULL, source_id,
                    NULL, NULL, NULL,
                    &child_ids, &jcoords, &child_flags, &m) == 0) {
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
        if (m > 0 && child_ids) {
            if (push_phys(r, id, source_id, st.comp_coord + idx * 4, &hb,
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

    for (size_t p = 0; p < n; ++p) {
        uint32_t prev = UINT32_MAX;
        int count = 0;
        for (size_t i = 0; i < n; ++i) {
            laplace_ast_node_t nd;
            if (laplace_ast_get_node(ast, i, &nd) != 0) continue;
            if (nd.parent != (uint32_t)p) continue;
            if (!st.comp_valid[i]) continue;
            if (prev != UINT32_MAX && st.comp_valid[prev]) {
                hash128_t a = st.comp_id[prev], b = st.comp_id[i];
                size_t found = (size_t)-1;
                for (size_t k = 0; k < r->precedes_count; ++k) {
                    if (r->precedes[k].subject_id.hi == a.hi && r->precedes[k].subject_id.lo == a.lo
                        && r->precedes[k].object_id.hi == b.hi && r->precedes[k].object_id.lo == b.lo) {
                        found = k; break;
                    }
                }
                if (found != (size_t)-1) {
                    r->precedes[found].games++;
                } else {
                    laplace_compose_precedes_t* pr = (laplace_compose_precedes_t*)realloc(
                        r->precedes, (r->precedes_count + 1) * sizeof(*pr));
                    if (!pr) { rc = -3; goto fail_emit; }
                    r->precedes = pr;
                    r->precedes[r->precedes_count].subject_id = a;
                    r->precedes[r->precedes_count].object_id = b;
                    r->precedes[r->precedes_count].games = 1;
                    r->precedes_count++;
                }
            }
            prev = (uint32_t)i;
            (void)count;
        }
    }

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
                                  size_t ast_node_index, hash128_t* out_id, uint8_t* out_tier) {
    if (!utf8 || !ast || !out_id) return -1;
    if (ast_node_index >= laplace_ast_node_count(ast)) return -1;

    tier_tree_t* tree = NULL;
    laplace_grapheme_floor_t floor;
    int rc = laplace_grapheme_floor_build(utf8, len, &tree, &floor);
    if (rc != 0) return rc;
    if (codepoint_table_is_loaded())
        hash_composer_run(tree, codepoint_resolver, NULL);

    compose_state_t st = {0};
    hash128_t no_source;
    hash128_zero(&no_source);
    rc = compose_ast_nodes(utf8, len, ast, "text", &floor, tree, NULL, no_source,
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
    free(r);
}
