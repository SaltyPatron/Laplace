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

static void encode_slot_v2(hash128_t *at, hash128_t ref, hash128_t type, hash128_t mode) {
    laplace_task_shape_markers_extended_t markers;
    hash128_t parts[4];
    laplace_task_shape_markers_extended_init(&markers);
    parts[0] = markers.slot_schema_v2; parts[1] = ref; parts[2] = type; parts[3] = mode;
    hash128_merkle(4, parts, 4, at);
    at[1] = ref; at[2] = type; at[3] = mode;
}

static void make_shape_v2(hash128_t *flat, laplace_task_shape_view_t *shape, hash128_t mode) {
    laplace_task_shape_markers_extended_t markers;
    laplace_task_shape_markers_extended_init(&markers);
    flat[0] = markers.schema_v2; flat[1] = id(1); flat[2] = id(2);
    encode_slot_v2(flat + 3, id(101), id(900), mode);
    flat[7] = markers.end_v2;
    CHECK(laplace_task_shape_decode_view(flat, 8, shape) == 0);
}

static void test_legacy_abi_boundaries(void) {
    struct { laplace_task_shape_markers_t value; unsigned char after[16]; } markers;
    struct { laplace_task_shape_t value; unsigned char after[16]; } legacy;
    laplace_task_shape_markers_extended_t extended;
    laplace_task_shape_view_t view;
    hash128_t flat[8];
    _Static_assert(sizeof(laplace_task_shape_markers_t) == 3 * sizeof(hash128_t),
                   "the original marker ABI must remain three IDs");
    _Static_assert(sizeof(laplace_task_shape_t) == 2 * sizeof(hash128_t) +
                   sizeof(const hash128_t *) + sizeof(size_t),
                   "the original borrowed-view ABI must not gain a stride field");
    memset(&markers, 0xa7, sizeof(markers));
    memset(&legacy, 0xa7, sizeof(legacy));
    laplace_task_shape_markers_init(&markers.value);
    laplace_task_shape_markers_extended_init(&extended);
    CHECK(memcmp(&markers.value, &extended, sizeof(markers.value)) == 0);
    make_shape(flat, &legacy.value);
    CHECK(laplace_task_shape_decode_view(flat, 7, &view) == 0);
    CHECK(view.slot_stride == 3 && view.slots == legacy.value.slots);
    CHECK(view.slot_count == legacy.value.slot_count);
    make_shape_v2(flat, &view, extended.current_form);
    CHECK(laplace_task_shape_decode(flat, 8, &legacy.value) == -1);
    CHECK(legacy.value.slots == NULL && legacy.value.slot_count == 0);
    CHECK(laplace_task_shape_decode(flat, 0, &legacy.value) != 0);
    for (size_t i = 0; i < sizeof(markers.after); ++i) {
        CHECK(markers.after[i] == 0xa7);
        CHECK(legacy.after[i] == 0xa7);
    }
}

static void test_v2_codec(void) {
    laplace_task_shape_markers_extended_t m;
    laplace_task_shape_view_t shape;
    hash128_t flat[13], expected, current_slot, current_shape, semantic_shape;
    laplace_task_shape_markers_extended_init(&m);
    const char *names[] = {
        "laplace/task-shape/relation-read/token-slots/v2",
        "laplace/task-shape/token-slot/v2", "laplace/task-shape/slots-end/v2",
        "laplace/task-shape/binding/current-form/v1",
        "laplace/task-shape/binding/witnessed-semantic/v1"
    };
    hash128_t markers[] = {m.schema_v2, m.slot_schema_v2, m.end_v2,
                           m.current_form, m.witnessed_semantic};
    for (size_t i = 0; i < sizeof(markers) / sizeof(markers[0]); ++i) {
        hash128_blake3_str(names[i], &expected);
        CHECK(memcmp(&expected, markers + i, sizeof(expected)) == 0);
    }
    laplace_task_shape_t legacy;
    make_shape(flat, &legacy);
    CHECK(laplace_task_shape_decode_view(flat, 7, &shape) == 0);
    CHECK(shape.slot_stride == 3);
    make_shape_v2(flat, &shape, m.current_form);
    CHECK(shape.slot_stride == 4 && shape.slot_count == 1 && shape.slots == flat + 3);
    CHECK(memcmp(&shape.exemplar_parse, flat + 1, sizeof(hash128_t)) == 0);
    CHECK(memcmp(&shape.predicate, flat + 2, sizeof(hash128_t)) == 0);
    current_slot = flat[3];
    hash128_merkle(4, flat, 8, &current_shape);
    flat[6] = m.witnessed_semantic;
    CHECK(laplace_task_shape_decode_view(flat, 8, &shape) != 0); /* mode is part of slot identity */
    encode_slot_v2(flat + 3, id(101), id(900), m.witnessed_semantic);
    CHECK(laplace_task_shape_decode_view(flat, 8, &shape) == 0);
    CHECK(memcmp(&current_slot, flat + 3, sizeof(hash128_t)) != 0);
    hash128_merkle(4, flat, 8, &semantic_shape);
    CHECK(memcmp(&current_shape, &semantic_shape, sizeof(hash128_t)) != 0);
    for (size_t cut = 0; cut < 8; ++cut)
        CHECK(laplace_task_shape_decode_view(flat, cut, &shape) != 0);
    flat[8] = id(800);
    CHECK(laplace_task_shape_decode_view(flat, 9, &shape) != 0);
    flat[7] = m.end;
    CHECK(laplace_task_shape_decode_view(flat, 8, &shape) != 0);
    make_shape_v2(flat, &shape, m.current_form);
    flat[0] = id(999);
    CHECK(laplace_task_shape_decode_view(flat, 8, &shape) == -1);
    make_shape_v2(flat, &shape, m.current_form);
    flat[11] = flat[7];
    encode_slot_v2(flat + 7, id(101), id(901), m.witnessed_semantic);
    CHECK(laplace_task_shape_decode_view(flat, 12, &shape) != 0); /* duplicate token across modes */
    encode_slot_v2(flat + 7, id(102), id(901), m.witnessed_semantic);
    CHECK(laplace_task_shape_decode_view(flat, 12, &shape) == 0);
    CHECK(shape.slot_count == 2 && shape.slot_stride == 4);
    CHECK(laplace_task_shape_decode_view(flat, SIZE_MAX, &shape) != 0);
    CHECK(laplace_task_shape_decode_view(flat, 12, NULL) != 0);
}

