#include "postgres.h"
#include "common/int.h"

#include "fmgr.h"
#include "miscadmin.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/tier_tree.h"

#include "content_resolve_internal.h"
#include "perfcache_native.h"
#include "public_direct.h"
#include "spi_common.h"
#include "consensus_scan.h"

/* Joint evidence for one candidate span: witness count and rating summed, and
 * minimum rd, over consensus cells whose both endpoints are candidates of the
 * same observation. `degree` counts shared trajectory containers and is filled
 * only when no such cell exists. Key first for HASH_BLOBS. */
typedef struct
{
    hash128_t key;
    int64     witnesses;
    int64     rating;
    int64     rd;
    int64     degree;
} joint_degree_entry;

/* Container -> how many distinct candidates its trajectory contains.
 * first_candidate is the last candidate counted, so one candidate's repeated
 * rows count once; n_candidates > 1 makes the container joint evidence. */
typedef struct
{
    hash128_t key;
    int       first_candidate;
    int       n_candidates;
} container_owner_entry;

/* Containers read per candidate. */
#define RESOLVE_CONTAINER_PROBE_LIMIT 64

/* Zero-SPI direct callbacks for public.laplace_present_ids / laplace_holders. */
typedef struct
{
    MemoryContext caller;
    HTAB         *present;
} ResolvePresentCtx;

static bool
resolve_visit_present(TupleTableSlot *slot, void *opaque)
{
    ResolvePresentCtx *ctx = (ResolvePresentCtx *) opaque;
    bool               isnull = false;
    Datum              d = slot_getattr(slot, 1, &isnull);
    hash128_t          h;
    bool               pfound = false;
    MemoryContext      prev;

    if (isnull)
        return true;
    h = laplace_pub_blake3_to_hash128(d);
    prev = MemoryContextSwitchTo(ctx->caller);
    (void) hash_search(ctx->present, &h, HASH_ENTER, &pfound);
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct
{
    int64     ordinal;
    hash128_t entity;
} ResolveHolderRow;

typedef struct
{
    MemoryContext     caller;
    ResolveHolderRow *rows;
    int               count;
    int               cap;
    int               n_pres;
} ResolveHolderCtx;

static bool
resolve_visit_holder(TupleTableSlot *slot, void *opaque)
{
    ResolveHolderCtx *ctx = (ResolveHolderCtx *) opaque;
    bool              isnull = false;
    Datum             d_hub, d_ord, d_ent;
    int64             ordinal;
    hash128_t         ent;
    MemoryContext     prev;

    d_hub = slot_getattr(slot, 7, &isnull);
    if (!isnull && DatumGetBool(d_hub))
        return true;

    d_ord = slot_getattr(slot, 1, &isnull);
    if (isnull)
        elog(ERROR, "resolve_phrase: invalid holder ordinal");
    ordinal = DatumGetInt64(d_ord);
    if (ordinal < 1 || ordinal > ctx->n_pres)
        elog(ERROR, "resolve_phrase: invalid holder ordinal");

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull)
        elog(ERROR, "resolve_phrase: invalid holder set");
    ent = laplace_pub_blake3_to_hash128(d_ent);

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 64;
        sz = sizeof(ResolveHolderRow) * (Size) ctx->cap;
        ctx->rows = ctx->rows
            ? (ResolveHolderRow *) repalloc(ctx->rows, sz)
            : (ResolveHolderRow *) palloc(sz);
    }
    ctx->rows[ctx->count++] = (ResolveHolderRow) { ordinal, ent };
    MemoryContextSwitchTo(prev);
    return true;
}

static int
resolve_holder_cmp(const void *a, const void *b)
{
    const ResolveHolderRow *x = (const ResolveHolderRow *) a;
    const ResolveHolderRow *y = (const ResolveHolderRow *) b;

    if (x->ordinal != y->ordinal)
        return x->ordinal < y->ordinal ? -1 : 1;
    return memcmp(&x->entity, &y->entity, sizeof(hash128_t));
}

typedef struct { HTAB *degree;bool any; } JointRead;

