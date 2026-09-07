#include "postgres.h"
#include "miscadmin.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "spi_common.h"
#include "spi_nested.h"
#include "trajectory_wkb.h"
#include "trajectory_continuations.h"
#include "laplace/core/trajectory.h"

/* GIN supplies containing trajectories, not sequence truth. Native matching
 * reads the mantissa-packed ordered occurrences once, including all runs and
 * separators. Exact reads probe the complete context; suffix proposal probes
 * its final constituent only when the exact probe is empty. Every admissible
 * suffix necessarily contains that identity. */
static const char *UNPACK_QUERY =
    "SELECT public.ST_AsBinary(p.trajectory) "
    "FROM laplace.physicalities p "
    "WHERE p.type = 1 AND p.trajectory IS NOT NULL "
    "AND public.laplace_trajectory_constituent_ids(p.trajectory) @> $1";
static SPIPlanPtr unpack_plan = NULL;

typedef struct SuccessorState
{
    HTAB *successors;
    size_t stride;
} SuccessorState;

typedef struct MatcherCleanup
{
    MemoryContextCallback callback;
    trajectory_suffix_matcher_t *matcher;
} MatcherCleanup;

static void
free_matcher(void *argument)
{
    MatcherCleanup *cleanup = argument;
    trajectory_suffix_matcher_free(cleanup->matcher);
}

static int
record_successor(void *context, size_t ordinal, size_t stride,
                 const hash128_t *successor)
{
    SuccessorState *state = context;
    bool found;
    (void) ordinal;
    CHECK_FOR_INTERRUPTS();
    if (stride == 0 || stride < state->stride) return 0;
    state->stride = stride;
    LaplaceContinuation *entry = hash_search(state->successors, successor, HASH_ENTER, &found);
    if (!found || entry->stride != (int) stride)
    {
        entry->occurrences = 0;
        entry->stride = (int) stride;
    }
    if (entry->occurrences == PG_INT64_MAX)
        ereport(ERROR, (errmsg("trajectory_continuations: occurrence count overflow")));
    ++entry->occurrences;
    return 0;
}

static int
successor_cmp(const void *a, const void *b)
{
    const LaplaceContinuation *x = a, *y = b;
    if (x->occurrences > y->occurrences) return -1;
    if (x->occurrences < y->occurrences) return 1;
    return memcmp(&x->id, &y->id, sizeof(hash128_t));
}

