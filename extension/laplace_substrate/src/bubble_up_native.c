#include "postgres.h"

#include <limits.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

#include "access/relscan.h"
#include "access/table.h"
#include "access/tableam.h"
#include "catalog/namespace.h"
#include "catalog/pg_inherits.h"
#include "catalog/pg_type.h"
#include "executor/tuptable.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/numeric.h"
#include "utils/rel.h"
#include "utils/snapmgr.h"

#include "laplace/core/attestation_engine.h"
#include "laplace/core/glicko2.h"
#include "laplace/core/hash128.h"
#include "laplace/core/language_law.h"
#include "laplace/core/relation_law.h"
#include "laplace_pg/executor.h"

#include "bubble_up_native.h"
#include "consensus_scan.h"
#include "converse_bindings.h"
#include "public_direct.h"
#include "relations_internal.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_bubble_domain_scores);
PG_FUNCTION_INFO_V1(pg_laplace_bubble_up_batch_impl);

static HTAB *
bu_hash_create(MemoryContext ctx, const char *name, Size key_sz, Size entry_sz, long init)
{
    HASHCTL ctl;
    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = key_sz;
    ctl.entrysize = entry_sz;
    ctl.hcxt = ctx;
    return hash_create(name, Max(init, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

static int
bu_id_cmp(const void *a, const void *b)
{
    return memcmp(a, b, sizeof(hash128_t));
}

static hash128_t *
bu_deconstruct_bytea_ids(ArrayType *arr, int *out_n)
{
    Datum     *vals;
    bool      *nulls;
    int        n = 0;
    hash128_t *ids = NULL;
    int        k = 0;

    *out_n = 0;
    if (arr == NULL)
        return NULL;
    deconstruct_array(arr, BYTEAOID, -1, false, TYPALIGN_INT, &vals, &nulls, &n);
    if (n > 0)
    {
        ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n);
        for (int i = 0; i < n; i++)
        {
            if (!nulls[i])
                ids[k++] = datum_to_hash128(vals[i]);
        }
    }
    pfree(vals);
    pfree(nulls);
    *out_n = k;
    return ids;
}

static int
bu_dedup_sort_ids(const hash128_t *in, int n, hash128_t *out)
{
    int w = 0;
    if (in == NULL || n <= 0)
        return 0;
    memcpy(out, in, sizeof(hash128_t) * (Size) n);
    if (n > 1)
        qsort(out, (Size) n, sizeof(hash128_t), bu_id_cmp);
    for (int i = 0; i < n; i++)
    {
        if (w == 0 || memcmp(&out[w - 1], &out[i], sizeof(hash128_t)) != 0)
            out[w++] = out[i];
    }
    return w;
}

static ArrayType *
bu_collect_has_sense_family_types(void)
{
    int        cap = (int) laplace_relation_table_count + 1;
    hash128_t *ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) cap);
    int        cnt = 0;
    ArrayType *arr;

    for (size_t i = 0; i < laplace_relation_table_count; i++)
    {
        hash128_t tid;
        int       in_fam = 0;
        if (laplace_relation_type_id(laplace_relation_table[i].canonical, &tid) != 0)
            continue;
        if (laplace_relation_in_family(&tid, "HAS_SENSE", &in_fam) == 0 && in_fam != 0)
            ids[cnt++] = tid;
    }
    arr = hash128_array_from_ids(ids, cnt);
    pfree(ids);
    return arr;
}

/* ========================================================================
 * Domain Scores (taxonomy._bubble_domain_scores)
 * ======================================================================== */

typedef struct BuSynMemKey
{
    hash128_t synset_id;
    hash128_t member_id;
} BuSynMemKey;

typedef struct BuMemNbrKey
{
    hash128_t member_id;
    hash128_t nbr_id;
} BuMemNbrKey;

typedef struct BuMemHitEntry
{
    hash128_t member_id;
    int64     hits;
} BuMemHitEntry;

typedef struct BuDomScoreEntry
{
    hash128_t synset_id;
    int64     domain_hits;
} BuDomScoreEntry;

typedef struct BuMemberCollectCtx
{
    MemoryContext caller;
    HTAB         *pair_set;
    BuSynMemKey  *pairs;
    int           n_pairs;
    int           cap_pairs;
    HTAB         *mem_set;
    hash128_t    *members;
    int           n_members;
    int           cap_members;
} BuMemberCollectCtx;

static void
bu_add_syn_member(BuMemberCollectCtx *ctx, hash128_t synset_id, hash128_t member_id)
{
    MemoryContext prev = MemoryContextSwitchTo(ctx->caller);
    BuSynMemKey   pk;
    bool          found = false;

    MemSet(&pk, 0, sizeof(pk));
    pk.synset_id = synset_id;
    pk.member_id = member_id;
    (void) hash_search(ctx->pair_set, &pk, HASH_ENTER, &found);
    if (!found)
    {
        if (ctx->n_pairs >= ctx->cap_pairs)
        {
            Size sz;
            ctx->cap_pairs = ctx->cap_pairs ? ctx->cap_pairs * 2 : 64;
            sz = sizeof(BuSynMemKey) * (Size) ctx->cap_pairs;
            ctx->pairs = ctx->pairs ? (BuSynMemKey *) repalloc(ctx->pairs, sz)
                                    : (BuSynMemKey *) palloc(sz);
        }
        ctx->pairs[ctx->n_pairs++] = pk;
    }

    (void) hash_search(ctx->mem_set, &member_id, HASH_ENTER, &found);
    if (!found)
    {
        if (ctx->n_members >= ctx->cap_members)
        {
            Size sz;
            ctx->cap_members = ctx->cap_members ? ctx->cap_members * 2 : 64;
            sz = sizeof(hash128_t) * (Size) ctx->cap_members;
            ctx->members = ctx->members ? (hash128_t *) repalloc(ctx->members, sz)
                                        : (hash128_t *) palloc(sz);
        }
        ctx->members[ctx->n_members++] = member_id;
    }
    MemoryContextSwitchTo(prev);
}

