#include "grammar_compose_internal.h"
#include "laplace/core/entity_type_law.h"

#include <stdlib.h>
#include <string.h>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/trajectory.h"

template <typename T>
static int reserve_compose_array(T*& rows, size_t& capacity, size_t required) {
    if (required <= capacity) return 0;
    const size_t maximum = SIZE_MAX / sizeof(T);
    if (required > maximum) return -3;
    size_t grown = capacity ? capacity : 16;
    while (grown < required) {
        if (grown > maximum / 2) { grown = required; break; }
        grown *= 2;
    }
    T* allocated = (T*)realloc(rows, grown * sizeof(T));
    if (!allocated) return -3;
    rows = allocated;
    capacity = grown;
    return 0;
}

int laplace_compose_reserve_rows(laplace_compose_result_t* r,
                                size_t entities, size_t physicalities,
                                size_t source_trees) {
    if (!r) return -1;
    if (reserve_compose_array(r->entities, r->entity_capacity, entities) != 0 ||
        reserve_compose_array(r->physicalities, r->phys_capacity, physicalities) != 0 ||
        reserve_compose_array(r->source_trees, r->source_tree_capacity, source_trees) != 0)
        return -3;
    return 0;
}

int laplace_compose_push_entity(laplace_compose_result_t* r, hash128_t id, uint8_t tier,
                                hash128_t type_id, uint8_t packaging) {
    if (laplace_compose_reserve_rows(r, r->entity_count + 1, 0, 0) != 0) return -3;
    laplace_compose_entity_t* e = &r->entities[r->entity_count++];
    e->id = id;
    e->tier = tier;
    e->type_id = type_id;
    e->packaging = packaging;
    return 0;
}

static int compose_id_seen(const hash128_t* ids, size_t count, hash128_t id) {
    for (size_t j = 0; j < count; ++j)
        if (ids[j].hi == id.hi && ids[j].lo == id.lo) return 1;
    return 0;
}

