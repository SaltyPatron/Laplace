#include "laplace/core/ud_parse.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define CHECK(expression) do { \
    if (!(expression)) { \
        fprintf(stderr, "%s:%d: %s\n", __FILE__, __LINE__, #expression); \
        exit(EXIT_FAILURE); \
    } \
} while (0)

static int eq(hash128_t a, hash128_t b) {
    return a.hi == b.hi && a.lo == b.lo;
}

/* Opaque fixture identities, deliberately unrelated to English word labels. */
static hash128_t id(uint64_t value) {
    hash128_t result = {UINT64_C(0x29ab778cee9d2201), value};
    return result;
}

typedef struct {
    hash128_t flat[128];
    size_t count;
    size_t first_token;
    size_t second_token;
    size_t third_token;
    size_t feature_end;
    size_t enhanced_head;
    size_t enhanced_end;
    size_t misc_end;
    size_t tokens_end;
    size_t mwt;
    size_t mwt_end;
} fixture_t;

static fixture_t fixture(const laplace_ud_markers_t *m) {
    fixture_t f = {0};
#define ADD(value) f.flat[f.count++] = (value)
    ADD(m->schema_v1); ADD(id(1)); ADD(id(2));
    f.first_token = f.count;
    ADD(id(10)); ADD(id(20)); ADD(id(21)); ADD(id(30)); ADD(id(31));
    ADD(id(40)); ADD(id(41)); ADD(id(42)); ADD(id(43));
    f.feature_end = f.count; ADD(m->features_end);
    ADD(m->root); ADD(id(50));
    f.enhanced_head = f.count; ADD(m->root); ADD(id(51));
    f.enhanced_end = f.count; ADD(m->enhanced_end);
    ADD(id(60)); ADD(m->present); ADD(id(61)); ADD(m->none);
    f.misc_end = f.count; ADD(m->misc_end);
    f.second_token = f.count;
    ADD(id(11)); ADD(id(22)); ADD(id(23)); ADD(m->none); ADD(m->none);
    ADD(m->features_end); ADD(id(10)); ADD(id(52));
    ADD(id(10)); ADD(id(53)); ADD(m->enhanced_end); ADD(m->misc_end);
    f.third_token = f.count;
    /* Repeated surface with a distinct reference, and absent basic annotation
     * as emitted for an empty node. Enhanced annotation remains addressable. */
    ADD(id(12)); ADD(id(22)); ADD(id(23)); ADD(id(30)); ADD(m->none);
    ADD(m->features_end); ADD(m->none); ADD(m->none);
    ADD(id(11)); ADD(id(54)); ADD(m->enhanced_end); ADD(m->misc_end);
    f.tokens_end = f.count; ADD(m->tokens_end);
    f.mwt = f.count;
    ADD(id(10)); ADD(id(11)); ADD(id(24)); ADD(id(62)); ADD(m->present);
    f.mwt_end = f.count; ADD(m->mwt_end);
#undef ADD
    return f;
}

static void require_empty(const laplace_ud_parse_t *parse) {
    CHECK(!parse->tokens && !parse->mwts);
    CHECK(parse->token_count == 0 && parse->mwt_count == 0);
    CHECK(parse->sentence_id.hi == 0 && parse->sentence_id.lo == 0);
    CHECK(parse->language_id.hi == 0 && parse->language_id.lo == 0);
}

static void reject(fixture_t *f) {
    laplace_ud_parse_t parsed;
    memset(&parsed, 0xa5, sizeof(parsed));
    CHECK(laplace_ud_parse_decode(f->flat, f->count, &parsed)
          != LAPLACE_UD_PARSE_OK);
    require_empty(&parsed);
    laplace_ud_parse_free(&parsed);
}