static void resolve_joint_cell(const LaplaceConsensusRow *row,void *context)
{
    JointRead *read=context;if(row->object_is_null)return;
    const hash128_t *ends[]={&row->subject,&row->object};
    for(int i=0;i<2;i++){
        bool found;joint_degree_entry *entry=hash_search(read->degree,ends[i],HASH_ENTER,&found);
        if(!found)*entry=(joint_degree_entry){.key=*ends[i],.rd=PG_INT64_MAX};
        if(pg_add_s64_overflow(entry->witnesses,row->witnesses,&entry->witnesses)
           || pg_add_s64_overflow(entry->rating,row->rating,&entry->rating))
            ereport(ERROR,(errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),errmsg("Joint evidence exceeds integer capacity")));
        entry->rd=Min(entry->rd,row->rd);
    }
    read->any=true;
}

PG_FUNCTION_INFO_V1(pg_laplace_resolve_phrase);

Datum
pg_laplace_resolve_phrase(PG_FUNCTION_ARGS)
{
    text          *t;
    const uint8_t *base;
    phrase_ctx     ctx;
    int            rc;
    bool           found = false;
    hash128_t      found_id = { 0, 0 };

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    t = PG_GETARG_TEXT_PP(0);
    if (VARSIZE_ANY_EXHDR(t) == 0)
        PG_RETURN_NULL();
    if (!laplace_perfcache_ready())
        ereport(ERROR,
                (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                 errmsg("resolve_phrase requires the T0 perfcache")));

    base = (const uint8_t *) VARDATA_ANY(t);
    memset(&ctx, 0, sizeof(ctx));

    /* The tree is freed once every sub-span root id is computed, before SPI
     * connects, so no native allocation crosses an elog. */
    tier_tree_t   *tree = NULL;
    const uint8_t *norm = NULL;
    size_t         norm_len = 0;

    rc = laplace_content_tree_build_public(base, (size_t) VARSIZE_ANY_EXHDR(t), &tree);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("resolve_phrase: segmentation failed (rc=%d)", rc)));
    norm = tier_tree_text(tree, &norm_len);
    if (norm == NULL || phrase_collect_from_tree(tree, norm, norm_len, &ctx) != 0)
    {
        tier_tree_free(tree);
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("resolve_phrase: span collection failed")));
    }
    if (ctx.n == 0)
    {
        tier_tree_free(tree);
        PG_RETURN_NULL();
    }

    /*
     * Every contiguous span of words is a candidate; root ids are computed
     * natively and presence is one set read through the canonical Native identity reader.
     * Candidates enumerate by length descending, then position ascending.
     *
     * Presence reads stored entities, not entity_exists(): that helper is true
     * for every Tier-0 codepoint, so a one-codepoint word would always count
     * as known content.
     */
    {
        int         n_span = ctx.n * (ctx.n + 1) / 2;
        hash128_t  *span_id = (hash128_t *) palloc(sizeof(hash128_t) * n_span);
        bool       *span_ok = (bool *) palloc(sizeof(bool) * n_span);
        hash128_t  *ok_ids = (hash128_t *) palloc(sizeof(hash128_t) * n_span);
        int         n_elems = 0;
        int         s;

        /* A span's bytes are the normalized text from its first word's start
         * to its last word's end, inter-word bytes included. */
        s = 0;
        for (int L = ctx.n; L >= 1; L--)
        {
            for (int i = 0; i + L <= ctx.n; i++, s++)
            {
                const uint8_t *sp = norm + ctx.off[i];
                size_t  splen = (size_t) ((ctx.off[i + L - 1] + ctx.len[i + L - 1])
                                          - ctx.off[i]);

                if (laplace_content_root_id(sp, splen, &span_id[s]) == 0)
                {
                    span_ok[s] = true;
                    ok_ids[n_elems++] = span_id[s];
                }
                else
                {
                    span_ok[s] = false;
                }
            }
        }

        /* Root ids are computed; nothing below reads the tree's text. */
        tier_tree_free(tree);
        tree = NULL;

        if (n_elems > 0)
        {
            HTAB             *present;
            HASHCTL           hctl;
            Oid               b3_arr_oid = laplace_pub_blake3_array_oid();
            ArrayType        *in_arr = laplace_pub_blake3_array(ok_ids, n_elems);
            Datum             parg = PointerGetDatum(in_arr);
            bool              pnull = false;
            ResolvePresentCtx pctx;

            memset(&hctl, 0, sizeof(hctl));
            hctl.keysize = sizeof(hash128_t);
            hctl.entrysize = sizeof(hash128_t);
            hctl.hcxt = CurrentMemoryContext;
            present = hash_create("resolve_phrase present",
                                  (long) n_elems,
                                  &hctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

            pctx.caller = CurrentMemoryContext;
            pctx.present = present;
            (void) laplace_pub_call_srf("public", "laplace_present_ids",
                                        1, &b3_arr_oid, &parg, &pnull,
                                        resolve_visit_present, &pctx);
            pfree(in_arr);

            /*
             * SELECT among present spans by joint evidence within this
             * observation: consensus shared with the other candidates decides,
             * not any span's standing alone. Length and position only break
             * ties.
             */
            {
                Datum     *pres_elems = (Datum *) palloc(sizeof(Datum) * n_span);
                hash128_t *pres_ids = (hash128_t *) palloc(sizeof(hash128_t) * n_span);
                int        n_pres = 0;
                HTAB      *degree = NULL;
                HASHCTL    dctl;

                s = 0;
                for (int L = ctx.n; L >= 1; L--)
                    for (int i = 0; i + L <= ctx.n; i++, s++)
                    {
                        bool pfound;
                        if (!span_ok[s])
                            continue;
                        hash_search(present, &span_id[s], HASH_FIND, &pfound);
                        if (pfound)
                        {
                            pres_ids[n_pres]     = span_id[s];
                            pres_elems[n_pres++] = hash128_to_datum(&span_id[s]);
                        }
                    }

                memset(&dctl, 0, sizeof(dctl));
                dctl.keysize   = sizeof(hash128_t);
                dctl.entrysize = sizeof(joint_degree_entry);
                degree = hash_create("resolve_phrase joint degree",
                                     (n_pres > 0 ? (long) n_pres : 16),
                                     &dctl, HASH_ELEM | HASH_BLOBS);

                if (n_pres > 1)
                {
                    ArrayType *cand_arr = construct_array(
                        pres_elems, n_pres, BYTEAOID, -1, false, TYPALIGN_INT);
                    JointRead joint={degree,false};
                    laplace_consensus_scan(cand_arr,cand_arr,NULL,resolve_joint_cell,&joint,NULL);
                    bool joint_from_consensus=joint.any;

                    /* With no consensus among the candidates, joint evidence
                     * is trajectory co-occurrence. */
                    if (!joint_from_consensus)
                    {
                        /*
                         * A candidate's degree is how many of its containers also
                         * contain another candidate: containment and co-occurrence
                         * are read from trajectories, not attestations. One kept
                         * single-key GIN probe per candidate; a multi-key `&&` probe
                         * makes the planner abandon the index.
                         */
                        HTAB      *owners;
                        HASHCTL    octl;
                        hash128_t *cont = (hash128_t *) palloc(
                            sizeof(hash128_t) * n_pres * RESOLVE_CONTAINER_PROBE_LIMIT);
                        int       *cont_n = (int *) palloc0(sizeof(int) * n_pres);
                        ArrayType *cand_b3 = laplace_pub_blake3_array(pres_ids, n_pres);
                        ArrayType *empty_b3 = laplace_pub_blake3_array(NULL, 0);
                        Oid        h_types[6] = {
                            b3_arr_oid, b3_arr_oid, BOOLOID, INT2OID, INT8OID, INT2OID
                        };
                        Datum      h_args[6] = {
                            PointerGetDatum(cand_b3),
                            PointerGetDatum(empty_b3),
                            BoolGetDatum(true),
                            Int16GetDatum(-1),
                            Int64GetDatum((int64) RESOLVE_CONTAINER_PROBE_LIMIT),
                            Int16GetDatum(3)
                        };
                        bool       h_nulls[6] = { false, false, false, false, false, false };
                        ResolveHolderCtx hctx;

                        memset(&octl, 0, sizeof(octl));
                        octl.keysize   = sizeof(hash128_t);
                        octl.entrysize = sizeof(container_owner_entry);
                        owners = hash_create("resolve_phrase containers",
                                             (long) n_pres * RESOLVE_CONTAINER_PROBE_LIMIT,
                                             &octl, HASH_ELEM | HASH_BLOBS);

                        /* Complete non-hub holders for every candidate, in one
                         * read. A hub contributes no arbitrary heap-order subset. */
                        MemSet(&hctx, 0, sizeof(hctx));
                        hctx.caller = CurrentMemoryContext;
                        hctx.n_pres = n_pres;
                        (void) laplace_pub_call_srf("public", "laplace_holders", 6,
                                                    h_types, h_args, h_nulls,
                                                    resolve_visit_holder, &hctx);
                        pfree(cand_b3);
                        pfree(empty_b3);
                        if (hctx.count > 1)
                            qsort(hctx.rows, (Size) hctx.count,
                                  sizeof(ResolveHolderRow), resolve_holder_cmp);

                        for (int r = 0; r < hctx.count; r++)
                        {
                            bool                   hfound;
                            int                    c = (int) hctx.rows[r].ordinal - 1;
                            hash128_t              cid = hctx.rows[r].entity;
                            container_owner_entry *owner;

                            if (cont_n[c] >= RESOLVE_CONTAINER_PROBE_LIMIT)
                                elog(ERROR, "resolve_phrase: invalid holder set");
                            cont[c * RESOLVE_CONTAINER_PROBE_LIMIT + cont_n[c]++] = cid;
                            owner = (container_owner_entry *)
                                hash_search(owners, &cid, HASH_ENTER, &hfound);
                            if (!hfound)
                                *owner = (container_owner_entry) { cid, c, 1 };
                            else if (owner->first_candidate != c)
                            {
                                owner->n_candidates++;
                                owner->first_candidate = c;
                            }
                        }
                        if (hctx.rows != NULL)
                            pfree(hctx.rows);

                        /* A container holding two or more candidates is joint
                         * evidence; credit every candidate inside it. */
                        for (int c = 0; c < n_pres; c++)
                        {
                            int64 shared = 0;
                            bool  dfound;
                            joint_degree_entry *ent;

                            for (int k = 0; k < cont_n[c]; k++)
                            {
                                bool ofound;
                                container_owner_entry *oe = (container_owner_entry *)
                                    hash_search(owners,
                                                &cont[c * RESOLVE_CONTAINER_PROBE_LIMIT + k],
                                                HASH_FIND, &ofound);
                                if (ofound && oe->n_candidates > 1)
                                    shared++;
                            }
                            if (shared == 0)
                                continue;
                            ent = (joint_degree_entry *)
                                hash_search(degree, &pres_ids[c], HASH_ENTER, &dfound);
                            if (!dfound)
                            {
                                ent->witnesses = 0;
                                ent->rating    = 0;
                                ent->rd        = PG_INT64_MAX;
                                ent->degree    = 0;
                            }
                            ent->degree += shared;
                        }
                    }
                }

                /*
                 * Order: summed witnesses, summed rating, lower minimum rd,
                 * co-occurrence degree; the first span in length-descending,
                 * position-ascending order wins ties.
                 */
                {
                    int64 best_w = 0, best_r = 0, best_rd = 0, best_d = 0;
                    bool  have_best = false;

                    s = 0;
                    for (int L = ctx.n; L >= 1; L--)
                        for (int i = 0; i + L <= ctx.n; i++, s++)
                        {
                            bool  pfound, dfound, better;
                            int64 w = 0, rt = 0, rd = PG_INT64_MAX, d = 0;
                            joint_degree_entry *ent;

                            if (!span_ok[s])
                                continue;
                            hash_search(present, &span_id[s], HASH_FIND, &pfound);
                            if (!pfound)
                                continue;
                            ent = (joint_degree_entry *)
                                hash_search(degree, &span_id[s], HASH_FIND, &dfound);
                            if (dfound)
                            {
                                w  = ent->witnesses;
                                rt = ent->rating;
                                rd = ent->rd;
                                d  = ent->degree;
                            }

                            if (!have_best)          better = true;
                            else if (w  != best_w)   better = w  > best_w;
                            else if (rt != best_r)   better = rt > best_r;
                            else if (rd != best_rd)  better = rd < best_rd;
                            else                     better = d  > best_d;

                            if (better)
                            {
                                best_w = w; best_r = rt; best_rd = rd; best_d = d;
                                have_best = true;
                                found_id  = span_id[s];
                                found     = true;
                            }
                        }
                }
            }
        }
    }

    if (!found)
        PG_RETURN_NULL();
    PG_RETURN_DATUM(hash128_to_datum(&found_id));
}

