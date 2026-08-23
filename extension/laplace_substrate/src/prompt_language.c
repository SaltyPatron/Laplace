/*
 * prompt_language — which language a prompt is written in.
 *
 * WHY THIS IS C. The SQL form used converse.prompt_state() as a table source (a per-row SRF)
 * and called consensus.eff_mu() per row over a partitioned consensus join. That is
 * replaced here by one indexed range read plus an O(1) hash probe per edge, the
 * same shape used by prompt_coherence.c and recall.c.
 *
 * WHAT IT IS FOR, AND WHAT IT IS NOT FOR. This substrate is omniglottal by
 * construction: a concept is language-free and its SURFACES are a family
 * spanning every language that witnesses it, converging on one ILI hub. That
 * mesh is the universal-translator property and must not be filtered away.
 *
 *   CONCEPT  language-free. The election chooses among CONCEPTS.
 *   SURFACE  language-bearing. Rendering chooses among SURFACES.
 *
 * So this returns a RANKED TALLY, never a single winner and never a filter
 * predicate. A caller BIASES with it; a caller that hard-filtered on it would
 * delete the translator to fix a ranking bug, and would break a genuinely
 * cross-lingual prompt.
 *
 * WHAT IT COMPUTES. Sum eff_mu over EVERY HAS_LANGUAGE edge carried by the
 * prompt's entities, at every tier that has one.
 *
 * Deliberately NOT converse.word_language() per token -- that is LIMIT 1, one language
 * per word, which discards the distribution and makes a token shared across
 * languages ("chat" English/French, "die" English/German, "a" in a dozen) look
 * monolingual. Summing lets genuinely ambiguous tokens contribute to every
 * tally they belong to and be settled by the tokens that are decisive, which
 * is how a reader disambiguates too.
 *
 * TIER-AGNOSTIC BY CONSTRUCTION: it tallies whatever the prompt resolved to,
 * so a sentence-root HAS_LANGUAGE counts alongside word-tier evidence rather
 * than being invisible to a word-only scan. Nothing here assumes which tier
 * answered, which is what makes it work for a modality that is not text.
 *
 * A DOUBLE-COUNT THE SQL FORM HAD. converse.prompt_state() returns one row per
 * (ord, id) and an id that exists at two tiers appears TWICE -- measured on
 * "What is a glacier?": 7 rows, 5 distinct ids, because the tier-collision
 * seam (GH #752) puts single-character surfaces at tier 0 and tier 2. The SQL
 * body joined consensus per ROW, so those tokens' language edges were counted
 * once per tier. Collecting ids and probing with `= ANY($1)` is a semi-join:
 * each edge counts once, whatever the fan-out. Measured effect on the same
 * prompt: English 12260 -> 8553, Irish 8364 -> 5149. The ranking is unchanged
 * (English still leads); the magnitudes were inflated.
 *
 * NO FLOOR, NO MINIMUM MARGIN, NO TOP-K: the fold already carries confidence,
 * ties break on the id for determinism, and a prompt with no language evidence
 * returns zero rows -- the honest answer when the prompt does not say.
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
         * prompt_words, NOT prompt_state. Only p.id is read here, and the two
         * agree on it exactly -- prompt_state is prompt_words plus a resolved
         * language column. Reading prompt_state for a column it does not use
         * inverts the layering: a token's language is resolved AGAINST this
         * tally (prompt_state.sql.in), so prompt_state -> prompt_language ->
         * prompt_state is a cycle that terminates in "stack depth limit
         * exceeded". The tally is over token IDENTITIES; it must sit below
         * anything that assigns a language.
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
            1, argtypes, args, NULL, true, 0);

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
                /* Conservative ranking key via the one native implementation
                 * (laplace_effective_mu_fp) — not a per-row SQL eff_mu call. */
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
