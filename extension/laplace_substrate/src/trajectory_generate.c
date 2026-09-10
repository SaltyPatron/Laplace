/*
 * trajectory_generate.c — canonical query-relative forward execution.
 *
 * One retained query state owns the semantic frontier for the whole pass.
 * Ordered physical continuation evidence and typed relation/evidence channels
 * are independent candidate providers; selection extends both the ordered
 * trajectory and the persistent query state before the next election.
 */
#include "postgres.h"

#include <math.h>

#include "catalog/pg_type.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "utils/tuplestore.h"

#include "laplace/core/hash128.h"

#include "prompt_input.h"
#include "query_evidence.h"
#include "relation_symmetry.h"
#include "spi_common.h"
#include "trajectory_continuations.h"
#include "walk_score.h"

PG_FUNCTION_INFO_V1(pg_laplace_walk_continuations);
PG_FUNCTION_INFO_V1(pg_laplace_forward_prompt);

typedef struct Candidate
{
    hash128_t id;
    int64 sequence_occurrences;
    int stride;
    double query_score;
    int64 query_edges;
    double projection_score;
    double effective;
} Candidate;

typedef struct CandidateIndex
{
    hash128_t id;
    int index;
} CandidateIndex;

typedef struct QueryScore
{
    hash128_t id;
    double score;
    int64 edges;
    int32 covered;
} QueryScore;

typedef struct QueryCoverageKey
{
    hash128_t id;
    int32 ordinal;
    int32 reserved;
} QueryCoverageKey;

typedef struct QueryCoverage
{
    QueryCoverageKey key;
} QueryCoverage;

typedef struct PromptFrontier
{
    HTAB *seen;
    ArrayBuildState *ids;
    MemoryContext owner;
} PromptFrontier;

static uint64
splitmix64(uint64 *state)
{
    uint64 z = (*state += UINT64CONST(0x9E3779B97F4A7C15));
    z = (z ^ (z >> 30)) * UINT64CONST(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)) * UINT64CONST(0x94D049BB133111EB);
    return z ^ (z >> 31);
}

static double
rng_uniform(uint64 *state)
{
    return ((double) (splitmix64(state) >> 11) + 0.5) *
           (1.0 / 9007199254740992.0);
}

static void
validate_id_array(ArrayType *array, const char *name, bool allow_nulls)
{
    Datum *values;
    bool *nulls;
    int count;

    if (!array)
        return;
    if (ARR_NDIM(array) > 1 || ARR_ELEMTYPE(array) != BYTEAOID)
        ereport(ERROR,
                (errmsg("forward execution: %s must be a one-dimensional bytea array", name)));
    deconstruct_array(array, BYTEAOID, -1, false, TYPALIGN_INT,
                      &values, &nulls, &count);
    for (int i = 0; i < count; ++i)
    {
        if (nulls[i])
        {
            if (!allow_nulls)
                ereport(ERROR,
                        (errmsg("forward execution: %s must not contain NULL", name)));
            continue;
        }
        if (VARSIZE_ANY_EXHDR(DatumGetByteaPP(values[i])) != sizeof(hash128_t))
            ereport(ERROR,
                    (errmsg("forward execution: %s identities must be 16 bytes", name)));
    }
    if (count > 0)
    {
        pfree(values);
        pfree(nulls);
    }
}

static bool
relation_can_traverse_reverse(const hash128_t *type)
{
    const laplace_relation_def_t *def = NULL;
    return laplace_relation_lookup(type, &def) == 0 && def != NULL &&
           def->symmetry == LAPLACE_REL_SYMMETRY_SYMMETRIC;
}

