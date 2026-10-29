/* Adapt Native's actual composition events to the existing contiguous-child
 * tree ABI. Unary layout bridges retain their child's identity/coordinate and
 * are collapsed by the existing emitter; they never become substrate rows. */
#include "laplace/core/text_decomposer.h"
#include "laplace/core/text_identity.h"
#include "laplace/tier0.h"
#include <limits.h>
#include <stdlib.h>
#include <string.h>

typedef struct { size_t first; uint32_t count, bytes; } Definition;
typedef struct { lp_id id; uint32_t offset, bytes, node; } Segment;
typedef struct {
    lp_ref value;
    uint32_t first, count, offset, bytes, atom;
} Occurrence;
typedef struct {
    const lp_tier0_record *floor;
    lp_idmap *definitions;
    lp_ref *children;
    size_t count, capacity;
    Segment *segments;
    size_t segment_count, segment_capacity;
    int error;
} Recording;

static int room(void **buffer, size_t *capacity, size_t need, size_t stride) {
    if (need <= *capacity) return 1;
    size_t n = *capacity ? *capacity : 64;
    while (n < need) { if (n > SIZE_MAX / 2) return 0; n *= 2; }
    if (n > SIZE_MAX / stride) return 0;
    void *next = realloc(*buffer, n * stride);
    if (!next) return 0;
    *buffer = next; *capacity = n; return 1;
}

static uint32_t content_bytes(Recording *recording, const lp_ref *value, uint32_t *atom) {
    Definition *definition = lp_idmap_lookup(recording->definitions, &value->id);
    if (definition) return definition->bytes;
    int64_t cp = lp_tier0_codepoint(recording->floor, &value->id);
    if (cp < 0) { recording->error = -7; return 0; }
    uint8_t utf8[4]; if (atom) *atom = (uint32_t)cp;
    return (uint32_t)lp_utf8_put((uint32_t)cp, utf8);
}

static void record_segment(void *sink, size_t offset, size_t bytes, const lp_ref *value) {
    Recording *recording = sink;
    if (recording->error) return;
    if (!room((void **)&recording->segments, &recording->segment_capacity,
              recording->segment_count + 1, sizeof(Segment))) { recording->error = -3; return; }
    recording->segments[recording->segment_count++] =
        (Segment){value->id, (uint32_t)offset, (uint32_t)bytes, TIER_TREE_INVALID};
}

static lp_ref record_composition(void *sink, const lp_ref *children, uint32_t count, uint8_t tier) {
    Recording *recording = sink;
    lp_ref value = lp_ref_compose(children, count, tier);
    if (recording->error || lp_idmap_lookup(recording->definitions, &value.id)) return value;
    if (!room((void **)&recording->children, &recording->capacity,
              recording->count + count, sizeof *children)) { recording->error = -3; return value; }
    uint64_t bytes = 0;
    for (uint32_t i = 0; i < count; i++) bytes += content_bytes(recording, children + i, NULL);
    if (recording->error || bytes > INT32_MAX) { recording->error = -7; return value; }
    Definition *definition = lp_idmap_get(recording->definitions, &value.id, NULL);
    *definition = (Definition){ recording->count, count, (uint32_t)bytes };
    memcpy(recording->children + recording->count, children, count * sizeof *children);
    recording->count += count;
    return value;
}

static size_t frontier(const Occurrence *nodes, uint32_t level, uint32_t *cut, uint32_t *stack) {
    size_t top = 1, count = 0; stack[0] = 0;
    while (top) {
        uint32_t index = stack[--top]; const Occurrence *node = nodes + index;
        if (node->value.tier <= level) cut[count++] = index;
        else for (uint32_t i = node->count; i; i--) stack[top++] = node->first + i - 1;
    }
    return count;
}

