#include "laplace/core/task_shape.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define CHECK(expression) do { if (!(expression)) { \
    fprintf(stderr, "%s:%d: %s\n", __FILE__, __LINE__, #expression); \
    exit(EXIT_FAILURE); } } while (0)

static hash128_t id(uint64_t n) {
    return (hash128_t) {UINT64_C(0x821caf357694ba00), n};
}

static void encode_slot(hash128_t *at, hash128_t ref, hash128_t type) {
    laplace_task_shape_markers_t markers;
    hash128_t parts[3];
    laplace_task_shape_markers_init(&markers);
    parts[0] = markers.slot_schema; parts[1] = ref; parts[2] = type;
    hash128_merkle(4, parts, 3, at);
    at[1] = ref; at[2] = type;
}

static void make_shape(hash128_t *flat, laplace_task_shape_t *shape) {
    laplace_task_shape_markers_t markers;
    laplace_task_shape_markers_init(&markers);
    flat[0] = markers.schema; flat[1] = id(1); flat[2] = id(2);
    encode_slot(flat + 3, id(101), id(900));
    flat[6] = markers.end;
    CHECK(laplace_task_shape_decode(flat, 7, shape) == 0);
}

static void test_codec(void) {
    hash128_t flat[13];
    laplace_task_shape_t shape;
    make_shape(flat, &shape);
    CHECK(shape.slot_count == 1 && shape.slots == flat + 3);
    for (size_t cut = 0; cut < 7; ++cut)
        CHECK(laplace_task_shape_decode(flat, cut, &shape) != 0);
    flat[7] = id(800);
    CHECK(laplace_task_shape_decode(flat, 8, &shape) != 0);
    make_shape(flat, &shape);
    flat[3] = id(888); /* replacing a type requires recomputing slot identity */
    CHECK(laplace_task_shape_decode(flat, 7, &shape) != 0);
    make_shape(flat, &shape);
    flat[9] = flat[6];
    encode_slot(flat + 6, id(101), id(901));
    CHECK(laplace_task_shape_decode(flat, 10, &shape) != 0); /* duplicate token */
    encode_slot(flat + 6, id(102), id(901));
    CHECK(laplace_task_shape_decode(flat, 10, &shape) == 0);
    CHECK(shape.slot_count == 2);
    flat[5] = (hash128_t) {0, 0};
    CHECK(laplace_task_shape_decode(flat, 10, &shape) != 0);
}

static void initialize(laplace_ud_parse_t *parse, laplace_ud_token_t *tokens,
                       uint64_t references) {
    laplace_ud_markers_t m;
    laplace_ud_markers_init(&m);
    memset(tokens, 0, 3 * sizeof(*tokens));
    memset(parse, 0, sizeof(*parse));
    parse->sentence_id = id(references + 1000);
    parse->language_id = id(20);
    parse->tokens = tokens; parse->token_count = 3;
    for (size_t i = 0; i < 3; ++i) {
        tokens[i].ref_id = id(references + i);
        tokens[i].form_id = id(40 + i);
        tokens[i].lemma_id = id(50 + i);
        tokens[i].upos_id = id(60 + i);
        tokens[i].xpos_id = m.none;
        tokens[i].head_ref_id = i ? id(references) : m.root;
        tokens[i].deprel_id = id(70 + i);
    }
}