static HTAB *
new_id_index(const char *name, long expected, MemoryContext owner, Size entry_size)
{
    HASHCTL ctl = {0};
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = entry_size;
    ctl.hcxt = owner;
    return hash_create(name, Max(expected, 16), &ctl,
                       HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

static void
prompt_frontier_add(PromptFrontier *frontier, const hash128_t *id)
{
    bool found;
    hash_search(frontier->seen, id, HASH_ENTER, &found);
    if (!found)
        frontier->ids = accumArrayResult(frontier->ids, hash128_to_datum(id),
                                         false, BYTEAOID, frontier->owner);
}

static ArrayType *
query_operands(ArrayType *ordered, ArrayType *frontier, MemoryContext owner)
{
    Datum *ordered_values = NULL, *frontier_values = NULL;
    bool *ordered_nulls = NULL, *frontier_nulls = NULL;
    int ordered_count = 0, frontier_count = 0;
    Datum *combined;
    int used = 0;
    HTAB *seen = new_id_index("forward query operand identities", 128, owner,
                              sizeof(hash128_t));

    deconstruct_array(ordered, BYTEAOID, -1, false, TYPALIGN_INT,
                      &ordered_values, &ordered_nulls, &ordered_count);
    if (frontier)
        deconstruct_array(frontier, BYTEAOID, -1, false, TYPALIGN_INT,
                          &frontier_values, &frontier_nulls, &frontier_count);

    if ((uint64) ordered_count + (uint64) frontier_count > INT_MAX ||
        (uint64) (ordered_count + frontier_count) > MaxAllocSize / sizeof(Datum))
        ereport(ERROR,
                (errmsg("forward execution: query operand set exceeds allocation capacity")));
    combined = palloc(sizeof(Datum) * Max(ordered_count + frontier_count, 1));

    /* Exact ordered occurrences remain exact occurrences. `seen` is used only
     * to prevent the supplemental frontier from duplicating identities that
     * are already represented by the ordered request. */
    for (int i = 0; i < ordered_count; ++i)
    {
        bytea *value;
        bool found;
        if (ordered_nulls[i])
            continue;
        value = DatumGetByteaPP(ordered_values[i]);
        if (VARSIZE_ANY_EXHDR(value) != sizeof(hash128_t))
            ereport(ERROR,
                    (errmsg("forward execution: ordered query identities must be 16 bytes")));
        combined[used++] = ordered_values[i];
        hash_search(seen, VARDATA_ANY(value), HASH_ENTER, &found);
    }

    for (int i = 0; i < frontier_count; ++i)
    {
        bytea *value;
        bool found;
        if (frontier_nulls[i])
            continue;
        value = DatumGetByteaPP(frontier_values[i]);
        if (VARSIZE_ANY_EXHDR(value) != sizeof(hash128_t))
            ereport(ERROR,
                    (errmsg("forward execution: supplemental query identities must be 16 bytes")));
        hash_search(seen, VARDATA_ANY(value), HASH_ENTER, &found);
        if (!found)
            combined[used++] = frontier_values[i];
    }

    ArrayType *result = construct_array(combined, used, BYTEAOID, -1, false, TYPALIGN_INT);
    hash_destroy(seen);
    pfree(combined);
    if (ordered_count > 0)
    {
        pfree(ordered_values);
        pfree(ordered_nulls);
    }
    if (frontier_count > 0)
    {
        pfree(frontier_values);
        pfree(frontier_nulls);
    }
    return result;
}

static HTAB *
query_scores(const LaplaceQueryState *state, bool projection_only,
             MemoryContext owner)
{
    int count = 0;
    const LaplaceQueryChannel *channels = laplace_query_state_channels(state, &count);
    HTAB *scores = new_id_index(projection_only ? "forward projection scores" :
                                "forward query scores", count, owner,
                                sizeof(QueryScore));
    HASHCTL coverage_ctl = {0};
    coverage_ctl.keysize = sizeof(QueryCoverageKey);
    coverage_ctl.entrysize = sizeof(QueryCoverage);
    coverage_ctl.hcxt = owner;
    HTAB *coverage = hash_create(projection_only ? "forward projection coverage" :
                                 "forward query coverage", Max(count, 16),
                                 &coverage_ctl,
                                 HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    for (int i = 0; i < count; ++i)
    {
        const LaplaceQueryChannel *channel = &channels[i];
        QueryScore *score;
        QueryCoverageKey coverage_key;
        double edge;
        bool found, covered;

        /* An incoming asymmetric claim remains in the retained query state as
         * evidence. It does not become an output traversal in reverse. */
        if (projection_only && !channel->outbound &&
            !relation_can_traverse_reverse(&channel->relation))
            continue;

        edge = walk_edge_score(channel->relation, channel->rating, channel->rd);
        score = hash_search(scores, &channel->candidate, HASH_ENTER, &found);
        if (!found)
        {
            score->score = 0.0;
            score->edges = 0;
            score->covered = 0;
        }
        score->score += edge;
        score->edges++;

        MemSet(&coverage_key, 0, sizeof(coverage_key));
        coverage_key.id = channel->candidate;
        coverage_key.ordinal = channel->ordinal;
        hash_search(coverage, &coverage_key, HASH_ENTER, &covered);
        if (!covered)
            score->covered++;
    }

    {
        HASH_SEQ_STATUS sequence;
        QueryScore *score;
        hash_seq_init(&sequence, scores);
        while ((score = hash_seq_search(&sequence)) != NULL)
        {
            if (score->covered > 1 && score->score > 0.0)
                score->score *= 1.0 + log((double) score->covered);
        }
    }
    hash_destroy(coverage);
    return scores;
}

static int
candidate_add(Candidate **items, int *count, int *capacity, HTAB *index,
              const hash128_t *id, int64 occurrences, int stride)
{
    CandidateIndex *entry;
    bool found;

    entry = hash_search(index, id, HASH_ENTER, &found);
    if (found)
    {
        Candidate *candidate = &(*items)[entry->index];
        if (stride > candidate->stride ||
            (stride == candidate->stride && occurrences > candidate->sequence_occurrences))
        {
            candidate->stride = stride;
            candidate->sequence_occurrences = occurrences;
        }
        return entry->index;
    }

    if (*count == *capacity)
    {
        int64 grown = *capacity ? (int64) *capacity * 2 : 32;
        if (grown > INT_MAX || (uint64) grown > MaxAllocSize / sizeof(Candidate))
            ereport(ERROR,
                    (errmsg("forward execution: candidate set exceeds allocation capacity")));
        *items = *items ? repalloc(*items, sizeof(Candidate) * (Size) grown)
                        : palloc(sizeof(Candidate) * (Size) grown);
        *capacity = (int) grown;
    }
    entry->index = *count;
    (*items)[*count] = (Candidate) {
        .id = *id,
        .sequence_occurrences = occurrences,
        .stride = stride,
        .query_score = 0.0,
        .query_edges = 0,
        .projection_score = 0.0,
        .effective = 0.0,
    };
    return (*count)++;
}

static int
candidate_compare(const void *left, const void *right)
{
    const Candidate *a = left;
    const Candidate *b = right;
    if (a->effective != b->effective)
        return a->effective > b->effective ? -1 : 1;
    if (a->sequence_occurrences != b->sequence_occurrences)
        return a->sequence_occurrences > b->sequence_occurrences ? -1 : 1;
    return memcmp(&a->id, &b->id, sizeof(hash128_t));
}

static void
emit_result(ReturnSetInfo *result, int32 step, const Candidate *candidate)
{
    Datum values[4];
    bool nulls[4] = {false, false, false, true};
    Datum id = hash128_to_datum(&candidate->id);
    values[0] = Int32GetDatum(step);
    values[1] = id;
    values[2] = Int32GetDatum(candidate->stride);
    values[3] = (Datum) 0;
    tuplestore_putvalues(result->setResult, result->setDesc, values, nulls);
    pfree(DatumGetPointer(id));
}

static Datum
walk_continuations(FunctionCallInfo fcinfo, const LaplacePromptInput *input,
                   int semantic_hop_limit)
{
    ReturnSetInfo *result = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType *context_array;
    ArrayType *frontier_array = NULL;
    ArrayType *relation_types = NULL;
    ArrayType *output_relations = NULL;
    int32 steps, max_stride, top_k, fanout;
    float8 spread;
    uint64 rng;
    Datum *context_values;
    bool *context_nulls;
    int context_count;
    Datum *context;
    int context_length = 0;
    int context_capacity;
    MemoryContext walk_context, step_context, old;
    LaplaceTrajectoryScope *trajectory_scope = NULL;
    LaplaceQueryState *query_state = NULL;
    LaplaceQueryState *output_state = NULL;
    int semantic_hops = 0;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("forward execution: context must not be NULL")));
    context_array = PG_GETARG_ARRAYTYPE_P(0);
    steps = PG_ARGISNULL(1) ? 24 : PG_GETARG_INT32(1);
    max_stride = PG_ARGISNULL(2) ? 5 : PG_GETARG_INT32(2);
    spread = PG_ARGISNULL(3) ? 0.7 : PG_GETARG_FLOAT8(3);
    top_k = PG_ARGISNULL(4) ? 10 : PG_GETARG_INT32(4);
    rng = PG_ARGISNULL(5) ? UINT64CONST(0x5851F42D4C957F2D) :
                            (uint64) PG_GETARG_INT64(5);
    if (PG_NARGS() > 6 && !PG_ARGISNULL(6))
        frontier_array = PG_GETARG_ARRAYTYPE_P(6);
    if (PG_NARGS() > 7 && !PG_ARGISNULL(7))
        relation_types = PG_GETARG_ARRAYTYPE_P(7);
    fanout = PG_NARGS() > 8 && !PG_ARGISNULL(8) ? PG_GETARG_INT32(8) : 8;
    if (PG_NARGS() > 11 && !PG_ARGISNULL(11))
        output_relations = PG_GETARG_ARRAYTYPE_P(11);

    if (steps < 0 || max_stride < 0 || top_k < 0 || fanout < 0)
        ereport(ERROR,
                (errmsg("forward execution: steps, stride, top-k and fanout must not be negative")));
    if (!isfinite(spread) || spread < 0.0)
        ereport(ERROR,
                (errmsg("forward execution: spread must be finite and not negative")));
    validate_id_array(context_array, "context", true);
    validate_id_array(frontier_array, "frontier", true);
    validate_id_array(relation_types, "relation types", false);
    validate_id_array(output_relations, "output relation types", false);

    InitMaterializedSRF(fcinfo, 0);
    if (steps == 0 || top_k == 0)
        return (Datum) 0;

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "forward execution: SPI_connect failed");

    walk_context = CurrentMemoryContext;
    deconstruct_array(context_array, BYTEAOID, -1, false, TYPALIGN_INT,
                      &context_values, &context_nulls, &context_count);
    if ((uint64) context_count + (uint64) steps > INT_MAX ||
        (uint64) (context_count + steps) > MaxAllocSize / sizeof(Datum))
        ereport(ERROR,
                (errmsg("forward execution: retained ordered context exceeds allocation capacity")));
    context_capacity = context_count + steps;
    context = palloc(sizeof(Datum) * Max(context_capacity, 1));
    for (int i = 0; i < context_count; ++i)
    {
        if (context_nulls[i])
            continue;
        context[context_length++] = PointerGetDatum(PG_DETOAST_DATUM_COPY(context_values[i]));
    }
    if (context_count > 0)
    {
        pfree(context_values);
        pfree(context_nulls);
    }
    if (context_length == 0)
    {
        SPI_finish();
        return (Datum) 0;
    }

    old = MemoryContextSwitchTo(walk_context);
    ArrayType *operands = query_operands(context_array, frontier_array, walk_context);
    query_state = laplace_query_state_create(operands, relation_types, fanout, NULL);

    if (output_relations &&
        ArrayGetNItems(ARR_NDIM(output_relations), ARR_DIMS(output_relations)) > 0)
    {
        ArrayType *output_operands;
        if (input)
        {
            Datum root = hash128_to_datum(&input->root);
            output_operands = construct_array(&root, 1, BYTEAOID, -1, false, TYPALIGN_INT);
            pfree(DatumGetPointer(root));
        }
        else
        {
            output_operands = construct_array(context, context_length,
                                              BYTEAOID, -1, false, TYPALIGN_INT);
        }
        output_state = laplace_query_state_create(output_operands, output_relations,
                                                  fanout, NULL);
        pfree(output_operands);
    }
    pfree(operands);

    if (PG_NARGS() > 9 && !PG_ARGISNULL(9) && max_stride > 0)
    {
        trajectory_scope = laplace_trajectory_scope_create();
        laplace_trajectory_scope_extend(trajectory_scope, PG_GETARG_ARRAYTYPE_P(9));
    }
    if (PG_NARGS() > 10 && !PG_ARGISNULL(10) && max_stride > 0)
    {
        if (!trajectory_scope)
            trajectory_scope = laplace_trajectory_scope_create();
        laplace_trajectory_scope_extend_containing(trajectory_scope,
                                                   PG_GETARG_ARRAYTYPE_P(10));
    }
    if (input && max_stride > 0)
    {
        if (!trajectory_scope)
            trajectory_scope = laplace_trajectory_scope_create();
        laplace_trajectory_scope_bind_input(trajectory_scope, input);
    }
    MemoryContextSwitchTo(old);

    step_context = AllocSetContextCreate(walk_context, "forward query election",
                                         ALLOCSET_DEFAULT_SIZES);

    for (int32 step = 1; step <= steps; ++step)
    {
        Candidate *candidates = NULL;
        int candidate_count = 0, candidate_capacity = 0;
        HTAB *candidate_index;
        HTAB *query_score_table;
        HTAB *projection_table = NULL;
        int pick = -1;

        MemoryContextReset(step_context);
        MemoryContextSwitchTo(step_context);
        CHECK_FOR_INTERRUPTS();
        candidate_index = new_id_index("forward candidate index", 128, step_context,
                                       sizeof(CandidateIndex));

        if (max_stride > 0)
        {
            int depth = input && step == 1 ? context_length : Min(context_length, max_stride);
            if (depth > 0)
            {
                ArrayType *tail = construct_array(context + context_length - depth, depth,
                                                  BYTEAOID, -1, false, TYPALIGN_INT);
                int count = 0;
                LaplaceContinuation *continuations =
                    laplace_trajectory_continuations_scoped(tail, true,
                                                            trajectory_scope, &count);
                for (int i = 0; i < count; ++i)
                    candidate_add(&candidates, &candidate_count, &candidate_capacity,
                                  candidate_index, &continuations[i].id,
                                  continuations[i].occurrences,
                                  continuations[i].stride);
                if (continuations)
                    pfree(continuations);
                pfree(tail);
            }
        }

        query_score_table = query_scores(query_state, false, step_context);

        if (output_state &&
            (semantic_hop_limit < 0 || semantic_hops < semantic_hop_limit))
        {
            projection_table = query_scores(output_state, true, step_context);
            HASH_SEQ_STATUS sequence;
            QueryScore *projection;
            hash_seq_init(&sequence, projection_table);
            while ((projection = hash_seq_search(&sequence)) != NULL)
            {
                if (!(projection->score > 0.0) || !isfinite(projection->score))
                    continue;
                /* Semantic-only output is a simple path. Exact observed
                 * sequence continuations may repeat independently. */
                bool already_in_context = false;
                for (int i = 0; i < context_length; ++i)
                {
                    if (memcmp(VARDATA_ANY(DatumGetByteaPP(context[i])),
                               &projection->id, sizeof(hash128_t)) == 0)
                    {
                        already_in_context = true;
                        break;
                    }
                }
                CandidateIndex *existing = hash_search(candidate_index,
                    &projection->id, HASH_FIND, NULL);
                if (!existing && already_in_context)
                    continue;
                int at = candidate_add(&candidates, &candidate_count, &candidate_capacity,
                                       candidate_index, &projection->id, 0, 0);
                candidates[at].projection_score = projection->score;
            }
        }

        if (candidate_count == 0)
            break;

        bool has_positive_query = false;
        for (int i = 0; i < candidate_count; ++i)
        {
            QueryScore *score = hash_search(query_score_table,
                                             &candidates[i].id, HASH_FIND, NULL);
            if (score)
            {
                candidates[i].query_score = score->score;
                candidates[i].query_edges = score->edges;
                if (score->edges > 0 && score->score > 0.0 && isfinite(score->score))
                    has_positive_query = true;
            }
            if (projection_table)
            {
                QueryScore *projection = hash_search(projection_table,
                    &candidates[i].id, HASH_FIND, NULL);
                if (projection && projection->score > candidates[i].projection_score)
                    candidates[i].projection_score = projection->score;
            }
        }

        int kept = 0;
        for (int i = 0; i < candidate_count; ++i)
        {
            Candidate candidate = candidates[i];
            if (candidate.query_edges > 0 &&
                (!(candidate.query_score > 0.0) || !isfinite(candidate.query_score)))
                continue;
            if (has_positive_query && candidate.query_edges == 0 &&
                !trajectory_scope && !(candidate.projection_score > 0.0))
                continue;
            if (candidate.sequence_occurrences == 0 &&
                !(candidate.projection_score > 0.0))
                continue;

            candidate.effective =
                (candidate.sequence_occurrences > 0 ?
                    (double) candidate.sequence_occurrences : 1.0) *
                (candidate.query_edges > 0 ? candidate.query_score :
                    candidate.projection_score > 0.0 ? candidate.projection_score : 1.0);
            if (!(candidate.effective > 0.0) || !isfinite(candidate.effective))
                continue;
            candidates[kept++] = candidate;
        }
        candidate_count = kept;
        if (candidate_count == 0)
            break;

        qsort(candidates, (size_t) candidate_count, sizeof(Candidate), candidate_compare);
        int limit = Min(candidate_count, top_k);
        if (spread == 0.0)
            pick = 0;
        else
        {
            double best = 0.0;
            for (int i = 0; i < limit; ++i)
            {
                double u = rng_uniform(&rng);
                double key = log(candidates[i].effective) / spread - log(-log(u));
                if (i == 0 || key > best)
                {
                    best = key;
                    pick = i;
                }
            }
        }
        if (pick < 0)
            break;

        emit_result(result, step, &candidates[pick]);

        MemoryContextSwitchTo(walk_context);
        Datum selected = hash128_to_datum(&candidates[pick].id);
        context[context_length++] = selected;
        laplace_query_state_extend(query_state, selected, NULL);
        if (output_state)
            laplace_query_state_extend(output_state, selected, NULL);

        if (trajectory_scope)
        {
            bool ordered = candidates[pick].stride > 0;
            laplace_trajectory_scope_select(trajectory_scope, selected, ordered);
            if (!ordered)
            {
                ArrayType *selection = construct_array(&selected, 1, BYTEAOID, -1,
                                                       false, TYPALIGN_INT);
                laplace_trajectory_scope_extend(trajectory_scope, selection);
                pfree(selection);
            }
        }
        if (candidates[pick].stride == 0)
            semantic_hops++;
        MemoryContextSwitchTo(step_context);
    }

    MemoryContextSwitchTo(walk_context);
    MemoryContextDelete(step_context);
    laplace_query_state_destroy(&output_state);
    laplace_query_state_destroy(&query_state);
    for (int i = 0; i < context_length; ++i)
        pfree(DatumGetPointer(context[i]));
    pfree(context);
    SPI_finish();
    return (Datum) 0;
}

