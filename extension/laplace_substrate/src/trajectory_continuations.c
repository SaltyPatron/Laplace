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

#include "laplace/core/mantissa.h"

/*
 * generation.trajectory_continuations(ctx, topk)
 *
 * SQL owns candidate reduction: one GIN containment probe over the complete
 * context.  C owns the ordinal work PostgreSQL is poorly suited to express:
 * compare exact ordered context, count every successor occurrence and rank.
 * Separators are content identities and remain in the emitted sequence.
 */

/*
 * ONE WKB BLOB PER TRAJECTORY, not a per-vertex LATERAL. This is the identical
 * transformation geometry_successors.c took on 2026-08-21 (#939), whose header
 * measured the shape this replaced at 136.7s for a 20-row question because
 * `CROSS JOIN LATERAL laplace_trajectory_constituents(...) ORDER BY p.id,
 * c.ordinal` materialises AND SORTS one executor tuple per vertex -- ~60 bytes
 * of overhead around 32 bytes of payload -- to answer a top-k question.
 *
 * Measured here 2026-08-23 before the change: continuations for `New` took
 * 2722ms warm, and EXPLAIN ANALYZE showed the GIN probe correctly reducing to
 * 23,606 candidate containers and the LATERAL then expanding them back out to
 * 1,966,012 rows, which were sorted. Reduce before expanding (Rule #5).
 *
 * The sort bought nothing: vertex order inside a LINESTRING ZM WKB IS ordinal
 * order, so the sequence arrives already ordered and the decode below reads it
 * with the same mantissa_unpack the rest of the tree uses.
 *
 * Note laplace_trajectory_constituent_ids() stays in the predicate and NOWHERE
 * else: it is the DEDUPED containment projection the GIN index is built on
 * (measured: it differs from the ordinal-ordered sequence for 45% of entities),
 * so using it to walk adjacency would silently drop repeats and order.
 */
static const char *UNPACK_QUERY =
    "SELECT public.ST_AsBinary(p.trajectory) "
    "FROM laplace.physicalities p "
    "WHERE p.type = 1 "
    "AND p.trajectory IS NOT NULL "
    "AND public.laplace_trajectory_constituent_ids(p.trajectory) @> $1";

typedef struct SuccEntry
{
    char key[16];
    int64 count;
} SuccEntry;

static SPIPlanPtr unpack_plan = NULL;

static void
record_successor(HTAB *successors, const hash128_t *successor)
{
    bool found;
    SuccEntry *entry = hash_search(successors, successor, HASH_ENTER, &found);
    if (!found) entry->count = 0;
    if (entry->count == PG_INT64_MAX)
        ereport(ERROR, (errmsg("trajectory_continuations: occurrence count overflow")));
    ++entry->count;
}

/* KMP consumes the ordinal stream, including separators and repeated runs.
 * Overlapping matches count independently. The WKB and prefix table bound
 * memory; an RLE trajectory is never expanded into a second identity array. */
static void
scan_trajectory(bytea *wkb, const char *context, int n_context,
                const int *prefix, HTAB *successors)
{
    uint32 npoints;
    const unsigned char *points = laplace_trajectory_wkb_points(wkb, &npoints);
    int matched = 0;
    for (uint32 v = 0; v < npoints; ++v)
    {
        double vertex[4];
        mantissa_payload_t payload;
        memcpy(vertex, points + (Size) v * 32, sizeof(vertex));
        mantissa_unpack(vertex, &payload);
        uint32 run = payload.run_length ? payload.run_length : 1;
        for (uint32 r = 0; r < run; ++r)
        {
            if (matched == n_context)
            {
                record_successor(successors, &payload.entity_id);
                matched = prefix[matched - 1];
            }
            while (matched > 0 && memcmp(context + (Size) matched * 16,
                                        &payload.entity_id, 16) != 0)
                matched = prefix[matched - 1];
            if (memcmp(context + (Size) matched * 16, &payload.entity_id, 16) == 0)
                ++matched;
            CHECK_FOR_INTERRUPTS();
        }
    }
}

static int
successor_cmp(const void *a, const void *b)
{
    const SuccEntry *x = (const SuccEntry *) a;
    const SuccEntry *y = (const SuccEntry *) b;

    if (x->count > y->count) return -1;
    if (x->count < y->count) return 1;
    return memcmp(x->key, y->key, 16);
}

PG_FUNCTION_INFO_V1(pg_laplace_trajectory_continuations);

