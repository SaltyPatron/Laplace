#include "laplace/core/ud_witness.h"
#include "laplace/core/ud_parse.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

/* The final two coordinates identify the annotation occurrence inside the
 * exact parse. They distinguish recurrence from repeated serialization of one
 * feature/edge. They never salt canonical content or the consensus cell. */
typedef struct {
    hash128_t subject, relation, object;
    size_t token, head;
} ud_claim_t;

typedef struct {
    hash128_t ref;
    size_t token;
} ud_reference_t;

typedef struct {
    ud_claim_t *items;
    size_t count, capacity;
    const laplace_ud_markers_t *markers;
} ud_claims_t;

static int
ud_same_id(const hash128_t *left, const hash128_t *right)
{
    return memcmp(left, right, sizeof(*left)) == 0;
}

static int
ud_reference_compare(const void *left, const void *right)
{
    const ud_reference_t *a = left, *b = right;
    return memcmp(&a->ref, &b->ref, sizeof(a->ref));
}

static int
ud_cell_compare(const ud_claim_t *a, const ud_claim_t *b)
{
    int order = memcmp(&a->subject, &b->subject, sizeof(a->subject));
    if (!order) order = memcmp(&a->relation, &b->relation, sizeof(a->relation));
    if (!order) order = memcmp(&a->object, &b->object, sizeof(a->object));
    return order;
}

static int
ud_claim_compare(const void *left, const void *right)
{
    const ud_claim_t *a = left, *b = right;
    int order = ud_cell_compare(a, b);
    if (order) return order;
    if (a->token != b->token) return a->token < b->token ? -1 : 1;
    if (a->head != b->head) return a->head < b->head ? -1 : 1;
    return 0;
}

static int
ud_claim_add(ud_claims_t *claims,
             const hash128_t *subject, const hash128_t *relation,
             const hash128_t *object, size_t token, size_t head)
{
    ud_claim_t *claim;
    /* None is an absent source annotation, not an observed value. */
    if (ud_same_id(subject, &claims->markers->none) ||
        ud_same_id(relation, &claims->markers->none) ||
        ud_same_id(object, &claims->markers->none)) return 0;
    if (claims->count == claims->capacity) return -3;
    claim = &claims->items[claims->count++];
    claim->subject = *subject;
    claim->relation = *relation;
    claim->object = *object;
    claim->token = token;
    claim->head = head;
    return 0;
}

static int
ud_dependency_add(ud_claims_t *claims, const laplace_ud_parse_t *parse,
                  const ud_reference_t *references, size_t dependent,
                  const hash128_t *head_ref, const hash128_t *relation)
{
    ud_reference_t key;
    const ud_reference_t *head;
    if (ud_same_id(head_ref, &claims->markers->none) ||
        ud_same_id(head_ref, &claims->markers->root) ||
        ud_same_id(relation, &claims->markers->none)) return 0;
    key.ref = *head_ref;
    key.token = 0;
    head = bsearch(&key, references, parse->token_count,
                   sizeof(*references), ud_reference_compare);
    if (!head) return -2;
    /* The UD dependency is head -> dependent, with its actual typed relation.
     * Position and root status remain in the witnessed parse, not word hashes. */
    return ud_claim_add(claims, &parse->tokens[head->token].form_id,
                         relation, &parse->tokens[dependent].form_id,
                         dependent, head->token);
}