Datum
pg_laplace_walk_continuations(PG_FUNCTION_ARGS)
{
    return walk_continuations(fcinfo, NULL, -1);
}

Datum
pg_laplace_forward_prompt(PG_FUNCTION_ARGS)
{
    int hops;
    int fanout;

    if (PG_ARGISNULL(0) || VARSIZE_ANY_EXHDR(PG_GETARG_TEXT_PP(0)) == 0)
    {
        InitMaterializedSRF(fcinfo, 0);
        return (Datum) 0;
    }

    hops = PG_ARGISNULL(6) ? 2 : PG_GETARG_INT32(6);
    fanout = PG_ARGISNULL(7) ? 8 : PG_GETARG_INT32(7);
    if (hops < 0 || fanout < 0)
        ereport(ERROR,
                (errmsg("forward prompt: hops and fanout must not be negative")));

    LaplacePromptInput *input = laplace_prompt_input(PG_GETARG_TEXT_PP(0));
    HASHCTL ctl = {0};
    ctl.keysize = ctl.entrysize = sizeof(hash128_t);
    ctl.hcxt = CurrentMemoryContext;
    PromptFrontier frontier = {
        .owner = CurrentMemoryContext,
        .seen = hash_create("forward prompt supplemental operands", 128, &ctl,
                            HASH_ELEM | HASH_BLOBS | HASH_CONTEXT),
    };

    ArrayIterator iterator = array_create_iterator(input->seeds, 0, NULL);
    Datum value;
    bool isnull;
    while (array_iterate(iterator, &value, &isnull))
    {
        if (isnull)
            continue;
        hash128_t id = datum_to_hash128(value);
        prompt_frontier_add(&frontier, &id);
    }
    array_free_iterator(iterator);

    if (!PG_ARGISNULL(8))
    {
        ArrayType *history = PG_GETARG_ARRAYTYPE_P(8);
        validate_id_array(history, "history", true);
        iterator = array_create_iterator(history, 0, NULL);
        while (array_iterate(iterator, &value, &isnull))
        {
            if (isnull)
                continue;
            hash128_t id = datum_to_hash128(value);
            prompt_frontier_add(&frontier, &id);
        }
        array_free_iterator(iterator);
    }

    LOCAL_FCINFO(walk_call, 12);
    InitFunctionCallInfoData(*walk_call, fcinfo->flinfo, 12, fcinfo->fncollation,
                             fcinfo->context, fcinfo->resultinfo);
    for (int i = 0; i < 12; ++i)
        walk_call->args[i].isnull = true;
    walk_call->args[0] = (NullableDatum) {PointerGetDatum(input->context), false};
    for (int i = 1; i <= 5; ++i)
        walk_call->args[i] = fcinfo->args[i];
    walk_call->args[6] = (NullableDatum) {
        makeArrayResult(frontier.ids, frontier.owner), false};
    walk_call->args[8] = (NullableDatum) {Int32GetDatum(fanout), false};
    walk_call->args[11] = fcinfo->args[9];

    return walk_continuations(walk_call, input, hops);
}
