#include "laplace/core/tier_tree.h"

#include <stdlib.h>
#include <string.h>

/* SoA arena. Parallel arrays — one per field. Allocated with a single
 * capacity; grown geometrically when add operations would exceed it.
 *
 * The id/coord/hilbert arrays are zeroed at allocation so hash_composer
 * can write to them without worrying about uninitialized state on the
 * pre-compose read paths. */

struct tier_tree {
    size_t count;
    size_t capacity;

    uint8_t*      tier;
    uint32_t*     parent_idx;
    uint32_t*     first_child_idx;
    uint32_t*     child_count;
    uint32_t*     text_range_off;
    uint32_t*     text_range_len;
    uint32_t*     atom;

    hash128_t*    id;
    double*       coord;     /* count * 4 doubles */
    hilbert128_t* hilbert;
};

static int tier_tree_grow(tier_tree_t* t, size_t min_capacity) {
    size_t new_cap = t->capacity > 0 ? t->capacity : 16;
    while (new_cap < min_capacity) {
        /* geometric growth — 2x is fine; we don't expect frequent reallocs
         * because callers pass a good capacity_hint (TextDecomposer upper-
         * bounds from input byte length). */
        if (new_cap > (SIZE_MAX / 2)) {
            return -1;
        }
        new_cap *= 2;
    }

    uint8_t*      new_tier            = (uint8_t*)     realloc(t->tier,            new_cap * sizeof(uint8_t));
    uint32_t*     new_parent_idx      = (uint32_t*)    realloc(t->parent_idx,      new_cap * sizeof(uint32_t));
    uint32_t*     new_first_child_idx = (uint32_t*)    realloc(t->first_child_idx, new_cap * sizeof(uint32_t));
    uint32_t*     new_child_count     = (uint32_t*)    realloc(t->child_count,     new_cap * sizeof(uint32_t));
    uint32_t*     new_text_range_off  = (uint32_t*)    realloc(t->text_range_off,  new_cap * sizeof(uint32_t));
    uint32_t*     new_text_range_len  = (uint32_t*)    realloc(t->text_range_len,  new_cap * sizeof(uint32_t));
    uint32_t*     new_atom            = (uint32_t*)    realloc(t->atom,            new_cap * sizeof(uint32_t));
    hash128_t*    new_id              = (hash128_t*)   realloc(t->id,              new_cap * sizeof(hash128_t));
    double*       new_coord           = (double*)      realloc(t->coord,           new_cap * 4 * sizeof(double));
    hilbert128_t* new_hilbert         = (hilbert128_t*)realloc(t->hilbert,         new_cap * sizeof(hilbert128_t));

    /* If any single realloc failed, leave the tree in its prior state. The
     * partial reallocs that succeeded just enlarge the buffer; the bookkeeping
     * (count, capacity) is unchanged, so the next add will retry and the extra
     * bytes are simply unused. */
    if (!new_tier || !new_parent_idx || !new_first_child_idx || !new_child_count
        || !new_text_range_off || !new_text_range_len || !new_atom
        || !new_id || !new_coord || !new_hilbert) {
        /* Best-effort: install whatever did succeed (so we don't leak), and
         * report failure. Subsequent add will see the unchanged capacity and
         * retry the grow. */
        if (new_tier)            t->tier            = new_tier;
        if (new_parent_idx)      t->parent_idx      = new_parent_idx;
        if (new_first_child_idx) t->first_child_idx = new_first_child_idx;
        if (new_child_count)     t->child_count     = new_child_count;
        if (new_text_range_off)  t->text_range_off  = new_text_range_off;
        if (new_text_range_len)  t->text_range_len  = new_text_range_len;
        if (new_atom)            t->atom            = new_atom;
        if (new_id)              t->id              = new_id;
        if (new_coord)           t->coord           = new_coord;
        if (new_hilbert)         t->hilbert         = new_hilbert;
        return -1;
    }

    /* Zero the newly added slots in id/coord/hilbert so pre-compose reads see
     * deterministic zero (matches hash128_zero). */
    memset(new_id + t->capacity, 0, (new_cap - t->capacity) * sizeof(hash128_t));
    memset(new_coord + t->capacity * 4, 0, (new_cap - t->capacity) * 4 * sizeof(double));
    memset(new_hilbert + t->capacity, 0, (new_cap - t->capacity) * sizeof(hilbert128_t));

    t->tier            = new_tier;
    t->parent_idx      = new_parent_idx;
    t->first_child_idx = new_first_child_idx;
    t->child_count     = new_child_count;
    t->text_range_off  = new_text_range_off;
    t->text_range_len  = new_text_range_len;
    t->atom            = new_atom;
    t->id              = new_id;
    t->coord           = new_coord;
    t->hilbert         = new_hilbert;
    t->capacity        = new_cap;
    return 0;
}

