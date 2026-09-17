#include "laplace/core/hash_composer.h"

#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>

#ifndef _WIN32
#include <pthread.h>
#endif

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/math4d.h"
#include "laplace/core/tier_tree.h"

static void compose_identity_and_hilbert(uint8_t tier, const hash128_t* child_ids,
    size_t n, const double coord[4], hash128_t* out_id, hilbert128_t* out_hb) {
    if (n == 0) {
        hash128_zero(out_id);
        for (int b = 0; b < 16; ++b) out_hb->bytes[b] = 0;
        return;
    }
    if (n == 1) {
        *out_id = child_ids[0];
    } else {
        hash128_merkle(tier, child_ids, n, out_id);
    }
    hilbert4d_encode(coord, out_hb);
}

void hash_composer_compose_node(
    uint8_t          tier,
    const hash128_t* child_ids,
    const double*    child_coords,
    size_t           n,
    hash128_t*       out_id,
    double           out_coord[4],
    hilbert128_t*    out_hb) {
    math4d_centroid(child_coords, n, out_coord);
    compose_identity_and_hilbert(tier, child_ids, n, out_coord, out_id, out_hb);
}

int hash_composer_compose_node_with_workspace(
    uint8_t tier, const hash128_t* child_ids, const double* child_coords, size_t n,
    void* workspace, size_t workspace_bytes,
    hash128_t* out_id, double out_coord[4], hilbert128_t* out_hb) {
    if (out_id == NULL || out_coord == NULL || out_hb == NULL ||
        (n != 0u && (child_ids == NULL || child_coords == NULL))) return -1;
    double coord[4];
    if (math4d_centroid_with_workspace(child_coords, n, workspace, workspace_bytes, coord) != 0)
        return -1;
    hash128_t id;
    hilbert128_t hilbert;
    compose_identity_and_hilbert(tier, child_ids, n, coord, &id, &hilbert);
    *out_id = id;
    for (size_t axis = 0; axis < 4u; ++axis) out_coord[axis] = coord[axis];
    *out_hb = hilbert;
    return 0;
}

typedef struct hash_composer_arrays {
    size_t count;
    const uint8_t* tiers;
    const uint32_t* first_child;
    const uint32_t* child_count;
    const uint32_t* atoms;
    hash128_t* ids;
    double* coords;
    hilbert128_t* hilberts;
    hash_composer_atom_resolver_fn resolver;
    void* resolver_user_data;
} hash_composer_arrays_t;

static int hash_composer_arrays_init(
    tier_tree_t* tree,
    hash_composer_atom_resolver_fn resolver,
    void* resolver_user_data,
    hash_composer_arrays_t* out) {
    if (!tree || !resolver || !out) return -1;
    *out = (hash_composer_arrays_t) {
        .count = tier_tree_node_count(tree),
        .tiers = tier_tree_tier_array(tree),
        .first_child = tier_tree_first_child_idx_array(tree),
        .child_count = tier_tree_child_count_array(tree),
        .atoms = tier_tree_atom_array(tree),
        .ids = tier_tree_id_array_mut(tree),
        .coords = tier_tree_coord_array_mut(tree),
        .hilberts = tier_tree_hilbert_array_mut(tree),
        .resolver = resolver,
        .resolver_user_data = resolver_user_data,
    };
    if (out->count == 0) return 0;
    return out->tiers && out->first_child && out->child_count && out->atoms &&
        out->ids && out->coords && out->hilberts ? 0 : -1;
}