LaplaceContinuation *
laplace_trajectory_continuations(ArrayType *context_array, bool suffix_backoff, int *count)
{
    MemoryContext owner = CurrentMemoryContext;
    MemoryContext work = AllocSetContextCreate(owner, "trajectory suffix operands",
                                               ALLOCSET_DEFAULT_SIZES);
    bool spi_top = false;
    Datum *ids;
    bool *nulls;
    int n_context;
    LaplaceContinuation *ordered;
    *count = 0;
    if (ARR_NDIM(context_array) > 1 || ARR_ELEMTYPE(context_array) != BYTEAOID)
        ereport(ERROR, (errmsg("trajectory_continuations: context must be a 1-D bytea array")));
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "trajectory_continuations: SPI_connect failed");
    MemoryContext spi_context = MemoryContextSwitchTo(work);
    deconstruct_array(context_array, BYTEAOID, -1, false, TYPALIGN_INT,
                      &ids, &nulls, &n_context);
    if (n_context < 1)
        ereport(ERROR, (errmsg("trajectory_continuations: context must not be empty")));
    if ((Size) n_context > MaxAllocSize / sizeof(hash128_t))
        ereport(ERROR, (errmsg("trajectory_continuations: context exceeds allocation capacity")));
    hash128_t *context = palloc((Size) n_context * sizeof(hash128_t));
    for (int i = 0; i < n_context; ++i)
    {
        if (nulls[i])
            ereport(ERROR, (errmsg("trajectory_continuations: context contains NULL")));
        bytea *id = DatumGetByteaPP(ids[i]);
        if (VARSIZE_ANY_EXHDR(id) != sizeof(hash128_t))
            ereport(ERROR, (errmsg("trajectory_continuations: context ids must be 16 bytes")));
        memcpy(context + i, VARDATA_ANY(id), sizeof(hash128_t));
    }
    MatcherCleanup *cleanup = palloc0(sizeof(*cleanup));
    cleanup->matcher = trajectory_suffix_matcher_create(context, n_context,
                                                        suffix_backoff ? 1 : n_context);
    if (!cleanup->matcher)
        ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY),
                        errmsg("trajectory_continuations: allocating native suffix matcher failed")));
    cleanup->callback.func = free_matcher;
    cleanup->callback.arg = cleanup;
    MemoryContextRegisterResetCallback(work, &cleanup->callback);
    HASHCTL ctl = {0};
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(LaplaceContinuation);
    ctl.hcxt = work;
    SuccessorState state = {
        hash_create("trajectory successors", 256, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT), 0
    };
    if (!unpack_plan)
    {
        Oid types[1] = {BYTEAARRAYOID};
        SPIPlanPtr plan = SPI_prepare(UNPACK_QUERY, 1, types);
        if (!plan || SPI_keepplan(plan) != 0)
            elog(ERROR, "trajectory_continuations: preparing containment query failed");
        unpack_plan = plan;
    }
    /* Preserve the selective full-context GIN probe. Reading every container
     * of a common final separator on successful exact matches would undo the
     * index reduction. Only an empty exact result needs a broader read, and
     * that one read evaluates every remaining suffix together in native code. */
    for (int phase = 0; phase < (suffix_backoff && n_context > 1 ? 2 : 1); ++phase)
    {
        if (phase > 0 && hash_get_num_entries(state.successors) > 0) break;
        state.stride = phase == 0 ? (size_t) n_context : 0;
        ArrayType *probe = phase == 0 ? context_array
            : construct_array(ids + n_context - 1, 1, BYTEAOID, -1, false, TYPALIGN_INT);
        Datum args[1] = {PointerGetDatum(probe)};
        Portal portal = SPI_cursor_open(NULL, unpack_plan, args, NULL, true);
        if (!portal) elog(ERROR, "trajectory_continuations: opening containment cursor failed");
        for (;;)
        {
            SPI_cursor_fetch(portal, true, 1024);
            uint64 rows = SPI_processed;
            for (uint64 row = 0; row < rows; ++row)
            {
                bool isnull;
                Datum datum = SPI_getbinval(SPI_tuptable->vals[row], SPI_tuptable->tupdesc, 1, &isnull);
                if (isnull) continue;
                uint32 npoints;
                const unsigned char *points = laplace_trajectory_wkb_points(DatumGetByteaPP(datum), &npoints);
                if (trajectory_match_suffixes(cleanup->matcher, points, npoints,
                                               record_successor, &state) != 0)
                    elog(ERROR, "trajectory_continuations: invalid packed trajectory");
            }
            if (SPI_tuptable)
            {
                SPI_freetuptable(SPI_tuptable);
                SPI_tuptable = NULL;
            }
            if (!rows) break;
            CHECK_FOR_INTERRUPTS();
        }
        SPI_cursor_close(portal);
    }

    long entries = hash_get_num_entries(state.successors);
    if (entries > INT_MAX || (uint64) entries > MaxAllocSize / sizeof(*ordered))
        ereport(ERROR, (errmsg("trajectory_continuations: successor set exceeds allocation capacity")));
    MemoryContextSwitchTo(owner);
    ordered = palloc(sizeof(*ordered) * Max(entries, 1));
    HASH_SEQ_STATUS sequence;
    LaplaceContinuation *entry;
    hash_seq_init(&sequence, state.successors);
    while ((entry = hash_seq_search(&sequence)) != NULL)
        if (entry->stride == (int) state.stride) ordered[(*count)++] = *entry;
    qsort(ordered, *count, sizeof(*ordered), successor_cmp);
    MemoryContextSwitchTo(spi_context);
    MemoryContextDelete(work);
    laplace_spi_finish(spi_top);
    return ordered;
}

PG_FUNCTION_INFO_V1(pg_laplace_trajectory_continuations);
Datum
pg_laplace_trajectory_continuations(PG_FUNCTION_ARGS)
{
    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("trajectory_continuations: context must not be NULL")));
    bool bounded = !PG_ARGISNULL(1);
    int topk = bounded ? PG_GETARG_INT32(1) : 0;
    if (topk < 0)
        ereport(ERROR, (errmsg("trajectory_continuations: topk must not be negative")));
    InitMaterializedSRF(fcinfo, 0);
    ReturnSetInfo *result = (ReturnSetInfo *) fcinfo->resultinfo;
    int count;
    LaplaceContinuation *successors = laplace_trajectory_continuations(
        PG_GETARG_ARRAYTYPE_P(0), false, &count);
    if (bounded && count > topk) count = topk;
    for (int i = 0; i < count; ++i)
    {
        Datum values[3] = {hash128_to_datum(&successors[i].id), (Datum) 0,
                           Int64GetDatum(successors[i].occurrences)};
        bool nulls[3] = {false, true, false};
        tuplestore_putvalues(result->setResult, result->setDesc, values, nulls);
        pfree(DatumGetPointer(values[0]));
    }
    pfree(successors);
    return (Datum) 0;
}
