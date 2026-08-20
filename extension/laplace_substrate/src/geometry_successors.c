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

/*
 * structural.geometry_successors_batch(points, limit, window)
 *   -> TABLE(point_id bytea, successor_id bytea, seen bigint)
 *
 * A sequence-projection operator: for a content POINT, walk every trajectory
 * that CONTAINS it and return the next CONTENT constituent in each, aggregated
 * by frequency. "What follows X in the projected content stream" is read from the
 * content-addressed geometry -- the same knowledge PRECEDES materialized and
 * gen_corpus rebuilt into a flat RAM suffix array, needed by neither: the
 * trajectory already holds the ordered sequence.  Removing separators is an
 * explicit property of THIS text-generation projection.  It does not assert a
 * direct semantic edge between the two surviving entities: composition remains
 * point -> containing trajectory -> constituent (for example, Captain -> the
 * composed "Captain Ahab" observation -> Ahab).
 *
 * WHY NATIVE (the valet/orchestrator law): SQL cannot do this at scale -- a
 * per-row LATERAL unpack + per-token whitespace render times out. The walk
 * (find the point, skip separators, take the successor, aggregate) is
 * pointer/loop work in C.
 *
 * TWO set-oriented wins over the naive shape:
 *   1. ONE containment+unpack query (`&& $1`, GIN-served) for every requested
 *      root, streamed and grouped by container in C -- not one query per root.
 *   2. ONE separator-alphabet read per call.  Membership is then an O(1) hash
 *      probe; no backend-local classification cache can go stale or impose a
 *      multi-second first-call tax on every connection.
 *
 * Correctness: laplace_trajectory_constituents() is the FULL, non-deduped,
 * ordinal-ordered sequence (unlike laplace_trajectory_constituent_ids(), which
 * dedups for the containment index and would break adjacency).
 */

static const char *BATCH_UNPACK_QUERY =
    "SELECT p.id, c.entity_id "
    "FROM laplace.physicalities p "
    "CROSS JOIN LATERAL public.laplace_trajectory_constituents(p.trajectory) c "
    "WHERE p.type = $2 "
    "AND p.trajectory IS NOT NULL "
    "AND public.laplace_trajectory_constituent_ids(p.trajectory) && $1 "
    "ORDER BY p.id, c.ordinal";

static const char *SEPARATOR_QUERY =
    "SELECT generation.separator_ids()";

static SPIPlanPtr batch_unpack_plan = NULL;
static SPIPlanPtr separator_plan = NULL;

typedef struct IdEntry { char key[16]; } IdEntry;

typedef struct RootEntry
{
    char  key[16];
    int32 index;
} RootEntry;

typedef struct PairEntry
{
    char  key[32];
    int64 count;
} PairEntry;

typedef struct BatchResult
{
    char  key[32];
    int32 root_index;
    int64 count;
} BatchResult;

static void
ensure_plans(void)
{
    if (batch_unpack_plan == NULL)
    {
        Oid        argtypes[2] = { BYTEAARRAYOID, INT2OID };
        SPIPlanPtr plan = SPI_prepare(BATCH_UNPACK_QUERY, 2, argtypes);

        if (plan == NULL)
            elog(ERROR, "geometry_successors: SPI_prepare(batch unpack) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "geometry_successors: SPI_keepplan(batch unpack) failed");
        batch_unpack_plan = plan;
    }

    if (separator_plan == NULL)
    {
        SPIPlanPtr plan = SPI_prepare(SEPARATOR_QUERY, 0, NULL);

        if (plan == NULL)
            elog(ERROR, "geometry_successors: SPI_prepare(separators) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "geometry_successors: SPI_keepplan(separators) failed");
        separator_plan = plan;
    }
}

static HTAB *
load_separators(MemoryContext cxt)
{
    HASHCTL ctl;
    HTAB   *separators;
    int     rc;

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(IdEntry);
    ctl.hcxt = cxt;
    separators = hash_create("geometry successor separators", 128, &ctl,
                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    rc = SPI_execute_plan(separator_plan, NULL, NULL, true, 1);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "geometry_successors: separator query failed: %s",
             SPI_result_code_string(rc));
    if (SPI_processed > 0)
    {
        bool  isnull;
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

                if (nulls[i])
                    continue;
                id = DatumGetByteaPP(ids[i]);
                if (VARSIZE_ANY_EXHDR(id) != 16)
                    continue;
                (void) hash_search(separators, VARDATA_ANY(id),
                                   HASH_ENTER, &found);
            }
        }
    }
    SPI_freetuptable(SPI_tuptable);
    return separators;
}