int laplace_compose_id_push(hash128_t** ids, size_t* count, size_t* cap, hash128_t id) {
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

int laplace_compose_push_phys(laplace_compose_result_t* r, hash128_t entity_id,
                              double coord[4], hilbert128_t* hb,
                              const hash128_t* child_ids, const uint64_t* child_flags, size_t m) {
    double* traj = (double*)malloc(m * 4 * sizeof(double));
    if (!traj && m > 0) return -3;
    size_t trajectory_vertices = 0;
    if (m > 0 && (trajectory_build_flagged_rle(
            child_ids, child_flags, m, traj, &trajectory_vertices) != 0
        || trajectory_vertices > UINT32_MAX)) {
        free(traj);
        return -3;
    }
    hash128_t phys_id;
    /* Identity is (entity_id, physicality_type) ONLY -- the coord/trajectory built
     * above are payload, never part of the id (see laplace_physicality_id_compute). */
    laplace_physicality_id_compute(entity_id, 1, &phys_id);

    if (laplace_compose_reserve_rows(r, 0, r->phys_count + 1, 0) != 0) {
        free(traj);
        return -3;
    }
    laplace_compose_physicality_t* p = &r->physicalities[r->phys_count++];
    p->id = phys_id;
    p->entity_id = entity_id;
    memcpy(p->coord, coord, 4 * sizeof(double));
    p->hilbert = *hb;
    p->trajectory_xyzm = traj;
    p->trajectory_n = trajectory_vertices * 4;
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

/* Grammar parents must retain every structural body they reference. Text
 * leaves are emitted from their Native trees, never as flat grapheme aliases. */
static int emit_ast_node_physicalities(
    laplace_compose_result_t* r, laplace_ast_t* ast,
    const compose_state_t* st, int json_mod) {
    const size_t n = st->n;
    for (size_t idx = n; idx-- > 0;) {
        if (!st->comp_valid[idx]) continue;
        laplace_ast_node_t node;
        if (laplace_ast_get_node(ast, idx, &node) != 0) return -1;
        if (json_mod && (laplace_json_node_is_string_fragment(ast, &node) ||
                         laplace_json_node_is_string(ast, &node))) continue;
        size_t child_count = 0;
        for (size_t i = 0; i < n; ++i) {
            laplace_ast_node_t child;
            if (laplace_ast_get_node(ast, i, &child) != 0) return -1;
            if (child.parent == (uint32_t)idx && st->comp_valid[i]) ++child_count;
        }
        if (!child_count) continue;
        hash128_t* ids = (hash128_t*)malloc(child_count * sizeof(*ids));
        uint64_t* flags = (uint64_t*)malloc(child_count * sizeof(*flags));
        if (!ids || !flags) { free(ids); free(flags); return -3; }
        size_t m = 0;
        for (size_t i = 0; i < n; ++i) {
            laplace_ast_node_t child;
            if (laplace_ast_get_node(ast, i, &child) != 0) {
                free(ids); free(flags); return -1;
            }
            if (child.parent != (uint32_t)idx || !st->comp_valid[i]) continue;
            ids[m] = st->comp_id[i];
            flags[m++] = laplace_vertex_flags(st->comp_tier[i], 0, 0);
        }
        const hash128_t id = st->comp_id[idx];
        int rc = 0;
        if (m != 1 || !hash128_equals(&id, &ids[0])) {
            hilbert128_t hb;
            hilbert4d_encode(st->comp_coord + idx * 4, &hb);
            rc = laplace_compose_push_phys(r, id, st->comp_coord + idx * 4, &hb, ids, flags, m);
        }
        free(ids); free(flags);
        if (rc != 0) return rc;
    }
    return 0;
}

int laplace_grammar_compose_materialize_phys(laplace_compose_result_t* r,
                                             const uint8_t* utf8, size_t len,
                                             laplace_ast_t* ast, const char* modality_id) {
    if (!r || !utf8 || !ast || !modality_id) return -1;
    if (r->source_mode || r->physicalities_complete) return 0;

    const size_t initial_phys_count = r->phys_count;
    compose_state_t st{};
    tier_tree_t* tree = NULL;
    laplace_grapheme_floor_t floor;
    memset(&floor, 0, sizeof(floor));
    const int json_mod = laplace_is_json_modality(modality_id);
    int rc = 0;

    if (!json_mod) {
        rc = laplace_grapheme_floor_build(utf8, len, &tree, &floor);
        if (rc != 0) return rc;
        if (codepoint_table_is_loaded()) {
            rc = hash_composer_run(tree, laplace_compose_codepoint_resolver, NULL);
            if (rc != 0) goto done;
        }
    }

    rc = laplace_compose_ast_nodes(utf8, len, ast, modality_id, &floor, tree, NULL,
                                   NULL, NULL, NULL, &st);
    if (rc != 0) goto done_st;

    rc = emit_ast_node_physicalities(r, ast, &st, json_mod);

done_st:
    free(st.comp_id);
    free(st.comp_coord);
    free(st.comp_tier);
    free(st.entity_tier);
    free(st.comp_valid);
done:
    laplace_grapheme_floor_free(&floor);
    tier_tree_free(tree);
    if (rc == 0) {
        r->physicalities_complete = 1;
    } else {
        for (size_t i = initial_phys_count; i < r->phys_count; ++i)
            free(r->physicalities[i].trajectory_xyzm);
        r->phys_count = initial_phys_count;
    }
    return rc;
}

static int grammar_compose_impl(const uint8_t* utf8, size_t len, laplace_ast_t* ast,
                            const char* modality_id, hash128_t source_id,
                            hash128_t type_meta_id, laplace_compose_result_t** out,
                            int materialize_phys) {
    (void)type_meta_id;
    if (!utf8 || !ast || !modality_id || !out) return -1;
    *out = NULL;
    (void)source_id;
    if (len == 0 || laplace_ast_node_count(ast) == 0) return 0;

    laplace_compose_result_t* r = (laplace_compose_result_t*)calloc(1, sizeof(*r));
    if (!r) return -3;

    compose_state_t st{};
    size_t n = 0;
    size_t span_cap = 0;
    hash128_t* emitted_entity = NULL;
    size_t emitted_entity_n = 0, emitted_entity_cap = 0;
    int rc = 0;
    uint32_t** children_of  = NULL;
    uint32_t*  child_counts = NULL;

    tier_tree_t* tree = NULL;
    laplace_grapheme_floor_t floor;
    memset(&floor, 0, sizeof(floor));
    const int json_mod = laplace_is_json_modality(modality_id);
    if (!json_mod) {
        rc = laplace_grapheme_floor_build(utf8, len, &tree, &floor);
        if (rc != 0) { free(r); return rc; }

        if (codepoint_table_is_loaded()) {
            rc = hash_composer_run(tree, laplace_compose_codepoint_resolver, NULL);
            if (rc != 0) {
                laplace_grapheme_floor_free(&floor);
                tier_tree_free(tree);
                free(r);
                return rc;
            }
        }

        hash128_t grapheme_type;
        (void)laplace_entity_type_id("Grapheme", &grapheme_type);

        size_t g_first = laplace_grapheme_floor_graph_first_idx(&floor);
        size_t g_count = laplace_grapheme_floor_graph_count(&floor);
        for (size_t g = 0; g < g_count; ++g) {
            tier_node_view_t gv;
            if (tier_tree_get_node(tree, (uint32_t)(g_first + g), &gv) != 0) continue;
            if (gv.child_count == 1) continue;
            if (laplace_compose_push_entity(r, gv.id, 1, grapheme_type, 0) != 0) { rc = -3; goto fail; }
        }
    }

    rc = laplace_compose_ast_nodes(utf8, len, ast, modality_id, &floor, tree, r,
                                   &emitted_entity, &emitted_entity_n, &emitted_entity_cap, &st);
    if (rc != 0) goto fail_emit;

    n = st.n;

    {
        size_t idx_cap = 64;
        while (idx_cap < (n ? n : 1) * 2) idx_cap <<= 1;
        r->span_index = (uint32_t*)malloc(idx_cap * sizeof(uint32_t));
        if (!r->span_index) { rc = -3; goto fail_emit; }
        memset(r->span_index, 0xFF, idx_cap * sizeof(uint32_t));
        r->span_index_cap = idx_cap;
    }

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
        laplace_ast_node_t node;
        if (laplace_ast_get_node(ast, idx, &node) != 0) continue;
        uint32_t kid_n = child_counts[idx];
        const int json_string = json_mod && laplace_json_node_is_string(ast, &node);
        const int json_fragment = json_mod && laplace_json_node_is_string_fragment(ast, &node);
        const int novel_entity = json_fragment ? 0 : laplace_compose_id_push(
            &emitted_entity, &emitted_entity_n, &emitted_entity_cap, id);
        if (novel_entity < 0) { rc = -3; goto fail_emit; }

        int packaging = !hash128_equals(&id, &st.comp_id[0]);

        hash128_t tier_type = laplace_content_tier_type_id(st.entity_tier[idx]);
        if (novel_entity &&
            laplace_compose_push_entity(r, id, st.entity_tier[idx], tier_type, (uint8_t)packaging) != 0) {
            rc = -3; goto fail_emit;
        }

        hilbert128_t hb;
        hilbert4d_encode(st.comp_coord + idx * 4, &hb);
        hash128_t* child_ids = NULL;
        uint64_t*  child_flags = NULL;
        size_t m = 0;
        if (kid_n > 0 && !json_string) {
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
        }
        if (m > 0 && child_ids && materialize_phys && !json_fragment &&
            (m != 1 || !hash128_equals(&id, &child_ids[0]))) {
            if (laplace_compose_push_phys(r, id, st.comp_coord + idx * 4, &hb,
                                          child_ids, child_flags, m) != 0) {
                free(child_ids); free(child_flags);
                rc = -3; goto fail_emit;
            }
        }
        free(child_ids);
        free(child_flags);

        if (r->span_count == span_cap) {
            size_t ncap = span_cap ? span_cap * 2 : 64;
            laplace_compose_span_t* sp = (laplace_compose_span_t*)realloc(
                r->spans, ncap * sizeof(*sp));
            if (!sp) { rc = -3; goto fail_emit; }
            r->spans = sp;
            span_cap = ncap;
            r->span_capacity = ncap;
        }
        r->spans[r->span_count].start_byte = node.start_byte;
        r->spans[r->span_count].end_byte   = node.end_byte;
        r->spans[r->span_count].entity_id  = id;

        {
            uint64_t key = ((uint64_t)node.start_byte << 32) | (uint64_t)node.end_byte;
            uint64_t h = key * 0x9E3779B97F4A7C15ULL;
            size_t s = (size_t)(h >> 32) & (r->span_index_cap - 1);
            while (r->span_index[s] != UINT32_MAX) s = (s + 1) & (r->span_index_cap - 1);
            r->span_index[s] = (uint32_t)r->span_count;
        }
        r->span_count++;
    }

    if (st.comp_valid[0])
        r->root_id = st.comp_id[0];

    for (size_t i = 0; i < n; ++i) free(children_of[i]);
    free(children_of);  children_of = NULL;
    free(child_counts); child_counts = NULL;

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
                r->precedes_capacity = ncap;
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
    free(st.comp_id);
    free(st.comp_coord);
    free(st.comp_tier);
    free(st.entity_tier);
    free(st.comp_valid);
    laplace_grapheme_floor_free(&floor);
    tier_tree_free(tree);
    r->physicalities_complete = materialize_phys ? 1 : 0;
    *out = r;
    return 0;

fail_emit:
    if (children_of) { for (size_t i = 0; i < n; ++i) free(children_of[i]); free(children_of); }
    free(child_counts);
    free(emitted_entity);
    free(st.comp_id);
    free(st.comp_coord);
    free(st.comp_tier);
    free(st.entity_tier);
    free(st.comp_valid);
fail:
    laplace_grapheme_floor_free(&floor);
    tier_tree_free(tree);
    laplace_compose_result_free(r);
    return rc;
}
