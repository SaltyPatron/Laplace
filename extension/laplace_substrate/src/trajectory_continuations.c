#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "spi_common.h"
#include "spi_nested.h"

/*
 * generation.trajectory_continuations(ctx, topk)
 *
 * SQL owns candidate reduction: one GIN containment probe over the complete
 * context.  C owns the ordinal work PostgreSQL is poorly suited to express:
 * strip separators, compare the rolling content context, count every successor
 * occurrence, carry the separator after the matched context, and rank.
 */

static const char *UNPACK_QUERY =
    "SELECT p.id, c.entity_id "
    "FROM laplace.physicalities p "
    "CROSS JOIN LATERAL public.laplace_trajectory_constituents(p.trajectory) c "
    "WHERE p.type = 1 "
    "AND p.trajectory IS NOT NULL "
    "AND public.laplace_trajectory_constituent_ids(p.trajectory) @> $1 "
    "ORDER BY p.id, c.ordinal";

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
        SPIPlanPtr plan = SPI_prepare(UNPACK_QUERY, 1, argtypes);

        if (plan == NULL)
            elog(ERROR, "trajectory_continuations: SPI_prepare(unpack) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "trajectory_continuations: SPI_keepplan(unpack) failed");
        unpack_plan = plan;
    }

    if (separator_plan == NULL)
    {
        SPIPlanPtr plan = SPI_prepare(SEPARATOR_QUERY, 0, NULL);

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
        char  current[16];
        bool  have_current = false;
        char *raw = NULL;
        int   n_raw = 0, raw_cap = 0;

        if (rc != SPI_OK_SELECT)
            elog(ERROR, "trajectory_continuations: unpack query failed: %s",
                 SPI_result_code_string(rc));

        for (uint64 row = 0; row < SPI_processed; row++)
        {
            HeapTuple tuple = SPI_tuptable->vals[row];
            TupleDesc desc  = SPI_tuptable->tupdesc;
            bool      container_null, token_null;
            Datum     container_datum = SPI_getbinval(
                tuple, desc, 1, &container_null);
            Datum     token_datum = SPI_getbinval(
                tuple, desc, 2, &token_null);
            bytea    *container;
            bytea    *token;

            if (container_null || token_null)
                continue;

            container = DatumGetByteaPP(container_datum);
            token = DatumGetByteaPP(token_datum);
            if (VARSIZE_ANY_EXHDR(container) != 16 ||
                VARSIZE_ANY_EXHDR(token) != 16)
                continue;

            if (!have_current || memcmp(current, VARDATA_ANY(container), 16) != 0)
            {
                if (have_current)
                    scan_trajectory(raw, n_raw, context, n_context,
                                    separators, successors, separator_counts);
                memcpy(current, VARDATA_ANY(container), 16);
                have_current = true;
                n_raw = 0;
            }

            if (n_raw == raw_cap)
            {
                raw_cap = raw_cap == 0 ? 32 : raw_cap * 2;
                raw = raw == NULL
                    ? (char *) palloc((Size) raw_cap * 16)
                    : (char *) repalloc(raw, (Size) raw_cap * 16);
            }
            memcpy(raw + (Size) n_raw * 16, VARDATA_ANY(token), 16);
            n_raw++;
        }

        if (have_current)
            scan_trajectory(raw, n_raw, context, n_context,
                            separators, successors, separator_counts);
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