static void
record_batch_pair(HTAB *pairs, const char *root, const char *successor)
{
    char       key[32];
    PairEntry *entry;
    bool       found;

    memcpy(key, root, 16);
    memcpy(key + 16, successor, 16);
    entry = (PairEntry *) hash_search(pairs, key, HASH_ENTER, &found);
    if (!found)
        entry->count = 0;
    entry->count++;
}

static void
scan_batch_trajectory(const char *raw, int n_raw, HTAB *roots, int n_roots,
                      HTAB *separators, HTAB *pairs, int window, bool backward)
{
    bool *seen;

    if (n_raw < 1)
        return;

    seen = (bool *) palloc0(sizeof(bool) * (Size) n_roots);
    for (int i = 0; i < n_raw; i++)
    {
        const char *token = raw + (Size) i * 16;
        RootEntry  *root = (RootEntry *) hash_search(roots, token, HASH_FIND, NULL);
        int         considered = 0;

        if (root == NULL || seen[root->index])
            continue;
        seen[root->index] = true;

        for (int j = i + (backward ? -1 : 1);
             j >= 0 && j < n_raw && considered < window;
             j += backward ? -1 : 1)
        {
            const char *candidate = raw + (Size) j * 16;

            /* The scalar forward walk ignores later occurrences of its root;
             * preserve that exact window accounting in the batched form. */
            if (!backward && memcmp(candidate, root->key, 16) == 0)
                continue;

            considered++;
            if (hash_search(separators, candidate, HASH_FIND, NULL) != NULL)
                continue;

            record_batch_pair(pairs, root->key, candidate);
            break;
        }
    }
    pfree(seen);
}

static int
batch_result_cmp(const void *a, const void *b)
{
    const BatchResult *x = (const BatchResult *) a;
    const BatchResult *y = (const BatchResult *) b;

    if (x->root_index < y->root_index) return -1;
    if (x->root_index > y->root_index) return 1;
    if (x->count > y->count) return -1;
    if (x->count < y->count) return 1;
    return memcmp(x->key + 16, y->key + 16, 16);
}

PG_FUNCTION_INFO_V1(pg_laplace_geometry_successors_batch);