static int hash_composer_process_node(const hash_composer_arrays_t* a, size_t i) {
    const uint32_t first = a->first_child[i];
    const uint32_t count = a->child_count[i];
    const int is_leaf = (first == TIER_TREE_INVALID) || (count == 0);

    if (is_leaf) {
        double leaf_coord[4] = {0.0, 0.0, 0.0, 0.0};
        hash128_t leaf_id;
        hash128_zero(&leaf_id);
        hilbert128_t leaf_hilbert;
        for (int b = 0; b < 16; ++b) leaf_hilbert.bytes[b] = 0;

        const int rc = a->resolver(a->atoms[i], a->resolver_user_data,
                                   &leaf_id, leaf_coord, &leaf_hilbert);
        if (rc != 0) return rc;

        a->ids[i] = leaf_id;
        a->coords[i * 4 + 0] = leaf_coord[0];
        a->coords[i * 4 + 1] = leaf_coord[1];
        a->coords[i * 4 + 2] = leaf_coord[2];
        a->coords[i * 4 + 3] = leaf_coord[3];
        a->hilberts[i] = leaf_hilbert;
        return 0;
    }

    if ((size_t) first >= a->count ||
        (size_t) first + (size_t) count > a->count)
        return -1;

    hash_composer_compose_node(a->tiers[i], &a->ids[first],
                               &a->coords[(size_t) first * 4], (size_t) count,
                               &a->ids[i], &a->coords[i * 4], &a->hilberts[i]);
    return 0;
}

int hash_composer_run(
    tier_tree_t*                   tree,
    hash_composer_atom_resolver_fn resolver,
    void*                          resolver_user_data) {
    hash_composer_arrays_t arrays;
    if (hash_composer_arrays_init(tree, resolver, resolver_user_data, &arrays) != 0)
        return -1;
    for (size_t i = 0; i < arrays.count; ++i) {
        const int rc = hash_composer_process_node(&arrays, i);
        if (rc != 0) return rc;
    }
    return 0;
}

#ifndef _WIN32
typedef struct hash_composer_task {
    const hash_composer_arrays_t* arrays;
    const size_t* nodes;
    size_t begin;
    size_t end;
    size_t error_index;
    int error_code;
} hash_composer_task_t;

static void* hash_composer_worker(void* raw) {
    hash_composer_task_t* task = (hash_composer_task_t*) raw;
    task->error_index = SIZE_MAX;
    task->error_code = 0;
    for (size_t pos = task->begin; pos < task->end; ++pos) {
        const size_t index = task->nodes[pos];
        const int rc = hash_composer_process_node(task->arrays, index);
        if (rc != 0) {
            task->error_index = index;
            task->error_code = rc;
            break;
        }
    }
    return NULL;
}
#endif