static void test_preserve_every_field(const laplace_ud_markers_t *m) {
    fixture_t f = fixture(m);
    laplace_ud_parse_t parsed;
    const laplace_ud_token_t *t;
    CHECK(laplace_ud_parse_decode(f.flat, f.count, &parsed) == LAPLACE_UD_PARSE_OK);
    CHECK(eq(parsed.sentence_id, id(1)) && eq(parsed.language_id, id(2)));
    CHECK(parsed.token_count == 3 && parsed.mwt_count == 1);
    t = &parsed.tokens[0];
    CHECK(eq(t->ref_id, id(10)) && eq(t->form_id, id(20)));
    CHECK(eq(t->lemma_id, id(21)) && eq(t->upos_id, id(30)));
    CHECK(eq(t->xpos_id, id(31)) && eq(t->head_ref_id, m->root));
    CHECK(eq(t->deprel_id, id(50)));
    CHECK(t->features.count == 2 && t->features.items == f.flat + f.first_token + 5);
    CHECK(eq(t->features.items[0], id(40)) && eq(t->features.items[1], id(41)));
    CHECK(eq(t->features.items[2], id(42)) && eq(t->features.items[3], id(43)));
    CHECK(t->enhanced.count == 1 && t->enhanced.items == f.flat + f.enhanced_head);
    CHECK(eq(t->enhanced.items[0], m->root) && eq(t->enhanced.items[1], id(51)));
    CHECK(t->misc.count == 2);
    CHECK(eq(t->misc.items[0], id(60)) && eq(t->misc.items[1], m->present));
    CHECK(eq(t->misc.items[2], id(61)) && eq(t->misc.items[3], m->none));
    CHECK(eq(parsed.tokens[1].head_ref_id, id(10)));
    CHECK(eq(parsed.tokens[1].upos_id, m->none));
    CHECK(eq(parsed.tokens[1].xpos_id, m->none));
    CHECK(parsed.tokens[1].features.count == 0 && parsed.tokens[1].misc.count == 0);
    CHECK(eq(parsed.tokens[1].form_id, parsed.tokens[2].form_id));
    CHECK(!eq(parsed.tokens[1].ref_id, parsed.tokens[2].ref_id));
    CHECK(eq(parsed.tokens[2].head_ref_id, m->none));
    CHECK(eq(parsed.tokens[2].enhanced.items[0], id(11)));
    CHECK(eq(parsed.mwts[0].start_ref_id, id(10)));
    CHECK(eq(parsed.mwts[0].end_ref_id, id(11)));
    CHECK(eq(parsed.mwts[0].form_id, id(24)));
    CHECK(parsed.mwts[0].misc.count == 1);
    CHECK(eq(parsed.mwts[0].misc.items[0], id(62)));
    CHECK(eq(parsed.mwts[0].misc.items[1], m->present));
    laplace_ud_parse_free(&parsed);
    require_empty(&parsed);
    laplace_ud_parse_free(&parsed);
}

static void test_invalid_structures(const laplace_ud_markers_t *m) {
    fixture_t original = fixture(m);
    fixture_t f;
    laplace_ud_parse_t parsed;
    /* Every cut inside a record fails atomically. A complete token-only
     * document is valid by schema-v1 and must be bounded by caller identity. */
    for (size_t cut = 0; cut < original.count; ++cut) {
        if (cut == original.tokens_end + 1) continue;
        f = original; f.count = cut; reject(&f);
    }
    f = original; f.flat[0] = id(999);
    CHECK(laplace_ud_parse_decode(f.flat, f.count, &parsed) == LAPLACE_UD_PARSE_SCHEMA);
    require_empty(&parsed);
    size_t markers[] = {original.feature_end, original.enhanced_end,
                        original.misc_end, original.tokens_end, original.mwt_end};
    for (size_t i = 0; i < sizeof(markers) / sizeof(markers[0]); ++i) {
        f = original; f.flat[markers[i]] = m->schema_v1; reject(&f);
    }
    f = original; f.flat[f.second_token] = f.flat[f.first_token]; reject(&f);
    f = original; f.flat[f.first_token + 1] = (hash128_t){0, 0}; reject(&f);
    f = original; f.flat[f.first_token + 1] = m->none; reject(&f);
    f = original; f.flat[f.first_token + 2] = m->features_end; reject(&f);
    f = original; f.flat[f.first_token + 5] = m->root; reject(&f);
    f = original; f.flat[f.first_token + 6] = m->features_end; reject(&f);
    f = original; f.flat[f.second_token + 6] = id(999); reject(&f);
    f = original; f.flat[f.second_token + 6] = id(11); reject(&f);
    f = original; f.flat[f.enhanced_head] = id(999); reject(&f);
    f = original; f.flat[f.enhanced_head] = m->none; reject(&f);
    f = original; f.flat[f.enhanced_head + 1] = m->enhanced_end; reject(&f);
    f = original; f.flat[f.mwt] = id(999); reject(&f);
    f = original; f.flat[f.mwt + 1] = id(10); reject(&f);
    f = original; f.flat[f.mwt] = id(11); f.flat[f.mwt + 1] = id(10); reject(&f);
    f = original; f.flat[f.mwt + 2] = (hash128_t){0, 0}; reject(&f);
    f = original; f.flat[f.count++] = id(999); reject(&f);
    CHECK(laplace_ud_parse_decode(NULL, 0, &parsed) == LAPLACE_UD_PARSE_ARGUMENT);
    require_empty(&parsed);
    CHECK(laplace_ud_parse_decode(original.flat, SIZE_MAX, &parsed)
          == LAPLACE_UD_PARSE_ARGUMENT);
    require_empty(&parsed);
    CHECK(laplace_ud_parse_decode(original.flat, original.count, NULL)
          == LAPLACE_UD_PARSE_ARGUMENT);
}

