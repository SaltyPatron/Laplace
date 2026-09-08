#include "laplace/core/grammar_compose.h"

#include <limits.h>
#include <stdlib.h>
#include <string.h>

#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/trajectory.h"

/* A source node is an AST span realized from its immediate AST children plus
 * the byte spans the grammar intentionally leaves between those children. */
typedef struct {
    hash128_t id;
    double coord[4];
    uint8_t tier, has_atom;
    uint32_t atom;
    hash128_t* kids;
    uint64_t* flags;
    size_t kid_count;
} source_node_t;

typedef struct {
    hash128_t hash;
    const uint8_t* bytes;
    size_t length;
    source_node_t node;
} raw_entry_t;

typedef struct {
    raw_entry_t* entries;
    size_t count, capacity;
} raw_cache_t;

static int raw_cache_grow(raw_cache_t* cache) {
    size_t capacity = cache->capacity ? cache->capacity * 2 : 16;
    if (capacity < cache->capacity || capacity > SIZE_MAX / sizeof(raw_entry_t)) return -1;
    raw_entry_t* entries = (raw_entry_t*)calloc(capacity, sizeof(*entries));
    if (!entries) return -1;
    for (size_t i = 0; i < cache->capacity; ++i) {
        if (!cache->entries[i].bytes) continue;
        size_t slot = (size_t)(cache->entries[i].hash.lo & (capacity - 1));
        while (entries[slot].bytes) slot = (slot + 1) & (capacity - 1);
        entries[slot] = cache->entries[i];
    }
    free(cache->entries);
    cache->entries = entries;
    cache->capacity = capacity;
    return 0;
}

static int append_tree(laplace_compose_result_t* r, tier_tree_t* tree) {
    if (laplace_compose_reserve_rows(r, 0, 0, r->source_tree_count + 1) != 0) return -1;
    r->source_trees[r->source_tree_count++] = tree;
    return 0;
}

/* The content witness tree remains the sole owner of lexical decomposition.
 * Its root is also the component that a grammar gap or lexical AST leaf uses. */
static int raw_component(laplace_compose_result_t* r, raw_cache_t* cache,
                         const uint8_t* p, size_t n,
                         source_node_t* out) {
    if (!p || n == 0) return -1;
    if (cache->count >= cache->capacity / 2 && raw_cache_grow(cache) != 0) return -1;
    hash128_t hash;
    hash128_blake3(p, n, &hash);
    size_t slot = (size_t)(hash.lo & (cache->capacity - 1));
    while (cache->entries[slot].bytes) {
        const raw_entry_t* entry = &cache->entries[slot];
        if (entry->length == n && hash128_equals(&entry->hash, &hash) &&
            memcmp(entry->bytes, p, n) == 0) {
            *out = entry->node;
            return 0;
        }
        slot = (slot + 1) & (cache->capacity - 1);
    }

    // Reuse only byte-identical decomposition within this source. Each occurrence
    // still receives its own span and ordinal in the enclosing composition.
    tier_tree_t* tree = NULL;
    if (content_witness_source_tree_build(p, n, &tree) != 0 || !tree)
        return -1;
    const size_t tree_n = tier_tree_node_count(tree);
    if (tree_n == 0 || tree_n > UINT32_MAX) { tier_tree_free(tree); return -1; }
    tier_node_view_t root;
    uint32_t root_index = laplace_tier_tree_collapse_index(tree, (uint32_t)(tree_n - 1));
    if (tier_tree_get_node(tree, root_index, &root) != 0 || append_tree(r, tree) != 0) {
        tier_tree_free(tree);
        return -1;
    }
    out->id = root.id;
    out->tier = root.tier;
    out->atom = root.atom;
    out->has_atom = root.tier == 0;
    memcpy(out->coord, root.coord, sizeof(out->coord));
    cache->entries[slot].hash = hash;
    cache->entries[slot].bytes = p;
    cache->entries[slot].length = n;
    cache->entries[slot].node = *out;
    ++cache->count;
    return 0;
}

/* Canonical identity deliberately has no tier input. When the same identity
 * reaches this result through distinct AST representations, retain its lowest
 * observed floor instead of letting reverse traversal choose a higher one. */
static int push_entity(laplace_compose_result_t* r, const source_node_t* n) {
    if (laplace_compose_reserve_rows(r, r->entity_count + 1, 0, 0) != 0) return -1;
    laplace_compose_entity_t* e = &r->entities[r->entity_count++];
    e->id = n->id;
    e->tier = n->tier;
    e->packaging = 0;
    /* An AST composition is generic source text structure. Its floor is
     * placement, never an implicit Sentence/Document category. `Text` is the
     * registered generic type (EntityTypeRegistry.Text = blake3("Text")). */
    hash128_blake3_str("Text", &e->type_id);
    return 0;
}

