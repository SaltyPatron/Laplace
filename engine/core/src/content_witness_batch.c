#include "laplace/core/content_witness_batch.h"

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

static int codepoint_resolver(uint32_t atom, void* /*user*/,
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

static uint32_t natural_unit_index(const tier_tree_t* tree) {
    size_t nc = tier_tree_node_count(tree);
    if (nc == 0) return 0;
    uint32_t idx = (uint32_t)(nc - 1);
    for (;;) {
        tier_node_view_t node;
        if (tier_tree_get_node(tree, idx, &node) != 0) break;
        if (node.tier <= 2 || node.child_count != 1) break;
        idx = node.first_child_idx;
    }
    return idx;
}

static int is_unary_ancestor_of(const tier_tree_t* tree, uint32_t ancestor, uint32_t descendant) {
    size_t nc = tier_tree_node_count(tree);
    uint32_t cur = (uint32_t)(nc - 1);
    while (cur != descendant) {
        if (cur == ancestor) return 1;
        tier_node_view_t n;
        if (tier_tree_get_node(tree, cur, &n) != 0) return 0;
        if (n.child_count != 1) return 0;
        cur = n.first_child_idx;
    }
    return 0;
}

static int should_emit_compositional(const tier_tree_t* tree, uint32_t idx) {
    uint32_t natural = natural_unit_index(tree);
    if (idx == natural) return 1;
    tier_node_view_t node, nat;
    if (tier_tree_get_node(tree, idx, &node) != 0) return 0;
    if (tier_tree_get_node(tree, natural, &nat) != 0) return 0;
    if (node.text_range_off != nat.text_range_off || node.text_range_len != nat.text_range_len)
        return 1;
    return !is_unary_ancestor_of(tree, idx, natural);
}

int content_witness_batch_add(
    intent_stage_t*  stage,
    const uint8_t*   utf8,
    size_t           len,
    const hash128_t* source_id,
    hash128_t*       out_root_id) {
    if (!stage || !utf8 || !source_id || !out_root_id) return -1;
    if (len == 0) return -2;

    /* fail loud, not AV: the codepoint floor must be loaded (perfcache) BEFORE
     * the text decomposer runs -- segmentation itself reads the perfcache tables,
     * so a post-decomposer guard is unreachable in the unloaded case */
    if (!codepoint_table_is_loaded()) return -3;

    tier_tree_t* tree = NULL;
    if (laplace_text_decomposer_run(utf8, len, &tree) != 0 || !tree) return -2;
    if (hash_composer_run(tree, codepoint_resolver, NULL) != 0) {
        tier_tree_free(tree);
        return -2;
    }

    size_t nc = tier_tree_node_count(tree);
    if (nc == 0) {
        tier_tree_free(tree);
        hash128_zero(out_root_id);
        return -2;
    }

    uint32_t root_idx = natural_unit_index(tree);
    tier_node_view_t root;
    tier_tree_get_node(tree, root_idx, &root);
    *out_root_id = root.id;

    int64_t now_us = INTENT_STAGE_PG_EPOCH_UNIX_US;

    for (uint32_t idx = 0; idx < (uint32_t)nc; ++idx) {
        tier_node_view_t node;
        if (tier_tree_get_node(tree, idx, &node) != 0) continue;
        if (node.tier == 0) continue;
        if (!should_emit_compositional(tree, idx)) continue;

        hash128_t type_id = tier_type_id(node.tier);
        if (intent_stage_add_entity(stage, &node.id, (int16_t)node.tier, &type_id, source_id) != 0) {
            tier_tree_free(tree);
            return -2;
        }

        double* traj = NULL;
        size_t m = node.child_count;
        if (m > 0) {
            hash128_t* child_ids = (hash128_t*)malloc(m * sizeof(hash128_t));
            uint64_t*  flags     = (uint64_t*)malloc(m * sizeof(uint64_t));
            if (!child_ids || !flags) {
                free(child_ids); free(flags); free(traj);
                tier_tree_free(tree);
                return -2;
            }
            for (uint32_t ci = 0; ci < m; ++ci) {
                tier_node_view_t ch;
                tier_tree_get_node(tree, node.first_child_idx + ci, &ch);
                child_ids[ci] = ch.id;
                flags[ci] = laplace_vertex_flags(
                    ch.tier, ch.tier == 0 ? 1 : 0, ch.atom);
            }
            traj = (double*)malloc(m * 4 * sizeof(double));
            if (!traj || trajectory_build_flagged(child_ids, flags, m, traj) != 0) {
                free(child_ids); free(flags); free(traj);
                tier_tree_free(tree);
                return -2;
            }
            free(child_ids);
            free(flags);
        }

        hash128_t phys_id;
        physicality_id_compute(node.id, *source_id, node.coord, traj, m * 4, &phys_id);
        if (intent_stage_add_physicality(
                stage, &phys_id, &node.id, source_id, 1,
                node.coord, &node.hilbert, traj, (uint32_t)(m * 4),
                (int32_t)m, 1, 0.0, 1, 0, now_us) != 0) {
            free(traj);
            tier_tree_free(tree);
            return -2;
        }
        free(traj);
    }

    tier_tree_free(tree);
    return 0;
}
