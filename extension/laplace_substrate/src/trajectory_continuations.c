#include "postgres.h"
#include "miscadmin.h"
#include "catalog/pg_type.h"
#include "catalog/namespace.h"
#include "parser/parse_func.h"
#include "utils/lsyscache.h"
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
#include "laplace/core/sql_catalog.h"
#include "content_trajectory_read.h"

/* GIN supplies containing trajectories, not sequence truth. Native matching
 * reads the mantissa-packed ordered occurrences once, including all runs and
 * separators. A suffix proposal narrows through progressively shorter indexed
 * suffix operands. The native matcher still elects the greatest exact stride;
 * a full-context miss does not immediately discard all but the final ID. */
static const char *UNPACK_QUERY =
    "SELECT public.ST_AsBinary(p.trajectory) "
    "FROM laplace.physicalities p "
    "WHERE p.type = 1 AND p.trajectory IS NOT NULL "
    "AND public.laplace_trajectory_constituent_ids(p.trajectory) @> $1";
static SPIPlanPtr unpack_plan = NULL;
static SPIPlanPtr bindings_plan = NULL;
static SPIPlanPtr containing_plan = NULL;

typedef struct ScopedTrajectory
{
    struct ScopedTrajectory *next;
    bytea *wkb;
} ScopedTrajectory;

struct LaplaceTrajectoryScope
{
    MemoryContext owner;
    HTAB *operands;
    HTAB *roots;
    ScopedTrajectory *trajectories;
    Oid as_binary;
};