static void
bu_visit_member_fwd(const LaplaceConsensusRow *row, void *opaque)
{
    if (row->object_is_null)
        return;
    bu_add_syn_member((BuMemberCollectCtx *) opaque, row->subject, row->object);
}

static void
bu_visit_member_rev(const LaplaceConsensusRow *row, void *opaque)
{
    if (row->object_is_null)
        return;
    bu_add_syn_member((BuMemberCollectCtx *) opaque, row->object, row->subject);
}

typedef struct BuHolderCtx
{
    MemoryContext    caller;
    const hash128_t *members;
    int              n_members;
    HTAB            *dom_set;
    HTAB            *seen_mn;
    HTAB            *mem_hits;
    Oid              arg_types[2];
} BuHolderCtx;

static bool
bu_visit_domain_holder(TupleTableSlot *slot, void *opaque)
{
    BuHolderCtx *ctx = (BuHolderCtx *) opaque;
    bool         isnull = false;
    Datum        d_ord, d_path;
    int64        ord;
    hash128_t    mem_id;
    ArrayType   *single_b3;
    Datum        args[2];
    bool         nulls[2] = { false, false };
    static const char * const fns[2] = { "laplace_follows", "laplace_precedes" };

    d_ord = slot_getattr(slot, 1, &isnull);
    if (isnull)
        return true;
    ord = DatumGetInt64(d_ord);
    if (ord < 1 || ord > ctx->n_members)
        return true;

    d_path = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;

    mem_id = ctx->members[ord - 1];
    single_b3 = laplace_pub_blake3_array(&mem_id, 1);
    args[0] = d_path;
    args[1] = PointerGetDatum(single_b3);

    for (int fi = 0; fi < 2; fi++)
    {
        bool  rn = false;
        Datum res_d = laplace_pub_call("public", fns[fi], 2,
                                       ctx->arg_types, args, nulls, &rn);
        int        nn = 0;
        hash128_t *nbrs = NULL;

        if (rn)
            continue;
        nn = laplace_pub_deconstruct_blake3_array(DatumGetArrayTypeP(res_d), &nbrs);
        if (nbrs != NULL)
        {
            MemoryContext prev = MemoryContextSwitchTo(ctx->caller);
            for (int ni = 0; ni < nn; ni++)
            {
                BuMemNbrKey mnk;
                bool        found = false;

                if (hash_search(ctx->dom_set, &nbrs[ni], HASH_FIND, NULL) == NULL)
                    continue;
                MemSet(&mnk, 0, sizeof(mnk));
                mnk.member_id = mem_id;
                mnk.nbr_id = nbrs[ni];
                (void) hash_search(ctx->seen_mn, &mnk, HASH_ENTER, &found);
                if (!found)
                {
                    bool           hf = false;
                    BuMemHitEntry *mh = (BuMemHitEntry *) hash_search(
                        ctx->mem_hits, &mem_id, HASH_ENTER, &hf);
                    mh->hits = hf ? (mh->hits + 1) : 1;
                }
            }
            MemoryContextSwitchTo(prev);
            pfree(nbrs);
        }
        pfree(DatumGetPointer(res_d));
    }

    pfree(single_b3);
    return true;
}

