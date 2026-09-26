/*
 * Language standing of one observation. Every entity the prompt resolved to,
 * at whatever tier, contributes the consensus of each HAS_LANGUAGE cell it is
 * the subject of; masses are summed per language by effective mu and emitted
 * as a ranked distribution over language ids, never a single winner or a
 * filter. A surface witnessed in several languages adds to each of them, and
 * the surfaces that are decisive settle the ranking. The ids are probed as one
 * set (= ANY), so an entity present at two tiers or occurring twice counts its
 * cells once. No floor or top-k; ties break on the language id; an observation
 * with no language evidence yields no rows.
 */

#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "utils/numeric.h"

#include "spi_common.h"
#include "spi_nested.h"

PG_FUNCTION_INFO_V1(pg_laplace_prompt_language);

typedef struct PlEntry
{
    uint8   key[16];
    double  mass;
} PlEntry;

static int
pl_cmp(const void *a, const void *b)
{
    const PlEntry *x = *(const PlEntry * const *) a;
    const PlEntry *y = *(const PlEntry * const *) b;

    if (x->mass > y->mass) return -1;
    if (x->mass < y->mass) return 1;
    return memcmp(x->key, y->key, 16);   /* deterministic tie-break on the id */
}

Datum
pg_laplace_prompt_language(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    text          *prompt;
    MemoryContext  work, old;
    HASHCTL        ctl;
    HTAB          *lang_h;
    Datum         *id_datums = NULL;
    int            n_ids = 0;
    bool           spi_top = false;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    prompt = PG_GETARG_TEXT_PP(0);

    InitMaterializedSRF(fcinfo, 0);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "prompt_language: SPI_connect failed");

    work = AllocSetContextCreate(CurrentMemoryContext, "prompt_language",
                                 ALLOCSET_DEFAULT_SIZES);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(PlEntry);
    ctl.hcxt = work;
    lang_h = hash_create("pl lang", 64, &ctl,
                         HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    /* ---- the prompt's entities, at whatever tier they resolved to ---- */
    {
        Oid    argtypes[1] = { TEXTOID };
        Datum  args[1];
        int    rc;

        /*
         * prompt_words carries only the resolved ids. prompt_state assigns a
         * token's language against this tally, so reading it here would
         * recurse without end.
         */
        args[0] = PointerGetDatum(prompt);
        rc = SPI_execute_with_args(
            "SELECT p.id FROM converse.prompt_words($1) p WHERE p.id IS NOT NULL",
            1, argtypes, args, NULL, true, 0);
        if (rc != SPI_OK_SELECT)
            elog(ERROR, "prompt_language: prompt_words read failed: %s",
                 SPI_result_code_string(rc));
        if (SPI_processed > (uint64) (MaxAllocSize / sizeof(Datum)))
            ereport(ERROR,
                    (errmsg("prompt_language: prompt exceeds PostgreSQL allocation capacity"),
                     errdetail("Requested %llu entity ids.",
                               (unsigned long long) SPI_processed)));

        if (SPI_processed > 0)
        {
            old = MemoryContextSwitchTo(work);
            id_datums = (Datum *) palloc(sizeof(Datum) * (Size) SPI_processed);
            MemoryContextSwitchTo(old);
        }

        for (uint64 r = 0; r < SPI_processed; r++)
        {
            bool   isnull;
            bytea *id;

            id = DatumGetByteaPP(SPI_getbinval(SPI_tuptable->vals[r],
                                               SPI_tuptable->tupdesc,
                                               1, &isnull));
            if (isnull || VARSIZE_ANY_EXHDR(id) != 16)
                continue;

            old = MemoryContextSwitchTo(work);
            {
                bytea *cp = (bytea *) palloc(VARSIZE_ANY(id));

                memcpy(cp, id, VARSIZE_ANY(id));
                id_datums[n_ids++] = PointerGetDatum(cp);
            }
            MemoryContextSwitchTo(old);
        }
        SPI_freetuptable(SPI_tuptable);
    }

    if (n_ids == 0)
    {
        MemoryContextDelete(work);
        laplace_spi_finish(spi_top);
        return (Datum) 0;
    }

    /* ---- one indexed range read; O(1) hash probe per edge ---- */
    {
        Oid        argtypes[1] = { BYTEAARRAYOID };
        Datum      args[1];
        ArrayType *arr;
        Portal     portal;

        old = MemoryContextSwitchTo(work);
        arr = construct_array(id_datums, n_ids, BYTEAOID, -1, false, TYPALIGN_INT);
        MemoryContextSwitchTo(old);

        args[0] = PointerGetDatum(arr);
        portal = SPI_cursor_open_with_args(
            "pl_edges",
            "SELECT c.object_id, c.rating, c.rd FROM laplace.consensus c "
            "WHERE c.subject_id = ANY($1) "
            "  AND c.type_id = laplace.relation_type_id('HAS_LANGUAGE') "
            "  AND c.object_id IS NOT NULL",
            1, argtypes, args, NULL, true, CURSOR_OPT_PARALLEL_OK);

        for (;;)
        {
            SPI_cursor_fetch(portal, true, 50000);
            if (SPI_processed == 0)
                break;

            for (uint64 r = 0; r < SPI_processed; r++)
            {
                HeapTuple tup = SPI_tuptable->vals[r];
                TupleDesc td = SPI_tuptable->tupdesc;
                bool      isnull, found;
                bytea    *obj;
                int64     rating, rd;
                PlEntry  *e;

                obj = DatumGetByteaPP(SPI_getbinval(tup, td, 1, &isnull));
                if (isnull || VARSIZE_ANY_EXHDR(obj) != 16) continue;
                rating = DatumGetInt64(SPI_getbinval(tup, td, 2, &isnull));
                if (isnull) continue;
                rd = DatumGetInt64(SPI_getbinval(tup, td, 3, &isnull));
                if (isnull) continue;

                e = (PlEntry *) hash_search(lang_h, VARDATA_ANY(obj),
                                            HASH_ENTER, &found);
                if (!found)
                    e->mass = 0.0;
                /* Effective mu: rating minus twice the deviation. */
                e->mass += (double) laplace_effective_mu_fp(rating, rd);
            }
            SPI_freetuptable(SPI_tuptable);
            CHECK_FOR_INTERRUPTS();
        }
        SPI_cursor_close(portal);
    }

    /* ---- rank and emit ---- */
    {
        HASH_SEQ_STATUS seq;
        PlEntry        *e;
        PlEntry       **rank;
        long            n = hash_get_num_entries(lang_h);
        long            i = 0;

        if (n > 0)
        {
            old = MemoryContextSwitchTo(work);
            rank = (PlEntry **) palloc(sizeof(PlEntry *) * n);
            MemoryContextSwitchTo(old);

            hash_seq_init(&seq, lang_h);
            while ((e = (PlEntry *) hash_seq_search(&seq)) != NULL)
                rank[i++] = e;

            qsort(rank, n, sizeof(PlEntry *), pl_cmp);

            for (i = 0; i < n; i++)
            {
                Datum  values[2];
                bool   nulls[2];
                bytea *idb = (bytea *) palloc(VARHDRSZ + 16);

                MemSet(nulls, 0, sizeof(nulls));
                SET_VARSIZE(idb, VARHDRSZ + 16);
                memcpy(VARDATA(idb), rank[i]->key, 16);
                values[0] = PointerGetDatum(idb);
                values[1] = DirectFunctionCall1(float8_numeric,
                                                Float8GetDatum(rank[i]->mass));
                tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc,
                                     values, nulls);
            }
        }
    }

    MemoryContextDelete(work);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
