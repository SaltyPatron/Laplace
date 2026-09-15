#include "laplace/core/ud_parse.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

void laplace_ud_markers_init(laplace_ud_markers_t *out) {
    if (!out) return;
    /* Hash128.OfCanonical hashes these exact UTF-8 bytes with BLAKE3-128.
     * It does not apply identity-key normalization or compose a text tree. */
    hash128_blake3_str("ud/parse/schema/v1", &out->schema_v1);
    hash128_blake3_str("ud/parse/none/v1", &out->none);
    hash128_blake3_str("ud/parse/root/v1", &out->root);
    hash128_blake3_str("ud/parse/present/v1", &out->present);
    hash128_blake3_str("ud/parse/features-end/v1", &out->features_end);
    hash128_blake3_str("ud/parse/enhanced-end/v1", &out->enhanced_end);
    hash128_blake3_str("ud/parse/misc-end/v1", &out->misc_end);
    hash128_blake3_str("ud/parse/tokens-end/v1", &out->tokens_end);
    hash128_blake3_str("ud/parse/mwt-end/v1", &out->mwt_end);
}

static int equal(hash128_t a, hash128_t b) {
    return a.hi == b.hi && a.lo == b.lo;
}

static int reserved(hash128_t id, const laplace_ud_markers_t *m) {
    return equal(id, m->schema_v1) || equal(id, m->none)
        || equal(id, m->root) || equal(id, m->present)
        || equal(id, m->features_end) || equal(id, m->enhanced_end)
        || equal(id, m->misc_end) || equal(id, m->tokens_end)
        || equal(id, m->mwt_end);
}

static int identity(hash128_t id, const laplace_ud_markers_t *m) {
    return (id.hi || id.lo) && !reserved(id, m);
}

static int optional_identity(hash128_t id, const laplace_ud_markers_t *m) {
    return equal(id, m->none) || identity(id, m);
}

typedef enum { FEATURE_PAIRS, ENHANCED_PAIRS, MISC_PAIRS } pair_kind_t;

static int read_pairs(const hash128_t *flat, size_t count, size_t *cursor,
                      hash128_t end, pair_kind_t kind,
                      const laplace_ud_markers_t *m, laplace_ud_pairs_t *out) {
    size_t start = *cursor;
    while (*cursor < count && !equal(flat[*cursor], end)) {
        hash128_t left, right;
        if (count - *cursor < 2) return 0;
        left = flat[*cursor];
        right = flat[*cursor + 1];
        if (kind == ENHANCED_PAIRS) {
            if ((!equal(left, m->root) && !identity(left, m))
                || !identity(right, m)) return 0;
        } else if (kind == MISC_PAIRS) {
            if (!identity(left, m)
                || (!optional_identity(right, m) && !equal(right, m->present)))
                return 0;
        } else if (!identity(left, m) || !identity(right, m)) {
            return 0;
        }
        *cursor += 2;
    }
    if (*cursor == count) return 0;
    out->items = flat + start;
    out->count = (*cursor - start) / 2;
    ++*cursor;
    return 1;
}

/* Two passes: first validate the complete grammar/count exact record storage;
 * then populate. A malformed suffix cannot return an executable prefix. */
static int read_structure(const hash128_t *flat, size_t count,
                          const laplace_ud_markers_t *m,
                          laplace_ud_parse_t *parse, int populate) {
    size_t cursor = 3, token_count = 0, mwt_count = 0;
    while (cursor < count && !equal(flat[cursor], m->tokens_end)) {
        laplace_ud_token_t token;
        if (count - cursor < 5) return 0;
        token.ref_id = flat[cursor++];
        token.form_id = flat[cursor++];
        token.lemma_id = flat[cursor++];
        token.upos_id = flat[cursor++];
        token.xpos_id = flat[cursor++];
        if (!identity(token.ref_id, m) || !identity(token.form_id, m)
            || !identity(token.lemma_id, m)
            || !optional_identity(token.upos_id, m)
            || !optional_identity(token.xpos_id, m)) return 0;
        if (!read_pairs(flat, count, &cursor, m->features_end, FEATURE_PAIRS,
                        m, &token.features) || count - cursor < 2) return 0;
        token.head_ref_id = flat[cursor++];
        token.deprel_id = flat[cursor++];
        if ((!optional_identity(token.head_ref_id, m)
             && !equal(token.head_ref_id, m->root))
            || !optional_identity(token.deprel_id, m)) return 0;
        if (!read_pairs(flat, count, &cursor, m->enhanced_end, ENHANCED_PAIRS,
                        m, &token.enhanced)
            || !read_pairs(flat, count, &cursor, m->misc_end, MISC_PAIRS,
                           m, &token.misc)) return 0;
        if (populate) parse->tokens[token_count] = token;
        ++token_count;
    }
    if (cursor == count) return 0;
    ++cursor; /* tokens_end */
    while (cursor < count) {
        laplace_ud_mwt_t mwt;
        if (count - cursor < 3) return 0;
        mwt.start_ref_id = flat[cursor++];
        mwt.end_ref_id = flat[cursor++];
        mwt.form_id = flat[cursor++];
        if (!identity(mwt.start_ref_id, m) || !identity(mwt.end_ref_id, m)
            || !identity(mwt.form_id, m)
            || !read_pairs(flat, count, &cursor, m->mwt_end, MISC_PAIRS,
                           m, &mwt.misc)) return 0;
        if (populate) parse->mwts[mwt_count] = mwt;
        ++mwt_count;
    }
    parse->token_count = token_count;
    parse->mwt_count = mwt_count;
    return 1;
}

