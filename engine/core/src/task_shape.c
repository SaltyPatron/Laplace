#include "laplace/core/task_shape.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

static int same(hash128_t a, hash128_t b) {
    return a.hi == b.hi && a.lo == b.lo;
}

void laplace_task_shape_markers_init(laplace_task_shape_markers_t *out) {
    if (!out) return;
    hash128_blake3_str("laplace/task-shape/relation-read/token-slots/v1", &out->schema);
    hash128_blake3_str("laplace/task-shape/token-slot/v1", &out->slot_schema);
    hash128_blake3_str("laplace/task-shape/slots-end/v1", &out->end);
}

static int compare_id(const void *a, const void *b) {
    return memcmp(a, b, sizeof(hash128_t));
}

int laplace_task_shape_decode(const hash128_t *flat, size_t count,
                              laplace_task_shape_t *out) {
    laplace_task_shape_markers_t markers;
    hash128_t *refs;
    size_t n;
    if (!out) return -2;
    memset(out, 0, sizeof(*out));
    if (!flat || count < 7 || count > SIZE_MAX / sizeof(*flat)) return -2;
    laplace_task_shape_markers_init(&markers);
    if (!same(flat[0], markers.schema)) return -1;
    if ((count - 4) % 3 || !same(flat[count - 1], markers.end)) return -2;
    for (size_t i = 1; i < count - 1; ++i)
        if (!(flat[i].hi || flat[i].lo) || same(flat[i], markers.schema)
            || same(flat[i], markers.slot_schema) || same(flat[i], markers.end))
            return -2;
    n = (count - 4) / 3;
    refs = (hash128_t *)malloc(n * sizeof(*refs));
    if (!refs) return -3;
    for (size_t i = 0; i < n; ++i) {
        const hash128_t *slot = flat + 3 + i * 3;
        hash128_t parts[3] = {markers.slot_schema, slot[1], slot[2]}, expected;
        hash128_merkle(4, parts, 3, &expected);
        if (!same(slot[0], expected)) { free(refs); return -2; }
        refs[i] = slot[1];
    }
    qsort(refs, n, sizeof(*refs), compare_id);
    for (size_t i = 1; i < n; ++i)
        if (same(refs[i - 1], refs[i])) { free(refs); return -2; }
    free(refs);
    out->exemplar_parse = flat[1];
    out->predicate = flat[2];
    out->slots = flat + 3;
    out->slot_count = n;
    return 0;
}

typedef struct { hash128_t id; size_t ordinal; } ref_index;

static int compare_ref(const void *a, const void *b) {
    return memcmp(&((const ref_index *)a)->id, &((const ref_index *)b)->id,
                  sizeof(hash128_t));
}

static size_t ordinal(const ref_index *refs, size_t n, hash128_t id) {
    ref_index key = {id, 0};
    const ref_index *found = (const ref_index *)bsearch(&key, refs, n,
                                                       sizeof(*refs), compare_ref);
    return found ? found->ordinal : SIZE_MAX;
}

static ref_index *make_refs(const laplace_ud_parse_t *p) {
    ref_index *refs;
    if (!p->token_count || p->token_count > SIZE_MAX / sizeof(*refs)) return NULL;
    refs = (ref_index *)malloc(p->token_count * sizeof(*refs));
    if (!refs) return NULL;
    for (size_t i = 0; i < p->token_count; ++i) {
        refs[i].id = p->tokens[i].ref_id;
        refs[i].ordinal = i;
    }
    qsort(refs, p->token_count, sizeof(*refs), compare_ref);
    return refs;
}

static int pairs_equal(laplace_ud_pairs_t a, laplace_ud_pairs_t b) {
    return a.count == b.count && (!a.count ||
        memcmp(a.items, b.items, a.count * 2 * sizeof(hash128_t)) == 0);
}

static size_t head_ordinal(const ref_index *refs, size_t n, hash128_t id,
                           const laplace_ud_markers_t *markers) {
    return same(id, markers->root) ? n : ordinal(refs, n, id);
}

/* A partial dependency annotation cannot authorize a complete request shape.
 * Validate the entire rooted tree, including disconnected cycles, in O(n). */
static int complete_tree(const laplace_ud_parse_t *p, const ref_index *refs,
                          const laplace_ud_markers_t *markers) {
    size_t n = p->token_count, roots = 0;
    size_t *heads;
    unsigned char *colors;
    int result = 0;
    if (n > SIZE_MAX / sizeof(*heads)) return -1;
    heads = (size_t *)malloc(n * sizeof(*heads));
    colors = (unsigned char *)calloc(n, 1);
    if (!heads || !colors) { free(heads); free(colors); return -1; }
    for (size_t i = 0; i < n; ++i) {
        if (same(p->tokens[i].deprel_id, markers->none)) goto done;
        heads[i] = head_ordinal(refs, n, p->tokens[i].head_ref_id, markers);
        if (heads[i] == SIZE_MAX) goto done;
        if (heads[i] == n) ++roots;
    }
    if (roots != 1) goto done;
    for (size_t i = 0; i < n; ++i) {
        size_t at = i;
        while (at != n && colors[at] == 0) {
            colors[at] = 1;
            at = heads[at];
        }
        if (at != n && colors[at] == 1) goto done;
        at = i;
        while (at != n && colors[at] == 1) {
            colors[at] = 2;
            at = heads[at];
        }
    }
    result = 1;
done:
    free(heads);
    free(colors);
    return result;
}

