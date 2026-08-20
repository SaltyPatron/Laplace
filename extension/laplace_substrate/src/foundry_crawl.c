/*
 * foundry_crawl.c — bounded-output, exact-depth vocabulary crawl.
 *
 * One SQL read is issued per frontier, not per node. p_budget limits only the
 * returned tier-2 vocabulary; traversal is defined exclusively by the caller's
 * p_hops and per-node p_fanout. Storage grows from the rows actually returned
 * and is rejected only at PostgreSQL's real allocation boundary.
 */
#include "postgres.h"

#include <math.h>

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"

#include "laplace/core/hash128.h"
#include "laplace/core/glicko2.h"
#include "spi_common.h"
#include "spi_nested.h"

PG_FUNCTION_INFO_V1(pg_laplace_foundry_crawl);

typedef struct CrawlEntry
{
    char    key[16];
    double  rel;
    int     depth;
    int16   tier;
} CrawlEntry;

typedef struct WordCand
{
    hash128_t id;
    double    rel;
} WordCand;

static int
word_cmp_desc(const void *a, const void *b)
{
    const WordCand *wa = (const WordCand *) a;
    const WordCand *wb = (const WordCand *) b;

    if (wa->rel < wb->rel) return 1;
    if (wa->rel > wb->rel) return -1;
    return memcmp(&wa->id, &wb->id, sizeof(hash128_t));
}

static void *
allocate_items(uint64 count, Size item_size, const char *what)
{
    if (count == 0)
        count = 1;
    if (count > (uint64) (MaxAllocSize / item_size))
        ereport(ERROR,
                (errmsg("foundry_crawl: %s exceeds PostgreSQL allocation capacity", what),
                 errdetail("Requested %llu items of %zu bytes.",
                           (unsigned long long) count, item_size)));
    return palloc((Size) count * item_size);
}

