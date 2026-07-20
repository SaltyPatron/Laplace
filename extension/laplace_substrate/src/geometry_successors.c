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
 * geometry_successors(point, limit, window)
 *   -> TABLE(successor_id bytea, seen bigint)
 *
 * The geometry-native continuation operator: for a content POINT, walk every
 * trajectory that CONTAINS it and return the next CONTENT constituent in each,
 * aggregated by frequency. "What follows X" read straight from the
 * content-addressed geometry -- the same knowledge PRECEDES materialized and
 * gen_corpus rebuilt into a flat RAM suffix array, needed by neither: the
 * trajectory already holds the ordered sequence, and the whitespace class of a
 * token is a deterministic, content-addressed fact.
 *
 * WHY NATIVE (the valet/orchestrator law): SQL cannot do this at scale -- a
 * per-row LATERAL unpack + per-token whitespace render times out. The walk
 * (find the point, skip separators, take the successor, aggregate) is
 * pointer/loop work in C.
 *
 * TWO deterministic-caching wins over the naive shape (profiled: 4.9s -> ~2s):
 *   1. ONE containment+unpack query (single-key `@> ARRAY[$1]`, GIN-served),
 *      streamed and grouped by container in C -- NOT 543 per-container fetches.
 *   2. A BACKEND-LIFETIME whitespace-classification cache: a token id is
 *      classified (via corpus_whitespace_vocab_indices) at most ONCE, ever.
 *      First touch warms it; every later call / repeated token is O(1). Same
 *      content = same hash = same answer forever, so it stays hot.
 *
 * Correctness: laplace_trajectory_constituents() is the FULL, non-deduped,
 * ordinal-ordered sequence (unlike laplace_trajectory_constituent_ids(), which
 * dedups for the containment index and would break adjacency).
 */

static const char *UNPACK_QUERY =
    "SELECT p.entity_id, c.entity_id "
    "FROM laplace.physicalities p "
    "CROSS JOIN LATERAL public.laplace_trajectory_constituents(p.trajectory) c "
    "WHERE p.type = 1 "
    "  AND public.laplace_trajectory_constituent_ids(p.trajectory) @> ARRAY[$1]::bytea[] "
    "ORDER BY p.entity_id, c.ordinal";

static const char *WS_CLASSIFY_QUERY =
    "SELECT vocab_idx FROM laplace.corpus_whitespace_vocab_indices($1)";

static SPIPlanPtr unpack_plan   = NULL;
static SPIPlanPtr classify_plan = NULL;

/* Backend-lifetime whitespace-class cache (deterministic; content-addressed). */
typedef struct WsEntry { char key[16]; bool is_sep; } WsEntry;
static HTAB *ws_cache = NULL;

/* Distinct-candidate registry for THIS call: id -> separator flag. */
typedef struct CandEntry { char key[16]; bool is_sep; bool resolved; } CandEntry;

/* Aggregation entry: successor id -> count. */
typedef struct SuccEntry { char key[16]; int64 count; } SuccEntry;