int hash_composer_run_workers(
    tier_tree_t*                   tree,
    hash_composer_atom_resolver_fn resolver,
    void*                          resolver_user_data,
    size_t                         worker_count) {
    if (worker_count == 0) return -1;
    if (worker_count == 1) return hash_composer_run(tree, resolver, resolver_user_data);

#ifdef _WIN32
    /* The production managed host is POSIX today. Preserve exact semantics on
     * Windows until its native worker primitive is admitted rather than hiding
     * a second scheduler behind this ABI. */
    return hash_composer_run(tree, resolver, resolver_user_data);
#else
    hash_composer_arrays_t arrays;
    if (hash_composer_arrays_init(tree, resolver, resolver_user_data, &arrays) != 0)
        return -1;
    if (arrays.count < 2) return hash_composer_run(tree, resolver, resolver_user_data);

    /* The tree is built bottom-up: every parent references an already-appended
     * contiguous child range. Compute dependency depth once, then execute all
     * nodes at one depth concurrently. Worker/task order is physical state only;
     * every node writes its own fixed output slot and parents begin only after
     * the complete child frontier joins. */
    uint32_t* depth = (uint32_t*) malloc(arrays.count * sizeof(*depth));
    if (!depth) return hash_composer_run(tree, resolver, resolver_user_data);

    uint32_t max_depth = 0;
    for (size_t i = 0; i < arrays.count; ++i) {
        const uint32_t first = arrays.first_child[i];
        const uint32_t count = arrays.child_count[i];
        if (first == TIER_TREE_INVALID || count == 0) {
            depth[i] = 0;
            continue;
        }
        if ((size_t) first >= i ||
            (size_t) first + (size_t) count > i) {
            free(depth);
            return -1;
        }
        uint32_t node_depth = 0;
        for (uint32_t c = 0; c < count; ++c)
            if (depth[first + c] > node_depth) node_depth = depth[first + c];
        if (node_depth == UINT32_MAX) {
            free(depth);
            return -1;
        }
        depth[i] = node_depth + 1;
        if (depth[i] > max_depth) max_depth = depth[i];
    }

    const size_t level_count = (size_t) max_depth + 1;
    if (level_count > SIZE_MAX / sizeof(size_t)) {
        free(depth);
        return -1;
    }
    size_t* counts = (size_t*) calloc(level_count, sizeof(*counts));
    size_t* offsets = (size_t*) malloc((level_count + 1) * sizeof(*offsets));
    size_t* cursors = (size_t*) malloc(level_count * sizeof(*cursors));
    size_t* nodes = (size_t*) malloc(arrays.count * sizeof(*nodes));
    if (!counts || !offsets || !cursors || !nodes) {
        free(nodes);
        free(cursors);
        free(offsets);
        free(counts);
        free(depth);
        return hash_composer_run(tree, resolver, resolver_user_data);
    }

    for (size_t i = 0; i < arrays.count; ++i) ++counts[depth[i]];
    offsets[0] = 0;
    for (size_t level = 0; level < level_count; ++level)
        offsets[level + 1] = offsets[level] + counts[level];
    for (size_t level = 0; level < level_count; ++level) cursors[level] = offsets[level];
    for (size_t i = 0; i < arrays.count; ++i) nodes[cursors[depth[i]]++] = i;

    int result = 0;
    for (size_t level = 0; level < level_count && result == 0; ++level) {
        const size_t begin = offsets[level];
        const size_t end = offsets[level + 1];
        const size_t width = end - begin;
        if (width == 0) continue;

        size_t active = worker_count < width ? worker_count : width;
        if (active <= 1) {
            for (size_t pos = begin; pos < end; ++pos) {
                result = hash_composer_process_node(&arrays, nodes[pos]);
                if (result != 0) break;
            }
            continue;
        }

        hash_composer_task_t* tasks =
            (hash_composer_task_t*) calloc(active, sizeof(*tasks));
        pthread_t* threads =
            (pthread_t*) malloc((active - 1) * sizeof(*threads));
        uint8_t* started = (uint8_t*) calloc(active - 1, sizeof(*started));
        if (!tasks || !threads || !started) {
            free(started);
            free(threads);
            free(tasks);
            for (size_t pos = begin; pos < end; ++pos) {
                result = hash_composer_process_node(&arrays, nodes[pos]);
                if (result != 0) break;
            }
            continue;
        }

        const size_t base = width / active;
        const size_t extra = width % active;
        size_t pos = begin;
        for (size_t w = 0; w < active; ++w) {
            const size_t span = base + (w < extra ? 1 : 0);
            tasks[w] = (hash_composer_task_t) {
                .arrays = &arrays,
                .nodes = nodes,
                .begin = pos,
                .end = pos + span,
                .error_index = SIZE_MAX,
                .error_code = 0,
            };
            pos += span;
        }

        for (size_t w = 0; w + 1 < active; ++w) {
            if (pthread_create(&threads[w], NULL, hash_composer_worker, &tasks[w]) == 0)
                started[w] = 1;
            else
                (void) hash_composer_worker(&tasks[w]);
        }
        (void) hash_composer_worker(&tasks[active - 1]);
        for (size_t w = 0; w + 1 < active; ++w)
            if (started[w]) (void) pthread_join(threads[w], NULL);

        size_t first_error = SIZE_MAX;
        for (size_t w = 0; w < active; ++w) {
            if (tasks[w].error_code != 0 && tasks[w].error_index < first_error) {
                first_error = tasks[w].error_index;
                result = tasks[w].error_code;
            }
        }

        free(started);
        free(threads);
        free(tasks);
    }

    free(nodes);
    free(cursors);
    free(offsets);
    free(counts);
    free(depth);
    return result;
#endif
}