Datum
pg_laplace_geometry_successors_batch(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType     *points_array;
    Datum         *point_datums;
    bool          *point_nulls;
    int            n_input;
    int32          limit_rows;
    int32          window;
    bool           backward;
    int16          physicality_type;
    bool           spi_top = false;
    HASHCTL        ctl;
    HTAB          *roots;
    HTAB          *separators;
    HTAB          *pairs;
    char          *root_ids;
    int            n_roots = 0;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("geometry_successors_batch: points must not be NULL")));

    points_array = PG_GETARG_ARRAYTYPE_P(0);
    limit_rows = PG_ARGISNULL(1) ? 20 : PG_GETARG_INT32(1);
    window = PG_ARGISNULL(2) ? 8 : PG_GETARG_INT32(2);
    backward = (PG_NARGS() > 3 && !PG_ARGISNULL(3)) ? PG_GETARG_BOOL(3) : false;
    physicality_type = (PG_NARGS() > 4 && !PG_ARGISNULL(4)) ? PG_GETARG_INT16(4) : 1;
    if (limit_rows < 0)
        ereport(ERROR, (errmsg("geometry_successors_batch: limit must not be negative")));
    if (window < 0)
        ereport(ERROR, (errmsg("geometry_successors_batch: window must not be negative")));

    deconstruct_array(points_array, BYTEAOID, -1, false, TYPALIGN_INT,
                      &point_datums, &point_nulls, &n_input);
    InitMaterializedSRF(fcinfo, 0);
    if (n_input < 1 || limit_rows == 0 || window == 0)
        return (Datum) 0;

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(RootEntry);
    ctl.hcxt = CurrentMemoryContext;
    roots = hash_create("geometry successor batch roots", n_input, &ctl,
                        HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    root_ids = (char *) palloc((Size) n_input * 16);

    for (int i = 0; i < n_input; i++)
    {
        bytea    *point;
        RootEntry *root;
        bool       found;

        if (point_nulls[i])
            ereport(ERROR, (errmsg("geometry_successors_batch: points contain NULL")));
        point = DatumGetByteaPP(point_datums[i]);
        if (VARSIZE_ANY_EXHDR(point) != 16)
            ereport(ERROR, (errmsg("geometry_successors_batch: points must be 16-byte ids")));

        root = (RootEntry *) hash_search(roots, VARDATA_ANY(point),
                                         HASH_ENTER, &found);
        if (!found)
        {
            root->index = n_roots;
            memcpy(root_ids + (Size) n_roots * 16, VARDATA_ANY(point), 16);
            n_roots++;
        }
    }

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "geometry_successors_batch: SPI_connect failed");
    ensure_plans();
    separators = load_separators(CurrentMemoryContext);

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 32;
    ctl.entrysize = sizeof(PairEntry);
    ctl.hcxt = CurrentMemoryContext;
    pairs = hash_create("geometry successor batch pairs", n_roots * 64,
                        &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    {
        Datum     *normalized_datums = (Datum *) palloc(sizeof(Datum) * (Size) n_roots);
        ArrayType *normalized;
        Datum      args[2];
        char       nulls[2] = { ' ', ' ' };
        Portal     portal;
        char        current[16];
        bool        have_current = false;
        char       *raw = NULL;
        int         n_raw = 0;
        int         raw_cap = 0;

        for (int i = 0; i < n_roots; i++)
        {
            bytea *point = (bytea *) palloc(VARHDRSZ + 16);

            SET_VARSIZE(point, VARHDRSZ + 16);
            memcpy(VARDATA(point), root_ids + (Size) i * 16, 16);
            normalized_datums[i] = PointerGetDatum(point);
        }
        normalized = construct_array(normalized_datums, n_roots, BYTEAOID,
                                     -1, false, TYPALIGN_INT);
        args[0] = PointerGetDatum(normalized);
        args[1] = Int16GetDatum(physicality_type);
        portal = SPI_cursor_open(NULL, batch_unpack_plan, args, nulls, true);
        if (portal == NULL)
            elog(ERROR, "geometry_successors_batch: unpack cursor failed: %s",
                 SPI_result_code_string(SPI_result));

        for (;;)
        {
            SPI_cursor_fetch(portal, true, 50000);
            if (SPI_processed == 0)
                break;

            for (uint64 r = 0; r < SPI_processed; r++)
            {
                HeapTuple tuple = SPI_tuptable->vals[r];
                TupleDesc desc = SPI_tuptable->tupdesc;
                bool      container_null;
                bool      token_null;
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

                if (!have_current ||
                    memcmp(current, VARDATA_ANY(container), 16) != 0)
                {
                    if (have_current)
                        scan_batch_trajectory(raw, n_raw, roots, n_roots,
                                              separators, pairs, window, backward);
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
            SPI_freetuptable(SPI_tuptable);
            CHECK_FOR_INTERRUPTS();
        }
        SPI_cursor_close(portal);
        if (have_current)
            scan_batch_trajectory(raw, n_raw, roots, n_roots,
                                  separators, pairs, window, backward);
        if (raw != NULL)
            pfree(raw);
    }

    {
        int             n_pairs = (int) hash_get_num_entries(pairs);
        BatchResult    *ordered = (BatchResult *) palloc(
            sizeof(BatchResult) * (n_pairs > 0 ? (Size) n_pairs : 1));
        HASH_SEQ_STATUS seq;
        PairEntry      *pair;
        int             n = 0;
        int             current_root = -1;
        int             emitted = 0;

        hash_seq_init(&seq, pairs);
        while ((pair = (PairEntry *) hash_seq_search(&seq)) != NULL)
        {
            RootEntry *root = (RootEntry *) hash_search(
                roots, pair->key, HASH_FIND, NULL);

            memcpy(ordered[n].key, pair->key, 32);
            ordered[n].root_index = root->index;
            ordered[n].count = pair->count;
            n++;
        }
        qsort(ordered, (size_t) n, sizeof(BatchResult), batch_result_cmp);

        for (int i = 0; i < n; i++)
        {
            bytea *root;
            bytea *successor;
            Datum  values[3];
            bool   nulls[3] = { false, false, false };

            if (ordered[i].root_index != current_root)
            {
                current_root = ordered[i].root_index;
                emitted = 0;
            }
            if (emitted >= limit_rows)
                continue;
            emitted++;

            root = (bytea *) palloc(VARHDRSZ + 16);
            successor = (bytea *) palloc(VARHDRSZ + 16);
            SET_VARSIZE(root, VARHDRSZ + 16);
            SET_VARSIZE(successor, VARHDRSZ + 16);
            memcpy(VARDATA(root), ordered[i].key, 16);
            memcpy(VARDATA(successor), ordered[i].key + 16, 16);
            values[0] = PointerGetDatum(root);
            values[1] = PointerGetDatum(successor);
            values[2] = Int64GetDatum(ordered[i].count);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc,
                                 values, nulls);
        }
    }

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
