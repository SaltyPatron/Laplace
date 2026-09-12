#ifndef LAPLACE_PROMPT_INTENT_H
#define LAPLACE_PROMPT_INTENT_H

#include "postgres.h"

#include "nodes/bitmapset.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"

#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"

#include "prompt_input.h"
#include "spi_common.h"

typedef struct LaplacePromptIntentToken
{
    hash128_t id;
    Bitmapset *origins;
} LaplacePromptIntentToken;

typedef struct LaplacePromptIntent
{
    ArrayType *relations;
    hash128_t *relation_ids;
    int relation_count;
    Bitmapset *operator_origins;
} LaplacePromptIntent;

/* These aliases already belong to the established prompt-coherence operation
 * naming law. They map a relation's canonical noun to a prompt-facing operation
 * name; they never name or synthesize an answer identity. */
static inline const char *
laplace_prompt_intent_alias(const char *segment)
{
    if (strcmp(segment, "antonym") == 0)
        return "opposite";
    if (strcmp(segment, "definition") == 0)
        return "define";
    return NULL;
}

static inline bool
laplace_prompt_intent_has_relation(const LaplacePromptIntent *intent,
                                   const hash128_t *relation)
{
    if (!intent || !relation)
        return false;
    for (int i = 0; i < intent->relation_count; ++i)
        if (memcmp(&intent->relation_ids[i], relation, sizeof(hash128_t)) == 0)
            return true;
    return false;
}

/* Compile S3 relation intent from the already-admitted canonical prompt tree.
 * No second prompt decomposition and no answer lookup occurs here. Relation
 * canonical names are manifest data. The longest identifier segment is the
 * lexical relation name (ANTONYM in IS_ANTONYM_OF, PART in HAS_PART); fragments
 * shorter than three bytes cannot become operators.
 *
 * The returned cue bitmap uses input->context occurrence ordinals, allowing the
 * cognition program to distinguish a relation operator from its operands and
 * from grammatical scaffolding while retaining exact prompt provenance. */
static inline LaplacePromptIntent
laplace_prompt_intent_compile(const LaplacePromptInput *input,
                              MemoryContext owner)
{
    LaplacePromptIntent result = {0};
    HASHCTL ctl = {0};
    HTAB *tokens;
    Datum *values = NULL;
    bool *nulls = NULL;
    int count = 0;
    size_t relation_capacity = laplace_relation_table_count;

    if (!input || !input->context || relation_capacity == 0)
        return result;

    deconstruct_array(input->context, BYTEAOID, -1, false, TYPALIGN_INT,
                      &values, &nulls, &count);
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(LaplacePromptIntentToken);
    ctl.hcxt = owner;
    tokens = hash_create("forward prompt intent tokens", Max(count, 16), &ctl,
                         HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    for (int i = 0; i < count; ++i)
    {
        bytea *value;
        hash128_t id;
        LaplacePromptIntentToken *entry;
        bool found;

        if (nulls[i])
            continue;
        value = DatumGetByteaPP(values[i]);
        if (VARSIZE_ANY_EXHDR(value) != sizeof(hash128_t))
            ereport(ERROR,
                    (errmsg("forward prompt intent: context identity must be 16 bytes")));
        memcpy(&id, VARDATA_ANY(value), sizeof(id));
        entry = hash_search(tokens, &id, HASH_ENTER, &found);
        if (!found)
            entry->origins = NULL;
        entry->origins = bms_add_member(entry->origins, i);
    }

    if (relation_capacity > (size_t) INT_MAX ||
        relation_capacity > MaxAllocSize / sizeof(hash128_t))
        ereport(ERROR,
                (errmsg("forward prompt intent: relation manifest exceeds allocation capacity")));
    result.relation_ids = palloc(sizeof(hash128_t) * Max(relation_capacity, (size_t) 1));

    for (size_t r = 0; r < laplace_relation_table_count; ++r)
    {
        const char *canonical = laplace_relation_table[r].canonical;
        const char *segment = NULL;
        const char *alias;
        size_t len = 0;
        char *copy;
        char *scan;
        char *lower;
        hash128_t cue_id;
        hash128_t relation_id;
        LaplacePromptIntentToken *cue;
        bool duplicate = false;

        if (!canonical || !*canonical)
            continue;

        copy = pstrdup(canonical);
        scan = copy;
        while (scan && *scan)
        {
            char *next = strchr(scan, '_');
            size_t part_len = next ? (size_t) (next - scan) : strlen(scan);
            if (part_len > len)
            {
                segment = scan;
                len = part_len;
            }
            if (!next)
                break;
            *next = '\0';
            scan = next + 1;
        }
        if (!segment || len < 3)
        {
            pfree(copy);
            continue;
        }

        lower = pnstrdup(segment, len);
        for (size_t i = 0; i < len; ++i)
            lower[i] = pg_ascii_tolower((unsigned char) lower[i]);
        if (laplace_content_root_id((const uint8_t *) lower, len, &cue_id) != 0)
        {
            pfree(lower);
            pfree(copy);
            continue;
        }
        cue = hash_search(tokens, &cue_id, HASH_FIND, NULL);

        alias = laplace_prompt_intent_alias(lower);
        if (!cue && alias)
        {
            if (laplace_content_root_id((const uint8_t *) alias, strlen(alias),
                                        &cue_id) == 0)
                cue = hash_search(tokens, &cue_id, HASH_FIND, NULL);
        }
        pfree(lower);
        pfree(copy);
        if (!cue)
            continue;
        if (laplace_relation_type_id(canonical, &relation_id) != 0)
            ereport(ERROR,
                    (errmsg("forward prompt intent: manifest relation %s has no identity",
                            canonical)));

        for (int i = 0; i < result.relation_count; ++i)
            if (memcmp(&result.relation_ids[i], &relation_id,
                       sizeof(hash128_t)) == 0)
            {
                duplicate = true;
                break;
            }
        if (!duplicate)
            result.relation_ids[result.relation_count++] = relation_id;
        result.operator_origins = bms_add_members(result.operator_origins,
                                                   cue->origins);
    }

    if (result.relation_count > 0)
        result.relations = hash128_array_from_ids(result.relation_ids,
                                                  result.relation_count);
    else
    {
        pfree(result.relation_ids);
        result.relation_ids = NULL;
        bms_free(result.operator_origins);
        result.operator_origins = NULL;
    }

    if (count > 0)
    {
        pfree(values);
        pfree(nulls);
    }
    hash_destroy(tokens);
    return result;
}

#endif