static int push_phys(laplace_compose_result_t* r, const source_node_t* n) {
    if (n->kid_count < 2) return 0;
    if (n->kid_count > SIZE_MAX / (4 * sizeof(double)) || n->kid_count > UINT32_MAX)
        return -1;
    double* trajectory = (double*)malloc(n->kid_count * 4 * sizeof(double));
    size_t trajectory_vertices = 0;
    if (!trajectory || trajectory_build_flagged_rle(
            n->kids, n->flags, n->kid_count, trajectory, &trajectory_vertices) != 0
        || trajectory_vertices > UINT32_MAX) {
        free(trajectory);
        return -1;
    }
    if (laplace_compose_reserve_rows(r, 0, r->phys_count + 1, 0) != 0) {
        free(trajectory);
        return -1;
    }
    laplace_compose_physicality_t* p = &r->physicalities[r->phys_count++];
    laplace_physicality_id_compute(n->id, 1, &p->id);
    p->entity_id = n->id;
    memcpy(p->coord, n->coord, sizeof(p->coord));
    hilbert4d_encode(n->coord, &p->hilbert);
    p->trajectory_xyzm = trajectory;
    p->trajectory_n = trajectory_vertices * 4;
    p->n_constituents = n->kid_count;
    return 0;
}

/* Exact identity deduplication for both emitted row sets. Every composition
 * used to scan all preceding entities and physicalities separately. The index
 * keeps insertion order and the same minimum-tier election without O(n²) scans. */
static int push_composition(laplace_compose_result_t* r, const source_node_t* n,
                            uint32_t* index, size_t capacity) {
    size_t slot = (size_t)(n->id.lo & (capacity - 1));
    while (index[slot] != UINT32_MAX) {
        laplace_compose_entity_t* prior = &r->entities[index[slot]];
        if (hash128_equals(&prior->id, &n->id)) {
            if (n->tier < prior->tier) prior->tier = n->tier;
            return 0;
        }
        slot = (slot + 1) & (capacity - 1);
    }
    if (r->entity_count >= UINT32_MAX) return -1;
    uint32_t entity = (uint32_t)r->entity_count;
    if (push_entity(r, n) != 0 || push_phys(r, n) != 0) return -1;
    index[slot] = entity;
    return 0;
}

static int child_compare(const void* a, const void* b, void* opaque) {
    const laplace_ast_node_t* ast_nodes = (const laplace_ast_node_t*)opaque;
    uint32_t ia = *(const uint32_t*)a, ib = *(const uint32_t*)b;
    if (ast_nodes[ia].start_byte != ast_nodes[ib].start_byte)
        return ast_nodes[ia].start_byte < ast_nodes[ib].start_byte ? -1 : 1;
    if (ast_nodes[ia].end_byte != ast_nodes[ib].end_byte)
        return ast_nodes[ia].end_byte < ast_nodes[ib].end_byte ? -1 : 1;
    return ia < ib ? -1 : ia != ib;
}

static void sift_children(uint32_t* child, size_t root, size_t n,
                           const laplace_ast_node_t* ast_nodes) {
    while (root < n / 2) {
        size_t larger = root * 2 + 1;
        if (larger + 1 < n && child_compare(&child[larger], &child[larger + 1],
                                            (void*)ast_nodes) < 0) ++larger;
        if (child_compare(&child[root], &child[larger], (void*)ast_nodes) >= 0) break;
        uint32_t swap = child[root]; child[root] = child[larger]; child[larger] = swap;
        root = larger;
    }
}

/* Keep the linear source-order fast path. Unordered children use bounded
 * O(n log n), allocation-free sorting, including parsers with wide AST roots. */
static void sort_children(uint32_t* child, size_t n, const laplace_ast_node_t* ast_nodes) {
    size_t ordered = 1;
    while (ordered < n && child_compare(&child[ordered - 1], &child[ordered],
                                         (void*)ast_nodes) <= 0) ++ordered;
    if (ordered >= n) return;
    for (size_t root = n / 2; root-- > 0;)
        sift_children(child, root, n, ast_nodes);
    for (size_t end = n; end > 1;) {
        --end;
        uint32_t swap = child[0]; child[0] = child[end]; child[end] = swap;
        sift_children(child, 0, end, ast_nodes);
    }
}