static HTAB *
bu_compute_domain_scores(MemoryContext target_cxt,
                         const hash128_t *synsets, int n_synsets,
                         const hash128_t *domain_ctx, int n_domain)
{
    MemoryContext      work, prev;
    HTAB              *scores = bu_hash_create(target_cxt, "bu dom scores",
                                               sizeof(hash128_t), sizeof(BuDomScoreEntry),
                                               Max(n_synsets, 16));
    HTAB              *dom_set;
    hash128_t         *uniq_syn;
    int                n_usyn;
    ArrayType         *syn_arr, *hs_arr;
    BuMemberCollectCtx mctx;

    if (synsets == NULL || n_synsets <= 0 || domain_ctx == NULL || n_domain <= 0)
        return scores;

    work = AllocSetContextCreate(CurrentMemoryContext, "bu_domain_scores", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    dom_set = bu_hash_create(work, "bu dom set", sizeof(hash128_t), sizeof(hash128_t), n_domain);
    for (int i = 0; i < n_domain; i++)
        (void) hash_search(dom_set, &domain_ctx[i], HASH_ENTER, NULL);

    uniq_syn = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_synsets);
    n_usyn = bu_dedup_sort_ids(synsets, n_synsets, uniq_syn);
    syn_arr = hash128_array_from_ids(uniq_syn, n_usyn);
    hs_arr = bu_collect_has_sense_family_types();

    MemSet(&mctx, 0, sizeof(mctx));
    mctx.caller = work;
    mctx.pair_set = bu_hash_create(work, "bu pairs", sizeof(BuSynMemKey), sizeof(BuSynMemKey), n_usyn * 2);
    mctx.mem_set = bu_hash_create(work, "bu mems", sizeof(hash128_t), sizeof(hash128_t), n_usyn * 2);

    laplace_consensus_candidates_stored(syn_arr, NULL, hs_arr, bu_visit_member_fwd, &mctx);
    laplace_consensus_candidates_stored(NULL, syn_arr, hs_arr, bu_visit_member_rev, &mctx);

    pfree(syn_arr);
    pfree(hs_arr);

    if (mctx.n_members > 0)
    {
        Oid         b3_arr_oid = laplace_pub_blake3_array_oid();
        ArrayType  *dm_b3 = laplace_pub_blake3_array(mctx.members, mctx.n_members);
        ArrayType  *empty_b3 = laplace_pub_blake3_array(NULL, 0);
        Oid         h_types[6] = { b3_arr_oid, b3_arr_oid, BOOLOID, INT2OID, INT8OID, INT2OID };
        Datum       h_args[6] = {
            PointerGetDatum(dm_b3),
            PointerGetDatum(empty_b3),
            BoolGetDatum(true),
            Int16GetDatum(-1),
            Int64GetDatum(4096),
            Int16GetDatum(2)
        };
        bool        h_nulls[6] = { false, false, false, false, false, false };
        BuHolderCtx hctx;

        MemSet(&hctx, 0, sizeof(hctx));
        hctx.caller = work;
        hctx.members = mctx.members;
        hctx.n_members = mctx.n_members;
        hctx.dom_set = dom_set;
        hctx.seen_mn = bu_hash_create(work, "bu mn", sizeof(BuMemNbrKey), sizeof(BuMemNbrKey),
                                      mctx.n_members * 4);
        hctx.mem_hits = bu_hash_create(work, "bu mh", sizeof(hash128_t), sizeof(BuMemHitEntry),
                                       mctx.n_members);
        hctx.arg_types[0] = laplace_pub_geometry_oid();
        hctx.arg_types[1] = b3_arr_oid;

        (void) laplace_pub_call_srf("public", "laplace_holders", 6,
                                    h_types, h_args, h_nulls,
                                    bu_visit_domain_holder, &hctx);

        pfree(dm_b3);
        pfree(empty_b3);

        for (int i = 0; i < mctx.n_pairs; i++)
        {
            BuMemHitEntry *mh = (BuMemHitEntry *) hash_search(
                hctx.mem_hits, &mctx.pairs[i].member_id, HASH_FIND, NULL);
            bool             sf = false;
            BuDomScoreEntry *se;
            if (mh == NULL || mh->hits <= 0)
                continue;
            se = (BuDomScoreEntry *) hash_search(
                scores, &mctx.pairs[i].synset_id, HASH_ENTER, &sf);
            se->domain_hits = sf ? (se->domain_hits + mh->hits) : mh->hits;
        }
    }

    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return scores;
}

Datum
pg_laplace_bubble_domain_scores(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    hash128_t     *synsets, *domain;
    int            n_syn = 0, n_dom = 0;
    HTAB          *scores;
    HASH_SEQ_STATUS seq;
    BuDomScoreEntry *se;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        return (Datum) 0;

    synsets = bu_deconstruct_bytea_ids(PG_GETARG_ARRAYTYPE_P(0), &n_syn);
    domain = bu_deconstruct_bytea_ids(PG_GETARG_ARRAYTYPE_P(1), &n_dom);
    if (n_syn > 0 && n_dom > 0)
    {
        scores = bu_compute_domain_scores(CurrentMemoryContext, synsets, n_syn, domain, n_dom);
        hash_seq_init(&seq, scores);
        while ((se = (BuDomScoreEntry *) hash_seq_search(&seq)) != NULL)
        {
            Datum vals[2] = { hash128_to_datum(&se->synset_id), Int64GetDatum(se->domain_hits) };
            bool  nulls[2] = { false, false };
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
            pfree(DatumGetPointer(vals[0]));
        }
        hash_destroy(scores);
    }
    if (synsets) pfree(synsets);
    if (domain) pfree(domain);
    return (Datum) 0;
}

/* ========================================================================
 * Bubble-Up Batch (taxonomy._bubble_up_batch_impl)
 * ======================================================================== */

typedef struct BuResolvedRow
{
    hash128_t   term;
    hash128_t   sense_id;
    hash128_t   synset_id;
    const char *via_relation;
    double      base_eff_mu;
    int64       witnesses;
    int64       rating;
    int64       rd;
    hash128_t   language;
    bool        language_null;
} BuResolvedRow;

typedef struct BuTermLangKey
{
    hash128_t term;
    hash128_t lang;
} BuTermLangKey;

typedef struct BuTermLangSum
{
    BuTermLangKey k;
    __int128      total_score;
    int64         total_obs;
} BuTermLangSum;

typedef struct BuTermBestLang
{
    hash128_t term;
    hash128_t lang;
    __int128  total_score;
    int64     total_obs;
} BuTermBestLang;

typedef struct BuTermSynKey
{
    hash128_t term;
    hash128_t synset_id;
} BuTermSynKey;

typedef struct BuRankedItem
{
    BuTermSynKey k;
    hash128_t    sense_id;
    const char  *via_relation;
    double       base_eff_mu;
    int64        domain_hits;
    int64        witnesses;
    double       score;
    int64        rank_key;
    int64        dedup_eff_fp;
} BuRankedItem;

static int
bu_ranked_cmp(const void *a, const void *b)
{
    const BuRankedItem *x = (const BuRankedItem *) a;
    const BuRankedItem *y = (const BuRankedItem *) b;
    int c = memcmp(&x->k.term, &y->k.term, sizeof(hash128_t));
    if (c != 0) return c;
    if (x->witnesses != y->witnesses) return x->witnesses > y->witnesses ? -1 : 1;
    if (x->score != y->score) return x->score > y->score ? -1 : 1;
    if (x->rank_key != y->rank_key) return x->rank_key > y->rank_key ? -1 : 1;
    return memcmp(&x->k.synset_id, &y->k.synset_id, sizeof(hash128_t));
}