Datum
pg_laplace_foundry_crawl(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType     *seedarr;
    Datum         *seed_elems;
    bool          *seed_nulls;
    int            seed_n;
    int32          budget;
    int32          hops;
    int32          fanout;
    bool           has_filter = false;
    Datum          type_arr_datum = (Datum) 0;
    HTAB          *seen;
    HASHCTL        ctl;
    hash128_t     *frontier;
    int            n_front = 0;
    bool           spi_top = false;
    SPIPlanPtr     plan = NULL;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("foundry_crawl: seed id array must not be NULL")));

    budget = PG_ARGISNULL(1) ? 32000 : PG_GETARG_INT32(1);
    hops   = PG_ARGISNULL(2) ? 3     : PG_GETARG_INT32(2);
    fanout = PG_ARGISNULL(3) ? 64    : PG_GETARG_INT32(3);
    if (budget < 0 || hops < 0 || fanout < 0)
        ereport(ERROR,
                (errmsg("foundry_crawl: budget, hops, and fanout must not be negative")));

    seedarr = PG_GETARG_ARRAYTYPE_P(0);
    if (ARR_NDIM(seedarr) > 1 || ARR_ELEMTYPE(seedarr) != BYTEAOID)
        ereport(ERROR, (errmsg("foundry_crawl: seeds must be a 1-D bytea[]")));
    deconstruct_array(seedarr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &seed_elems, &seed_nulls, &seed_n);

    if (!PG_ARGISNULL(4))
    {
        ArrayType *tarr = PG_GETARG_ARRAYTYPE_P(4);

        if (ARR_NDIM(tarr) > 1 || ARR_ELEMTYPE(tarr) != BYTEAOID)
            ereport(ERROR,
                    (errmsg("foundry_crawl: rel_types must be a 1-D bytea[]")));
        if (ArrayGetNItems(ARR_NDIM(tarr), ARR_DIMS(tarr)) > 0)
        {
            has_filter = true;
            type_arr_datum = PointerGetDatum(tarr);
        }
    }

    InitMaterializedSRF(fcinfo, 0);
    if (budget == 0 || seed_n == 0)
        return (Datum) 0;

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(CrawlEntry);
    seen = hash_create("foundry_crawl seen", seed_n > 0 ? seed_n : 1,
                       &ctl, HASH_ELEM | HASH_BLOBS);
    frontier = (hash128_t *) allocate_items((uint64) seed_n,
                                             sizeof(hash128_t), "seed frontier");

    for (int s = 0; s < seed_n; s++)
    {
        hash128_t   id;
        CrawlEntry *entry;
        bool         found;

        if (seed_nulls[s])
            continue;
        id = datum_to_hash128(seed_elems[s]);
        entry = (CrawlEntry *) hash_search(seen, &id, HASH_ENTER, &found);
        if (found)
            continue;
        entry->rel = 1.0;
        entry->depth = 0;
        entry->tier = 2;
        frontier[n_front++] = id;
    }

    if (n_front > 0 && hops > 0 && fanout > 0)
    {
        Oid argtypes[3] = { BYTEAARRAYOID, INT4OID, BYTEAARRAYOID };

        if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
            elog(ERROR, "foundry_crawl: SPI_connect failed");
        plan = SPI_prepare(
            "SELECT frontier_id, object_id, tier, rating, rd "
            "FROM consensus.foundry_crawl_neighbors($1, $2, $3)",
            3, argtypes);
        if (plan == NULL)
            elog(ERROR, "foundry_crawl: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));

        for (int depth = 0; depth < hops && n_front > 0; depth++)
        {
            Datum      *front_datums;
            ArrayType  *front_array;
            Datum       args[3];
            char        argnulls[3] = { ' ', ' ', ' ' };
            hash128_t  *next_front;
            int         n_next = 0;
            int         rc;

            front_datums = (Datum *) allocate_items((uint64) n_front,
                                                      sizeof(Datum), "frontier datums");
            for (int i = 0; i < n_front; i++)
                front_datums[i] = hash128_to_datum(&frontier[i]);
            front_array = construct_array(front_datums, n_front, BYTEAOID,
                                          -1, false, TYPALIGN_INT);
            args[0] = PointerGetDatum(front_array);
            args[1] = Int32GetDatum(fanout);
            if (has_filter)
                args[2] = type_arr_datum;
            else
            {
                args[2] = (Datum) 0;
                argnulls[2] = 'n';
            }

            rc = SPI_execute_plan(plan, args, argnulls, true, 0);
            if (rc != SPI_OK_SELECT)
                elog(ERROR, "foundry_crawl: frontier probe failed: %s",
                     SPI_result_code_string(rc));
            next_front = (hash128_t *) allocate_items(SPI_processed,
                                                       sizeof(hash128_t), "next frontier");

            for (uint64 r = 0; r < SPI_processed; r++)
            {
                HeapTuple   tuple = SPI_tuptable->vals[r];
                TupleDesc   desc = SPI_tuptable->tupdesc;
                bool        front_null;
                bool        object_null;
                bool        tier_null;
                bool        rating_null;
                bool        rd_null;
                Datum       front_datum;
                Datum       object_datum;
                Datum       tier_datum;
                Datum       rating_datum;
                Datum       rd_datum;
                hash128_t   front_id;
                hash128_t   object_id;
                int16       object_tier;
                int64       rating;
                int64       rd;
                CrawlEntry *parent;
                CrawlEntry *child;
                bool        parent_found;
                bool        child_found;
                double      child_rel;

                front_datum = SPI_getbinval(tuple, desc, 1, &front_null);
                object_datum = SPI_getbinval(tuple, desc, 2, &object_null);
                tier_datum = SPI_getbinval(tuple, desc, 3, &tier_null);
                rating_datum = SPI_getbinval(tuple, desc, 4, &rating_null);
                rd_datum = SPI_getbinval(tuple, desc, 5, &rd_null);
                if (front_null || object_null || tier_null || rating_null || rd_null)
                    continue;
                front_id = datum_to_hash128(front_datum);
                object_id = datum_to_hash128(object_datum);
                object_tier = DatumGetInt16(tier_datum);
                rating = DatumGetInt64(rating_datum);
                rd = DatumGetInt64(rd_datum);

                parent = (CrawlEntry *) hash_search(seen, &front_id,
                                                     HASH_FIND, &parent_found);
                if (!parent_found)
                    continue;
                child_rel = parent->rel * laplace_edge_strength(rating, rd);
                if (!isfinite(child_rel))
                    ereport(ERROR,
                            (errmsg("foundry_crawl: relevance overflow while traversing frontier")));

                child = (CrawlEntry *) hash_search(seen, &object_id,
                                                    HASH_ENTER, &child_found);
                if (!child_found)
                {
                    child->rel = child_rel;
                    child->depth = depth + 1;
                    child->tier = object_tier;
                    if (child->depth < hops)
                        next_front[n_next++] = object_id;
                }
                else if (child_rel > child->rel)
                    child->rel = child_rel;

                CHECK_FOR_INTERRUPTS();
            }

            if (SPI_tuptable != NULL)
                SPI_freetuptable(SPI_tuptable);
            pfree(front_datums);
            pfree(front_array);
            pfree(frontier);
            frontier = next_front;
            n_front = n_next;
        }
    }

    {
        long            n_seen = hash_get_num_entries(seen);
        WordCand       *words;
        int             n_words = 0;
        HASH_SEQ_STATUS seq;
        CrawlEntry     *entry;
        int             emit_n;

        if (n_seen > INT_MAX)
            ereport(ERROR,
                    (errmsg("foundry_crawl: visited set exceeds addressable result capacity")));
        words = (WordCand *) allocate_items((uint64) n_seen,
                                             sizeof(WordCand), "word candidates");
        hash_seq_init(&seq, seen);
        while ((entry = (CrawlEntry *) hash_seq_search(&seq)) != NULL)
        {
            if (entry->tier != 2)
                continue;
            memcpy(&words[n_words].id, entry->key, 16);
            words[n_words].rel = entry->rel;
            n_words++;
        }
        if (n_words > 1)
            qsort(words, (size_t) n_words, sizeof(WordCand), word_cmp_desc);

        emit_n = n_words < budget ? n_words : budget;
        for (int i = 0; i < emit_n; i++)
        {
            Datum       values[2];
            bool        nulls[2] = { false, false };
            long double scaled = (long double) words[i].rel * 1000000.0L;

            if (!isfinite((double) scaled) ||
                scaled > (long double) PG_INT64_MAX ||
                scaled < (long double) PG_INT64_MIN)
                ereport(ERROR,
                        (errmsg("foundry_crawl: result weight exceeds bigint capacity")));
            values[0] = hash128_to_datum(&words[i].id);
            values[1] = Int64GetDatum((int64) scaled);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        }
    }

    if (plan != NULL)
    {
        SPI_freeplan(plan);
        laplace_spi_finish(spi_top);
    }
    return (Datum) 0;
}