static int append_component(source_node_t* component, hash128_t* ids, double* coords,
                            uint64_t* flags, size_t cap, size_t* used) {
    if (*used >= cap) return -1;
    ids[*used] = component->id;
    memcpy(coords + *used * 4, component->coord, 4 * sizeof(double));
    flags[*used] = laplace_vertex_flags(component->tier,
                                        component->has_atom, component->atom);
    ++*used;
    return 0;
}

static int compose_components(source_node_t* target, hash128_t* ids, double* coords,
                              uint64_t* flags, size_t used) {
    if (used == 0) return -1;
    if (used == 1) {
        target->id = ids[0];
        memcpy(target->coord, coords, 4 * sizeof(double));
        target->tier = laplace_vflag_tier(flags[0]);
        target->has_atom = (uint8_t)laplace_vflag_has_atom(flags[0]);
        target->atom = target->has_atom ? laplace_vflag_atom(flags[0]) : 0;
        return 0;
    }
    uint8_t max_tier = 0;
    for (size_t i = 0; i < used; ++i) {
        uint8_t tier = laplace_vflag_tier(flags[i]);
        if (tier > max_tier) max_tier = tier;
    }
    if (max_tier == UINT8_MAX) return -1;
    target->tier = (uint8_t)(max_tier + 1);
    target->has_atom = 0;
    target->atom = 0;
    hilbert128_t ignored_hilbert;
    hash_composer_compose_node(target->tier, ids, coords, used,
                               &target->id, target->coord, &ignored_hilbert);
    return 0;
}

static int add_span(laplace_compose_result_t* r, size_t span_capacity,
                    uint32_t start, uint32_t end, const source_node_t* n) {
    if (r->span_count >= span_capacity || !r->span_index || r->span_index_cap == 0)
        return -1;
    size_t at = r->span_count;
    r->spans[at].start_byte = start;
    r->spans[at].end_byte = end;
    r->spans[at].entity_id = n->id;
    uint64_t key = ((uint64_t)start << 32) | end;
    uint64_t h = key * 0x9E3779B97F4A7C15ULL;
    size_t slot = (size_t)(h >> 32) & (r->span_index_cap - 1);
    while (r->span_index[slot] != UINT32_MAX)
        slot = (slot + 1) & (r->span_index_cap - 1);
    r->span_index[slot] = (uint32_t)at;
    r->span_count++;
    return 0;
}

