// Global content-witness dedup bank (reset per ingest run via content_witness_reset).
#include "laplace/core/content_witness_batch.h"

#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/merkle_dedup.h"
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

static void physicality_id_compute(hash128_t entity_id,
                                   const double coord[4], const double* traj, size_t traj_n,
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















// The global cross-run dedup bank (g_canon_slots / g_entity_slots) was DELETED. It deduped once
// across the entire source run -- accumulating every canonical root + entity id in RAM (unbounded
// for big multilingual sources -> boil-the-ocean) behind one process-global spinlock (so concurrent
// file workers serialized on it and bought no parallelism). Dedup is now PER-BATCH: within a batch
// the per-stage witness set (intent_stage_witness_record/_seen) collapses repeats; ACROSS batches
// the DB deduplicates by content address (INSERT ... ON CONFLICT (id) DO NOTHING in the writer).
// That keeps memory bounded by the batch and lets file chunks compose concurrently with no shared
// mutable state. content_witness_reset is now a no-op; the "proven across everything" export is
// gone (it had no callers after the referential pre-check was removed).
void content_witness_reset(void) { }

int content_witness_entity_proven(const hash128_t* id) { (void)id; return 0; }

static int emit_node(
    intent_stage_t*    stage,
    const tier_tree_t* tree,
    uint32_t           idx,
    const hash128_t*   source_id,
    int64_t            now_us) {
    tier_node_view_t node;
    if (tier_tree_get_node(tree, idx, &node) != 0) return 0;
    if (node.tier == 0) return 0;
    if (!should_emit_compositional(tree, idx)) return 0;

    if (intent_stage_witness_record(stage, &node.id)) return 0;

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
    physicality_id_compute(node.id, node.coord, traj, m * 4, &phys_id);
    if (intent_stage_add_physicality(
            stage, &phys_id, &node.id, 1,
            node.coord, &node.hilbert, traj, (uint32_t)m,
            (int32_t)m, 1, 0.0, 1, 0, now_us) != 0) {
        free(traj);
        return -2;
    }
    free(traj);
    return 0;
}

// Build the content tier tree for a UTF-8 span and hand back the retained handle. The caller owns
// it and must release it with tier_tree_free. This is the first half of the two-phase containment
// path: build once, probe the node ids against the DB existing-bitmap, then emit only novel nodes
// via content_witness_emit_tree — avoiding a second decomposition (the grammar compose path does the
// same with its retained compose result).
int content_witness_tree_build(
    const uint8_t* utf8,
    size_t         len,
    tier_tree_t**  out_tree) {
    if (!utf8 || !out_tree) return -1;
    return content_tree_build(utf8, len, out_tree);
}

// Emit a pre-built content tier tree into the stage. When existing_bitmap is non-NULL, only the
// novel subtrees (MerkleDedup.TrunkShortcircuit over the tree node order) are emitted — a present
// trunk skips its whole subtree, exactly like TextEntityBuilder. existing_bitmap == NULL emits all
// nodes (tier-0 is skipped inside emit_node). out_root_id always receives the natural-unit root id
// so the caller can wire attestations even when the subtree is skipped.
int content_witness_emit_tree(
    intent_stage_t*    stage,
    const tier_tree_t* tree,
    const hash128_t*   source_id,
    const uint8_t*     existing_bitmap,
    size_t             bitmap_bits,
    hash128_t*         out_root_id) {
    if (!stage || !tree || !source_id || !out_root_id) return -1;

    size_t nc = tier_tree_node_count(tree);

    uint32_t root_idx = natural_unit_index(tree);
    tier_node_view_t root;
    tier_tree_get_node(tree, root_idx, &root);
    *out_root_id = root.id;

    if (intent_stage_witness_seen(stage, &root.id)) return 0;

    int64_t now_us = INTENT_STAGE_PG_EPOCH_UNIX_US;

    if (existing_bitmap && bitmap_bits > 0) {
        uint32_t* novel = (uint32_t*)malloc(nc * sizeof(uint32_t));
        if (!novel) return -2;
        size_t novel_n = 0;
        if (merkle_dedup_trunk_shortcircuit(
                tree, existing_bitmap, bitmap_bits, novel, &novel_n) != 0) {
            free(novel);
            return -2;
        }
        for (size_t k = 0; k < novel_n; ++k) {
            int rc = emit_node(stage, tree, novel[k], source_id, now_us);
            if (rc != 0) { free(novel); return rc; }
        }
        free(novel);
        return 0;
    }

    for (uint32_t idx = 0; idx < (uint32_t)nc; ++idx) {
        int rc = emit_node(stage, tree, idx, source_id, now_us);
        if (rc != 0) return rc;
    }
    return 0;
}

int content_witness_batch_add(
    intent_stage_t*  stage,
    const uint8_t*   utf8,
    size_t           len,
    const hash128_t* source_id,
    hash128_t*       out_root_id) {
    if (!stage || !utf8 || !source_id || !out_root_id) return -1;

    tier_tree_t* tree = NULL;
    {
        int rc = content_tree_build(utf8, len, &tree);
        if (rc != 0) {
            hash128_zero(out_root_id);
            return rc;
        }
    }

    int rc = content_witness_emit_tree(stage, tree, source_id, NULL, 0, out_root_id);
    tier_tree_free(tree);
    return rc;
}