typedef struct {
    hash128_t id;
    size_t ordinal;
} ref_index_t;

static int compare_ref(const void *left, const void *right) {
    const ref_index_t *a = (const ref_index_t *)left;
    const ref_index_t *b = (const ref_index_t *)right;
    return memcmp(&a->id, &b->id, sizeof(hash128_t));
}

static size_t ref_ordinal(const ref_index_t *refs, size_t count, hash128_t id) {
    ref_index_t key;
    const ref_index_t *found;
    if (!count) return SIZE_MAX;
    key.id = id;
    key.ordinal = 0;
    found = (const ref_index_t *)bsearch(&key, refs, count, sizeof(*refs), compare_ref);
    return found ? found->ordinal : SIZE_MAX;
}

static laplace_ud_parse_status_t validate_references(
    const laplace_ud_parse_t *parse, const laplace_ud_markers_t *m) {
    size_t n = parse->token_count;
    ref_index_t *refs;
    laplace_ud_parse_status_t status = LAPLACE_UD_PARSE_MALFORMED;
    if (n > SIZE_MAX / sizeof(*refs)) return LAPLACE_UD_PARSE_MEMORY;
    refs = n ? (ref_index_t *)malloc(n * sizeof(*refs)) : NULL;
    if (n && !refs) return LAPLACE_UD_PARSE_MEMORY;
    for (size_t i = 0; i < n; ++i) {
        refs[i].id = parse->tokens[i].ref_id;
        refs[i].ordinal = i;
    }
    if (n) qsort(refs, n, sizeof(*refs), compare_ref);
    for (size_t i = 1; i < n; ++i)
        if (equal(refs[i - 1].id, refs[i].id)) goto done;
    for (size_t i = 0; i < n; ++i) {
        const laplace_ud_token_t *token = &parse->tokens[i];
        if (!equal(token->head_ref_id, m->none)
            && !equal(token->head_ref_id, m->root)) {
            size_t head = ref_ordinal(refs, n, token->head_ref_id);
            if (head == SIZE_MAX || head == i) goto done;
        }
        for (size_t j = 0; j < token->enhanced.count; ++j) {
            hash128_t head = token->enhanced.items[j * 2];
            if (!equal(head, m->root) && ref_ordinal(refs, n, head) == SIZE_MAX)
                goto done;
        }
    }
    for (size_t i = 0; i < parse->mwt_count; ++i) {
        size_t start = ref_ordinal(refs, n, parse->mwts[i].start_ref_id);
        size_t end = ref_ordinal(refs, n, parse->mwts[i].end_ref_id);
        if (start == SIZE_MAX || end == SIZE_MAX || start >= end) goto done;
    }
    status = LAPLACE_UD_PARSE_OK;
done:
    free(refs);
    return status;
}

void laplace_ud_parse_free(laplace_ud_parse_t *parse) {
    if (!parse) return;
    free(parse->tokens);
    free(parse->mwts);
    memset(parse, 0, sizeof(*parse));
}

laplace_ud_parse_status_t laplace_ud_parse_decode(
    const hash128_t *flat, size_t count, laplace_ud_parse_t *out) {
    laplace_ud_markers_t markers;
    laplace_ud_parse_t parse = {0};
    laplace_ud_parse_status_t status;
    if (!out) return LAPLACE_UD_PARSE_ARGUMENT;
    memset(out, 0, sizeof(*out));
    if (!flat || count > SIZE_MAX / sizeof(*flat)) return LAPLACE_UD_PARSE_ARGUMENT;
    if (count < 4) return LAPLACE_UD_PARSE_MALFORMED;
    laplace_ud_markers_init(&markers);
    if (!equal(flat[0], markers.schema_v1)) return LAPLACE_UD_PARSE_SCHEMA;
    if (!optional_identity(flat[1], &markers) || !identity(flat[2], &markers))
        return LAPLACE_UD_PARSE_MALFORMED;
    parse.sentence_id = flat[1];
    parse.language_id = flat[2];
    if (!read_structure(flat, count, &markers, &parse, 0))
        return LAPLACE_UD_PARSE_MALFORMED;
    if (parse.token_count > SIZE_MAX / sizeof(*parse.tokens)
        || parse.mwt_count > SIZE_MAX / sizeof(*parse.mwts))
        return LAPLACE_UD_PARSE_MEMORY;
    if (parse.token_count)
        parse.tokens = (laplace_ud_token_t *)malloc(parse.token_count * sizeof(*parse.tokens));
    if (parse.mwt_count)
        parse.mwts = (laplace_ud_mwt_t *)malloc(parse.mwt_count * sizeof(*parse.mwts));
    if ((parse.token_count && !parse.tokens) || (parse.mwt_count && !parse.mwts)) {
        laplace_ud_parse_free(&parse);
        return LAPLACE_UD_PARSE_MEMORY;
    }
    if (!read_structure(flat, count, &markers, &parse, 1)) {
        laplace_ud_parse_free(&parse);
        return LAPLACE_UD_PARSE_MALFORMED;
    }
    status = validate_references(&parse, &markers);
    if (status != LAPLACE_UD_PARSE_OK) {
        laplace_ud_parse_free(&parse);
        return status;
    }
    *out = parse;
    return LAPLACE_UD_PARSE_OK;
}