LaplaceTrajectoryScope *
laplace_trajectory_scope_create(void)
{
    LaplaceTrajectoryScope *scope = palloc0(sizeof(*scope));
    HASHCTL ctl = {0};
    scope->owner = CurrentMemoryContext;
    Oid physicalities = get_relname_relid("physicalities", get_namespace_oid("laplace", false));
    Oid geometry = get_atttype(physicalities, get_attnum(physicalities, "trajectory"));
    scope->as_binary = LookupFuncName(list_make2(makeString("public"), makeString("st_asbinary")),
                                      1, &geometry, false);
    ctl.keysize = ctl.entrysize = sizeof(hash128_t);
    ctl.hcxt = scope->owner;
    scope->operands = hash_create("observed trajectory operands", 128, &ctl,
                                 HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    scope->roots = hash_create("observed trajectory roots", 256, &ctl,
                              HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    return scope;
}

static void
scope_retain_trajectory(Datum geometry, void *context)
{
    LaplaceTrajectoryScope *scope = context;
    MemoryContext previous = MemoryContextSwitchTo(scope->owner);
    ScopedTrajectory *entry = palloc(sizeof(*entry));
    entry->wkb = DatumGetByteaP(OidFunctionCall1(scope->as_binary, geometry));
    entry->next = scope->trajectories;
    scope->trajectories = entry;
    MemoryContextSwitchTo(previous);
}

static void
scope_add_id(HTAB *known, ArrayBuildState **batch, Datum value, MemoryContext work)
{
    bytea *id = DatumGetByteaPP(value);
    bool found;
    if (VARSIZE_ANY_EXHDR(id) != sizeof(hash128_t))
        ereport(ERROR, (errmsg("trajectory scope: ids must be 16 bytes")));
    hash_search(known, VARDATA_ANY(id), HASH_ENTER, &found);
    if (!found) *batch = accumArrayResult(*batch, value, false, BYTEAOID, work);
}

/* Each distinct observed identity is bound once, each resulting root loaded
 * once, under the caller's snapshot. The ordered physicality is retained as
 * packed WKB; no per-codepoint SQL, flattened corpus or label lookup. A miss
 * remains a miss and cannot silently reopen the whole corpus. */
void
laplace_trajectory_scope_extend(LaplaceTrajectoryScope *scope, ArrayType *operands)
{
    MemoryContext work, previous;
    ArrayBuildState *missing = NULL, *roots = NULL;
    Datum *ids;
    bool *nulls;
    int count;
    bool spi_top = false;
    if (ARR_NDIM(operands) > 1 || ARR_ELEMTYPE(operands) != BYTEAOID)
        ereport(ERROR, (errmsg("trajectory scope: operands must be a 1-D bytea array")));
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "trajectory scope: SPI_connect failed");
    work = AllocSetContextCreate(CurrentMemoryContext, "trajectory scope batch",
                                 ALLOCSET_DEFAULT_SIZES);
    previous = MemoryContextSwitchTo(work);
    deconstruct_array(operands, BYTEAOID, -1, false, TYPALIGN_INT, &ids, &nulls, &count);
    for (int i = 0; i < count; ++i)
    {
        if (nulls[i]) continue;
        scope_add_id(scope->operands, &missing, ids[i], work);
        scope_add_id(scope->roots, &roots, ids[i], work);
    }
    if (!bindings_plan)
    {
        Oid types[1] = {BYTEAARRAYOID};
        bindings_plan = SPI_prepare_cursor(laplace_sql_query_text("generation.observation_bindings"),
                                            1, types, CURSOR_OPT_PARALLEL_OK);
        if (!bindings_plan || SPI_keepplan(bindings_plan) != 0)
            elog(ERROR, "trajectory scope: preparing observation bindings failed");
    }
    if (missing)
    {
        Datum args[1] = {makeArrayResult(missing, work)};
        Portal portal = SPI_cursor_open(NULL, bindings_plan, args, NULL, true);
        if (!portal) elog(ERROR, "trajectory scope: opening binding cursor failed");
        for (;;)
        {
            SPI_cursor_fetch(portal, true, 1024);
            uint64 rows = SPI_processed;
            for (uint64 row = 0; row < rows; ++row)
                for (int col = 1; col <= 2; ++col)
                {
                    bool isnull;
                    Datum id = SPI_getbinval(SPI_tuptable->vals[row], SPI_tuptable->tupdesc, col, &isnull);
                    if (!isnull) scope_add_id(scope->roots, &roots, id, work);
                }
            if (SPI_tuptable) { SPI_freetuptable(SPI_tuptable); SPI_tuptable = NULL; }
            if (!rows) break;
            CHECK_FOR_INTERRUPTS();
        }
        SPI_cursor_close(portal);
    }
    if (roots)
        laplace_content_trajectory_read(DatumGetArrayTypeP(makeArrayResult(roots, work)),
                                        scope_retain_trajectory, scope);
    MemoryContextSwitchTo(previous);
    MemoryContextDelete(work);
    laplace_spi_finish(spi_top);
}

void
laplace_trajectory_scope_extend_containing(LaplaceTrajectoryScope *scope, ArrayType *members)
{
    bool spi_top = false;
    if (ARR_NDIM(members) > 1 || ARR_ELEMTYPE(members) != BYTEAOID)
        ereport(ERROR, (errmsg("trajectory scope: members must be a 1-D bytea array")));
    if (ArrayGetNItems(ARR_NDIM(members), ARR_DIMS(members)) == 0) return;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "trajectory scope: SPI_connect failed");
    MemoryContext work = AllocSetContextCreate(CurrentMemoryContext,
        "containing observation roots", ALLOCSET_DEFAULT_SIZES);
    MemoryContext previous = MemoryContextSwitchTo(work);
    ArrayBuildState *roots = NULL;
    Datum *ids;
    bool *nulls;
    int count;
    deconstruct_array(members, BYTEAOID, -1, false, TYPALIGN_INT, &ids, &nulls, &count);
    for (int i = 0; i < count; ++i)
        if (!nulls[i] && VARSIZE_ANY_EXHDR(DatumGetByteaPP(ids[i])) != sizeof(hash128_t))
            ereport(ERROR, (errmsg("trajectory scope: ids must be 16 bytes")));
    pfree(ids);
    pfree(nulls);
    /* The same fixed indexed containment operation used by Browse. No per-word
     * attestations, corpus-wide suffix fallback, or surrogate text index. */
    if (!containing_plan)
    {
        Oid types[1] = {BYTEAARRAYOID};
        containing_plan = SPI_prepare_cursor(laplace_sql_query_text("browse.containing"),
                                              1, types, CURSOR_OPT_PARALLEL_OK);
        if (!containing_plan || SPI_keepplan(containing_plan) != 0)
            elog(ERROR, "trajectory scope: preparing containment read failed");
    }
    Datum args[1] = {PointerGetDatum(members)};
    Portal portal = SPI_cursor_open(NULL, containing_plan, args, NULL, true);
    if (!portal) elog(ERROR, "trajectory scope: opening containment cursor failed");
    for (;;)
    {
        SPI_cursor_fetch(portal, true, 1024);
        uint64 rows = SPI_processed;
        for (uint64 row = 0; row < rows; ++row)
        {
            bool isnull;
            Datum id = SPI_getbinval(SPI_tuptable->vals[row], SPI_tuptable->tupdesc, 1, &isnull);
            if (!isnull) scope_add_id(scope->roots, &roots, id, work);
        }
        if (SPI_tuptable) { SPI_freetuptable(SPI_tuptable); SPI_tuptable = NULL; }
        if (!rows) break;
        CHECK_FOR_INTERRUPTS();
    }
    SPI_cursor_close(portal);
    if (roots)
        laplace_content_trajectory_read(DatumGetArrayTypeP(makeArrayResult(roots, work)),
                                        scope_retain_trajectory, scope);
    MemoryContextSwitchTo(previous);
    MemoryContextDelete(work);
    laplace_spi_finish(spi_top);
}

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
    return laplace_trajectory_continuations_scoped(context_array, suffix_backoff, NULL, count);
}