static void test_versioned_reserved_ids(void) {
    laplace_task_shape_markers_extended_t m;
    laplace_task_shape_view_t shape;
    hash128_t flat[8];
    laplace_task_shape_markers_extended_init(&m);
    hash128_t reserved[] = {m.schema, m.slot_schema, m.end, m.schema_v2,
        m.slot_schema_v2, m.end_v2, m.current_form, m.witnessed_semantic, {0, 0}};
    for (size_t i = 0; i < sizeof(reserved) / sizeof(reserved[0]); ++i) {
        for (size_t field = 1; field <= 5; ++field) {
            make_shape_v2(flat, &shape, m.current_form);
            flat[field] = reserved[i];
            if (field == 4 || field == 5)
                encode_slot_v2(flat + 3, flat[4], flat[5], m.current_form);
            CHECK(laplace_task_shape_decode_view(flat, 8, &shape) != 0);
            CHECK(shape.slots == NULL && shape.slot_count == 0 && shape.slot_stride == 0);
        }
        /* Only the two mode identifiers are admitted in a v2 mode field. */
        make_shape_v2(flat, &shape, m.current_form);
        encode_slot_v2(flat + 3, id(101), id(900), reserved[i]);
        int rc = laplace_task_shape_decode_view(flat, 8, &shape);
        CHECK((i == 6 || i == 7) ? rc == 0 : rc != 0);
    }
    make_shape_v2(flat, &shape, m.current_form);
    encode_slot_v2(flat + 3, id(101), id(900), id(9999));
    CHECK(laplace_task_shape_decode_view(flat, 8, &shape) != 0); /* even with a correct slot hash */

    /* V2 markers did not exist in the v1 reserved set. Adding a schema must not
     * retroactively reject an otherwise valid v1 external identifier. */
    for (size_t i = 3; i < 8; ++i) {
        laplace_task_shape_t legacy;
        make_shape(flat, &legacy);
        flat[1] = reserved[i]; flat[2] = reserved[i];
        encode_slot(flat + 3, reserved[i], reserved[i]);
        CHECK(laplace_task_shape_decode_view(flat, 7, &shape) == 0);
        CHECK(shape.slot_stride == 3);
    }
}

static void test_v2_mixed_binding_modes_keep_complete_structure(void) {
    laplace_task_shape_markers_extended_t m;
    laplace_task_shape_view_t shape;
    laplace_ud_parse_t exemplar, current;
    laplace_ud_token_t left[3], right[3];
    hash128_t flat[12], forms[3] = {id(40), id(777), id(888)};
    size_t slots[2] = {SIZE_MAX, SIZE_MAX};
    laplace_task_shape_markers_extended_init(&m);
    make_shape_v2(flat, &shape, m.current_form);
    flat[11] = flat[7];
    encode_slot_v2(flat + 7, id(102), id(901), m.witnessed_semantic);
    CHECK(laplace_task_shape_decode_view(flat, 12, &shape) == 0);
    initialize(&exemplar, left, 100); initialize(&current, right, 200);
    right[1].form_id = forms[1]; right[1].lemma_id = id(5001);
    right[2].form_id = forms[2]; right[2].lemma_id = id(5002);
    CHECK(laplace_task_shape_match_forms_view(&shape, &exemplar, forms, 3, slots) == 1);
    CHECK(slots[0] == 1 && slots[1] == 2);
    CHECK(laplace_task_shape_match_view(&shape, &exemplar, &current, slots) == 1);
    CHECK(slots[0] == 1 && slots[1] == 2);
    right[1].deprel_id = id(9001);
    CHECK(laplace_task_shape_match_view(&shape, &exemplar, &current, slots) == 0);
    forms[0] = id(999);
    CHECK(laplace_task_shape_match_forms_view(&shape, &exemplar, forms, 3, slots) == 0);
}

int main(void) {
    test_codec();
    test_full_structure();
    test_novel_request_projection();
    test_legacy_abi_boundaries();
    test_v2_codec();
    test_versioned_reserved_ids();
    test_v2_mixed_binding_modes_keep_complete_structure();
    puts("task shape codec, exact topology and novel request projection passed");
    puts("task shape v2 binding modes, canonical identities and complete matching passed");
    return EXIT_SUCCESS;
}