static void test_full_structure(void) {
    hash128_t flat[13], feature_a[2] = {id(800), id(801)};
    hash128_t feature_b[2] = {id(800), id(802)};
    laplace_task_shape_t shape;
    laplace_ud_parse_t a, b;
    laplace_ud_token_t left[3], right[3];
    size_t slots[3] = {SIZE_MAX, SIZE_MAX, SIZE_MAX};
    make_shape(flat, &shape);
    initialize(&a, left, 100); initialize(&b, right, 200);
    right[1].form_id = id(4001); right[1].lemma_id = id(5001);
    CHECK(laplace_task_shape_match(&shape, &a, &b, slots) == 1);
    CHECK(slots[0] == 1); /* new content, differently scoped token references */
    right[0].form_id = id(4000);
    CHECK(laplace_task_shape_match(&shape, &a, &b, slots) == 0);
    right[0] = left[0]; right[0].ref_id = id(200);
    right[1].deprel_id = id(7001);
    CHECK(laplace_task_shape_match(&shape, &a, &b, slots) == 0);
    right[1].deprel_id = left[1].deprel_id;
    right[1].features = (laplace_ud_pairs_t) {feature_a, 1};
    CHECK(laplace_task_shape_match(&shape, &a, &b, slots) == 0);
    left[1].features = right[1].features;
    CHECK(laplace_task_shape_match(&shape, &a, &b, slots) == 1);
    right[1].features.items = feature_b;
    CHECK(laplace_task_shape_match(&shape, &a, &b, slots) == 0);
    right[1].features.items = feature_a;
    right[2].head_ref_id = id(201); /* same lexemes, different complete topology */
    CHECK(laplace_task_shape_match(&shape, &a, &b, slots) == 0);
    right[2].head_ref_id = id(200);
    b.language_id = id(21);
    CHECK(laplace_task_shape_match(&shape, &a, &b, slots) == 0);
    b.language_id = a.language_id;
    hash128_t enhanced_a[2] = {id(100), id(830)};
    hash128_t enhanced_b[2] = {id(200), id(830)};
    left[1].enhanced = (laplace_ud_pairs_t) {enhanced_a, 1};
    right[1].enhanced = (laplace_ud_pairs_t) {enhanced_b, 1};
    CHECK(laplace_task_shape_match(&shape, &a, &b, slots) == 1);
    enhanced_b[0] = id(202);
    CHECK(laplace_task_shape_match(&shape, &a, &b, slots) == 0);
    enhanced_b[0] = id(200);
    right[1].head_ref_id = id(202); right[2].head_ref_id = id(201);
    CHECK(laplace_task_shape_match(&shape, &a, &b, slots) == 0); /* disconnected cycle */
}

static void test_novel_request_projection(void) {
    hash128_t flat[13], forms[5] = {id(40), id(777), id(42), id(888), id(999)};
    laplace_task_shape_t shape;
    laplace_ud_parse_t exemplar;
    laplace_ud_token_t tokens[3];
    size_t slots[3];
    make_shape(flat, &shape);
    initialize(&exemplar, tokens, 100);
    /* No current parse is provided. Exact declared pattern plus later semantic
     * type binding can instantiate a new request; this is not corpus replay. */
    CHECK(laplace_task_shape_match_forms(&shape, &exemplar, forms, 3, slots) == 1);
    CHECK(slots[0] == 1);
    CHECK(laplace_task_shape_match_forms(&shape, &exemplar, forms, 4, slots) == 0);
    CHECK(laplace_task_shape_match_forms(&shape, &exemplar, forms, 2, slots) == 0);
    forms[0] = id(888); /* changed cue / quote insertion cannot disappear */
    CHECK(laplace_task_shape_match_forms(&shape, &exemplar, forms, 3, slots) == 0);
    forms[0] = id(40); forms[2] = id(888);
    CHECK(laplace_task_shape_match_forms(&shape, &exemplar, forms, 3, slots) == 0);
    forms[2] = id(42);
    laplace_ud_markers_t m;
    laplace_ud_markers_init(&m);
    tokens[1].head_ref_id = m.none;
    CHECK(laplace_task_shape_match_forms(&shape, &exemplar, forms, 3, slots) == 0);
    tokens[1].head_ref_id = id(100);
    flat[9] = flat[6];
    encode_slot(flat + 6, id(102), id(901));
    CHECK(laplace_task_shape_decode(flat, 10, &shape) == 0);
    CHECK(laplace_task_shape_match_forms(&shape, &exemplar, forms, 3, slots) == 1);
    CHECK(slots[0] == 1 && slots[1] == 2);
    flat[12] = flat[9];
    encode_slot(flat + 9, id(100), id(902));
    CHECK(laplace_task_shape_decode(flat, 13, &shape) == 0);
    CHECK(laplace_task_shape_match_forms(&shape, &exemplar, forms, 3, slots) == 0);
}

int main(void) {
    test_codec();
    test_full_structure();
    test_novel_request_projection();
    puts("task shape codec, exact topology and novel request projection passed");
    return EXIT_SUCCESS;
}