static void
ensure_plan(SPIPlanPtr *slot, const char *sql, Oid argtype)
{
    if (*slot == NULL)
    {
        Oid        argtypes[1] = { argtype };
        SPIPlanPtr plan = SPI_prepare(sql, 1, argtypes);
        if (plan == NULL)
            elog(ERROR, "geometry_successors: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "geometry_successors: SPI_keepplan failed");
        *slot = plan;
    }
}

static void
ensure_ws_cache(void)
{
    if (ws_cache == NULL)
    {
        HASHCTL ctl;
        memset(&ctl, 0, sizeof(ctl));
        ctl.keysize = 16;
        ctl.entrysize = sizeof(WsEntry);
        ctl.hcxt = TopMemoryContext;   /* survives the call */
        ws_cache = hash_create("geomsucc ws cache", 8192, &ctl,
                               HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    }
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

    HTAB   *cand_reg;
    HTAB   *succ_agg;
    HASHCTL ctl;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("geometry_successors: point must not be NULL")));
    point      = PG_GETARG_BYTEA_PP(0);
    limit_rows = PG_ARGISNULL(1) ? 20 : PG_GETARG_INT32(1);
    window     = PG_ARGISNULL(2) ? 8  : PG_GETARG_INT32(2);
    /* Direction. The trajectory holds the exact ordered sequence, so "what comes
     * BEFORE x" is as readable as "what comes after" -- same containment query,
     * same separator cache, same aggregation. Only the candidate walk flips.
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
    ensure_ws_cache();

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "geometry_successors: SPI_connect failed");
    ensure_plan(&unpack_plan,   UNPACK_QUERY,      BYTEAOID);
    ensure_plan(&classify_plan, WS_CLASSIFY_QUERY, BYTEAARRAYOID);

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(CandEntry);
    ctl.hcxt = CurrentMemoryContext;
    cand_reg = hash_create("geomsucc cands", 4096, &ctl,
                           HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    /* (1) ONE containment+unpack query, streamed and grouped by container. */
    {
        Datum args[1] = { PointerGetDatum(point) };
        char  nulls[1] = { ' ' };
        int   rc = SPI_execute_plan(unpack_plan, args, nulls, true, 0);
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

        if (rc != SPI_OK_SELECT)
            elog(ERROR, "geometry_successors: unpack query failed: %s",
                 SPI_result_code_string(rc));

        cont_cap = (int) SPI_processed + 1;   /* upper bound on containers */
        cand_lists = (char **) palloc(sizeof(char *) * cont_cap);
        cand_lens  = (int *)   palloc(sizeof(int) * cont_cap);

        for (uint64 r = 0; r < SPI_processed; r++)
        {
            HeapTuple tup = SPI_tuptable->vals[r];
            TupleDesc td  = SPI_tuptable->tupdesc;
            bool   s_null, t_null;
            bytea *sb = DatumGetByteaPP(SPI_getbinval(tup, td, 1, &s_null));
            bytea *tb = DatumGetByteaPP(SPI_getbinval(tup, td, 2, &t_null));

            if (s_null || t_null || VARSIZE_ANY_EXHDR(sb) != 16 ||
                VARSIZE_ANY_EXHDR(tb) != 16)
                continue;

            /* container boundary: finalize previous, reset */
            if (!have_sent || memcmp(VARDATA_ANY(sb), cur_sent, 16) != 0)
            {
                if (have_sent && clist != NULL && ncand > 0)
                {
                    cand_lists[n_cont] = clist;
                    cand_lens[n_cont]  = ncand;
                    n_cont++;
                }
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
                            int        idx = (ring_head - i + window) % window;
                            CandEntry *ce; bool found;
                            memcpy(clist + ncand * 16, ring + idx * 16, 16);
                            ncand++;
                            ce = (CandEntry *) hash_search(cand_reg, ring + idx * 16,
                                                           HASH_ENTER, &found);
                            if (!found) { ce->is_sep = false; ce->resolved = false; }
                        }
                    }
                    after = 0;
                }
                else if (after < 0) after = 0;   /* first occurrence: collect after */
                continue;
            }

            if (backward)
            {
                if (after < 0)                   /* still before the point */
                {
                    if (ring == NULL) ring = (char *) palloc(16 * window);
                    memcpy(ring + ring_head * 16, VARDATA_ANY(tb), 16);
                    ring_head = (ring_head + 1) % window;
                    if (ring_n < window) ring_n++;
                }
                continue;
            }

            if (after >= 0 && ncand < window)
            {
                CandEntry *ce; bool found;
                if (clist == NULL)
                    clist = (char *) palloc(16 * window);
                memcpy(clist + ncand * 16, VARDATA_ANY(tb), 16);
                ncand++;
                ce = (CandEntry *) hash_search(cand_reg, VARDATA_ANY(tb),
                                               HASH_ENTER, &found);
                if (!found) { ce->is_sep = false; ce->resolved = false; }
            }
        }
        /* finalize last container */
        if (have_sent && clist != NULL && ncand > 0)
        {
            cand_lists[n_cont] = clist;
            cand_lens[n_cont]  = ncand;
            n_cont++;
        }
        SPI_freetuptable(SPI_tuptable);
    }

    if (n_cont == 0)
    {
        laplace_spi_finish(spi_top);
        return (Datum) 0;
    }

    /* (2) resolve separators: cache hits first, then ONE classify of the misses. */
    {
        HASH_SEQ_STATUS seq;
        CandEntry      *ce;
        Datum          *miss = NULL;
        int             n_miss = 0;

        n_miss = 0;
        miss = (Datum *) palloc(sizeof(Datum) *
                                (hash_get_num_entries(cand_reg) > 0
                                 ? hash_get_num_entries(cand_reg) : 1));

        hash_seq_init(&seq, cand_reg);
        while ((ce = (CandEntry *) hash_seq_search(&seq)) != NULL)
        {
            WsEntry *wsc = (WsEntry *) hash_search(ws_cache, ce->key, HASH_FIND, NULL);
            if (wsc != NULL)
            {
                ce->is_sep = wsc->is_sep;
                ce->resolved = true;
            }
            else
            {
                bytea *b = (bytea *) palloc(VARHDRSZ + 16);
                SET_VARSIZE(b, VARHDRSZ + 16);
                memcpy(VARDATA(b), ce->key, 16);
                miss[n_miss++] = PointerGetDatum(b);
            }
        }

        if (n_miss > 0)
        {
            ArrayType *arr = construct_array(miss, n_miss, BYTEAOID, -1, false, TYPALIGN_INT);
            Datum      cargs[1] = { PointerGetDatum(arr) };
            char       cnulls[1] = { ' ' };
            bool      *sep = (bool *) palloc0(sizeof(bool) * n_miss);
            int        rc = SPI_execute_plan(classify_plan, cargs, cnulls, true, 0);

            if (rc != SPI_OK_SELECT)
                elog(ERROR, "geometry_successors: separator classify failed: %s",
                     SPI_result_code_string(rc));

            for (uint64 r = 0; r < SPI_processed; r++)
            {
                bool  isnull;
                int32 vi = DatumGetInt32(SPI_getbinval(SPI_tuptable->vals[r],
                                         SPI_tuptable->tupdesc, 1, &isnull));
                if (!isnull && vi >= 0 && vi < n_miss) sep[vi] = true;
            }
            SPI_freetuptable(SPI_tuptable);

            /* fold flags back onto cand_reg AND the backend-lifetime cache */
            for (int i = 0; i < n_miss; i++)
            {
                char    *id = VARDATA_ANY(DatumGetByteaPP(miss[i]));
                bool     found;
                WsEntry *wsc;
                CandEntry *c2 = (CandEntry *) hash_search(cand_reg, id, HASH_FIND, NULL);
                if (c2 != NULL) { c2->is_sep = sep[i]; c2->resolved = true; }

                /* cache the classification into TopMemoryContext (persists) */
                {
                    MemoryContext old = MemoryContextSwitchTo(TopMemoryContext);
                    wsc = (WsEntry *) hash_search(ws_cache, id, HASH_ENTER, &found);
                    MemoryContextSwitchTo(old);
                    wsc->is_sep = sep[i];
                }
            }
        }
    }

    /* (3) aggregate: first non-separator candidate per container */
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
            CandEntry *ce = (CandEntry *) hash_search(cand_reg, id, HASH_FIND, NULL);
            bool       found;
            SuccEntry *se;

            if (ce != NULL && ce->is_sep) continue;   /* skip separator */

            se = (SuccEntry *) hash_search(succ_agg, id, HASH_ENTER, &found);
            if (!found) se->count = 0;
            se->count++;
            break;   /* first content token wins for this container */
        }
    }

    /* (4) rank by count desc, emit top limit_rows */
    {
        int             n = (int) hash_get_num_entries(succ_agg);
        SuccEntry      *arr = (SuccEntry *) palloc(sizeof(SuccEntry) * (n > 0 ? n : 1));
        HASH_SEQ_STATUS seq;
        SuccEntry      *se;
        int             m = 0, top;

        hash_seq_init(&seq, succ_agg);
        while ((se = (SuccEntry *) hash_seq_search(&seq)) != NULL)
            arr[m++] = *se;

        top = m < limit_rows ? m : limit_rows;
        for (int a = 0; a < top; a++)
        {
            int    best = a;
            bytea *out;
            Datum  vals[2];
            bool   nulls[2] = { false, false };

            for (int b = a + 1; b < m; b++)
                if (arr[b].count > arr[best].count) best = b;
            if (best != a) { SuccEntry t = arr[a]; arr[a] = arr[best]; arr[best] = t; }

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