LaplaceContinuation *
laplace_trajectory_continuations_scoped(ArrayType *context_array, bool suffix_backoff,
                                       LaplaceTrajectoryScope *scope, int *count)
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
    if (scope)
    {
        for (ScopedTrajectory *entry = scope->trajectories; entry; entry = entry->next)
        {
            uint32 npoints;
            const unsigned char *points = laplace_trajectory_wkb_points(entry->wkb, &npoints);
            if (trajectory_match_suffixes(cleanup->matcher, points, npoints,
                                           record_successor, &state) != 0)
                elog(ERROR, "trajectory_continuations: invalid packed trajectory");
            CHECK_FOR_INTERRUPTS();
        }
    }
    if (!scope && !unpack_plan)
    {
        Oid types[1] = {BYTEAARRAYOID};
        SPIPlanPtr plan = SPI_prepare_cursor(UNPACK_QUERY, 1, types, CURSOR_OPT_PARALLEL_OK);
        if (!plan || SPI_keepplan(plan) != 0)
            elog(ERROR, "trajectory_continuations: preparing containment query failed");
        unpack_plan = plan;
    }
    /* A successful probe of suffix length k includes EVERY trajectory that
     * could match any longer suffix. The native matcher evaluates those longer
     * strides too, so its maximum and occurrence counts are globally complete.
     * On a miss, halve the operand length (rounding up). This requires at most
     * ceil(log2(n_context))+1 bulk index probes, preserves exact election, and
     * avoids reading the corpus-wide SPACE posting when a longer suffix works.
     * No constituent, separator, repeated occurrence or candidate is dropped. */
    int probe_length = n_context;
    while (!scope)
    {
        state.stride = (size_t) probe_length;
        ArrayType *probe = probe_length == n_context ? context_array
            : construct_array(ids + n_context - probe_length, probe_length,
                              BYTEAOID, -1, false, TYPALIGN_INT);
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
        if (probe != context_array) pfree(probe);
        if (!suffix_backoff || probe_length == 1 ||
            hash_get_num_entries(state.successors) > 0) break;
        probe_length = probe_length / 2 + probe_length % 2;
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
    LaplaceTrajectoryScope *scope = NULL;
    if (PG_NARGS() > 2 && !PG_ARGISNULL(2))
    {
        scope = laplace_trajectory_scope_create();
        laplace_trajectory_scope_extend(scope, PG_GETARG_ARRAYTYPE_P(2));
    }
    LaplaceContinuation *successors = laplace_trajectory_continuations_scoped(
        PG_GETARG_ARRAYTYPE_P(0), false, scope, &count);
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
