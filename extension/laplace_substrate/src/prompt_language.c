/*
 * Language standing of one observation. Every entity the prompt resolves to,
 * at whatever tier, contributes the consensus of each HAS_LANGUAGE cell it is
 * the subject of; masses are summed per language and emitted as a ranked
 * distribution over language ids, never a single winner or a filter. A surface
 * witnessed in several languages adds to each of them, and the surfaces that
 * are decisive settle the ranking. The ids are probed as one set, so an entity
 * present at two tiers or occurring twice counts its cells once. No floor or
 * top-k; ties break on the language id; an observation with no language
 * evidence yields no rows.
 */

#include "postgres.h"

#include <limits.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

#include "catalog/pg_type.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "utils/numeric.h"

#include "laplace/core/glicko2.h"
#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"

#include "consensus_scan.h"
#include "converse_bindings.h"
#include "public_direct.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_prompt_language);

typedef struct PlEntry
{
    hash128_t key;
    double    mass;
} PlEntry;

typedef struct PlIdEntry
{
    hash128_t id;
} PlIdEntry;

typedef struct PlSegCtx
{
    MemoryContext caller;
    HTAB         *seen_ids;
    hash128_t    *ids;
    int           count;
    int           cap;
    Datum         ws_prop;
    Datum         ws_val;
} PlSegCtx;

