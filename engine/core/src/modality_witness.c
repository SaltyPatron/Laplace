#include "laplace/core/modality_witness.h"

#include <stdlib.h>
#include <string.h>

#include "laplace/core/audio_decomposer.h"
#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/image_decomposer.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/math4d.h"
#include "laplace/core/merkle_dedup.h"
#include "laplace/core/modality_decimal.h"
#include "laplace/core/modality_number_table.h"
#include "laplace/core/trajectory.h"

/* Parse ASCII digit bytes to 0..255 for modality_number_perfcache O(1). */
static int parse_u8_digits(const uint8_t* buf, size_t n, uint32_t* out) {
    if (!buf || !out || n == 0 || n > 3u) return -1;
    uint32_t v = 0;
    for (size_t i = 0; i < n; ++i) {
        if (buf[i] < (uint8_t)'0' || buf[i] > (uint8_t)'9') return -1;
        v = v * 10u + (uint32_t)(buf[i] - (uint8_t)'0');
    }
    if (v > 255u) return -1;
    *out = v;
    return 0;
}

/*
 * Number/Sample id: prefer modality_number ROM (0..255) when loaded; else
 * ScalarId content_root of the digit UTF-8. Geometry always from child centroid
 * when falling back; ROM path uses precomputed geom when available.
 */
static int resolve_number_id(
    const uint8_t* buf, size_t n,
    const double* child_coords, size_t child_n,
    hash128_t* out_id, double out_coord[4], hilbert128_t* out_hb) {
    uint32_t v = 0;
    if (parse_u8_digits(buf, n, &v) == 0
        && modality_number_table_is_loaded()) {
        if (modality_number_table_lookup_geom(
                v, out_id, out_coord, out_hb, NULL, NULL) == 0) {
            return 0;
        }
    }
    if (laplace_content_root_id(buf, n, out_id) != 0) return -6;
    math4d_centroid(child_coords, child_n, out_coord);
    hilbert4d_encode(out_coord, out_hb);
    return 0;
}

#ifdef _WIN32
#define LAPLACE_TIER_TLS __declspec(thread)
#else
#define LAPLACE_TIER_TLS __thread
#endif

hash128_t laplace_modality_tier_type_id(laplace_modality_t modality, uint8_t tier) {
    static LAPLACE_TIER_TLS hash128_t image_cache[7];
    static LAPLACE_TIER_TLS hash128_t audio_cache[6];
    static LAPLACE_TIER_TLS int image_ready = 0;
    static LAPLACE_TIER_TLS int audio_ready = 0;

    if (modality == LAPLACE_MODALITY_IMAGE) {
        if (!image_ready) {
            hash128_blake3_str("Codepoint", &image_cache[0]);
            hash128_blake3_str("Number", &image_cache[1]);
            hash128_blake3_str("Channel", &image_cache[2]);
            hash128_blake3_str("Pixel", &image_cache[3]);
            hash128_blake3_str("Patch", &image_cache[4]);
            hash128_blake3_str("Region", &image_cache[5]);
            hash128_blake3_str("Image", &image_cache[6]);
            image_ready = 1;
        }
        return image_cache[tier <= 5 ? tier : 6];
    }
    if (modality == LAPLACE_MODALITY_AUDIO) {
        if (!audio_ready) {
            hash128_blake3_str("Codepoint", &audio_cache[0]);
            hash128_blake3_str("Sample", &audio_cache[1]);
            hash128_blake3_str("Window", &audio_cache[2]);
            hash128_blake3_str("OnsetSegment", &audio_cache[3]);
            hash128_blake3_str("Phrase", &audio_cache[4]);
            hash128_blake3_str("Track", &audio_cache[5]);
            audio_ready = 1;
        }
        return audio_cache[tier <= 4 ? tier : 5];
    }
    hash128_t z; hash128_zero(&z); return z;
}

int laplace_modality_hash_composer_resolver(
    uint32_t atom, void* user_data,
    hash128_t* out_id, double out_coord[4], hilbert128_t* out_hilbert) {
    (void)user_data;
    /* Image + audio T0 = Unicode codepoints (shared floor). No private PCM/RGBA atoms. */
    return codepoint_table_resolve_atom(atom, out_id, out_coord, out_hilbert);
}

