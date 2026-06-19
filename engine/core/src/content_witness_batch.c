// Global content-witness dedup bank (reset per ingest run via content_witness_reset).
#include "laplace/core/content_witness_batch.h"

#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/text_decomposer.h"
#include "laplace/core/tier_tree.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/trajectory.h"

static void hash_canonical(const char* s, hash128_t* out) {
    hash128_blake3((const uint8_t*)s, strlen(s), out);
}

static hash128_t tier_type_id(uint8_t tier) {
    hash128_t id;
    switch (tier) {
        case 0: hash_canonical("substrate/type/Codepoint/v1", &id); break;
        case 1: hash_canonical("substrate/type/Grapheme/v1", &id); break;
        case 2: hash_canonical("substrate/type/Word/v1", &id); break;
        case 3: hash_canonical("substrate/type/Sentence/v1", &id); break;
        default: hash_canonical("substrate/type/Document/v1", &id); break;
    }
    return id;
}

static int codepoint_resolver(uint32_t atom, void* ,
                              hash128_t* out_id, double out_coord[4],
                              hilbert128_t* out_hb) {
    return codepoint_table_resolve_atom(atom, out_id, out_coord, out_hb);
}

static void physicality_id_compute(hash128_t entity_id, hash128_t source_id,
                                   const double coord[4], const double* traj, size_t traj_n,
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







static uint32_t collapse_idx(const tier_tree_t* tree, uint32_t idx) {
    for (;;) {
        tier_node_view_t node;
        if (tier_tree_get_node(tree, idx, &node) != 0) break;
        if (node.tier <= 1 || node.child_count != 1) break;
        tier_node_view_t child;
        if (tier_tree_get_node(tree, node.first_child_idx, &child) != 0) break;
        if (child.text_range_off != node.text_range_off
            || child.text_range_len != node.text_range_len) break;
        idx = node.first_child_idx;
    }
    return idx;
}


static uint32_t natural_unit_index(const tier_tree_t* tree) {
    size_t nc = tier_tree_node_count(tree);
    if (nc == 0) return 0;
    return collapse_idx(tree, (uint32_t)(nc - 1));
}

static int should_emit_compositional(const tier_tree_t* tree, uint32_t idx) {
    
    if (collapse_idx(tree, idx) != idx) return 0;
    tier_node_view_t node;
    if (tier_tree_get_node(tree, idx, &node) != 0) return 0;
    if (node.tier == 0) return 0;
    return 1;
}





static int content_tree_build(const uint8_t* utf8, size_t len, tier_tree_t** out_tree) {
    if (len == 0) return -2;
    if (!codepoint_table_is_loaded()) return -3;

    tier_tree_t* tree = NULL;
    if (laplace_text_decomposer_run(utf8, len, &tree) != 0 || !tree) return -2;
    if (hash_composer_run(tree, codepoint_resolver, NULL) != 0) {
        tier_tree_free(tree);
        return -2;
    }
    if (tier_tree_node_count(tree) == 0) {
        tier_tree_free(tree);
        return -2;
    }
    *out_tree = tree;
    return 0;
}

int laplace_content_root_id(
    const uint8_t* utf8,
    size_t         len,
    hash128_t*     out_root_id) {
    if (!utf8 || !out_root_id) return -1;

    tier_tree_t* tree = NULL;
    int rc = content_tree_build(utf8, len, &tree);
    if (rc != 0) return rc;

    tier_node_view_t root;
    tier_tree_get_node(tree, natural_unit_index(tree), &root);
    *out_root_id = root.id;
    tier_tree_free(tree);
    return 0;
}




typedef struct { uint32_t off; uint32_t idx; } seg_order_t;

static int seg_order_cmp(const void* a, const void* b) {
    uint32_t oa = ((const seg_order_t*)a)->off;
    uint32_t ob = ((const seg_order_t*)b)->off;
    return (oa > ob) - (oa < ob);
}

int laplace_content_word_segment(
    const uint8_t*       utf8,
    size_t               len,
    laplace_word_emit_fn emit,
    void*                ctx) {
    if (!utf8 || !emit) return -1;

    tier_tree_t* tree = NULL;
    int rc = content_tree_build(utf8, len, &tree);
    if (rc != 0) return rc;

    size_t nc = tier_tree_node_count(tree);
    seg_order_t* words = (seg_order_t*)malloc(nc * sizeof(seg_order_t));
    if (!words) { tier_tree_free(tree); return -2; }

    
    size_t nw = 0;
    for (uint32_t idx = 0; idx < (uint32_t)nc; ++idx) {
        tier_node_view_t node;
        if (tier_tree_get_node(tree, idx, &node) != 0) continue;
        if (node.tier != 2) continue;
        words[nw].off = node.text_range_off;
        words[nw].idx = idx;
        nw++;
    }
    qsort(words, nw, sizeof(seg_order_t), seg_order_cmp);

    uint32_t ord = 0;
    for (size_t w = 0; w < nw; ++w) {
        tier_node_view_t node;
        if (tier_tree_get_node(tree, words[w].idx, &node) != 0) continue;
        if (node.text_range_len == 0) continue;
        const uint8_t* span = utf8 + node.text_range_off;
        

        if (laplace_text_is_all_whitespace(span, node.text_range_len)) continue;

        

        tier_node_view_t stand_in;
        tier_tree_get_node(tree, collapse_idx(tree, words[w].idx), &stand_in);
        emit(ctx, ord++, span, node.text_range_len, &stand_in.id);
    }

    free(words);
    tier_tree_free(tree);
    return 0;
}















typedef struct {
    hash128_t key;
    hash128_t root;
} canon_entry_t;

static canon_entry_t* g_canon_slots  = NULL;
static size_t         g_canon_cap    = 0;
static size_t         g_canon_count  = 0;

static hash128_t* g_entity_slots = NULL;
static size_t     g_entity_cap   = 0;
static size_t     g_entity_count = 0;

static atomic_flag g_bank_lock = ATOMIC_FLAG_INIT;

static void bank_lock(void) {
    while (atomic_flag_test_and_set_explicit(&g_bank_lock, memory_order_acquire)) { }
}

static void bank_unlock(void) {
    atomic_flag_clear_explicit(&g_bank_lock, memory_order_release);
}

static int slot_empty(const hash128_t* h) { return (h->hi | h->lo) == 0; }

static int canon_lookup(const hash128_t* key, hash128_t* out_root) {
    if (!g_canon_slots || g_canon_cap == 0) return 0;
    bank_lock();
    size_t mask = g_canon_cap - 1;
    size_t j = (size_t)key->lo & mask;
    int found = 0;
    while (!slot_empty(&g_canon_slots[j].key)) {
        if (hash128_equals(&g_canon_slots[j].key, key)) {
            *out_root = g_canon_slots[j].root;
            found = 1;
            break;
        }
        j = (j + 1) & mask;
    }
    bank_unlock();
    return found;
}

static int canon_insert(const hash128_t* key, const hash128_t* root) {
    bank_lock();
    if (g_canon_cap == 0) {
        g_canon_cap = (size_t)1 << 18;
        g_canon_slots = (canon_entry_t*)calloc(g_canon_cap, sizeof(canon_entry_t));
        if (!g_canon_slots) { bank_unlock(); return -2; }
    } else if ((g_canon_count + 1) * 4 >= g_canon_cap * 3) {
        size_t ncap = g_canon_cap << 1;
        canon_entry_t* ns = (canon_entry_t*)calloc(ncap, sizeof(canon_entry_t));
        if (!ns) { bank_unlock(); return -2; }
        size_t nmask = ncap - 1;
        for (size_t i = 0; i < g_canon_cap; ++i) {
            if (slot_empty(&g_canon_slots[i].key)) continue;
            size_t j = (size_t)g_canon_slots[i].key.lo & nmask;
            while (!slot_empty(&ns[j].key)) j = (j + 1) & nmask;
            ns[j] = g_canon_slots[i];
        }
        free(g_canon_slots);
        g_canon_slots = ns;
        g_canon_cap = ncap;
    }
    size_t mask = g_canon_cap - 1;
    size_t j = (size_t)key->lo & mask;
    int rc = 0;
    while (!slot_empty(&g_canon_slots[j].key)) {
        if (hash128_equals(&g_canon_slots[j].key, key)) { rc = 0; goto done; }
        j = (j + 1) & mask;
    }
    g_canon_slots[j].key = *key;
    g_canon_slots[j].root = *root;
    g_canon_count++;
done:
    bank_unlock();
    return rc;
}

static int entity_record(const hash128_t* id) {
    if (!id) return 0;
    bank_lock();
    if (g_entity_cap == 0) {
        g_entity_cap = (size_t)1 << 20;
        g_entity_slots = (hash128_t*)calloc(g_entity_cap, sizeof(hash128_t));
        if (!g_entity_slots) { bank_unlock(); return 0; }
    } else if ((g_entity_count + 1) * 4 >= g_entity_cap * 3) {
        size_t ncap = g_entity_cap << 1;
        hash128_t* ns = (hash128_t*)calloc(ncap, sizeof(hash128_t));
        if (!ns) { bank_unlock(); return 0; }
        size_t nmask = ncap - 1;
        for (size_t i = 0; i < g_entity_cap; ++i) {
            if (slot_empty(&g_entity_slots[i])) continue;
            size_t j = (size_t)g_entity_slots[i].lo & nmask;
            while (!slot_empty(&ns[j])) j = (j + 1) & nmask;
            ns[j] = g_entity_slots[i];
        }
        free(g_entity_slots);
        g_entity_slots = ns;
        g_entity_cap = ncap;
    }
    size_t mask = g_entity_cap - 1;
    size_t j = (size_t)id->lo & mask;
    int found = 0;
    while (!slot_empty(&g_entity_slots[j])) {
        if (hash128_equals(&g_entity_slots[j], id)) { found = 1; break; }
        j = (j + 1) & mask;
    }
    if (!found) {
        g_entity_slots[j] = *id;
        g_entity_count++;
    }
    bank_unlock();
    return found;
}

static int entity_contains(const hash128_t* id) {
    if (!id || !g_entity_slots || g_entity_cap == 0) return 0;
    bank_lock();
    size_t mask = g_entity_cap - 1;
    size_t j = (size_t)id->lo & mask;
    int found = 0;
    while (!slot_empty(&g_entity_slots[j])) {
        if (hash128_equals(&g_entity_slots[j], id)) { found = 1; break; }
        j = (j + 1) & mask;
    }
    bank_unlock();
    return found;
}

void content_witness_reset(void) {
    bank_lock();
    free(g_canon_slots);
    g_canon_slots = NULL;
    g_canon_cap = 0;
    g_canon_count = 0;
    free(g_entity_slots);
    g_entity_slots = NULL;
    g_entity_cap = 0;
    g_entity_count = 0;
    bank_unlock();
}

int content_witness_entity_proven(const hash128_t* id) {
    return entity_contains(id);
}

static int emit_node(
    intent_stage_t*  stage,
    tier_tree_t*     tree,
    uint32_t         idx,
    const hash128_t* source_id,
    int64_t          now_us) {
    tier_node_view_t node;
    if (tier_tree_get_node(tree, idx, &node) != 0) return 0;
    if (node.tier == 0) return 0;
    if (!should_emit_compositional(tree, idx)) return 0;

    if (intent_stage_witness_record(stage, &node.id)) return 0;
    entity_record(&node.id);

    hash128_t type_id = tier_type_id(node.tier);
    if (intent_stage_add_entity(stage, &node.id, (int16_t)node.tier, &type_id, source_id) != 0)
        return -2;

    double* traj = NULL;
    size_t m = node.child_count;
    if (m > 0) {
        hash128_t* child_ids = (hash128_t*)malloc(m * sizeof(hash128_t));
        uint64_t*  flags     = (uint64_t*)malloc(m * sizeof(uint64_t));
        if (!child_ids || !flags) {
            free(child_ids); free(flags); free(traj);
            return -2;
        }
        for (uint32_t ci = 0; ci < m; ++ci) {
            tier_node_view_t ch;
            tier_tree_get_node(tree, collapse_idx(tree, node.first_child_idx + ci), &ch);
            child_ids[ci] = ch.id;
            flags[ci] = laplace_vertex_flags(
                ch.tier, ch.tier == 0 ? 1 : 0, ch.atom);
        }
        traj = (double*)malloc(m * 4 * sizeof(double));
        if (!traj || trajectory_build_flagged(child_ids, flags, m, traj) != 0) {
            free(child_ids); free(flags); free(traj);
            return -2;
        }
        free(child_ids);
        free(flags);
    }

    hash128_t phys_id;
    physicality_id_compute(node.id, *source_id, node.coord, traj, m * 4, &phys_id);
    if (intent_stage_add_physicality(
            stage, &phys_id, &node.id, source_id, 1,
            node.coord, &node.hilbert, traj, (uint32_t)m,
            (int32_t)m, 1, 0.0, 1, 0, now_us) != 0) {
        free(traj);
        return -2;
    }
    free(traj);
    return 0;
}

int content_witness_batch_add(
    intent_stage_t*  stage,
    const uint8_t*   utf8,
    size_t           len,
    const hash128_t* source_id,
    hash128_t*       out_root_id) {
    if (!stage || !utf8 || !source_id || !out_root_id) return -1;

    hash128_t canon_key;
    hash128_blake3(utf8, len, &canon_key);
    if (canon_lookup(&canon_key, out_root_id)) {
        entity_record(out_root_id);
        return 0;
    }

    tier_tree_t* tree = NULL;
    {
        int rc = content_tree_build(utf8, len, &tree);
        if (rc != 0) {
            hash128_zero(out_root_id);
            return rc;
        }
    }

    size_t nc = tier_tree_node_count(tree);

    uint32_t root_idx = natural_unit_index(tree);
    tier_node_view_t root;
    tier_tree_get_node(tree, root_idx, &root);
    *out_root_id = root.id;

    if (intent_stage_witness_seen(stage, &root.id)) {
        tier_tree_free(tree);
        canon_insert(&canon_key, out_root_id);
        entity_record(out_root_id);
        return 0;
    }

    int64_t now_us = INTENT_STAGE_PG_EPOCH_UNIX_US;

    for (uint32_t idx = 0; idx < (uint32_t)nc; ++idx) {
        int rc = emit_node(stage, tree, idx, source_id, now_us);
        if (rc != 0) {
            tier_tree_free(tree);
            return rc;
        }
    }

    canon_insert(&canon_key, out_root_id);
    entity_record(out_root_id);
    tier_tree_free(tree);
    return 0;
}