int native_text_tree_build(const uint8_t *utf8, size_t length, tier_tree_t **out_tree) {
    if (!out_tree || (!utf8 && length) || length > INT32_MAX) return -1;
    *out_tree = NULL;
    if (!length) { *out_tree = tier_tree_new(0); return *out_tree ? 0 : -3; }
    for (size_t i = 0; i < length;) {
        size_t start = i; uint32_t cp; uint8_t canonical[4];
        if (!lp_utf8_next(utf8, length, &i, &cp) || !lp_unicode_scalar(cp)) return -2;
        size_t size = lp_utf8_put(cp, canonical);
        if (i - start != size || memcmp(utf8 + start, canonical, size)) return -2;
    }
    lp_text *text = text_identity_thread_context();
    const lp_tier0_record *floor = text_identity_scalar_floor();
    if (!text || !floor) return -3;
    Recording recording = { .floor = floor, .definitions = lp_idmap_sized(sizeof(Definition)) };
    lp_ref root = lp_text_decompose_segments(text, utf8, length,
        record_composition, &recording, record_segment, &recording);
    int error = recording.error;
    Occurrence *nodes = NULL; size_t count = 0, capacity = 0;
    uint32_t *previous = NULL, *current = NULL, *stack = NULL;
    tier_tree_t *tree = NULL;
    if (error) goto done;
    if (!room((void **)&nodes, &capacity, 1, sizeof *nodes)) { error = -3; goto done; }
    nodes[count++] = (Occurrence){ .value = root, .bytes = (uint32_t)length };
    for (size_t i = 0; i < count; i++) {
        Occurrence parent = nodes[i];
        Definition *definition = lp_idmap_lookup(recording.definitions, &parent.value.id);
        if (!definition) { content_bytes(&recording, &parent.value, &nodes[i].atom); continue; }
        Definition def = *definition;
        if (count + def.count > UINT32_MAX ||
            !room((void **)&nodes, &capacity, count + def.count, sizeof *nodes)) { error = -3; goto done; }
        nodes[i].first = (uint32_t)count; nodes[i].count = def.count;
        uint32_t offset = parent.offset;
        for (uint32_t child = 0; child < def.count; child++) {
            lp_ref value = recording.children[def.first + child];
            uint32_t bytes = content_bytes(&recording, &value, NULL);
            nodes[count++] = (Occurrence){ .value = value, .offset = offset, .bytes = bytes };
            offset += bytes;
        }
        if (offset != parent.offset + parent.bytes) { error = -7; goto done; }
    }
    if (recording.error) { error = recording.error; goto done; }
    previous = malloc(count * sizeof *previous); current = malloc(count * sizeof *current);
    stack = malloc(count * sizeof *stack); tree = tier_tree_new(count);
    if (!previous || !current || !stack || !tree) { error = -3; goto done; }
    uint8_t *owned = malloc(length);
    if (!owned) { error = -3; goto done; }
    memcpy(owned, utf8, length); tier_tree_set_text(tree, owned, length);
    uint32_t previous_base = 0; size_t previous_count = 0;
    for (uint32_t level = 0; level <= root.tier; level++) {
        size_t size = frontier(nodes, level, current, stack), child = 0;
        uint32_t base = (uint32_t)tier_tree_node_count(tree);
        size_t segment_at = 0;
        for (size_t i = 0; i < size; i++) {
            const Occurrence *node = nodes + current[i]; uint32_t index;
            if (!level) index = tier_tree_add_leaf(tree, 0, node->atom, node->offset, node->bytes);
            else {
                size_t first = child; uint32_t end = node->offset + node->bytes;
                while (child < previous_count && nodes[previous[child]].offset < end) child++;
                if (first == child || nodes[previous[first]].offset != node->offset) { error = -7; goto done; }
                index = tier_tree_add_node(tree, (uint8_t)level, previous_base + (uint32_t)first,
                                          (uint32_t)(child - first), node->offset, node->bytes);
            }
            if (index == TIER_TREE_INVALID) { error = -3; goto done; }
            double coord[4]; lp_coord_xyzm(&node->value.c, coord);
            hilbert128_t hilbert; hilbert4d_encode(coord, &hilbert);
            tier_tree_set_id(tree, index, (const hash128_t *)&node->value.id);
            tier_tree_set_coord(tree, index, coord); tier_tree_set_hilbert(tree, index, &hilbert);
            while (segment_at < recording.segment_count && recording.segments[segment_at].offset < node->offset)
                segment_at++;
            if (segment_at < recording.segment_count) {
                Segment *segment = recording.segments + segment_at;
                if (segment->node == TIER_TREE_INVALID && segment->offset == node->offset
                    && segment->bytes == node->bytes && lp_id_eq(&segment->id, &node->value.id))
                    segment->node = index;
            }
        }
        previous_count = size; previous_base = base;
        uint32_t *swap = previous; previous = current; current = swap;
    }
    tier_tree_finalize(tree);
    for (size_t i = 0; i < recording.segment_count; i++) {
        if (recording.segments[i].node == TIER_TREE_INVALID) { error = -7; goto done; }
        stack[i] = recording.segments[i].node;
    }
    if (tier_tree_set_word_segments(tree, stack, recording.segment_count)) { error = -7; goto done; }
    if (laplace_text_decomposer_validate_roundtrip(tree)) { error = -7; goto done; }
    tier_tree_mark_native_text(tree); *out_tree = tree; tree = NULL;
done:
    tier_tree_free(tree); free(nodes); free(previous); free(current); free(stack);
    free(recording.children); free(recording.segments); lp_idmap_free(recording.definitions);
    return error;
}