int laplace_task_shape_match(const laplace_task_shape_t *shape,
    const laplace_ud_parse_t *exemplar, const laplace_ud_parse_t *current,
    size_t *slot_ordinals) {
    ref_index *a = NULL, *b = NULL;
    unsigned char *variables = NULL;
    laplace_ud_markers_t markers;
    size_t n;
    int result = 0, complete;
    if (!shape || !exemplar || !current || !slot_ordinals || !shape->slots)
        return -1;
    n = exemplar->token_count;
    if (!n || n != current->token_count || !shape->slot_count
        || shape->slot_count >= n || exemplar->mwt_count != current->mwt_count
        || !same(exemplar->language_id, current->language_id)) return 0;
    laplace_ud_markers_init(&markers);
    a = make_refs(exemplar); b = make_refs(current);
    variables = (unsigned char *)calloc(n, 1);
    if (!a || !b || !variables) { result = -1; goto done; }
    complete = complete_tree(exemplar, a, &markers);
    if (complete != 1) { result = complete; goto done; }
    complete = complete_tree(current, b, &markers);
    if (complete != 1) { result = complete; goto done; }
    for (size_t i = 0; i < shape->slot_count; ++i) {
        size_t at = ordinal(a, n, shape->slots[i * 3 + 1]);
        if (at == SIZE_MAX || variables[at]) goto done;
        variables[at] = 1;
        slot_ordinals[i] = at;
    }
    for (size_t i = 0; i < n; ++i) {
        const laplace_ud_token_t *left = &exemplar->tokens[i];
        const laplace_ud_token_t *right = &current->tokens[i];
        if ((!variables[i] && (!same(left->form_id, right->form_id)
                                || !same(left->lemma_id, right->lemma_id)))
            || !same(left->upos_id, right->upos_id)
            || !same(left->xpos_id, right->xpos_id)
            || !same(left->deprel_id, right->deprel_id)
            || !pairs_equal(left->features, right->features)
            || !pairs_equal(left->misc, right->misc)
            || left->enhanced.count != right->enhanced.count
            || head_ordinal(a, n, left->head_ref_id, &markers)
                != head_ordinal(b, n, right->head_ref_id, &markers)) goto done;
        for (size_t j = 0; j < left->enhanced.count; ++j)
            if (!same(left->enhanced.items[j * 2 + 1], right->enhanced.items[j * 2 + 1])
                || head_ordinal(a, n, left->enhanced.items[j * 2], &markers)
                    != head_ordinal(b, n, right->enhanced.items[j * 2], &markers)) goto done;
    }
    for (size_t i = 0; i < exemplar->mwt_count; ++i) {
        const laplace_ud_mwt_t *left = &exemplar->mwts[i], *right = &current->mwts[i];
        if (!same(left->form_id, right->form_id) || !pairs_equal(left->misc, right->misc)
            || ordinal(a, n, left->start_ref_id) != ordinal(b, n, right->start_ref_id)
            || ordinal(a, n, left->end_ref_id) != ordinal(b, n, right->end_ref_id)) goto done;
    }
    result = 1;
done:
    free(a); free(b); free(variables);
    return result;
}

int laplace_task_shape_match_forms(const laplace_task_shape_t *shape,
    const laplace_ud_parse_t *exemplar, const hash128_t *forms, size_t form_count,
    size_t *slot_ordinals) {
    ref_index *refs = NULL;
    unsigned char *variables = NULL;
    laplace_ud_markers_t markers;
    int result = 0;
    if (!shape || !exemplar || !forms || !slot_ordinals || !shape->slots) return -1;
    if (!form_count || form_count != exemplar->token_count || !shape->slot_count
        || shape->slot_count >= form_count || exemplar->mwt_count) return 0;
    refs = make_refs(exemplar);
    variables = (unsigned char *)calloc(form_count, 1);
    if (!refs || !variables) { result = -1; goto done; }
    laplace_ud_markers_init(&markers);
    result = complete_tree(exemplar, refs, &markers);
    if (result != 1) goto done;
    result = 0;
    for (size_t i = 0; i < shape->slot_count; ++i) {
        size_t at = ordinal(refs, form_count, shape->slots[i * 3 + 1]);
        if (at == SIZE_MAX || variables[at]) goto done;
        variables[at] = 1;
        slot_ordinals[i] = at;
    }
    for (size_t i = 0; i < form_count; ++i)
        if (!(forms[i].hi || forms[i].lo) ||
            (!variables[i] && !same(forms[i], exemplar->tokens[i].form_id))) goto done;
    result = 1;
done:
    free(refs);
    free(variables);
    return result;
}