static LaplaceBubbleRow *
bu_finish_resolved(MemoryContext caller, MemoryContext work,
                   BuResolvedRow *resolved, int n_resolved,
                   const hash128_t *domain_ctx, int n_domain, bool domain_is_null,
                   int32 k, bool k_is_null, int *out_count)
{
    bool              lang_filter = domain_is_null || (n_domain > 0);
    HTAB             *term_lang = bu_hash_create(work, "bu tl", sizeof(hash128_t), sizeof(BuTermBestLang), Max(n_resolved, 16));
    HTAB             *cand_has_any = bu_hash_create(work, "bu ca", sizeof(hash128_t), sizeof(hash128_t), Max(n_resolved, 16));
    HTAB             *cand_langs = bu_hash_create(work, "bu cl", sizeof(BuTermLangKey), sizeof(BuTermLangKey), Max(n_resolved, 16));
    BuResolvedRow    *scoped = (BuResolvedRow *) palloc(sizeof(BuResolvedRow) * (Size) Max(n_resolved, 1));
    int               n_scoped = 0;
    hash128_t        *sc_syns = (hash128_t *) palloc(sizeof(hash128_t) * (Size) Max(n_resolved, 1));
    int               n_sc_syns = 0;
    HTAB             *dom_scores = NULL;
    HTAB             *best_map;
    HASH_SEQ_STATUS   seq;
    BuRankedItem     *ranked;
    int               n_ranked = 0;
    LaplaceBubbleRow *out = NULL;
    int               n_out = 0;

    if (lang_filter)
    {
        HTAB          *tl_sums = bu_hash_create(work, "bu tls", sizeof(BuTermLangKey), sizeof(BuTermLangSum), Max(n_resolved, 16));
        BuTermLangSum *sum_e;

        for (int i = 0; i < n_resolved; i++)
        {
            const BuResolvedRow *r = &resolved[i];
            BuTermLangKey        lk;
            bool                 f = false;
            BuTermLangSum       *sl;

            if (r->language_null)
                continue;

            (void) hash_search(cand_has_any, &r->synset_id, HASH_ENTER, NULL);
            MemSet(&lk, 0, sizeof(lk));
            lk.term = r->synset_id;
            lk.lang = r->language;
            (void) hash_search(cand_langs, &lk, HASH_ENTER, NULL);

            MemSet(&lk, 0, sizeof(lk));
            lk.term = r->term;
            lk.lang = r->language;
            sl = (BuTermLangSum *) hash_search(tl_sums, &lk, HASH_ENTER, &f);
            if (!f)
            {
                sl->total_score = (__int128) r->witnesses * (__int128) 1000000000LL;
                sl->total_obs = r->witnesses;
            }
            else
            {
                sl->total_score += (__int128) r->witnesses * (__int128) 1000000000LL;
                sl->total_obs += r->witnesses;
            }
        }

        hash_seq_init(&seq, tl_sums);
        while ((sum_e = (BuTermLangSum *) hash_seq_search(&seq)) != NULL)
        {
            bool            f = false;
            BuTermBestLang *best = (BuTermBestLang *) hash_search(
                term_lang, &sum_e->k.term, HASH_ENTER, &f);
            if (!f ||
                sum_e->total_score > best->total_score ||
                (sum_e->total_score == best->total_score &&
                 (sum_e->total_obs > best->total_obs ||
                  (sum_e->total_obs == best->total_obs &&
                   memcmp(&sum_e->k.lang, &best->lang, sizeof(hash128_t)) < 0))))
            {
                best->lang = sum_e->k.lang;
                best->total_score = sum_e->total_score;
                best->total_obs = sum_e->total_obs;
            }
        }
    }

    for (int i = 0; i < n_resolved; i++)
    {
        const BuResolvedRow *r = &resolved[i];
        bool                 keep = true;

        if (lang_filter)
        {
            BuTermBestLang *tl = (BuTermBestLang *) hash_search(term_lang, &r->term, HASH_FIND, NULL);
            if (tl != NULL &&
                hash_search(cand_has_any, &r->synset_id, HASH_FIND, NULL) != NULL)
            {
                BuTermLangKey ck;
                MemSet(&ck, 0, sizeof(ck));
                ck.term = r->synset_id;
                ck.lang = tl->lang;
                if (hash_search(cand_langs, &ck, HASH_FIND, NULL) == NULL)
                    keep = false;
            }
        }

        if (keep)
        {
            scoped[n_scoped++] = *r;
            sc_syns[n_sc_syns++] = r->synset_id;
        }
    }

    if (n_scoped == 0)
        return NULL;

    if (!domain_is_null && n_domain > 0 && n_sc_syns > 0)
        dom_scores = bu_compute_domain_scores(work, sc_syns, n_sc_syns, domain_ctx, n_domain);

    best_map = bu_hash_create(work, "bu ranked", sizeof(BuTermSynKey), sizeof(BuRankedItem), n_scoped);
    for (int i = 0; i < n_scoped; i++)
    {
        const BuResolvedRow *r = &scoped[i];
        BuTermSynKey         tk;
        bool                 f = false;
        BuRankedItem        *it;
        int64                eff_fp = laplace_effective_mu_fp(r->rating, r->rd);

        MemSet(&tk, 0, sizeof(tk));
        tk.term = r->term;
        tk.synset_id = r->synset_id;
        it = (BuRankedItem *) hash_search(best_map, &tk, HASH_ENTER, &f);
        if (!f ||
            r->witnesses > it->witnesses ||
            (r->witnesses == it->witnesses && eff_fp > it->dedup_eff_fp))
        {
            BuDomScoreEntry *ds = dom_scores
                ? (BuDomScoreEntry *) hash_search(dom_scores, &r->synset_id, HASH_FIND, NULL)
                : NULL;
            int64 dh = ds ? ds->domain_hits : 0;

            it->sense_id = r->sense_id;
            it->via_relation = r->via_relation;
            it->base_eff_mu = r->base_eff_mu;
            it->domain_hits = dh;
            it->witnesses = r->witnesses;
            it->score = (dh > 0) ? (r->base_eff_mu * (1.0 + log(1.0 + (double) dh))) : r->base_eff_mu;
            it->rank_key = eff_fp;
            it->dedup_eff_fp = eff_fp;
        }
    }

    ranked = (BuRankedItem *) palloc(sizeof(BuRankedItem) * (Size) Max(hash_get_num_entries(best_map), 1));
    hash_seq_init(&seq, best_map);
    {
        BuRankedItem *it;
        while ((it = (BuRankedItem *) hash_seq_search(&seq)) != NULL)
            ranked[n_ranked++] = *it;
    }

    if (n_ranked > 1)
        qsort(ranked, (Size) n_ranked, sizeof(BuRankedItem), bu_ranked_cmp);

    {
        int *keep_idx = (int *) palloc(sizeof(int) * (Size) Max(n_ranked, 1));
        hash128_t cur_term = {0, 0};
        bool      have_term = false;
        int32     rn = 0;

        for (int i = 0; i < n_ranked; i++)
        {
            if (!have_term || !hash128_eq(&ranked[i].k.term, &cur_term))
            {
                cur_term = ranked[i].k.term;
                have_term = true;
                rn = 0;
            }
            rn++;
            if (k_is_null || rn <= k)
                keep_idx[n_out++] = i;
        }

        if (n_out > 0)
        {
            MemoryContextSwitchTo(caller);
            out = (LaplaceBubbleRow *) MemoryContextAlloc(
                caller, sizeof(LaplaceBubbleRow) * (Size) n_out);
            for (int i = 0; i < n_out; i++)
            {
                const BuRankedItem *r = &ranked[keep_idx[i]];
                out[i].term = r->k.term;
                out[i].sense_id = r->sense_id;
                out[i].synset_id = r->k.synset_id;
                out[i].via_relation = r->via_relation;
                out[i].score = r->score;
                out[i].base_eff_mu = r->base_eff_mu;
                out[i].domain_hits = r->domain_hits;
                out[i].witnesses = r->witnesses;
            }
        }
    }

    *out_count = n_out;
    return out;
}