/*
 * Image compose: T0 via codepoint_table; Number id = text content root of the
 * digit string (ScalarId / laplace_content_root_id); Channel+ via merkle/centroid.
 */
static int compose_image_tree(tier_tree_t* tree) {
    if (!tree) return -1;
    if (!codepoint_table_is_loaded()) return -3;

    const size_t count = tier_tree_node_count(tree);
    if (count == 0) return 0;

    const uint8_t*  tiers  = tier_tree_tier_array(tree);
    const uint32_t* fci    = tier_tree_first_child_idx_array(tree);
    const uint32_t* cc     = tier_tree_child_count_array(tree);
    const uint32_t* atoms  = tier_tree_atom_array(tree);
    hash128_t*      ids    = tier_tree_id_array_mut(tree);
    double*         coords = tier_tree_coord_array_mut(tree);
    hilbert128_t*   hbs    = tier_tree_hilbert_array_mut(tree);
    if (!tiers || !fci || !cc || !atoms || !ids || !coords || !hbs) return -1;

    for (size_t i = 0; i < count; ++i) {
        const uint32_t first = fci[i];
        const uint32_t cnt   = cc[i];
        const int is_leaf = (first == TIER_TREE_INVALID) || (cnt == 0);

        if (is_leaf) {
            double leaf_coord[4] = {0.0, 0.0, 0.0, 0.0};
            hash128_t leaf_id;
            hash128_zero(&leaf_id);
            hilbert128_t leaf_hb;
            for (int b = 0; b < 16; ++b) leaf_hb.bytes[b] = 0;
            const int rc = codepoint_table_resolve_atom(
                atoms[i], &leaf_id, leaf_coord, &leaf_hb);
            if (rc != 0) return rc;
            ids[i] = leaf_id;
            coords[i * 4 + 0] = leaf_coord[0];
            coords[i * 4 + 1] = leaf_coord[1];
            coords[i * 4 + 2] = leaf_coord[2];
            coords[i * 4 + 3] = leaf_coord[3];
            hbs[i] = leaf_hb;
            continue;
        }

        if ((size_t)first >= count || (size_t)first + (size_t)cnt > count)
            return -1;

        if (tiers[i] == LAPLACE_IMAGE_TIER_NUMBER) {
            /* Digit codepoints are ASCII '0'..'9' — UTF-8 is one byte each. */
            if (cnt == 0 || cnt > 3u) return -1;
            uint8_t buf[3];
            for (uint32_t k = 0; k < cnt; ++k) {
                uint32_t cp = atoms[first + k];
                if (cp < (uint32_t)'0' || cp > (uint32_t)'9') return -1;
                buf[k] = (uint8_t)cp;
            }
            if (resolve_number_id(buf, (size_t)cnt,
                                  &coords[(size_t)first * 4], (size_t)cnt,
                                  &ids[i], &coords[i * 4], &hbs[i]) != 0)
                return -6;
            continue;
        }

        hash_composer_compose_node(tiers[i], &ids[first],
                                   &coords[(size_t)first * 4], (size_t)cnt,
                                   &ids[i], &coords[i * 4], &hbs[i]);
    }
    return 0;
}

/*
 * Audio compose: T0 via codepoint_table; Sample (Number) id = text content root
 * of the decimal digit string (same ScalarId law as image Number; signed may
 * prefix U+002D); Window+ via merkle/centroid.
 */