int laplace_grammar_source_compose(const uint8_t* utf8, size_t len,
                                   laplace_ast_t* ast, const char* modality_id,
                                   laplace_compose_result_t** out) {
    (void)modality_id;
    if (!utf8 || !ast || !out || len == 0 || len > UINT32_MAX) return -1;
    *out = NULL;
    const size_t count = laplace_ast_node_count(ast);
    if (count == 0 || count > UINT32_MAX) return -1;

    int rc = -1;
    laplace_compose_result_t* r = (laplace_compose_result_t*)calloc(1, sizeof(*r));
    source_node_t* nodes = (source_node_t*)calloc(count, sizeof(*nodes));
    laplace_ast_node_t* ast_nodes = (laplace_ast_node_t*)malloc(count * sizeof(*ast_nodes));
    uint32_t* child_counts = (uint32_t*)calloc(count, sizeof(*child_counts));
    uint32_t* child_offsets = (uint32_t*)calloc(count + 1, sizeof(*child_offsets));
    uint32_t* child_cursor = NULL;
    uint32_t* children = NULL;
    uint32_t* entity_index = NULL;
    size_t entity_index_capacity = 16;
    size_t span_capacity = 0;
    raw_cache_t raw_cache = {0};
    if (!r || !nodes || !ast_nodes || !child_counts || !child_offsets) { rc = -3; goto done; }
    r->source_mode = 1;
    if (count > SIZE_MAX / 2 - 1) { rc = -3; goto done; }
    while (entity_index_capacity < (count + 1) * 2) {
        if (entity_index_capacity > SIZE_MAX / 2) { rc = -3; goto done; }
        entity_index_capacity *= 2;
    }
    if (entity_index_capacity > SIZE_MAX / sizeof(*entity_index)) { rc = -3; goto done; }
    entity_index = (uint32_t*)malloc(entity_index_capacity * sizeof(*entity_index));
    if (!entity_index) { rc = -3; goto done; }
    memset(entity_index, 0xFF, entity_index_capacity * sizeof(*entity_index));
    if (count > (SIZE_MAX - 3) / 3) { rc = -3; goto done; }
    span_capacity = count * 3 + 3; /* every AST span plus all lexical gaps/edges */
    r->spans = (laplace_compose_span_t*)calloc(span_capacity, sizeof(*r->spans));
    if (!r->spans) { rc = -3; goto done; }
    r->span_capacity = span_capacity;
    r->span_index_cap = 64;
    while (r->span_index_cap < span_capacity * 2) r->span_index_cap <<= 1;
    r->span_index = (uint32_t*)malloc(r->span_index_cap * sizeof(*r->span_index));
    if (!r->span_index) { rc = -3; goto done; }
    memset(r->span_index, 0xFF, r->span_index_cap * sizeof(*r->span_index));

    for (size_t i = 0; i < count; ++i) {
        if (laplace_ast_get_node(ast, i, &ast_nodes[i]) != 0 ||
            ast_nodes[i].start_byte > ast_nodes[i].end_byte ||
            ast_nodes[i].end_byte > len ||
            (ast_nodes[i].parent != LAPLACE_AST_ROOT &&
             (ast_nodes[i].parent >= count || ast_nodes[i].parent >= i))) {
            rc = -1;
            goto done;
        }
        /* Parser bookkeeping and recovery tokens can have empty spans. They
         * describe syntax, but contribute no witnessed bytes or content ID.
         * Keep the AST intact; exclude only these empty occurrences from the
         * physical composition, including ordinary Markdown continuations. */
        if (ast_nodes[i].parent != LAPLACE_AST_ROOT &&
            ast_nodes[i].start_byte != ast_nodes[i].end_byte) {
            if (child_counts[ast_nodes[i].parent] == UINT32_MAX) { rc = -1; goto done; }
            ++child_counts[ast_nodes[i].parent];
        }
    }
    if (ast_nodes[0].parent != LAPLACE_AST_ROOT) { rc = -1; goto done; }
    for (size_t i = 0; i < count; ++i) {
        if (child_offsets[i] > UINT32_MAX - child_counts[i]) { rc = -1; goto done; }
        child_offsets[i + 1] = child_offsets[i] + child_counts[i];
    }
    children = (uint32_t*)malloc(child_offsets[count] * sizeof(*children));
    child_cursor = (uint32_t*)malloc(count * sizeof(*child_cursor));
    if ((child_offsets[count] && !children) || !child_cursor) { rc = -3; goto done; }
    memcpy(child_cursor, child_offsets, count * sizeof(*child_cursor));
    for (size_t i = 1; i < count; ++i) {
        uint32_t parent = ast_nodes[i].parent;
        if (parent == LAPLACE_AST_ROOT) { rc = -1; goto done; } /* one AST root */
        if (ast_nodes[i].start_byte < ast_nodes[parent].start_byte ||
            ast_nodes[i].end_byte > ast_nodes[parent].end_byte) { rc = -1; goto done; }
        if (ast_nodes[i].start_byte == ast_nodes[i].end_byte) continue;
        children[child_cursor[parent]++] = (uint32_t)i;
    }
    for (size_t i = 0; i < count; ++i)
        if (child_counts[i] > 1)
            sort_children(children + child_offsets[i], child_counts[i], ast_nodes);

    for (size_t idx = count; idx-- > 0;) {
        const laplace_ast_node_t* node = &ast_nodes[idx];
        if (node->start_byte == node->end_byte) continue;
        const size_t child_count = child_counts[idx];
        if (child_count == 0) {
            if (raw_component(r, &raw_cache, utf8 + node->start_byte,
                              node->end_byte - node->start_byte, &nodes[idx]) != 0) {
                rc = -3; goto done;
            }
            if (add_span(r, span_capacity, node->start_byte, node->end_byte, &nodes[idx]) != 0) { rc = -3; goto done; }
            continue;
        }

        /* At most one gap before/after every child. */
        if (child_count > (SIZE_MAX - 1) / 2) { rc = -3; goto done; }
        const size_t cap = child_count * 2 + 1;
        hash128_t* ids = (hash128_t*)malloc(cap * sizeof(*ids));
        double* coords = (double*)malloc(cap * 4 * sizeof(*coords));
        uint64_t* flags = (uint64_t*)malloc(cap * sizeof(*flags));
        if (!ids || !coords || !flags) {
            free(ids); free(coords); free(flags); rc = -3; goto done;
        }
        size_t used = 0, cursor = node->start_byte;
        for (size_t k = 0; k < child_count; ++k) {
            uint32_t child_index = children[child_offsets[idx] + k];
            const laplace_ast_node_t* child_ast = &ast_nodes[child_index];
            if (child_ast->start_byte < cursor || child_ast->end_byte > node->end_byte) {
                free(ids); free(coords); free(flags); rc = -1; goto done;
            }
            if (child_ast->start_byte > cursor) {
                source_node_t gap = {0};
                if (raw_component(r, &raw_cache, utf8 + cursor, child_ast->start_byte - cursor, &gap) != 0 ||
                    add_span(r, span_capacity, cursor, child_ast->start_byte, &gap) != 0 ||
                    append_component(&gap, ids, coords, flags, cap, &used) != 0) {
                    free(ids); free(coords); free(flags); rc = -3; goto done;
                }
            }
            if (append_component(&nodes[child_index], ids, coords, flags, cap, &used) != 0) {
                free(ids); free(coords); free(flags); rc = -3; goto done;
            }
            cursor = child_ast->end_byte;
        }
        if (cursor < node->end_byte) {
            source_node_t gap = {0};
            if (raw_component(r, &raw_cache, utf8 + cursor, node->end_byte - cursor, &gap) != 0 ||
                add_span(r, span_capacity, cursor, node->end_byte, &gap) != 0 ||
                append_component(&gap, ids, coords, flags, cap, &used) != 0) {
                free(ids); free(coords); free(flags); rc = -3; goto done;
            }
        }
        nodes[idx].kids = ids;
        nodes[idx].flags = flags;
        nodes[idx].kid_count = used;
        rc = compose_components(&nodes[idx], ids, coords, flags, used);
        free(coords);
        if (rc != 0) goto done;
        if (used > 1 && push_composition(r, &nodes[idx], entity_index, entity_index_capacity) != 0) {
            rc = -3; goto done;
        }
        if (add_span(r, span_capacity, node->start_byte, node->end_byte, &nodes[idx]) != 0) { rc = -3; goto done; }
    }

    /* A parser root generally covers the full source. If it does not, make the
     * full artifact root from its source-edge gaps; never lose those bytes. */
    source_node_t root = nodes[0];
    if (ast_nodes[0].start_byte != 0 || ast_nodes[0].end_byte != len) {
        hash128_t ids[3]; double coords[12]; uint64_t flags[3]; size_t used = 0;
        if (ast_nodes[0].start_byte != 0) {
            source_node_t lead = {0};
            if (raw_component(r, &raw_cache, utf8, ast_nodes[0].start_byte, &lead) != 0 ||
                add_span(r, span_capacity, 0, ast_nodes[0].start_byte, &lead) != 0 ||
                append_component(&lead, ids, coords, flags, 3, &used) != 0) { rc = -3; goto done; }
        }
        if (ast_nodes[0].start_byte != ast_nodes[0].end_byte &&
            append_component(&nodes[0], ids, coords, flags, 3, &used) != 0) { rc = -3; goto done; }
        if (ast_nodes[0].end_byte != len) {
            source_node_t tail = {0};
            if (raw_component(r, &raw_cache, utf8 + ast_nodes[0].end_byte, len - ast_nodes[0].end_byte, &tail) != 0 ||
                add_span(r, span_capacity, ast_nodes[0].end_byte, (uint32_t)len, &tail) != 0 ||
                append_component(&tail, ids, coords, flags, 3, &used) != 0) { rc = -3; goto done; }
        }
        /* ids/flags are live through push_phys; this virtual source root does
         * not own them (they are stack arrays) and needs no retained wrapper. */
        root.kids = ids;
        root.flags = flags;
        root.kid_count = used;
        if (compose_components(&root, ids, coords, flags, used) != 0 ||
            (used > 1 && push_composition(r, &root, entity_index, entity_index_capacity) != 0)) { rc = -3; goto done; }
    }
    r->root_id = root.id;
    memcpy(r->source_root_coord, root.coord, sizeof(root.coord));
    r->source_root_tier = root.tier;
    r->source_root_atom = root.atom;
    r->source_root_has_atom = root.has_atom;
    r->source_root_valid = 1;
    *out = r;
    r = NULL;
    rc = 0;

done:
    for (size_t i = 0; nodes && i < count; ++i) { free(nodes[i].kids); free(nodes[i].flags); }
    free(nodes);
    free(ast_nodes);
    free(child_counts);
    free(child_offsets);
    free(child_cursor);
    free(children);
    free(entity_index);
    free(raw_cache.entries);
    if (r) laplace_compose_result_free(r);
    return rc;
}
