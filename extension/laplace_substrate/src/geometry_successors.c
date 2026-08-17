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
 * structural.geometry_successors(point, limit, window)
 *   -> TABLE(successor_id bytea, seen bigint)
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
 *   1. ONE containment+unpack query (single-key `@> ARRAY[$1]`, GIN-served),
 *      streamed and grouped by container in C -- NOT 543 per-container fetches.
 *   2. ONE separator-alphabet read per call.  Membership is then an O(1) hash
 *      probe; no backend-local classification cache can go stale or impose a
 *      multi-second first-call tax on every connection.
 *
 * Correctness: laplace_trajectory_constituents() is the FULL, non-deduped,
 * ordinal-ordered sequence (unlike laplace_trajectory_constituent_ids(), which
 * dedups for the containment index and would break adjacency).
 */

static const char *UNPACK_QUERY =
    "SELECT p.id, c.entity_id "
    "FROM laplace.physicalities p "
    "CROSS JOIN LATERAL public.laplace_trajectory_constituents(p.trajectory) c "
    "WHERE p.type = 1 "
    "AND p.trajectory IS NOT NULL "
    "AND public.laplace_trajectory_constituent_ids(p.trajectory) @> ARRAY[$1]::bytea[] "
    "ORDER BY p.id, c.ordinal";

static const char *BATCH_UNPACK_QUERY =
    "SELECT p.id, c.entity_id "
    "FROM laplace.physicalities p "
    "CROSS JOIN LATERAL public.laplace_trajectory_constituents(p.trajectory) c "
    "WHERE p.type = 1 "
    "AND p.trajectory IS NOT NULL "
    "AND public.laplace_trajectory_constituent_ids(p.trajectory) && $1 "
    "ORDER BY p.id, c.ordinal";

static const char *SEPARATOR_QUERY =
    "SELECT generation.separator_ids()";

static SPIPlanPtr unpack_plan = NULL;
static SPIPlanPtr batch_unpack_plan = NULL;
static SPIPlanPtr separator_plan = NULL;

typedef struct IdEntry { char key[16]; } IdEntry;

/* Aggregation entry: successor id -> count. */
typedef struct SuccEntry { char key[16]; int64 count; } SuccEntry;