typedef struct BuLegEdge
{
    hash128_t   term;
    hash128_t   target;
    hash128_t   type;
    const char *canon;
    int64       rating;
    int64       rd;
    int64       wit;
} BuLegEdge;

typedef struct BuLegEdgeCtx
{
    MemoryContext caller;
    BuLegEdge    *items;
    int           count;
    int           cap;
} BuLegEdgeCtx;

static void
bu_visit_leg_hs(const LaplaceConsensusRow *row, void *opaque)
{
    BuLegEdgeCtx *ctx = (BuLegEdgeCtx *) opaque;
    const char   *cname = laplace_relation_canonical_for_type_id(&row->type);
    MemoryContext prev;
    if (row->object_is_null || cname == NULL)
        return;
    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 32;
        sz = sizeof(BuLegEdge) * (Size) ctx->cap;
        ctx->items = ctx->items ? (BuLegEdge *) repalloc(ctx->items, sz) : (BuLegEdge *) palloc(sz);
    }
    ctx->items[ctx->count].term = row->subject;
    ctx->items[ctx->count].target = row->object;
    ctx->items[ctx->count].type = row->type;
    ctx->items[ctx->count].canon = cname;
    ctx->items[ctx->count].rating = row->rating;
    ctx->items[ctx->count].rd = row->rd;
    ctx->items[ctx->count].wit = row->witnesses;
    ctx->count++;
    MemoryContextSwitchTo(prev);
}

typedef struct BuLegSenseOf
{
    hash128_t subj;
    hash128_t obj;
    int64     rating;
    int64     rd;
    int64     wit;
} BuLegSenseOf;

typedef struct BuLegSenseOfCtx
{
    MemoryContext caller;
    HTAB         *map;
} BuLegSenseOfCtx;

static void
bu_visit_leg_ss(const LaplaceConsensusRow *row, void *opaque)
{
    BuLegSenseOfCtx *ctx = (BuLegSenseOfCtx *) opaque;
    const char      *cname = laplace_relation_canonical_for_type_id(&row->type);
    MemoryContext    prev;
    bool             found = false;
    BuLegSenseOf    *e;

    if (row->object_is_null || cname == NULL || strcmp(cname, "IS_SENSE_OF") != 0)
        return;
    prev = MemoryContextSwitchTo(ctx->caller);
    e = (BuLegSenseOf *) hash_search(ctx->map, &row->subject, HASH_ENTER, &found);
    if (!found || laplace_effective_mu_fp(row->rating, row->rd) > laplace_effective_mu_fp(e->rating, e->rd))
    {
        e->obj = row->object;
        e->rating = row->rating;
        e->rd = row->rd;
        e->wit = row->witnesses;
    }
    MemoryContextSwitchTo(prev);
}