static void test_ids_are_language_independent(const laplace_ud_markers_t *m) {
    fixture_t a = fixture(m), b = fixture(m);
    laplace_ud_parse_t first, second;
    b.flat[1] = id(101); b.flat[2] = id(102);
    b.flat[b.first_token + 1] = id(120); b.flat[b.first_token + 2] = id(121);
    b.flat[b.second_token + 1] = id(122); b.flat[b.second_token + 2] = id(123);
    b.flat[b.third_token + 1] = id(122); b.flat[b.third_token + 2] = id(123);
    b.flat[b.mwt + 2] = id(124);
    CHECK(laplace_ud_parse_decode(a.flat, a.count, &first) == LAPLACE_UD_PARSE_OK);
    CHECK(laplace_ud_parse_decode(b.flat, b.count, &second) == LAPLACE_UD_PARSE_OK);
    CHECK(!eq(first.sentence_id, second.sentence_id));
    CHECK(!eq(first.language_id, second.language_id));
    for (size_t i = 0; i < first.token_count; ++i) {
        CHECK(!eq(first.tokens[i].form_id, second.tokens[i].form_id));
        CHECK(eq(first.tokens[i].ref_id, second.tokens[i].ref_id));
        CHECK(eq(first.tokens[i].head_ref_id, second.tokens[i].head_ref_id));
        CHECK(eq(first.tokens[i].deprel_id, second.tokens[i].deprel_id));
    }
    laplace_ud_parse_free(&first);
    laplace_ud_parse_free(&second);
}

static void test_logical_reference_ordinals(const laplace_ud_markers_t *m) {
    /* Logical token order is size_t, not a 16-bit packed-coordinate ordinal. */
    const size_t tokens = 65537;
    size_t count = tokens * 10 + 4, cursor = 0;
    hash128_t *flat = (hash128_t *)malloc(count * sizeof(*flat));
    laplace_ud_parse_t parsed;
    CHECK(flat != NULL);
    flat[cursor++] = m->schema_v1; flat[cursor++] = m->none; flat[cursor++] = id(2);
    for (size_t i = 0; i < tokens; ++i) {
        flat[cursor++] = id(1000 + i);
        flat[cursor++] = id(20); flat[cursor++] = id(20);
        flat[cursor++] = m->none; flat[cursor++] = m->none;
        flat[cursor++] = m->features_end;
        flat[cursor++] = i ? id(1000 + i - 1) : m->root;
        flat[cursor++] = id(50);
        flat[cursor++] = m->enhanced_end; flat[cursor++] = m->misc_end;
    }
    flat[cursor++] = m->tokens_end;
    CHECK(cursor == count);
    CHECK(laplace_ud_parse_decode(flat, count, &parsed) == LAPLACE_UD_PARSE_OK);
    CHECK(parsed.token_count == tokens && parsed.mwt_count == 0);
    CHECK(eq(parsed.tokens[tokens - 1].ref_id, id(1000 + tokens - 1)));
    CHECK(eq(parsed.tokens[tokens - 1].head_ref_id, id(1000 + tokens - 2)));
    laplace_ud_parse_free(&parsed);
    free(flat);
}

int main(void) {
    laplace_ud_markers_t markers;
    laplace_ud_markers_init(&markers);
    test_preserve_every_field(&markers);
    test_invalid_structures(&markers);
    test_ids_are_language_independent(&markers);
    test_logical_reference_ordinals(&markers);
    puts("UD schema-v1 decoder: field fidelity, malformed input, scoped references, language identities and logical ordinals passed");
    return EXIT_SUCCESS;
}