tier_tree_t* tier_tree_new(size_t capacity_hint) {
    tier_tree_t* t = (tier_tree_t*)calloc(1, sizeof(tier_tree_t));
    if (!t) return NULL;
    if (capacity_hint > 0) {
        if (tier_tree_grow(t, capacity_hint) != 0) {
            tier_tree_free(t);
            return NULL;
        }
    }
    return t;
}

void tier_tree_free(tier_tree_t* tree) {
    if (!tree) return;
    free(tree->tier);
    free(tree->parent_idx);
    free(tree->first_child_idx);
    free(tree->child_count);
    free(tree->text_range_off);
    free(tree->text_range_len);
    free(tree->atom);
    free(tree->id);
    free(tree->coord);
    free(tree->hilbert);
    free(tree);
}

size_t tier_tree_node_count(const tier_tree_t* tree) {
    return tree ? tree->count : 0;
}

size_t tier_tree_capacity(const tier_tree_t* tree) {
    return tree ? tree->capacity : 0;
}

static uint32_t tier_tree_append(
    tier_tree_t* tree,
    uint8_t      tier,
    uint32_t     first_child_idx,
    uint32_t     child_count,
    uint32_t     text_range_off,
    uint32_t     text_range_len,
    uint32_t     atom) {
    if (!tree) return TIER_TREE_INVALID;
    if (tree->count >= tree->capacity) {
        if (tier_tree_grow(tree, tree->count + 1) != 0) {
            return TIER_TREE_INVALID;
        }
    }
    const uint32_t idx = (uint32_t)tree->count;
    tree->tier[idx]            = tier;
    tree->parent_idx[idx]      = TIER_TREE_INVALID; /* set by finalize */
    tree->first_child_idx[idx] = first_child_idx;
    tree->child_count[idx]     = child_count;
    tree->text_range_off[idx]  = text_range_off;
    tree->text_range_len[idx]  = text_range_len;
    tree->atom[idx]            = atom;
    /* id/coord/hilbert already zeroed by grow. */
    tree->count++;
    return idx;
}

uint32_t tier_tree_add_leaf(
    tier_tree_t* tree,
    uint8_t      tier,
    uint32_t     atom,
    uint32_t     text_range_off,
    uint32_t     text_range_len) {
    return tier_tree_append(tree, tier, TIER_TREE_INVALID, 0,
                            text_range_off, text_range_len, atom);
}

uint32_t tier_tree_add_node(
    tier_tree_t* tree,
    uint8_t      tier,
    uint32_t     first_child_idx,
    uint32_t     child_count,
    uint32_t     text_range_off,
    uint32_t     text_range_len) {
    if (!tree) return TIER_TREE_INVALID;
    /* Validate: child range must be entirely within existing nodes
     * (topological order). child_count==0 is allowed and produces an interior
     * node with no children — caller can treat as an empty container. */
    if (child_count > 0) {
        if (first_child_idx == TIER_TREE_INVALID) return TIER_TREE_INVALID;
        /* Overflow check: first_child_idx + child_count must not wrap. */
        if (first_child_idx > UINT32_MAX - child_count) return TIER_TREE_INVALID;
        const uint32_t last_child = first_child_idx + child_count - 1;
        if (last_child >= tree->count) return TIER_TREE_INVALID;
    } else if (first_child_idx != TIER_TREE_INVALID) {
        /* zero count but non-sentinel first_child_idx is malformed */
        return TIER_TREE_INVALID;
    }
    return tier_tree_append(tree, tier, first_child_idx, child_count,
                            text_range_off, text_range_len, 0);
}