static int compose_audio_tree(tier_tree_t* tree) {
    if (!tree) return -1;
    if (!codepoint_table_is_loaded()) return -3;

    const size_t count = tier_tree_node_count(tree);
    if (count == 0) return 0;

    const uint8_t*  tiers  = tier_tree_tier_array(tree);
    const uint32_t* fci    = tier_tree_first_child_idx_array(tree);
    const uint32_t* cc     = tier_tree_child_count_array(tree);
    const uint32_t* atoms  = tier_tree_atom_array(tree);
    hash128_t*      ids    = tier_tree_id_array_mut(tree);
    double*         coords = tier_tree_coord_array_mut(tree);
    hilbert128_t*   hbs    = tier_tree_hilbert_array_mut(tree);
    if (!tiers || !fci || !cc || !atoms || !ids || !coords || !hbs) return -1;

    for (size_t i = 0; i < count; ++i) {
        const uint32_t first = fci[i];
        const uint32_t cnt   = cc[i];
        const int is_leaf = (first == TIER_TREE_INVALID) || (cnt == 0);

        if (is_leaf) {
            double leaf_coord[4] = {0.0, 0.0, 0.0, 0.0};
            hash128_t leaf_id;
            hash128_zero(&leaf_id);
            hilbert128_t leaf_hb;
            for (int b = 0; b < 16; ++b) leaf_hb.bytes[b] = 0;
            const int rc = codepoint_table_resolve_atom(
                atoms[i], &leaf_id, leaf_coord, &leaf_hb);
            if (rc != 0) return rc;
            ids[i] = leaf_id;
            coords[i * 4 + 0] = leaf_coord[0];
            coords[i * 4 + 1] = leaf_coord[1];
            coords[i * 4 + 2] = leaf_coord[2];
            coords[i * 4 + 3] = leaf_coord[3];
            hbs[i] = leaf_hb;
            continue;
        }

        if ((size_t)first >= count || (size_t)first + (size_t)cnt > count)
            return -1;

        if (tiers[i] == 1u) { /* Sample = Number */
            if (cnt == 0 || cnt > LAPLACE_DECIMAL_MAX_CPS) return -1;
            uint8_t buf[LAPLACE_DECIMAL_MAX_CPS];
            int signed_neg = 0;
            for (uint32_t k = 0; k < cnt; ++k) {
                uint32_t cp = atoms[first + k];
                if (k == 0 && cp == 0x2Du) {
                    buf[k] = (uint8_t)'-';
                    signed_neg = 1;
                    continue;
                }
                if (cp < (uint32_t)'0' || cp > (uint32_t)'9') return -1;
                buf[k] = (uint8_t)cp;
            }
            /* u8 ROM covers unsigned 0..255 only; signed/out-of-range → content_root. */
            if (!signed_neg
                && resolve_number_id(buf, (size_t)cnt,
                                     &coords[(size_t)first * 4], (size_t)cnt,
                                     &ids[i], &coords[i * 4], &hbs[i]) == 0) {
                continue;
            }
            if (laplace_content_root_id(buf, (size_t)cnt, &ids[i]) != 0) return -6;
            math4d_centroid(&coords[(size_t)first * 4], (size_t)cnt, &coords[i * 4]);
            hilbert4d_encode(&coords[i * 4], &hbs[i]);
            continue;
        }

        hash_composer_compose_node(tiers[i], &ids[first],
                                   &coords[(size_t)first * 4], (size_t)cnt,
                                   &ids[i], &coords[i * 4], &hbs[i]);
    }
    return 0;
}

int laplace_image_tree_build(
    const uint8_t* rgba, uint32_t width, uint32_t height, tier_tree_t** out_tree) {
    if (!out_tree) return -1;
    tier_tree_t* tree = NULL;
    int rc = laplace_image_decomposer_run(rgba, width, height, &tree);
    if (rc != 0 || !tree) return rc != 0 ? rc : -5;
    /* Propagate the compose failure code (offset to stay out of this
     * function's own -1..-5 range) instead of flattening every cause to one
     * value — managed callers surface this rc in their diagnostics. */
    rc = compose_image_tree(tree);
    if (rc != 0) {
        tier_tree_free(tree);
        return rc < 0 ? -100 + rc : -6;
    }
    *out_tree = tree;
    return 0;
}

int laplace_audio_tree_build(
    const int16_t* pcm, size_t n_samples, tier_tree_t** out_tree) {
    if (!out_tree) return -1;
    tier_tree_t* tree = NULL;
    int rc = laplace_audio_decomposer_run(pcm, n_samples, &tree);
    if (rc != 0 || !tree) return rc != 0 ? rc : -5;
    /* Same propagation as the image twin: compose cause survives, offset out
     * of this function's own -1..-5 range. */
    rc = compose_audio_tree(tree);
    if (rc != 0) {
        tier_tree_free(tree);
        return rc < 0 ? -100 + rc : -6;
    }
    *out_tree = tree;
    return 0;
}