static AttrNumber
bu_find_att(TupleDesc desc, const char *name)
{
    for (int i = 0; i < desc->natts; i++)
    {
        Form_pg_attribute att = TupleDescAttr(desc, i);
        if (!att->attisdropped && strcmp(NameStr(att->attname), name) == 0)
            return att->attnum;
    }
    return InvalidAttrNumber;
}

typedef struct BuLegAttCtx
{
    MemoryContext caller;
    HTAB         *subj_set;
    HTAB         *valid_langs;
    HTAB         *pair_set;
} BuLegAttCtx;

static void
bu_scan_legacy_att(Oid relid, BuLegAttCtx *ctx, HTAB *seen_ctx, hash128_t **ctxs, int *nc, int *cc)
{
    char     relkind = get_rel_relkind(relid);
    Snapshot snap = GetActiveSnapshot();

    if (relkind == RELKIND_PARTITIONED_TABLE)
    {
        List     *children = find_inheritance_children(relid, AccessShareLock);
        ListCell *lc;
        foreach(lc, children)
            bu_scan_legacy_att(lfirst_oid(lc), ctx, seen_ctx, ctxs, nc, cc);
        list_free(children);
        return;
    }

    {
        Relation   rel = table_open(relid, AccessShareLock);
        TupleDesc  desc = RelationGetDescr(rel);
        AttrNumber a_sub = bu_find_att(desc, "subject_id");
        AttrNumber a_ctx = bu_find_att(desc, "context_id");
        if (a_sub != InvalidAttrNumber && a_ctx != InvalidAttrNumber && snap != NULL)
        {
            TupleTableSlot *slot = table_slot_create(rel, NULL);
            TableScanDesc   scan = table_beginscan(rel, snap, 0, NULL);
            while (table_scan_getnextslot(scan, ForwardScanDirection, slot))
            {
                bool      n1 = false, n2 = false;
                Datum     d_sub = slot_getattr(slot, a_sub, &n1);
                Datum     d_ctx = slot_getattr(slot, a_ctx, &n2);
                bytea    *sb, *cb;
                hash128_t s, c;
                BuTermLangKey pk;
                MemoryContext prev;

                if (n1 || n2) continue;
                sb = DatumGetByteaPP(d_sub);
                cb = DatumGetByteaPP(d_ctx);
                if (VARSIZE_ANY_EXHDR(sb) != 16 || VARSIZE_ANY_EXHDR(cb) != 16)
                    continue;
                memcpy(&s, VARDATA_ANY(sb), 16);
                memcpy(&c, VARDATA_ANY(cb), 16);
                if (hash_search(ctx->subj_set, &s, HASH_FIND, NULL) == NULL)
                    continue;

                prev = MemoryContextSwitchTo(ctx->caller);
                MemSet(&pk, 0, sizeof(pk));
                pk.term = s;
                pk.lang = c;
                (void) hash_search(ctx->pair_set, &pk, HASH_ENTER, NULL);
                {
                    bool found = false;
                    (void) hash_search(seen_ctx, &c, HASH_ENTER, &found);
                    if (!found)
                    {
                        if (*nc >= *cc)
                        {
                            Size sz;
                            *cc = *cc ? *cc * 2 : 16;
                            sz = sizeof(hash128_t) * (Size) (*cc);
                            *ctxs = *ctxs ? (hash128_t *) repalloc(*ctxs, sz) : (hash128_t *) palloc(sz);
                        }
                        (*ctxs)[(*nc)++] = c;
                    }
                }
                MemoryContextSwitchTo(prev);
                ExecClearTuple(slot);
            }
            table_endscan(scan);
            ExecDropSingleTupleTableSlot(slot);
        }
        table_close(rel, AccessShareLock);
    }
}

typedef struct BuValidLangCtx
{
    MemoryContext caller;
    HTAB         *valid;
} BuValidLangCtx;

static bool
bu_visit_lang_lbl(TupleTableSlot *slot, void *opaque)
{
    BuValidLangCtx *ctx = (BuValidLangCtx *) opaque;
    bool            isnull = false;
    Datum           d_id = slot_getattr(slot, 2, &isnull);
    Datum           d_lbl;
    char           *tag;
    const char     *canon = NULL;
    MemoryContext   prev;

    if (isnull) return true;
    d_lbl = slot_getattr(slot, 3, &isnull);
    if (isnull) return true;
    tag = TextDatumGetCString(d_lbl);
    if (laplace_language_canonical(tag, &canon) == 0 && canon != NULL)
    {
        hash128_t id = laplace_pub_blake3_to_hash128(d_id);
        prev = MemoryContextSwitchTo(ctx->caller);
        (void) hash_search(ctx->valid, &id, HASH_ENTER, NULL);
        MemoryContextSwitchTo(prev);
    }
    pfree(tag);
    return true;
}