int tier_tree_finalize(tier_tree_t* tree) {
    if (!tree) return -1;
    /* Reset all parent_idx to INVALID, then walk each node and set
     * parent_idx[child] = parent for every child in its range. Single
     * pass; O(N). */
    for (size_t i = 0; i < tree->count; ++i) {
        tree->parent_idx[i] = TIER_TREE_INVALID;
    }
    for (size_t i = 0; i < tree->count; ++i) {
        const uint32_t first = tree->first_child_idx[i];
        const uint32_t cnt   = tree->child_count[i];
        if (cnt == 0 || first == TIER_TREE_INVALID) continue;
        for (uint32_t c = 0; c < cnt; ++c) {
            tree->parent_idx[first + c] = (uint32_t)i;
        }
    }
    return 0;
}

int tier_tree_get_node(const tier_tree_t* tree, uint32_t idx, tier_node_view_t* out) {
    if (!tree || !out || idx >= tree->count) return -1;
    memset(out, 0, sizeof(*out));
    out->tier             = tree->tier[idx];
    out->parent_idx       = tree->parent_idx[idx];
    out->first_child_idx  = tree->first_child_idx[idx];
    out->child_count      = tree->child_count[idx];
    out->text_range_off   = tree->text_range_off[idx];
    out->text_range_len   = tree->text_range_len[idx];
    out->atom             = tree->atom[idx];
    out->id               = tree->id[idx];
    out->coord[0]         = tree->coord[idx * 4 + 0];
    out->coord[1]         = tree->coord[idx * 4 + 1];
    out->coord[2]         = tree->coord[idx * 4 + 2];
    out->coord[3]         = tree->coord[idx * 4 + 3];
    out->hilbert          = tree->hilbert[idx];
    return 0;
}

int tier_tree_set_id(tier_tree_t* tree, uint32_t idx, const hash128_t* id) {
    if (!tree || !id || idx >= tree->count) return -1;
    tree->id[idx] = *id;
    return 0;
}

int tier_tree_set_coord(tier_tree_t* tree, uint32_t idx, const double coord[4]) {
    if (!tree || !coord || idx >= tree->count) return -1;
    tree->coord[idx * 4 + 0] = coord[0];
    tree->coord[idx * 4 + 1] = coord[1];
    tree->coord[idx * 4 + 2] = coord[2];
    tree->coord[idx * 4 + 3] = coord[3];
    return 0;
}

int tier_tree_set_hilbert(tier_tree_t* tree, uint32_t idx, const hilbert128_t* hilbert) {
    if (!tree || !hilbert || idx >= tree->count) return -1;
    tree->hilbert[idx] = *hilbert;
    return 0;
}

const uint8_t*      tier_tree_tier_array(const tier_tree_t* tree)            { return tree ? tree->tier            : NULL; }
const uint32_t*     tier_tree_first_child_idx_array(const tier_tree_t* tree) { return tree ? tree->first_child_idx : NULL; }
const uint32_t*     tier_tree_child_count_array(const tier_tree_t* tree)     { return tree ? tree->child_count     : NULL; }
const uint32_t*     tier_tree_parent_idx_array(const tier_tree_t* tree)      { return tree ? tree->parent_idx      : NULL; }
const uint32_t*     tier_tree_atom_array(const tier_tree_t* tree)            { return tree ? tree->atom            : NULL; }
const uint32_t*     tier_tree_text_off_array(const tier_tree_t* tree)        { return tree ? tree->text_range_off  : NULL; }
const uint32_t*     tier_tree_text_len_array(const tier_tree_t* tree)        { return tree ? tree->text_range_len  : NULL; }
const hash128_t*    tier_tree_id_array(const tier_tree_t* tree)              { return tree ? tree->id              : NULL; }
hash128_t*          tier_tree_id_array_mut(tier_tree_t* tree)                { return tree ? tree->id              : NULL; }
const double*       tier_tree_coord_array(const tier_tree_t* tree)           { return tree ? tree->coord           : NULL; }
double*             tier_tree_coord_array_mut(tier_tree_t* tree)             { return tree ? tree->coord           : NULL; }
const hilbert128_t* tier_tree_hilbert_array(const tier_tree_t* tree)         { return tree ? tree->hilbert         : NULL; }
hilbert128_t*       tier_tree_hilbert_array_mut(tier_tree_t* tree)           { return tree ? tree->hilbert         : NULL; }
