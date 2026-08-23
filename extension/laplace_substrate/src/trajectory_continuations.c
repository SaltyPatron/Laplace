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

#include "laplace/core/mantissa.h"

/*
 * generation.trajectory_continuations(ctx, topk)
 *
 * SQL owns candidate reduction: one GIN containment probe over the complete
 * context.  C owns the ordinal work PostgreSQL is poorly suited to express:
 * strip separators, compare the rolling content context, count every successor
 * occurrence, carry the separator after the matched context, and rank.
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

static const char *SEPARATOR_QUERY =
    "SELECT generation.separator_ids()";

static SPIPlanPtr unpack_plan = NULL;
static SPIPlanPtr separator_plan = NULL;

typedef struct IdEntry
{
    char key[16];
} IdEntry;

typedef struct SepCountEntry
{
    char  key[32];                 /* successor || separator */
    int64 count;
} SepCountEntry;

typedef struct SuccEntry
{
    char  key[16];
    int64 count;
    bool  has_separator;
    char  separator[16];
    int64 separator_count;
} SuccEntry;

static void
ensure_plans(void)
{
    if (unpack_plan == NULL)
    {
        Oid        argtypes[1] = { BYTEAARRAYOID };
        /*
         * SPI_prepare_cursor(..., CURSOR_OPT_PARALLEL_OK), not SPI_prepare.
         *
         * SPI_prepare plans with parallelism DISABLED. This probe is a GIN containment
         * scan across all 64 hash partitions of laplace.physicalities -- the partition key
         * is `id` and the predicate is on constituents, so nothing prunes and every
         * partition is scanned. Standalone the planner uses a Parallel Append with 7
         * workers and finishes in ~42 ms; planned through SPI_prepare the identical query
         * runs serially.
         *
         * The plan is read-only (SPI_execute_plan passes read_only = true), which is what
         * makes it eligible.
         */
        SPIPlanPtr plan = SPI_prepare_cursor(UNPACK_QUERY, 1, argtypes,
                                            CURSOR_OPT_PARALLEL_OK);

        if (plan == NULL)
            elog(ERROR, "trajectory_continuations: SPI_prepare(unpack) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "trajectory_continuations: SPI_keepplan(unpack) failed");
        unpack_plan = plan;
    }

    if (separator_plan == NULL)
    {
        SPIPlanPtr plan = SPI_prepare_cursor(SEPARATOR_QUERY, 0, NULL, CURSOR_OPT_PARALLEL_OK);

        if (plan == NULL)
            elog(ERROR, "trajectory_continuations: SPI_prepare(separators) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "trajectory_continuations: SPI_keepplan(separators) failed");
        separator_plan = plan;
    }
}

static bool
is_separator(HTAB *separators, const char *id)
{
    return hash_search(separators, id, HASH_FIND, NULL) != NULL;
}

static void
record_successor(HTAB *successors, HTAB *separator_counts,
                 const char *successor, const char *separator)
{
    SuccEntry *se;
    bool       found;

    se = (SuccEntry *) hash_search(successors, successor, HASH_ENTER, &found);
    if (!found)
    {
        se->count = 0;
        se->has_separator = false;
        se->separator_count = 0;
    }
    se->count++;

    /* PostgreSQL's ordered-set mode ignores NULL inputs. */
    if (separator != NULL)
    {
        char           key[32];
        SepCountEntry *sc;

        memcpy(key, successor, 16);
        memcpy(key + 16, separator, 16);
        sc = (SepCountEntry *) hash_search(separator_counts, key, HASH_ENTER, &found);
        if (!found) sc->count = 0;
        sc->count++;

        if (!se->has_separator || sc->count > se->separator_count ||
            (sc->count == se->separator_count &&
             memcmp(separator, se->separator, 16) < 0))
        {
            se->has_separator = true;
            memcpy(se->separator, separator, 16);
            se->separator_count = sc->count;
        }
    }
}

static void
scan_trajectory(const char *raw, int n_raw,
                const char *context, int n_context,
                HTAB *separators, HTAB *successors, HTAB *separator_counts)
{
    char *content;
    char *sep_after;
    bool *has_sep;
    int   n_content = 0;

    if (n_raw <= 0) return;

    content   = (char *) palloc((Size) n_raw * 16);
    sep_after = (char *) palloc((Size) n_raw * 16);
    has_sep   = (bool *) palloc0(sizeof(bool) * (Size) n_raw);

    for (int i = 0; i < n_raw; i++)
    {
        const char *id = raw + (Size) i * 16;

        if (is_separator(separators, id))
            continue;

        memcpy(content + (Size) n_content * 16, id, 16);
        if (i + 1 < n_raw && is_separator(separators, raw + (Size) (i + 1) * 16))
        {
            has_sep[n_content] = true;
            memcpy(sep_after + (Size) n_content * 16,
                   raw + (Size) (i + 1) * 16, 16);
        }
        n_content++;
    }

    for (int end = n_context - 1; end + 1 < n_content; end++)
    {
        int start = end - n_context + 1;

        if (memcmp(content + (Size) start * 16,
                   context, (Size) n_context * 16) != 0)
            continue;

        record_successor(successors, separator_counts,
                         content + (Size) (end + 1) * 16,
                         has_sep[end] ? sep_after + (Size) end * 16 : NULL);
    }

    pfree(content);
    pfree(sep_after);
    pfree(has_sep);
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
    HTAB          *separators;
    HTAB          *successors;
    HTAB          *separator_counts;
    HASHCTL        ctl;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("trajectory_continuations: context must not be NULL")));

    ctx_array = PG_GETARG_ARRAYTYPE_P(0);
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
    ensure_plans();

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(IdEntry);
    ctl.hcxt = CurrentMemoryContext;
    separators = hash_create("trajectory continuation separators", 128, &ctl,
                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(SuccEntry);
    ctl.hcxt = CurrentMemoryContext;
    successors = hash_create("trajectory continuation successors", 256, &ctl,
                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 32;
    ctl.entrysize = sizeof(SepCountEntry);
    ctl.hcxt = CurrentMemoryContext;
    separator_counts = hash_create("trajectory continuation separator modes", 256, &ctl,
                                   HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    /* Resolve the separator alphabet once for the whole call. */
    {
        int rc = SPI_execute_plan(separator_plan, NULL, NULL, true, 1);
        if (rc != SPI_OK_SELECT)
            elog(ERROR, "trajectory_continuations: separator query failed: %s",
                 SPI_result_code_string(rc));
        if (SPI_processed > 0)
        {
            bool isnull;
            Datum value = SPI_getbinval(SPI_tuptable->vals[0],
                                        SPI_tuptable->tupdesc, 1, &isnull);
            if (!isnull)
            {
                Datum *ids;
                bool  *nulls;
                int    n_ids;
                deconstruct_array(DatumGetArrayTypeP(value), BYTEAOID, -1, false,
                                  TYPALIGN_INT, &ids, &nulls, &n_ids);
                for (int i = 0; i < n_ids; i++)
                {
                    bytea *id;
                    bool   found;
                    if (nulls[i]) continue;
                    id = DatumGetByteaPP(ids[i]);
                    if (VARSIZE_ANY_EXHDR(id) != 16) continue;
                    (void) hash_search(separators, VARDATA_ANY(id), HASH_ENTER, &found);
                }
            }
        }
        SPI_freetuptable(SPI_tuptable);
    }

    /* One GIN-served candidate probe; all ordinal work stays in this loop. */
    {
        Datum args[1] = { PointerGetDatum(ctx_array) };
        char  nulls[1] = { ' ' };
        int   rc = SPI_execute_plan(unpack_plan, args, nulls, true, 0);
        char *raw = NULL;
        int   n_raw = 0, raw_cap = 0;

        if (rc != SPI_OK_SELECT)
            elog(ERROR, "trajectory_continuations: unpack query failed: %s",
                 SPI_result_code_string(rc));

        for (uint64 row = 0; row < SPI_processed; row++)
        {
            HeapTuple            tuple = SPI_tuptable->vals[row];
            TupleDesc            desc  = SPI_tuptable->tupdesc;
            bool                 wkb_null;
            Datum                wkb_datum = SPI_getbinval(tuple, desc, 1, &wkb_null);
            bytea               *wkb;
            const unsigned char *b;
            Size                 len;
            uint32               wkb_type;
            uint32               npoints;
            Size                 need;

            if (wkb_null)
                continue;

            wkb = DatumGetByteaPP(wkb_datum);
            b = (const unsigned char *) VARDATA_ANY(wkb);
            len = (Size) VARSIZE_ANY_EXHDR(wkb);

            /* ISO WKB from ST_AsBinary: byte order, uint32 type, then for
             * LINESTRING ZM (3002) uint32 npoints and npoints*4 float8s;
             * POINT ZM (3001) is one vertex with no count. Same framing checks
             * as geometry_successors.c -- machine-order NDR only, and anything
             * else is refused loudly rather than mis-decoded. */
            if (len < 5 || b[0] != 1)
                elog(ERROR,
                     "trajectory_continuations: unexpected WKB framing "
                     "(len=%zu, order=%d)", (size_t) len, len > 0 ? b[0] : -1);
            memcpy(&wkb_type, b + 1, 4);
            if (wkb_type == 3001u)
            {
                npoints = 1;
                b += 5;
                need = (Size) 32;
            }
            else if (wkb_type == 3002u)
            {
                if (len < 9)
                    elog(ERROR, "trajectory_continuations: truncated WKB");
                memcpy(&npoints, b + 5, 4);
                b += 9;
                need = (Size) npoints * 32;
            }
            else
                elog(ERROR,
                     "trajectory_continuations: trajectory is not POINT/"
                     "LINESTRING ZM (wkb type %u)", wkb_type);
            if ((Size) (len - (Size) (b - (const unsigned char *) VARDATA_ANY(wkb))) < need)
                elog(ERROR, "trajectory_continuations: truncated WKB body");

            if ((int) npoints > raw_cap)
            {
                raw_cap = (int) npoints;
                raw = raw == NULL
                    ? (char *) palloc((Size) raw_cap * 16)
                    : (char *) repalloc(raw, (Size) raw_cap * 16);
            }

            n_raw = 0;
            for (uint32 v = 0; v < npoints; v++)
            {
                double             vertex[4];
                mantissa_payload_t payload;

                memcpy(vertex, b + (Size) v * 32, 32);
                mantissa_unpack(vertex, &payload);
                memcpy(raw + (Size) n_raw * 16, &payload.entity_id, 16);
                n_raw++;
            }

            scan_trajectory(raw, n_raw, context, n_context,
                            separators, successors, separator_counts);
            CHECK_FOR_INTERRUPTS();
        }

        if (raw != NULL) pfree(raw);
        SPI_freetuptable(SPI_tuptable);
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

            if (ordered[i].has_separator)
            {
                bytea *separator = (bytea *) palloc(VARHDRSZ + 16);
                SET_VARSIZE(separator, VARHDRSZ + 16);
                memcpy(VARDATA(separator), ordered[i].separator, 16);
                values[1] = PointerGetDatum(separator);
            }
            else
            {
                values[1] = (Datum) 0;
                nulls[1] = true;
            }

            values[2] = Int64GetDatum(ordered[i].count);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        }
    }

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