static LaplaceBubbleRow *
bu_legacy_fallback(MemoryContext caller, MemoryContext work,
                   const hash128_t *terms, int n_terms,
                   const hash128_t *domain_ctx, int n_domain, bool domain_is_null,
                   int32 k, bool k_is_null, int *out_count)
{
    ArrayType      *term_arr = hash128_array_from_ids(terms, n_terms);
    ArrayType      *hs_arr = bu_collect_has_sense_family_types();
    BuLegEdgeCtx    ectx = { work, NULL, 0, 0 };
    hash128_t      *hs_targs;
    int             n_ht = 0;
    HTAB           *ss_map = bu_hash_create(work, "bu leg ss", sizeof(hash128_t), sizeof(BuLegSenseOf), 32);
    BuResolvedRow  *resolved;
    int             n_res = 0;
    HTAB           *subj_set, *seen_ctx, *att_pairs, *valid_langs;
    hash128_t      *ctxs = NULL;
    int             nc = 0, cc = 0;
    Oid             ns = get_namespace_oid("laplace", true);
    Oid             att_rel = OidIsValid(ns) ? get_relname_relid("attestations", ns) : InvalidOid;

    laplace_consensus_candidates_stored(term_arr, NULL, hs_arr, bu_visit_leg_hs, &ectx);
    pfree(term_arr);
    if (ectx.count == 0)
    {
        pfree(hs_arr);
        return NULL;
    }

    hs_targs = (hash128_t *) palloc(sizeof(hash128_t) * (Size) ectx.count);
    for (int i = 0; i < ectx.count; i++)
    {
        if (strcmp(ectx.items[i].canon, "HAS_SENSE") == 0)
            hs_targs[n_ht++] = ectx.items[i].target;
    }
    if (n_ht > 0)
    {
        hash128_t       *uht = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_ht);
        int              n_uht = bu_dedup_sort_ids(hs_targs, n_ht, uht);
        ArrayType       *ht_arr = hash128_array_from_ids(uht, n_uht);
        BuLegSenseOfCtx  sctx = { work, ss_map };
        laplace_consensus_candidates_stored(ht_arr, NULL, hs_arr, bu_visit_leg_ss, &sctx);
        pfree(ht_arr);
    }
    pfree(hs_arr);

    resolved = (BuResolvedRow *) palloc(sizeof(BuResolvedRow) * (Size) ectx.count);
    subj_set = bu_hash_create(work, "bu ls", sizeof(hash128_t), sizeof(hash128_t), ectx.count * 2);

    for (int i = 0; i < ectx.count; i++)
    {
        const BuLegEdge *e = &ectx.items[i];
        int64            ef_e = laplace_effective_mu_fp(e->rating, e->rd);
        double           disp_e = round((double) ef_e / 1000000.0) / 1000.0;

        if (strcmp(e->canon, "HAS_SENSE") == 0)
        {
            BuLegSenseOf *ss = (BuLegSenseOf *) hash_search(ss_map, &e->target, HASH_FIND, NULL);
            double        disp_ss = ss ? (round((double) laplace_effective_mu_fp(ss->rating, ss->rd) / 1000000.0) / 1000.0) : 0.0;
            resolved[n_res].term = e->term;
            resolved[n_res].sense_id = e->target;
            resolved[n_res].synset_id = ss ? ss->obj : e->target;
            resolved[n_res].via_relation = "HAS_SENSE";
            resolved[n_res].base_eff_mu = (ss && disp_ss < disp_e) ? disp_ss : disp_e;
            resolved[n_res].witnesses = e->wit + (ss ? ss->wit : 0);
            resolved[n_res].rating = e->rating;
            resolved[n_res].rd = e->rd;
            resolved[n_res].language_null = true;
            n_res++;
        }
        else if (strcmp(e->canon, "IS_SYNONYM_OF") == 0)
        {
            resolved[n_res].term = e->term;
            resolved[n_res].sense_id = e->target;
            resolved[n_res].synset_id = e->target;
            resolved[n_res].via_relation = "IS_SYNONYM_OF";
            resolved[n_res].base_eff_mu = disp_e;
            resolved[n_res].witnesses = e->wit;
            resolved[n_res].rating = e->rating;
            resolved[n_res].rd = e->rd;
            resolved[n_res].language_null = true;
            n_res++;
        }
        else if (strcmp(e->canon, "IS_SENSE_OF") == 0)
        {
            resolved[n_res].term = e->term;
            resolved[n_res].sense_id = e->term;
            resolved[n_res].synset_id = e->target;
            resolved[n_res].via_relation = "IS_SENSE_OF";
            resolved[n_res].base_eff_mu = disp_e;
            resolved[n_res].witnesses = e->wit;
            resolved[n_res].rating = e->rating;
            resolved[n_res].rd = e->rd;
            resolved[n_res].language_null = true;
            n_res++;
        }
    }

    if (n_res == 0)
        return NULL;

    for (int i = 0; i < n_res; i++)
        (void) hash_search(subj_set, &resolved[i].synset_id, HASH_ENTER, NULL);

    seen_ctx = bu_hash_create(work, "bu sc", sizeof(hash128_t), sizeof(hash128_t), 32);
    att_pairs = bu_hash_create(work, "bu ap", sizeof(BuTermLangKey), sizeof(BuTermLangKey), n_res * 2);
    valid_langs = bu_hash_create(work, "bu vl", sizeof(hash128_t), sizeof(hash128_t), 32);

    if (OidIsValid(att_rel) && (domain_is_null || n_domain > 0))
    {
        BuLegAttCtx actx = { work, subj_set, valid_langs, att_pairs };
        bu_scan_legacy_att(att_rel, &actx, seen_ctx, &ctxs, &nc, &cc);
        if (nc > 0)
        {
            Oid           b3_arr_oid = laplace_pub_blake3_array_oid();
            ArrayType    *c_arr = laplace_pub_blake3_array(ctxs, nc);
            Datum         ca = PointerGetDatum(c_arr);
            bool          cn = false;
            BuValidLangCtx vctx = { work, valid_langs };
            (void) laplace_pub_call_srf("public", "laplace_labels", 1,
                                        &b3_arr_oid, &ca, &cn, bu_visit_lang_lbl, &vctx);
            pfree(c_arr);

            for (int i = 0; i < n_res; i++)
            {
                for (int ci = 0; ci < nc; ci++)
                {
                    BuTermLangKey pk;
                    if (hash_search(valid_langs, &ctxs[ci], HASH_FIND, NULL) == NULL)
                        continue;
                    MemSet(&pk, 0, sizeof(pk));
                    pk.term = resolved[i].synset_id;
                    pk.lang = ctxs[ci];
                    if (hash_search(att_pairs, &pk, HASH_FIND, NULL) != NULL)
                    {
                        resolved[i].language = ctxs[ci];
                        resolved[i].language_null = false;
                        break;
                    }
                }
            }
        }
    }

    return bu_finish_resolved(caller, work, resolved, n_res,
                              domain_ctx, n_domain, domain_is_null, k, k_is_null, out_count);
}

