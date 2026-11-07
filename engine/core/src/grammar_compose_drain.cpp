#include <unordered_map>
#include <unordered_set>
#include <string>
#include "grammar_compose_internal.h"

#include <stdlib.h>
#include <string.h>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/merkle_dedup.h"

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
        hash_composer_run(tree, laplace_compose_codepoint_resolver, NULL);

    compose_state_t st{};
    rc = laplace_compose_ast_nodes(utf8, len, ast, modality_id, &floor, tree, NULL,
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
    free(st.entity_tier);
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
    if (r->span_index && r->span_index_cap > 0) {
        uint64_t key = ((uint64_t)start_byte << 32) | (uint64_t)end_byte;
        uint64_t h = key * 0x9E3779B97F4A7C15ULL;
        size_t s = (size_t)(h >> 32) & (r->span_index_cap - 1);
        for (;;) {
            uint32_t idx = r->span_index[s];
            if (idx == UINT32_MAX) return -1;
            if (r->spans[idx].start_byte == start_byte && r->spans[idx].end_byte == end_byte) {
                *out_id = r->spans[idx].entity_id;
                return 0;
            }
            s = (s + 1) & (r->span_index_cap - 1);
        }
    }
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
size_t laplace_compose_resident_bytes(const laplace_compose_result_t* r) {
    if (!r) return 0;
    size_t bytes = sizeof(*r)
        + r->entity_capacity * sizeof(*r->entities)
        + r->phys_capacity * sizeof(*r->physicalities)
        + r->precedes_capacity * sizeof(*r->precedes)
        + r->span_capacity * sizeof(*r->spans)
        + r->span_index_cap * sizeof(*r->span_index)
        + r->source_tree_capacity * sizeof(*r->source_trees)
        + tier_tree_resident_bytes(r->tree);
    for (size_t i = 0; i < r->phys_count; ++i)
        bytes += (size_t)r->physicalities[i].n_constituents * 4 * sizeof(double);
    for (size_t i = 0; i < r->source_tree_count; ++i)
        bytes += tier_tree_resident_bytes(r->source_trees[i]);
    return bytes;
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

int laplace_compose_root_placement(const laplace_compose_result_t* r,
                                   double out_coord[4], uint8_t* out_tier,
                                   uint32_t* out_atom, uint8_t* out_has_atom) {
    if (!r || !out_coord || !out_tier || !out_atom || !out_has_atom)
        return -1;
    if (r->source_root_valid) {
        memcpy(out_coord, r->source_root_coord, 4 * sizeof(double));
        *out_tier = r->source_root_tier;
        *out_atom = r->source_root_atom;
        *out_has_atom = r->source_root_has_atom;
        return 0;
    }
    for (size_t i = 0; i < r->source_tree_count; ++i) {
        tier_tree_t* tree = r->source_trees[i];
        size_t count = tier_tree_node_count(tree);
        if (count == 0 || count > UINT32_MAX) continue;
        tier_node_view_t node;
        uint32_t idx = laplace_tier_tree_collapse_index(tree, (uint32_t)(count - 1));
        if (tier_tree_get_node(tree, idx, &node) != 0 ||
            !hash128_equals(&node.id, &r->root_id)) continue;
        memcpy(out_coord, node.coord, 4 * sizeof(double));
        *out_tier = node.tier;
        *out_atom = node.atom;
        *out_has_atom = node.tier == 0 ? 1 : 0;
        return 0;
    }
    for (size_t i = 0; i < r->phys_count; ++i) {
        if (!hash128_equals(&r->physicalities[i].entity_id, &r->root_id)) continue;
        memcpy(out_coord, r->physicalities[i].coord, 4 * sizeof(double));
        *out_atom = 0;
        *out_has_atom = 0;
        for (size_t e = 0; e < r->entity_count; ++e) {
            if (hash128_equals(&r->entities[e].id, &r->root_id)) {
                *out_tier = r->entities[e].tier;
                return 0;
            }
        }
    }
    return -2;
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
} compose_emit_filter_t;

static compose_emit_filter_t make_emit_filter(
    const laplace_compose_result_t* r,
    const uint8_t*                  existing_bitmap,
    size_t                          bitmap_bits) {
    compose_emit_filter_t f = {1, NULL, 0};
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

    free(novel_idx);
    return f;
}

static void free_emit_filter(compose_emit_filter_t* f) {
    if (!f) return;
    free(f->novel_entity);
    f->novel_entity = NULL;
}

static int entity_novel(const compose_emit_filter_t* f, size_t idx) {
    return f->emit_all || (f->novel_entity && idx < f->entity_n && f->novel_entity[idx]);
}

struct owner_novelty {
    std::unordered_map<std::string, bool> novel;
    owner_novelty(const laplace_compose_result_t* r, const compose_emit_filter_t* f) {
        novel.reserve(r->entity_count);
        for (size_t i = 0; i < r->entity_count; ++i) {
            auto placed = novel.emplace(
                std::string(reinterpret_cast<const char*>(&r->entities[i].id), sizeof(hash128_t)),
                entity_novel(f, i) != 0);
            if (!placed.second && entity_novel(f, i)) placed.first->second = true;
        }
    }
    bool operator()(const hash128_t* owner) const {
        auto it = novel.find(std::string(reinterpret_cast<const char*>(owner), sizeof(hash128_t)));
        return it == novel.end() || it->second;
    }
};

int laplace_compose_drain_into_stage(
    const laplace_compose_result_t* r,
    intent_stage_t*                 stage,
    const hash128_t*                source_id,
    int64_t                         now_unix_us,
    double                          witness_weight,
    const uint8_t*                  existing_bitmap,
    size_t                          bitmap_bits) {
    (void)witness_weight;
    if (!r || !stage || !source_id) return -1;

    compose_emit_filter_t filter = make_emit_filter(r, existing_bitmap, bitmap_bits);
    const owner_novelty owner_novel(r, &filter);
    std::unordered_set<std::string> body_owners;
    body_owners.reserve(r->phys_count);
    for (size_t i = 0; i < r->phys_count; ++i)
        body_owners.emplace(reinterpret_cast<const char*>(&r->physicalities[i].entity_id),
                            sizeof(hash128_t));
    {
        for (size_t i = 0; i < r->source_tree_count; ++i) {
            hash128_t ignored; tier_node_view_t root;
            if (content_witness_tree_root_node(r->source_trees[i], &root) != 0) {
                free_emit_filter(&filter); return -1;
            }
            if (!filter.emit_all && !owner_novel(&root.id)) continue;
            if (content_witness_emit_tree_at(stage, r->source_trees[i], source_id,
                                          NULL, 0, &ignored, now_unix_us) != 0) {
                free_emit_filter(&filter);
                return -1;
            }
        }
    }

    for (size_t i = 0; i < r->entity_count; ++i) {
        const laplace_compose_entity_t* e = &r->entities[i];
        if (e->packaging && !body_owners.contains(
                std::string(reinterpret_cast<const char*>(&e->id), sizeof e->id))) continue;
        const int novel = entity_novel(&filter, i);
        const int seen = intent_stage_witness_seen(stage, &e->id);
        if (!novel) continue;
        if (seen) {
            if (intent_stage_lower_entity_tier(stage, &e->id, (int16_t)e->tier) < 0) {
                free_emit_filter(&filter);
                return -1;
            }
            continue;
        }
        if (intent_stage_add_entity(stage, &e->id, (int16_t)e->tier, &e->type_id) != 0) {
            free_emit_filter(&filter);
            return -1;
        }
        if (intent_stage_witness_record(stage, &e->id) != 0 ||
            intent_stage_allocation_failed(stage)) {
            free_emit_filter(&filter);
            return -1;
        }
    }

    for (size_t i = 0; i < r->phys_count; ++i) {
        const laplace_compose_physicality_t* ph = &r->physicalities[i];
        if (!filter.emit_all && !owner_novel(&ph->entity_id)) continue;
        if (intent_stage_witness_seen(stage, &ph->id)) continue;
        if (intent_stage_add_physicality(
                stage, &ph->id, &ph->entity_id, 1, ph->coord, &ph->hilbert,
                ph->trajectory_xyzm, (uint32_t)(ph->trajectory_n / 4), (int32_t)ph->n_constituents,
                1, 0.0, 1, 0, now_unix_us) != 0) {
            free_emit_filter(&filter);
            return -1;
        }
        (void)intent_stage_witness_record(stage, &ph->id);
        if (intent_stage_allocation_failed(stage)) {
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
    free(r->span_index);
    if (r->source_trees) {
        for (size_t i = 0; i < r->source_tree_count; ++i)
            tier_tree_free(r->source_trees[i]);
    }
    free(r->source_trees);
    tier_tree_free(r->tree);
    free(r);
}