Datum
pg_laplace_trajectory_continuations(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType     *ctx_array;
    Datum         *ctx_datums;
    bool          *ctx_nulls;
    int            n_context;
    char          *context;
    int32          topk = 0;
    bool           bounded;
    bool           spi_top = false;
    HTAB          *successors;
    int           *prefix;
    HASHCTL        ctl;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("trajectory_continuations: context must not be NULL")));

    ctx_array = PG_GETARG_ARRAYTYPE_P(0);
    if (ARR_NDIM(ctx_array) > 1 || ARR_ELEMTYPE(ctx_array) != BYTEAOID)
        ereport(ERROR, (errmsg("trajectory_continuations: context must be a 1-D bytea array")));
    bounded = !PG_ARGISNULL(1);
    if (bounded)
    {
        topk = PG_GETARG_INT32(1);
        if (topk < 0)
            ereport(ERROR, (errmsg("trajectory_continuations: topk must not be negative")));
    }

    deconstruct_array(ctx_array, BYTEAOID, -1, false, TYPALIGN_INT,
                      &ctx_datums, &ctx_nulls, &n_context);
    if (n_context < 1)
        ereport(ERROR, (errmsg("trajectory_continuations: context must not be empty")));
    if ((uint64) n_context > (uint64) (MaxAllocSize / 16))
        ereport(ERROR,
                (errmsg("trajectory_continuations: context exceeds PostgreSQL allocation capacity")));

    context = (char *) palloc((Size) n_context * 16);
    for (int i = 0; i < n_context; i++)
    {
        bytea *id;
        if (ctx_nulls[i])
            ereport(ERROR, (errmsg("trajectory_continuations: context contains NULL")));
        id = DatumGetByteaPP(ctx_datums[i]);
        if (VARSIZE_ANY_EXHDR(id) != 16)
            ereport(ERROR, (errmsg("trajectory_continuations: context ids must be 16 bytes")));
        memcpy(context + (Size) i * 16, VARDATA_ANY(id), 16);
    }

    InitMaterializedSRF(fcinfo, 0);
    if (bounded && topk == 0)
        return (Datum) 0;

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "trajectory_continuations: SPI_connect failed");

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(SuccEntry);
    ctl.hcxt = CurrentMemoryContext;
    successors = hash_create("trajectory continuation successors", 256, &ctl,
                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    prefix = palloc0(sizeof(int) * n_context);
    for (int i = 1, matched = 0; i < n_context; ++i)
    {
        while (matched > 0 && memcmp(context + (Size) i * 16,
                                    context + (Size) matched * 16, 16) != 0)
            matched = prefix[matched - 1];
        if (memcmp(context + (Size) i * 16, context + (Size) matched * 16, 16) == 0)
            ++matched;
        prefix[i] = matched;
    }

    /* Stream the complete indexed candidate set in bounded internal pages. */
    {
        Datum args[1] = { PointerGetDatum(ctx_array) };
        if (unpack_plan == NULL)
        {
            Oid argtypes[1] = { BYTEAARRAYOID };
            SPIPlanPtr plan = SPI_prepare(UNPACK_QUERY, 1, argtypes);
            if (plan == NULL || SPI_keepplan(plan) != 0)
                elog(ERROR, "trajectory_continuations: could not prepare containment query");
            unpack_plan = plan;
        }
        Portal portal = SPI_cursor_open(NULL, unpack_plan, args, NULL, true);
        if (portal == NULL)
            elog(ERROR, "trajectory_continuations: could not open containment cursor");
        for (;;)
        {
            SPI_cursor_fetch(portal, true, 1024);
            uint64 rows = SPI_processed;
            for (uint64 row = 0; row < rows; ++row)
            {
                bool isnull;
                Datum datum = SPI_getbinval(SPI_tuptable->vals[row],
                                            SPI_tuptable->tupdesc, 1, &isnull);
                if (!isnull)
                    scan_trajectory(DatumGetByteaPP(datum), context, n_context,
                                    prefix, successors);
            }
            if (SPI_tuptable != NULL)
            {
                SPI_freetuptable(SPI_tuptable);
                SPI_tuptable = NULL;
            }
            if (rows == 0) break;
            CHECK_FOR_INTERRUPTS();
        }
        SPI_cursor_close(portal);
    }

    /* Total order: weight descending, successor id ascending. */
    {
        long            entries = hash_get_num_entries(successors);
        int             n;
        int             m = 0;
        SuccEntry      *ordered;
        HASH_SEQ_STATUS seq;
        SuccEntry      *se;

        if (entries > INT_MAX ||
            (uint64) entries > (uint64) (MaxAllocSize / sizeof(SuccEntry)))
            ereport(ERROR,
                    (errmsg("trajectory_continuations: successor set exceeds PostgreSQL allocation capacity")));
        n = (int) entries;
        ordered = (SuccEntry *) palloc(sizeof(SuccEntry) * (n > 0 ? n : 1));

        hash_seq_init(&seq, successors);
        while ((se = (SuccEntry *) hash_seq_search(&seq)) != NULL)
            ordered[m++] = *se;
        qsort(ordered, (size_t) m, sizeof(SuccEntry), successor_cmp);

        if (bounded && m > topk) m = topk;
        for (int i = 0; i < m; i++)
        {
            bytea *object = (bytea *) palloc(VARHDRSZ + 16);
            Datum  values[3];
            bool   nulls[3] = { false, false, false };

            SET_VARSIZE(object, VARHDRSZ + 16);
            memcpy(VARDATA(object), ordered[i].key, 16);
            values[0] = PointerGetDatum(object);

            /* Compatibility column: the exact next constituent itself
             * carries any separator identity, so no bytes are synthesized. */
            values[1] = (Datum) 0;
            nulls[1] = true;

            values[2] = Int64GetDatum(ordered[i].count);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
            pfree(object);
        }
    }

    hash_destroy(successors);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