LaplaceBubbleRow *
laplace_bubble_up_batch_c(const hash128_t *terms, int n_terms,
                          const hash128_t *domain_ctx, int n_domain, bool domain_is_null,
                          int32 k, bool k_is_null, int *out_count)
{
    MemoryContext       caller = CurrentMemoryContext;
    MemoryContext       work, prev;
    hash128_t          *uniq_terms;
    int                 n_ut;
    ConverseBindingRow *brows;
    int                 n_be = 0;
    LaplaceBubbleRow   *res = NULL;

    *out_count = 0;
    if (terms == NULL || n_terms <= 0 || (!k_is_null && k <= 0))
        return NULL;

    work = AllocSetContextCreate(caller, "laplace_bubble_up_batch_c", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    uniq_terms = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_terms);
    n_ut = bu_dedup_sort_ids(terms, n_terms, uniq_terms);
    if (n_ut > 0)
    {
        brows = converse_binding_evidence_c(uniq_terms, n_ut, NULL, false, &n_be);
        if (brows != NULL && n_be > 0)
        {
            BuResolvedRow *resolved = (BuResolvedRow *) palloc(sizeof(BuResolvedRow) * (Size) n_be);
            for (int i = 0; i < n_be; i++)
            {
                resolved[i].term = brows[i].term;
                resolved[i].sense_id = brows[i].binding;
                resolved[i].synset_id = brows[i].key;
                resolved[i].via_relation = "HAS_SENSE";
                resolved[i].base_eff_mu = brows[i].eff_mu;
                resolved[i].witnesses = !brows[i].witnesses_null
                    ? brows[i].witnesses : (brows[i].games > 1 ? brows[i].games : 1);
                resolved[i].rating = (int64) llround(brows[i].eff_mu * 1e9);
                resolved[i].rd = 0;
                resolved[i].language = brows[i].language;
                resolved[i].language_null = brows[i].language_null;
            }
            pfree(brows);
            res = bu_finish_resolved(caller, work, resolved, n_be,
                                     domain_ctx, n_domain, domain_is_null,
                                     k, k_is_null, out_count);
        }
        else if (converse_legacy_consensus_has_rows())
        {
            res = bu_legacy_fallback(caller, work, uniq_terms, n_ut,
                                     domain_ctx, n_domain, domain_is_null,
                                     k, k_is_null, out_count);
        }
    }

    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return res;
}

Datum
pg_laplace_bubble_up_batch_impl(PG_FUNCTION_ARGS)
{
    ReturnSetInfo    *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    hash128_t        *terms, *dom = NULL;
    int               n_terms = 0, n_dom = 0;
    bool              dom_null = PG_ARGISNULL(1);
    bool              k_null = PG_ARGISNULL(2);
    int32             k = k_null ? 0 : PG_GETARG_INT32(2);
    LaplaceBubbleRow *rows;
    int               n_rows = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    terms = bu_deconstruct_bytea_ids(PG_GETARG_ARRAYTYPE_P(0), &n_terms);
    if (n_terms == 0)
    {
        if (terms) pfree(terms);
        return (Datum) 0;
    }
    if (!dom_null)
        dom = bu_deconstruct_bytea_ids(PG_GETARG_ARRAYTYPE_P(1), &n_dom);

    rows = laplace_bubble_up_batch_c(terms, n_terms, dom, n_dom, dom_null, k, k_null, &n_rows);
    pfree(terms);
    if (dom) pfree(dom);

    if (rows != NULL)
    {
        for (int i = 0; i < n_rows; i++)
        {
            Datum vals[8] = {
                hash128_to_datum(&rows[i].term),
                hash128_to_datum(&rows[i].sense_id),
                hash128_to_datum(&rows[i].synset_id),
                CStringGetTextDatum(rows[i].via_relation),
                DirectFunctionCall1(float8_numeric, Float8GetDatum(rows[i].score)),
                DirectFunctionCall1(float8_numeric, Float8GetDatum(rows[i].base_eff_mu)),
                Int64GetDatum(rows[i].domain_hits),
                Int64GetDatum(rows[i].witnesses)
            };
            bool nulls[8] = { false, false, false, false, false, false, false, false };

            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
            pfree(DatumGetPointer(vals[0]));
            pfree(DatumGetPointer(vals[1]));
            pfree(DatumGetPointer(vals[2]));
            pfree(DatumGetPointer(vals[3]));
            pfree(DatumGetPointer(vals[4]));
            pfree(DatumGetPointer(vals[5]));
        }
        pfree(rows);
    }
    return (Datum) 0;
}