static bool
pl_receive_segment(TupleTableSlot *slot, void *opaque)
{
    PlSegCtx     *ctx = (PlSegCtx *) opaque;
    bool          isnull = false;
    Datum         d_seg, d_id;
    Oid           types[3] = { TEXTOID, TEXTOID, TEXTOID };
    Datum         args[3];
    bool          argnulls[3] = { false, false, false };
    bool          ws_null = false;
    Datum         ws_res;
    hash128_t     id;
    bool          found = false;
    MemoryContext prev;

    d_id = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;

    d_seg = slot_getattr(slot, 2, &isnull);
    if (!isnull)
    {
        args[0] = d_seg;
        args[1] = ctx->ws_prop;
        args[2] = ctx->ws_val;
        ws_res = laplace_pub_call("public", "laplace_text_is", 3,
                                  types, args, argnulls, &ws_null);
        if (!ws_null && DatumGetBool(ws_res))
            return true;
    }

    id = laplace_pub_blake3_to_hash128(d_id);

    prev = MemoryContextSwitchTo(ctx->caller);
    (void) hash_search(ctx->seen_ids, &id, HASH_ENTER, &found);
    if (!found)
    {
        if (ctx->count >= ctx->cap)
        {
            if (ctx->cap > INT_MAX / 2)
                ereport(ERROR,
                        (errmsg("prompt_language: prompt exceeds allocation capacity")));
            ctx->cap *= 2;
            ctx->ids = (hash128_t *) repalloc(ctx->ids, sizeof(hash128_t) * (Size) ctx->cap);
        }
        ctx->ids[ctx->count++] = id;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

static void
pl_add_mass(HTAB *languages, const hash128_t *lang_id, int64 rating, int64 rd)
{
    bool     found = false;
    PlEntry *entry = (PlEntry *) hash_search(languages, lang_id, HASH_ENTER, &found);

    if (!found)
        entry->mass = 0.0;
    entry->mass += (double) laplace_effective_mu_fp(rating, rd);
}

static void
pl_receive_consensus(const LaplaceConsensusRow *row, void *context)
{
    HTAB *languages = (HTAB *) context;

    if (row->object_is_null)
        return;
    pl_add_mass(languages, &row->object, row->rating, row->rd);
}

static int
pl_cmp(const void *a, const void *b)
{
    const PlEntry *x = *(const PlEntry * const *) a;
    const PlEntry *y = *(const PlEntry * const *) b;

    if (x->mass > y->mass)
        return -1;
    if (x->mass < y->mass)
        return 1;
    return memcmp(&x->key, &y->key, sizeof(hash128_t));
}

Datum
pg_laplace_prompt_language(PG_FUNCTION_ARGS)
{
    ReturnSetInfo      *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    text               *prompt;
    MemoryContext       caller = CurrentMemoryContext;
    MemoryContext       work, old;
    HASHCTL             ctl;
    HTAB               *lang_h;
    PlSegCtx            sctx;
    Oid                 text_oid = TEXTOID;
    Datum               seg_arg[1];
    bool                seg_null[1] = { false };
    hash128_t           has_lang_id;
    ArrayType          *ids_arr, *has_lang_arr;
    ConverseBindingRow *brows = NULL;
    int                 n_brows = 0;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    prompt = PG_GETARG_TEXT_PP(0);

    InitMaterializedSRF(fcinfo, 0);

    work = AllocSetContextCreate(caller, "prompt_language", ALLOCSET_DEFAULT_SIZES);
    old = MemoryContextSwitchTo(work);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(PlEntry);
    ctl.hcxt = work;
    lang_h = hash_create("pl lang", 64, &ctl,
                         HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(PlIdEntry);
    ctl.hcxt = work;

    sctx.caller = work;
    sctx.seen_ids = hash_create("pl seg ids", 64, &ctl,
                                HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    sctx.cap = 64;
    sctx.count = 0;
    sctx.ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) sctx.cap);
    sctx.ws_prop = CStringGetTextDatum("White_Space");
    sctx.ws_val = CStringGetTextDatum("Yes");

    seg_arg[0] = PointerGetDatum(prompt);
    (void) laplace_pub_call_srf("public", "laplace_segments", 1,
                                &text_oid, seg_arg, seg_null,
                                pl_receive_segment, &sctx);

    if (sctx.count == 0)
    {
        MemoryContextSwitchTo(old);
        MemoryContextDelete(work);
        return (Datum) 0;
    }

    if (laplace_relation_type_id("HAS_LANGUAGE", &has_lang_id) != 0)
        elog(ERROR, "prompt_language: HAS_LANGUAGE type unavailable");

    ids_arr = hash128_array_from_ids(sctx.ids, sctx.count);
    has_lang_arr = hash128_array_from_ids(&has_lang_id, 1);
    laplace_consensus_candidates_stored(ids_arr, NULL, has_lang_arr,
                                        pl_receive_consensus, lang_h);
    pfree(ids_arr);
    pfree(has_lang_arr);

    brows = converse_binding_evidence_c(sctx.ids, sctx.count, NULL, false, &n_brows);
    for (int i = 0; i < n_brows; i++)
    {
        if (brows[i].language_null)
            continue;
        pl_add_mass(lang_h, &brows[i].language,
                    (int64) llround(brows[i].eff_mu * 1e9), 0);
    }

    {
        HASH_SEQ_STATUS  seq;
        PlEntry         *e;
        long             n = hash_get_num_entries(lang_h);

        if (n > 0)
        {
            PlEntry **rank = (PlEntry **) palloc(sizeof(PlEntry *) * (Size) n);
            long      i = 0;

            hash_seq_init(&seq, lang_h);
            while ((e = (PlEntry *) hash_seq_search(&seq)) != NULL)
                rank[i++] = e;

            if (n > 1)
                qsort(rank, (Size) n, sizeof(PlEntry *), pl_cmp);

            for (i = 0; i < n; i++)
            {
                Datum  values[2];
                bool   nulls[2] = { false, false };
                bytea *idb = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));

                SET_VARSIZE(idb, VARHDRSZ + sizeof(hash128_t));
                memcpy(VARDATA(idb), &rank[i]->key, sizeof(hash128_t));
                values[0] = PointerGetDatum(idb);
                values[1] = DirectFunctionCall1(float8_numeric,
                                                Float8GetDatum(rank[i]->mass));
                tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc,
                                     values, nulls);
                pfree(idb);
                pfree(DatumGetPointer(values[1]));
            }
        }
    }

    MemoryContextSwitchTo(old);
    MemoryContextDelete(work);
    return (Datum) 0;
}