static int
successor_cmp(const void *a, const void *b)
{
    const SuccEntry *x = (const SuccEntry *) a;
    const SuccEntry *y = (const SuccEntry *) b;

    if (x->count > y->count) return -1;
    if (x->count < y->count) return 1;
    return memcmp(x->key, y->key, 16);
}

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
    if (unpack_plan == NULL)
    {
        Oid        argtypes[1] = { BYTEAOID };
        SPIPlanPtr plan = SPI_prepare(UNPACK_QUERY, 1, argtypes);

        if (plan == NULL)
            elog(ERROR, "geometry_successors: SPI_prepare(unpack) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "geometry_successors: SPI_keepplan(unpack) failed");
        unpack_plan = plan;
    }

    if (batch_unpack_plan == NULL)
    {
        Oid        argtypes[1] = { BYTEAARRAYOID };
        SPIPlanPtr plan = SPI_prepare(BATCH_UNPACK_QUERY, 1, argtypes);

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
store_candidate_list(char ***lists, int **lens, int *count, int *capacity,
                     char *list, int length)
{
    if (list == NULL || length < 1)
        return;

    if (*count == *capacity)
    {
        *capacity = *capacity == 0 ? 1024 : *capacity * 2;
        *lists = *lists == NULL
            ? (char **) palloc(sizeof(char *) * (Size) *capacity)
            : (char **) repalloc(*lists, sizeof(char *) * (Size) *capacity);
        *lens = *lens == NULL
            ? (int *) palloc(sizeof(int) * (Size) *capacity)
            : (int *) repalloc(*lens, sizeof(int) * (Size) *capacity);
    }

    (*lists)[*count] = list;
    (*lens)[*count] = length;
    (*count)++;
}

PG_FUNCTION_INFO_V1(pg_laplace_geometry_successors);

Datum
pg_laplace_geometry_successors(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    bytea  *point;
    int32   limit_rows, window;
    bool    backward;
    bool    spi_top = false;

    char  **cand_lists = NULL;   /* per container: ncand*16 bytes of ids */
    int    *cand_lens  = NULL;
    int     n_cont = 0, cont_cap = 0;

    HTAB   *separators;
    HTAB   *succ_agg;
    HASHCTL ctl;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("geometry_successors: point must not be NULL")));
    point      = PG_GETARG_BYTEA_PP(0);
    limit_rows = PG_ARGISNULL(1) ? 20 : PG_GETARG_INT32(1);
    window     = PG_ARGISNULL(2) ? 8  : PG_GETARG_INT32(2);
    /* Direction. The trajectory holds the exact ordered sequence, so "what comes
     * BEFORE x" is as readable as "what comes after" -- same containment query,
     * same separator set, same aggregation. Only the candidate walk flips.
     *
     * PG_NARGS() guard is load-bearing: the extension version hash is fixed at
     * configure time, so a rebuilt .so can be installed while the catalog still
     * declares the OLD 3-arg signature. Reading arg 3 unconditionally in that
     * window reads past fcinfo->args and can take down the backend. Default to
     * forward whenever the argument was not declared. */
    backward   = (PG_NARGS() > 3 && !PG_ARGISNULL(3)) ? PG_GETARG_BOOL(3) : false;
    if (VARSIZE_ANY_EXHDR(point) != 16)
        ereport(ERROR, (errmsg("geometry_successors: point must be a 16-byte id")));
    if (limit_rows < 1) limit_rows = 20;
    if (window < 1)     window = 8;

    InitMaterializedSRF(fcinfo, 0);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "geometry_successors: SPI_connect failed");
    ensure_plans();

    separators = load_separators(CurrentMemoryContext);

    /* (1) ONE containment+unpack query, grouped by physical observation. */
    {
        Datum args[1] = { PointerGetDatum(point) };
        char  nulls[1] = { ' ' };
        Portal portal;
        char  cur_sent[16];
        bool  have_sent = false;
        int   after = -1;               /* -1 = point not yet seen in this sent */
        char *clist = NULL;
        int   ncand = 0;
        /* backward walk: rolling window of the ids seen BEFORE the point in this
         * container. On first hit of the point we materialise it nearest-first,
         * so the "first content token wins" rule below picks the nearest
         * non-separator predecessor -- the exact mirror of the forward case. */
        char *ring = NULL;
        int   ring_n = 0, ring_head = 0;

        portal = SPI_cursor_open(NULL, unpack_plan, args, nulls, true);
        if (portal == NULL)
            elog(ERROR, "geometry_successors: unpack cursor failed: %s",
                 SPI_result_code_string(SPI_result));

        for (;;)
        {
            SPI_cursor_fetch(portal, true, 50000);
            if (SPI_processed == 0)
                break;

            for (uint64 r = 0; r < SPI_processed; r++)
            {
                HeapTuple tup = SPI_tuptable->vals[r];
                TupleDesc td  = SPI_tuptable->tupdesc;
                bool   s_null, t_null;
                Datum  s_datum = SPI_getbinval(tup, td, 1, &s_null);
                Datum  t_datum = SPI_getbinval(tup, td, 2, &t_null);
                bytea *sb;
                bytea *tb;

                if (s_null || t_null)
                    continue;

                sb = DatumGetByteaPP(s_datum);
                tb = DatumGetByteaPP(t_datum);
                if (VARSIZE_ANY_EXHDR(sb) != 16 || VARSIZE_ANY_EXHDR(tb) != 16)
                    continue;

                /* container boundary: finalize previous, reset */
                if (!have_sent || memcmp(VARDATA_ANY(sb), cur_sent, 16) != 0)
                {
                    if (have_sent && clist != NULL && ncand > 0)
                        store_candidate_list(&cand_lists, &cand_lens,
                                             &n_cont, &cont_cap, clist, ncand);
                    memcpy(cur_sent, VARDATA_ANY(sb), 16);
                    have_sent = true;
                    after = -1;
                    clist = NULL;
                    ncand = 0;
                    ring_n = 0;
                    ring_head = 0;
                }

                if (memcmp(VARDATA_ANY(tb), VARDATA_ANY(point), 16) == 0)
                {
                    if (backward)
                    {
                        /* first occurrence only: flush the preceding window,
                         * nearest-first (walk the ring backwards from the head). */
                        if (after < 0 && ring_n > 0)
                        {
                            int take = ring_n < window ? ring_n : window;
                            clist = (char *) palloc(16 * window);
                            for (int i = 1; i <= take; i++)
                            {
                                int idx = (ring_head - i + window) % window;

                                memcpy(clist + ncand * 16, ring + idx * 16, 16);
                                ncand++;
                            }
                        }
                        after = 0;
                    }
                    else if (after < 0)
                        after = 0;   /* first occurrence: collect after */
                    continue;
                }

                if (backward)
                {
                    if (after < 0)               /* still before the point */
                    {
                        if (ring == NULL)
                            ring = (char *) palloc(16 * window);
                        memcpy(ring + ring_head * 16, VARDATA_ANY(tb), 16);
                        ring_head = (ring_head + 1) % window;
                        if (ring_n < window)
                            ring_n++;
                    }
                    continue;
                }

                if (after >= 0 && ncand < window)
                {
                    if (clist == NULL)
                        clist = (char *) palloc(16 * window);
                    memcpy(clist + ncand * 16, VARDATA_ANY(tb), 16);
                    ncand++;
                }
            }
            SPI_freetuptable(SPI_tuptable);
            CHECK_FOR_INTERRUPTS();
        }
        SPI_cursor_close(portal);

        /* finalize last container */
        if (have_sent && clist != NULL && ncand > 0)
            store_candidate_list(&cand_lists, &cand_lens,
                                 &n_cont, &cont_cap, clist, ncand);
    }

    if (n_cont == 0)
    {
        laplace_spi_finish(spi_top);
        return (Datum) 0;
    }

    /* (2) aggregate: first non-separator candidate per container. */
    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(SuccEntry);
    ctl.hcxt = CurrentMemoryContext;
    succ_agg = hash_create("geomsucc agg", 4096, &ctl,
                           HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    for (int c = 0; c < n_cont; c++)
    {
        for (int j = 0; j < cand_lens[c]; j++)
        {
            char      *id = cand_lists[c] + j * 16;
            bool       found;
            SuccEntry *se;

            if (hash_search(separators, id, HASH_FIND, NULL) != NULL)
                continue;

            se = (SuccEntry *) hash_search(succ_agg, id, HASH_ENTER, &found);
            if (!found) se->count = 0;
            se->count++;
            break;   /* first content token wins for this container */
        }
    }

    /* (3) rank by count desc, emit top limit_rows. */
    {
        int             n = (int) hash_get_num_entries(succ_agg);
        SuccEntry      *arr = (SuccEntry *) palloc(sizeof(SuccEntry) * (n > 0 ? n : 1));
        HASH_SEQ_STATUS seq;
        SuccEntry      *se;
        int             m = 0, top;

        hash_seq_init(&seq, succ_agg);
        while ((se = (SuccEntry *) hash_seq_search(&seq)) != NULL)
            arr[m++] = *se;

        qsort(arr, (size_t) m, sizeof(SuccEntry), successor_cmp);
        top = m < limit_rows ? m : limit_rows;
        for (int a = 0; a < top; a++)
        {
            bytea *out;
            Datum  vals[2];
            bool   nulls[2] = { false, false };

            out = (bytea *) palloc(VARHDRSZ + 16);
            SET_VARSIZE(out, VARHDRSZ + 16);
            memcpy(VARDATA(out), arr[a].key, 16);
            vals[0] = PointerGetDatum(out);
            vals[1] = Int64GetDatum(arr[a].count);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
        }
    }

    laplace_spi_finish(spi_top);
    return (Datum) 0;
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
    if (limit_rows < 1) limit_rows = 20;
    if (window < 1) window = 8;

    deconstruct_array(points_array, BYTEAOID, -1, false, TYPALIGN_INT,
                      &point_datums, &point_nulls, &n_input);
    InitMaterializedSRF(fcinfo, 0);
    if (n_input < 1)
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
        Datum      args[1];
        char       nulls[1] = { ' ' };
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