static uint32_t collapse_idx(const tier_tree_t* tree, uint32_t idx) {
    for (;;) {
        tier_node_view_t node;
        if (tier_tree_get_node(tree, idx, &node) != 0) break;
        if (node.tier == 0 || node.child_count != 1) break;
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

static int should_emit(const tier_tree_t* tree, uint32_t idx, laplace_modality_t modality) {
    (void)modality;
    if (collapse_idx(tree, idx) != idx) return 0;
    tier_node_view_t node;
    if (tier_tree_get_node(tree, idx, &node) != 0) return 0;
    /* Shared codepoint T0 floor — already in T0 perfcache; do not re-deposit. */
    if (node.tier == 0) return 0;
    return 1;
}

static int emit_node(
    intent_stage_t*       stage,
    const tier_tree_t*    tree,
    uint32_t              idx,
    laplace_modality_t    modality,
    const hash128_t*      source_id,
    int64_t               now_us) {
    tier_node_view_t node;
    if (tier_tree_get_node(tree, idx, &node) != 0) return 0;
    if (!should_emit(tree, idx, modality)) return 0;
    if (intent_stage_witness_record(stage, &node.id)) return 0;

    hash128_t type_id = laplace_modality_tier_type_id(modality, node.tier);
    if (intent_stage_add_entity(stage, &node.id, (int16_t)node.tier, &type_id, source_id) != 0)
        return -2;

    double* traj = NULL;
    size_t m = node.child_count;
    size_t n_traj = 0;
    if (m > 1) {
        hash128_t* child_ids = (hash128_t*)malloc(m * sizeof(hash128_t));
        uint64_t*  flags     = (uint64_t*)malloc(m * sizeof(uint64_t));
        if (!child_ids || !flags) {
            free(child_ids); free(flags);
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
        if (!traj || trajectory_build_flagged_rle(
                child_ids, flags, m, traj, &n_traj) != 0 || n_traj > UINT32_MAX) {
            free(child_ids); free(flags); free(traj);
            return -2;
        }
        free(child_ids);
        free(flags);
    }

    hash128_t phys_id;
    laplace_physicality_id_compute(node.id, 1, &phys_id);
    if (intent_stage_add_physicality(
            stage, &phys_id, &node.id, 1,
            node.coord, &node.hilbert, traj, (uint32_t)n_traj,
            (int32_t)(m > 1 ? m : 0), 1, 0.0, 1, 0, now_us) != 0) {
        free(traj);
        return -2;
    }
    free(traj);
    return 0;
}

int laplace_modality_witness_emit_tree(
    intent_stage_t*       stage,
    const tier_tree_t*    tree,
    laplace_modality_t    modality,
    const hash128_t*      source_id,
    const uint8_t*        existing_bitmap,
    size_t                bitmap_bits,
    hash128_t*            out_root_id) {
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
            int rc = emit_node(stage, tree, novel[k], modality, source_id, now_us);
            if (rc != 0) { free(novel); return rc; }
        }
        free(novel);
        return 0;
    }

    for (uint32_t idx = 0; idx < (uint32_t)nc; ++idx) {
        int rc = emit_node(stage, tree, idx, modality, source_id, now_us);
        if (rc != 0) return rc;
    }
    return 0;
}

int laplace_image_root_id(
    const uint8_t* rgba, uint32_t width, uint32_t height, hash128_t* out_root_id) {
    if (!out_root_id) return -1;
    tier_tree_t* tree = NULL;
    int rc = laplace_image_tree_build(rgba, width, height, &tree);
    if (rc != 0) return rc;
    uint32_t root_idx = natural_unit_index(tree);
    tier_node_view_t root;
    tier_tree_get_node(tree, root_idx, &root);
    *out_root_id = root.id;
    tier_tree_free(tree);
    return 0;
}

int laplace_audio_root_id(
    const int16_t* pcm, size_t n_samples, hash128_t* out_root_id) {
    if (!out_root_id) return -1;
    tier_tree_t* tree = NULL;
    int rc = laplace_audio_tree_build(pcm, n_samples, &tree);
    if (rc != 0) return rc;
    uint32_t root_idx = natural_unit_index(tree);
    tier_node_view_t root;
    tier_tree_get_node(tree, root_idx, &root);
    *out_root_id = root.id;
    tier_tree_free(tree);
    return 0;
}
