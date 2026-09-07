/* S7: typed standing over the intersection of candidates and the live frontier.
 * PostgreSQL owns cells and MVCC; consensus_scan owns native batch access;
 * walk_score.h owns edge scoring. SQL only binds operands and returns rows. */
#include "postgres.h"
#include <math.h>
#include "catalog/pg_type.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "steer_candidates.h"
#include "walk_score.h"

PG_FUNCTION_INFO_V1(pg_laplace_steer_candidates);

typedef struct PairEntry { char key[32]; double score; } PairEntry;
typedef struct SteeringState
{
    HTAB *candidates;
    HTAB *pairs;
    bool reverse;
    bool typed;
} SteeringState;

static void
validate_id_array(ArrayType *array, const char *name, bool allow_nulls)
{
    Datum *elems;
    bool *nulls;
    int count;
    if (ARR_NDIM(array) > 1 || ARR_ELEMTYPE(array) != BYTEAOID)
        ereport(ERROR, (errmsg("steer_candidates: %s must be a 1-D bytea array", name)));
    deconstruct_array(array, BYTEAOID, -1, false, TYPALIGN_INT, &elems, &nulls, &count);
    for (int i = 0; i < count; ++i)
    {
        if (nulls[i])
        {
            if (!allow_nulls)
                ereport(ERROR, (errmsg("steer_candidates: %s must not contain NULL", name)));
            continue;
        }
        if (VARSIZE_ANY_EXHDR(DatumGetByteaPP(elems[i])) != 16)
            ereport(ERROR, (errmsg("steer_candidates: %s ids must be 16 bytes", name)));
    }
    pfree(elems);
    pfree(nulls);
}

static void
steer_cell(const LaplaceConsensusRow *row, void *opaque)
{
    SteeringState *state = opaque;
    const hash128_t *candidate = state->reverse ? &row->subject : &row->object;
    const hash128_t *frontier = state->reverse ? &row->object : &row->subject;
    LaplaceSteeredCandidate *owner;
    PairEntry *pair;
    char key[32];
    bool found;
    if (row->object_is_null) return;
    if (state->reverse && state->typed)
    {
        const laplace_relation_def_t *def = NULL;
        if (laplace_relation_lookup(&row->type, &def) != 0 || def == NULL ||
            def->symmetry != LAPLACE_REL_SYMMETRY_SYMMETRIC) return;
    }
    owner = hash_search(state->candidates, candidate, HASH_FIND, NULL);
    if (owner == NULL) return;
    owner->edges++;
    memcpy(key, candidate, 16);
    memcpy(key + 16, frontier, 16);
    pair = hash_search(state->pairs, key, HASH_ENTER, &found);
    if (!found)
    {
        pair->score = 0.0;
        owner->covered++;
    }
    pair->score += walk_edge_score(row->type, row->rating, row->rd);
}

static int
steer_id_compare(const void *left, const void *right)
{
    const LaplaceSteeredCandidate *a = left, *b = right;
    return memcmp(&a->id, &b->id, sizeof(hash128_t));
}

LaplaceSteeredCandidate *
laplace_steer_candidates(ArrayType *candidates, ArrayType *frontier,
                         ArrayType *types, int *count,
                         LaplaceConsensusScanStats *stats)
{
    HASHCTL ctl = {0};
    SteeringState state = {0};
    Datum *elems;
    bool *nulls;
    int n;
    HASH_SEQ_STATUS seq;
    PairEntry *pair;
    LaplaceSteeredCandidate *owner, *result;
    long entries;
    validate_id_array(candidates, "candidates", true);
    validate_id_array(frontier, "frontier", true);
    if (types != NULL) validate_id_array(types, "relation types", false);
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(LaplaceSteeredCandidate);
    ctl.hcxt = CurrentMemoryContext;
    state.candidates = hash_create("steered candidates", 256, &ctl,
                                  HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    ctl.keysize = 32;
    ctl.entrysize = sizeof(PairEntry);
    state.pairs = hash_create("steering pairs", 256, &ctl,
                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    state.typed = types != NULL;
    deconstruct_array(candidates, BYTEAOID, -1, false, TYPALIGN_INT, &elems, &nulls, &n);
    for (int i = 0; i < n; ++i)
    {
        bool found;
        if (nulls[i]) continue;
        owner = hash_search(state.candidates,
            VARDATA_ANY(DatumGetByteaPP(elems[i])), HASH_ENTER, &found);
        if (!found)
        {
            owner->steer = 0.0;
            owner->edges = 0;
            owner->covered = 0;
        }
    }
    pfree(elems);
    pfree(nulls);
    /* Each direction is one native batch. Typed reverse traversal is admitted
     * by the canonical relation's symmetry; untyped inspection sees both ends. */
    laplace_consensus_scan(frontier, candidates, types, steer_cell, &state, stats);
    state.reverse = true;
    laplace_consensus_scan(candidates, frontier, types, steer_cell, &state, stats);
    hash_seq_init(&seq, state.pairs);
    while ((pair = hash_seq_search(&seq)) != NULL)
    {
        owner = hash_search(state.candidates, pair->key, HASH_FIND, NULL);
        if (owner != NULL) owner->steer += pair->score;
    }
    entries = hash_get_num_entries(state.candidates);
    if (entries > INT_MAX || (Size) entries > MaxAllocSize / sizeof(*result))
        ereport(ERROR, (errmsg("steer_candidates: result exceeds PostgreSQL allocation capacity")));
    *count = (int) entries;
    result = palloc(sizeof(*result) * Max(*count, 1));
    n = 0;
    hash_seq_init(&seq, state.candidates);
    while ((owner = hash_seq_search(&seq)) != NULL)
    {
        /* Preserve the existing S7 score while replacing storage access.
         * Unattested and refuted candidates stay distinct through edges. */
        if (owner->covered > 1 && owner->steer > 0.0)
            owner->steer *= 1.0 + log((double) owner->covered);
        result[n++] = *owner;
    }
    qsort(result, n, sizeof(*result), steer_id_compare);
    hash_destroy(state.pairs);
    hash_destroy(state.candidates);
    return result;
}

Datum
pg_laplace_steer_candidates(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo;
    LaplaceSteeredCandidate *rows;
    int count;
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("steer_candidates: candidates and frontier must not be NULL")));
    InitMaterializedSRF(fcinfo, 0);
    rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    rows = laplace_steer_candidates(PG_GETARG_ARRAYTYPE_P(0), PG_GETARG_ARRAYTYPE_P(1),
        PG_NARGS() > 2 && !PG_ARGISNULL(2) ? PG_GETARG_ARRAYTYPE_P(2) : NULL, &count, NULL);
    for (int i = 0; i < count; ++i)
    {
        Datum values[4];
        bool nulls[4] = {false, false, false, false};
        bytea *id = palloc(VARHDRSZ + 16);
        SET_VARSIZE(id, VARHDRSZ + 16);
        memcpy(VARDATA(id), &rows[i].id, 16);
        values[0] = PointerGetDatum(id);
        values[1] = Float8GetDatum(rows[i].steer);
        values[2] = Int64GetDatum(rows[i].edges);
        values[3] = Int64GetDatum(rows[i].covered);
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        pfree(id);
    }
    pfree(rows);
    return (Datum) 0;
}