int
laplace_ud_witness_build(const hash128_t *flat, size_t flat_count,
                         const hash128_t *source, const hash128_t *parse_occurrence,
                         const hash128_t *language_misc_key,
                         double witness_weight, int64_t now_unix_us,
                         laplace_attestation_staged_t *out, size_t out_capacity,
                         size_t *out_count)
{
    laplace_ud_parse_t parse = {0};
    laplace_ud_markers_t markers;
    hash128_t has_language, has_pos, has_xpos, is_lemma_of;
    ud_reference_t *references = NULL;
    ud_claims_t claims = {0};
    size_t result_count = 0;
    int status = 0;

    if (out_count) *out_count = 0;
    if (!out_count || !flat || !flat_count || !source || !parse_occurrence ||
        !language_misc_key || !out) return -1;
    if (flat_count > SIZE_MAX / sizeof(ud_claim_t)) return -3;
    if (laplace_ud_parse_decode(flat, flat_count, &parse) != LAPLACE_UD_PARSE_OK)
        return -2;
    laplace_ud_markers_init(&markers);
    /* These are governed relation identifiers, not input-language patterns. */
    if (laplace_relation_resolve("HAS_LANGUAGE", &has_language) < 0 ||
        laplace_relation_resolve("HAS_POS", &has_pos) < 0 ||
        laplace_relation_resolve("HAS_XPOS", &has_xpos) < 0 ||
        laplace_relation_resolve("IS_LEMMA_OF", &is_lemma_of) < 0)
    {
        status = -4;
        goto done;
    }
    claims.items = malloc(sizeof(*claims.items) * flat_count);
    claims.capacity = flat_count;
    claims.markers = &markers;
    if (!claims.items || parse.token_count > SIZE_MAX / sizeof(*references))
    {
        status = -3;
        goto done;
    }
    if (parse.token_count)
    {
        references = malloc(sizeof(*references) * parse.token_count);
        if (!references) { status = -3; goto done; }
        for (size_t i = 0; i < parse.token_count; ++i)
        {
            references[i].ref = parse.tokens[i].ref_id;
            references[i].token = i;
        }
        qsort(references, parse.token_count, sizeof(*references), ud_reference_compare);
    }

    for (size_t i = 0; i < parse.token_count; ++i)
    {
        const laplace_ud_token_t *token = &parse.tokens[i];
        int language_override = 0;
        for (size_t j = 0; j < token->misc.count; ++j)
        {
            const hash128_t *key = &token->misc.items[2 * j];
            const hash128_t *value = &token->misc.items[2 * j + 1];
            if (ud_same_id(key, language_misc_key) && !ud_same_id(value, &markers.none))
            {
                language_override = 1;
                status = ud_claim_add(&claims, &token->form_id, &has_language,
                                       value, i, SIZE_MAX);
                if (status) goto done;
            }
        }
        if (!language_override)
        {
            status = ud_claim_add(&claims, &token->form_id, &has_language,
                                   &parse.language_id, i, SIZE_MAX);
            if (status) goto done;
        }
        status = ud_claim_add(&claims, &token->form_id, &has_pos,
                               &token->upos_id, i, SIZE_MAX);
        if (status) goto done;
        status = ud_claim_add(&claims, &token->form_id, &has_xpos,
                               &token->xpos_id, i, SIZE_MAX);
        if (status) goto done;
        /* The common relation law is lemma --IS_LEMMA_OF--> inflected form.
         * An absent lemma previously represented by the form itself cannot
         * become a fabricated self-claim. */
        if (!ud_same_id(&token->lemma_id, &token->form_id))
        {
            status = ud_claim_add(&claims, &token->lemma_id, &is_lemma_of,
                                   &token->form_id, i, SIZE_MAX);
            if (status) goto done;
        }
        for (size_t j = 0; j < token->features.count; ++j)
        {
            status = ud_claim_add(&claims, &token->form_id,
                                   &token->features.items[2 * j],
                                   &token->features.items[2 * j + 1], i, SIZE_MAX);
            if (status) goto done;
        }
        status = ud_dependency_add(&claims, &parse, references, i,
                                    &token->head_ref_id, &token->deprel_id);
        if (status) goto done;
        for (size_t j = 0; j < token->enhanced.count; ++j)
        {
            status = ud_dependency_add(&claims, &parse, references, i,
                                        &token->enhanced.items[2 * j],
                                        &token->enhanced.items[2 * j + 1]);
            if (status) goto done;
        }
    }

    /* MWT surface language is an explicit property of that witnessed surface;
     * its expansion/order is already exact in the parse and is not copied into
     * invented word-to-word relations. */
    for (size_t i = 0; i < parse.mwt_count; ++i)
    {
        const laplace_ud_mwt_t *mwt = &parse.mwts[i];
        int language_override = 0;
        if (i > SIZE_MAX - parse.token_count) { status = -3; goto done; }
        for (size_t j = 0; j < mwt->misc.count; ++j)
        {
            const hash128_t *key = &mwt->misc.items[2 * j];
            const hash128_t *value = &mwt->misc.items[2 * j + 1];
            if (ud_same_id(key, language_misc_key) && !ud_same_id(value, &markers.none))
            {
                language_override = 1;
                status = ud_claim_add(&claims, &mwt->form_id, &has_language,
                                       value, parse.token_count + i, SIZE_MAX);
                if (status) goto done;
            }
        }
        if (!language_override)
        {
            status = ud_claim_add(&claims, &mwt->form_id, &has_language,
                                   &parse.language_id, parse.token_count + i, SIZE_MAX);
            if (status) goto done;
        }
    }

    qsort(claims.items, claims.count, sizeof(*claims.items), ud_claim_compare);
    for (size_t first = 0; first < claims.count; )
    {
        size_t last = first + 1;
        int64_t occurrences = 1;
        while (last < claims.count &&
               ud_cell_compare(&claims.items[first], &claims.items[last]) == 0)
        {
            if (ud_claim_compare(&claims.items[last - 1], &claims.items[last]) != 0)
            {
                if (occurrences == INT64_MAX) { status = -3; goto done; }
                ++occurrences;
            }
            ++last;
        }
        if (result_count >= out_capacity) { status = -3; goto done; }
        if (laplace_attestation_resolved_build(
                &claims.items[first].subject, &claims.items[first].relation,
                &claims.items[first].object, 0, source, parse_occurrence, 0,
                witness_weight, 1, occurrences, now_unix_us,
                &out[result_count]) != 0)
        {
            status = -5;
            goto done;
        }
        ++result_count;
        first = last;
    }
    *out_count = result_count;
done:
    free(references);
    free(claims.items);
    laplace_ud_parse_free(&parse);
    return status;
}
