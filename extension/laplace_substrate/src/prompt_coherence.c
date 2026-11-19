/*
 * Joint sense election for one observation. The candidate senses of every
 * resolved token are scored against each other through the consensus cells
 * they share:
 *
 *   coherence   rank-weighted effective mu of cells joining a candidate to a
 *               candidate of a different token, read in both directions;
 *   total_mass  the same weight over every cell incident to the candidate,
 *               the denominator that makes coherence a scale-free share;
 *   rel_mass    weight of the candidate's cells in a relation type that a
 *               different token names.
 *
 * A token names a relation type when its content id is the id of the longest
 * segment of the type's canonical name, of a fixed alias of that segment, or
 * of a lemma it is IS_LEMMA_OF-linked to. Each token emits its one elected
 * sense with the keys the election used. SQL fetches sets in index range
 * reads; C folds them with O(1) hash probes. No floors, caps or top-k; ties
 * end on the sense id.
 */

#include "prompt_coherence_internal.h"

PG_FUNCTION_INFO_V1(pg_laplace_prompt_coherence);

typedef struct PcPromptTok
{
    int32     ord;
    hash128_t id;
} PcPromptTok;

typedef struct PcSegLoadCtx
{
    MemoryContext caller;
    PcPromptTok  *toks;
    int           n_toks;
    int           cap_toks;
    HTAB         *uniq_set;
    hash128_t    *uniq_ids;
    int           n_uniq;
    int           cap_uniq;
    Datum         ws_prop;
    Datum         ws_val;
} PcSegLoadCtx;

static bool
pc_receive_prompt_seg(TupleTableSlot *slot, void *opaque)
{
    PcSegLoadCtx *ctx = (PcSegLoadCtx *) opaque;
    bool          isnull = false;
    Datum         d_ord, d_seg, d_id;
    Oid           types[3] = { TEXTOID, TEXTOID, TEXTOID };
    Datum         args[3];
    bool          argnulls[3] = { false, false, false };
    bool          ws_null = false;
    Datum         ws_res;
    int32         ord;
    hash128_t     id;
    bool          found = false;
    MemoryContext prev;

    d_ord = slot_getattr(slot, 1, &isnull);
    if (isnull)
        return true;
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

    ord = (int32) DatumGetInt64(d_ord);
    id = laplace_pub_blake3_to_hash128(d_id);

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->n_toks >= ctx->cap_toks)
    {
        Size sz;
        ctx->cap_toks = ctx->cap_toks ? ctx->cap_toks * 2 : 32;
        sz = sizeof(PcPromptTok) * (Size) ctx->cap_toks;
        ctx->toks = ctx->toks ? (PcPromptTok *) repalloc(ctx->toks, sz)
                              : (PcPromptTok *) palloc(sz);
    }
    ctx->toks[ctx->n_toks].ord = ord;
    ctx->toks[ctx->n_toks].id = id;
    ctx->n_toks++;

    (void) hash_search(ctx->uniq_set, &id, HASH_ENTER, &found);
    if (!found)
    {
        if (ctx->n_uniq >= ctx->cap_uniq)
        {
            Size sz;
            ctx->cap_uniq = ctx->cap_uniq ? ctx->cap_uniq * 2 : 32;
            sz = sizeof(hash128_t) * (Size) ctx->cap_uniq;
            ctx->uniq_ids = ctx->uniq_ids ? (hash128_t *) repalloc(ctx->uniq_ids, sz)
                                          : (hash128_t *) palloc(sz);
        }
        ctx->uniq_ids[ctx->n_uniq++] = id;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct PcLangMassEntry
{
    hash128_t key;
    double    mass;
} PcLangMassEntry;

typedef struct PcBestLangEntry
{
    hash128_t id;
    hash128_t lang;
    double    tally_mass;
    double    cand_mu;
} PcBestLangEntry;

static void
pc_tally_cons_lang_cb(const LaplaceConsensusRow *row, void *opaque)
{
    HTAB            *lang_h = (HTAB *) opaque;
    bool             found = false;
    PcLangMassEntry *le;
    if (row->object_is_null)
        return;
    le = (PcLangMassEntry *) hash_search(lang_h, &row->object, HASH_ENTER, &found);
    if (!found)
        le->mass = 0.0;
    le->mass += (double) laplace_effective_mu_fp(row->rating, row->rd);
}

static int
pc_tok_ord_cmp(const void *a, const void *b)
{
    const PcPromptTok *x = (const PcPromptTok *) a;
    const PcPromptTok *y = (const PcPromptTok *) b;
    if (x->ord != y->ord)
        return x->ord < y->ord ? -1 : 1;
    return 0;
}

static void
pc_load_candidates_c(text *prompt, PcCandidateRead *read)
{
    MemoryContext        caller = CurrentMemoryContext;
    MemoryContext        tmp, old;
    HASHCTL              ctl;
    PcSegLoadCtx         sctx;
    Oid                  text_oid = TEXTOID;
    Datum                seg_arg[1];
    bool                 seg_null[1] = { false };
    hash128_t            has_lang_id;
    ArrayType           *tok_arr, *has_lang_arr;
    HTAB                *lang_tally, *agreed_h, *fallback_h, *sense_lang_h;
    ConverseBindingRow  *brows;
    int                  n_brows = 0;
    ConverseWordLangRow *wl_rows;
    int                  n_wl = 0;
    LaplaceBubbleRow    *bub_rows;
    int                  n_bub = 0;

    tmp = AllocSetContextCreate(caller, "pc_load_candidates", ALLOCSET_DEFAULT_SIZES);
    old = MemoryContextSwitchTo(tmp);

    MemSet(&sctx, 0, sizeof(sctx));
    sctx.caller = tmp;
    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(hash128_t);
    ctl.hcxt = tmp;
    sctx.uniq_set = hash_create("pc load uniq", 64, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    sctx.ws_prop = CStringGetTextDatum("White_Space");
    sctx.ws_val = CStringGetTextDatum("Yes");

    seg_arg[0] = PointerGetDatum(prompt);
    (void) laplace_pub_call_srf("public", "laplace_segments", 1, &text_oid,
                                seg_arg, seg_null, pc_receive_prompt_seg, &sctx);

    if (sctx.n_toks == 0 || sctx.n_uniq == 0)
    {
        MemoryContextSwitchTo(old);
        MemoryContextDelete(tmp);
        return;
    }
    if (sctx.n_toks > 1)
        qsort(sctx.toks, (Size) sctx.n_toks, sizeof(PcPromptTok), pc_tok_ord_cmp);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(PcLangMassEntry);
    ctl.hcxt = tmp;
    lang_tally = hash_create("pc load ltally", 32, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    if (laplace_relation_type_id("HAS_LANGUAGE", &has_lang_id) != 0)
        elog(ERROR, "prompt_coherence: HAS_LANGUAGE type unavailable");

    tok_arr = hash128_array_from_ids(sctx.uniq_ids, sctx.n_uniq);
    has_lang_arr = hash128_array_from_ids(&has_lang_id, 1);
    laplace_consensus_candidates_stored(tok_arr, NULL, has_lang_arr,
                                        pc_tally_cons_lang_cb, lang_tally);
    pfree(tok_arr);
    pfree(has_lang_arr);

    brows = converse_binding_evidence_c(sctx.uniq_ids, sctx.n_uniq, NULL, false, &n_brows);
    for (int i = 0; i < n_brows; i++)
    {
        bool             f = false;
        PcLangMassEntry *le;
        if (brows[i].language_null)
            continue;
        le = (PcLangMassEntry *) hash_search(lang_tally, &brows[i].language, HASH_ENTER, &f);
        if (!f)
            le->mass = 0.0;
        le->mass += (double) laplace_effective_mu_fp((int64) llround(brows[i].eff_mu * 1e9), 0);
    }

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(PcBestLangEntry);
    ctl.hcxt = tmp;
    agreed_h = hash_create("pc load ag", sctx.n_uniq, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    fallback_h = hash_create("pc load fb", sctx.n_uniq, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    wl_rows = converse_word_language_candidates_batch_c(sctx.uniq_ids, sctx.n_uniq, &n_wl);
    for (int i = 0; i < n_wl; i++)
    {
        const ConverseWordLangRow *w = &wl_rows[i];
        bool                       f = false;
        PcBestLangEntry           *fb = (PcBestLangEntry *) hash_search(fallback_h, &w->word, HASH_ENTER, &f);
        PcLangMassEntry           *tm = (PcLangMassEntry *) hash_search(lang_tally, &w->lang, HASH_FIND, NULL);

        if (!f || w->mu > fb->cand_mu ||
            (w->mu == fb->cand_mu && memcmp(&w->lang, &fb->lang, sizeof(hash128_t)) < 0))
        {
            fb->lang = w->lang;
            fb->cand_mu = w->mu;
        }
        if (tm != NULL)
        {
            bool             af = false;
            PcBestLangEntry *ag = (PcBestLangEntry *) hash_search(agreed_h, &w->word, HASH_ENTER, &af);
            if (!af ||
                tm->mass > ag->tally_mass ||
                (tm->mass == ag->tally_mass &&
                 (w->mu > ag->cand_mu ||
                  (w->mu == ag->cand_mu && memcmp(&w->lang, &ag->lang, sizeof(hash128_t)) < 0))))
            {
                ag->lang = w->lang;
                ag->tally_mass = tm->mass;
                ag->cand_mu = w->mu;
            }
        }
    }

    bub_rows = laplace_bubble_up_batch_c(sctx.uniq_ids, sctx.n_uniq,
                                         NULL, 0, false, 0, true, &n_bub);
    if (bub_rows != NULL && n_bub > 0)
    {
        HTAB                *seen_senses = hash_create("pc load ss", n_bub, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        hash128_t           *sids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_bub);
        int                  ns = 0;
        ConverseWordLangRow *sl_rows;
        int                  n_sl = 0;

        for (int i = 0; i < n_bub; i++)
        {
            bool f = false;
            (void) hash_search(seen_senses, &bub_rows[i].sense_id, HASH_ENTER, &f);
            if (!f)
                sids[ns++] = bub_rows[i].sense_id;
        }

        sense_lang_h = hash_create("pc load sl", Max(ns, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        sl_rows = converse_word_language_candidates_batch_c(sids, ns, &n_sl);
        for (int i = 0; i < n_sl; i++)
        {
            const ConverseWordLangRow *w = &sl_rows[i];
            bool                       f = false;
            PcBestLangEntry           *se = (PcBestLangEntry *) hash_search(sense_lang_h, &w->word, HASH_ENTER, &f);
            if (!f || w->mu > se->cand_mu ||
                (w->mu == se->cand_mu && memcmp(&w->lang, &se->lang, sizeof(hash128_t)) < 0))
            {
                se->lang = w->lang;
                se->cand_mu = w->mu;
            }
        }

        for (int ti = 0; ti < sctx.n_toks; ti++)
        {
            const PcPromptTok *pt = &sctx.toks[ti];
            PcBestLangEntry   *tl = (PcBestLangEntry *) hash_search(agreed_h, &pt->id, HASH_FIND, NULL);
            if (tl == NULL)
                tl = (PcBestLangEntry *) hash_search(fallback_h, &pt->id, HASH_FIND, NULL);

            for (int bi = 0; bi < n_bub; bi++)
            {
                const LaplaceBubbleRow *b = &bub_rows[bi];
                PcBestLangEntry        *sl;
                int32                   lang_agree = 0;

                if (!hash128_eq(&b->term, &pt->id))
                    continue;
                sl = (PcBestLangEntry *) hash_search(sense_lang_h, &b->sense_id, HASH_FIND, NULL);
                if (tl != NULL && sl != NULL)
                    lang_agree = hash128_eq(&tl->lang, &sl->lang) ? 1 : -1;
                pc_add_candidate_c(read, pt->ord, &pt->id, &b->synset_id,
                                   b->base_eff_mu, b->witnesses, lang_agree);
            }
        }
    }

    MemoryContextSwitchTo(old);
    MemoryContextDelete(tmp);
}

typedef struct PcSingleMassCtx
{
    HTAB   *syn_h;
    PcCand *cands;
} PcSingleMassCtx;

static void
pc_single_mass_add(PcSingleMassCtx *ctx, const hash128_t *id,
                   const hash128_t *tid, int64 rating, int64 rd)
{
    PcSynEntry *me = (PcSynEntry *) hash_search(ctx->syn_h, id, HASH_FIND, NULL);
    double      rk = 0.0;
    double      mass;

    if (me == NULL)
        return;
    if (!laplace_relation_rank_resolve_c(*tid, &rk) || rk == 0.0)
        return;
    mass = rk * (double) ((__int128) rating - 2 * (__int128) rd);
    for (int i = 0; i < me->n_idx; i++)
        ctx->cands[me->idx[i]].total_mass += mass;
}

static void
pc_single_mass_fwd_cb(const LaplaceConsensusRow *crow, void *vctx)
{
    pc_single_mass_add((PcSingleMassCtx *) vctx, &crow->subject,
                       &crow->type, crow->rating, crow->rd);
}

static void
pc_single_mass_rev_cb(const LaplaceConsensusRow *crow, void *vctx)
{
    if (crow->object_is_null)
        return;
    pc_single_mass_add((PcSingleMassCtx *) vctx, &crow->object,
                       &crow->type, crow->rating, crow->rd);
}

typedef struct PcLemmaCtx
{
    HTAB          *tok_h;
    HTAB          *namer_h;
    MemoryContext  work;
    Datum         *nw_datums;
    PcTypeEntry  **nw_owner;
    int            n_nw;
} PcLemmaCtx;

static void
pc_lemma_row_cb(const LaplaceConsensusRow *crow, void *vctx)
{
    PcLemmaCtx *ctx = (PcLemmaCtx *) vctx;
    PcTokEntry *tk;

    if (crow->object_is_null)
        return;
    tk = (PcTokEntry *) hash_search(ctx->tok_h, &crow->object, HASH_FIND, NULL);
    if (tk == NULL)
        return;
    for (int i = 0; i < ctx->n_nw; i++)
    {
        bytea *nb = DatumGetByteaPP(ctx->nw_datums[i]);
        if (memcmp(VARDATA_ANY(nb), &crow->subject, 16) == 0)
            pc_type_add_namers(ctx->nw_owner[i], tk, ctx->namer_h, ctx->work);
    }
}

static void
pc_rel_fwd_cb(const LaplaceConsensusRow *crow, void *vctx)
{
    pc_credit_relation_mass_c((PcEdgeRead *) vctx, &crow->subject,
                              &crow->type, crow->rating, crow->rd);
}

static void
pc_rel_rev_cb(const LaplaceConsensusRow *crow, void *vctx)
{
    if (crow->object_is_null || memcmp(&crow->subject, &crow->object, 16) == 0)
        return;
    pc_credit_relation_mass_c((PcEdgeRead *) vctx, &crow->object,
                              &crow->type, crow->rating, crow->rd);
}

Datum
pg_laplace_prompt_coherence(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    text          *prompt;
    MemoryContext  work, old;
    HASHCTL        ctl;
    HTAB          *syn_h, *tok_h, *type_h, *peer_h, *namer_h;
    PcCand        *cands = NULL;
    int            n_cand = 0, cap_cand = 0;
    Datum         *syn_datums = NULL;
    int            n_syn = 0;
    ArrayType     *syn_arr;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    prompt = PG_GETARG_TEXT_PP(0);

    InitMaterializedSRF(fcinfo, 0);

    work = AllocSetContextCreate(CurrentMemoryContext, "prompt_coherence",
                                 ALLOCSET_DEFAULT_SIZES);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(PcSynEntry);
    ctl.hcxt = work;
    syn_h = hash_create("pc syn", 512, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(PcTokEntry);
    ctl.hcxt = work;
    tok_h = hash_create("pc tok", 64, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(PcTypeEntry);
    ctl.hcxt = work;
    type_h = hash_create("pc type", 256, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(PcPeerKey);
    ctl.entrysize = sizeof(PcPeerEntry);
    ctl.hcxt = work;
    peer_h = hash_create("pc peers", 512, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(int32);
    ctl.entrysize = sizeof(PcOrdEntry);
    ctl.hcxt = work;
    namer_h = hash_create("pc namers", 64, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    /* One complete candidate set; the row arena does not own retained candidates. */
    {
        PcCandidateRead read = {syn_h, tok_h, cands, n_cand, cap_cand, work};
        pc_load_candidates_c(prompt, &read);
        cands = read.cands; n_cand = read.count; cap_cand = read.capacity;
    }

    if (n_cand == 0)
    {
        MemoryContextDelete(work);
        return (Datum) 0;
    }

    {
        HASH_SEQ_STATUS seq;
        PcSynEntry     *se;
        long            count = hash_get_num_entries(syn_h);

        if (count <= 0 || (uint64) count > (uint64) INT_MAX ||
            (Size) count > MaxAllocSize / sizeof(Datum))
            ereport(ERROR,
                    (errmsg("prompt_coherence: unique candidate set exceeds PostgreSQL array capacity")));
        old = MemoryContextSwitchTo(work);
        syn_datums = (Datum *) palloc(sizeof(Datum) * count);
        hash_seq_init(&seq, syn_h);
        while ((se = (PcSynEntry *) hash_seq_search(&seq)) != NULL)
        {
            bytea *cp = (bytea *) palloc(VARHDRSZ + 16);

            SET_VARSIZE(cp, VARHDRSZ + 16);
            memcpy(VARDATA(cp), se->key, 16);
            syn_datums[n_syn++] = PointerGetDatum(cp);
        }
        syn_arr = construct_array(syn_datums, n_syn, BYTEAOID, -1, false, TYPALIGN_INT);
        MemoryContextSwitchTo(old);
    }

    /* ---- single-token fast path: coherence and relation naming are zero when
     * only one token resolved; compute total_mass via set aggregation in SQL
     * instead of streaming every incident edge into C. ---- */
    {
        bool  multi_tok = false;
        int32 first_ord = cands[0].ord;

        for (int i = 1; i < n_cand; i++)
        {
            if (cands[i].ord != first_ord)
            {
                multi_tok = true;
                break;
            }
        }

        if (!multi_tok)
        {
            PcSingleMassCtx    sctx = { syn_h, cands };
            LaplaceEdgeRawRow *raw_rows;
            int                n_raw = 0;

            laplace_consensus_candidates_stored(syn_arr, NULL, NULL,
                                                pc_single_mass_fwd_cb, &sctx);
            laplace_consensus_candidates_stored(NULL, syn_arr, NULL,
                                                pc_single_mass_rev_cb, &sctx);

            raw_rows = laplace_edges_raw_batch_c(syn_arr, "both", NULL,
                                                 true, 128, true, "edge_rank", &n_raw);
            for (int ri = 0; ri < n_raw; ri++)
            {
                const LaplaceEdgeRawRow *er = &raw_rows[ri];
                if (er->type_null)
                    continue;
                pc_single_mass_add(&sctx, &er->subject_id, &er->type_id,
                                   er->rating, er->rd);
            }
            if (raw_rows != NULL)
                pfree(raw_rows);
            goto emit_winners;
        }
    }

    /* ---- coherence: two indexed range reads, O(1) probe per edge ---- */
    pc_scan_edges(syn_h, type_h, peer_h, cands, syn_arr, true);
    pc_scan_edges(syn_h, type_h, peer_h, cands, syn_arr, false);

    /* ---- which relation types does a token name? A name's concept segment
     * becomes a content id through the substrate's content hash; a token names
     * the type by id equality, or as the object of an IS_LEMMA_OF cell whose
     * subject is that id. ---- */
    {
        PcTypeEntry     *te;
        Datum           *nw_datums = NULL;
        int              n_nw = 0;
        PcTypeEntry    **nw_owner = NULL;
        size_t           nw_capacity = laplace_relation_table_count;

        if (nw_capacity > (size_t) INT_MAX ||
            nw_capacity > (size_t) (MaxAllocSize / sizeof(Datum)) ||
            nw_capacity > (size_t) (MaxAllocSize / sizeof(PcTypeEntry *)))
            ereport(ERROR,
                    (errmsg("prompt_coherence: relation manifest exceeds PostgreSQL array capacity")));
        old = MemoryContextSwitchTo(work);
        nw_datums = (Datum *) palloc(sizeof(Datum) * Max(nw_capacity, (size_t) 1));
        nw_owner = (PcTypeEntry **) palloc(sizeof(PcTypeEntry *) * Max(nw_capacity, (size_t) 1));
        MemoryContextSwitchTo(old);

        /* Iterate the whole manifest, not only the types the candidates carry:
         * naming selects which relation to traverse, so it cannot depend on that
         * relation already appearing among the candidates' cells. The manifest
         * is bounded, so this is a fixed cost per call. Each manifest type is
         * entered into type_h for the rel_mass pass. */
        for (size_t ri = 0; ri < laplace_relation_table_count; ri++)
        {
            const laplace_relation_def_t *def = &laplace_relation_table[ri];
            const char *name, *last;
            size_t      len;
            hash128_t   wid;
            hash128_t   type_id;
            PcTokEntry *tk;
            bool        found;

            name = def->canonical;
            if (name == NULL)
                continue;
            tk = NULL;

            /* Canonical ids come from the native law cache; the static table's
             * type_id field is not populated. */
            if (laplace_relation_type_id(name, &type_id) < 0)
                continue;
            te = (PcTypeEntry *) hash_search(type_h, &type_id, HASH_ENTER, &found);
            if (!found)
            {
                te->namer_ords = NULL;
                te->n_namers = 0;
                te->cap_namers = 0;
                te->named = false;
            }

            /* A canonical name's concept is its longest underscore-delimited
             * segment; ties keep the earlier segment. */
            {
                const char *seg = name;
                const char *best_seg = NULL;
                size_t      best_len = 0;

                for (;;)
                {
                    const char *us = strchr(seg, '_');
                    size_t      slen = us ? (size_t) (us - seg) : strlen(seg);

                    if (slen > best_len)
                    {
                        best_seg = seg;
                        best_len = slen;
                    }
                    if (us == NULL)
                        break;
                    seg = us + 1;
                }
                if (best_seg == NULL || best_len < 2)
                    continue;           /* under two characters names nothing */
                last = best_seg;
                len = best_len;
            }

            {
                char *lower = pnstrdup(last, len);

                for (size_t i = 0; i < len; i++)
                    lower[i] = pg_ascii_tolower(lower[i]);
                if (laplace_content_root_id((const uint8_t *) lower, len, &wid) != 0)
                {
                    pfree(lower);
                    continue;
                }
                /* With no token carrying the segment's id, a fixed alias of the
                 * segment is tried instead. */
                {
                    static const struct { const char *from; const char *to; } aliases[] = {
                        {"antonym", "opposite"},
                        {"definition", "define"},
                        {NULL, NULL}
                    };
                    hash128_t alias_wid;
                    int       ai;

                    tk = (PcTokEntry *) hash_search(tok_h, &wid, HASH_FIND, NULL);
                    if (tk == NULL)
                    {
                        for (ai = 0; aliases[ai].from != NULL; ai++)
                        {
                            if (strcmp(lower, aliases[ai].from) != 0)
                                continue;
                            if (laplace_content_root_id(
                                    (const uint8_t *) aliases[ai].to,
                                    strlen(aliases[ai].to), &alias_wid) == 0)
                                tk = (PcTokEntry *) hash_search(
                                    tok_h, &alias_wid, HASH_FIND, NULL);
                            break;
                        }
                    }
                }
                pfree(lower);
            }

            if (tk != NULL)
            {
                pc_type_add_namers(te, tk, namer_h, work);
                continue;
            }
            /* No direct hit: queue the name id for the lemma probe. */
            {
                bytea *b;

                if ((size_t) n_nw >= nw_capacity)
                    ereport(ERROR,
                            (errmsg("prompt_coherence: relation-name cardinality exceeded its exact manifest bound")));
                old = MemoryContextSwitchTo(work);
                b = (bytea *) palloc(VARHDRSZ + 16);
                SET_VARSIZE(b, VARHDRSZ + 16);
                memcpy(VARDATA(b), &wid, 16);
                MemoryContextSwitchTo(old);
                nw_datums[n_nw] = PointerGetDatum(b);
                nw_owner[n_nw] = te;
                n_nw++;
            }
        }

        if (n_nw > 0)
        {
            hash128_t  is_lemma_tid;
            ArrayType *nw_arr;

            old = MemoryContextSwitchTo(work);
            nw_arr = construct_array(nw_datums, n_nw, BYTEAOID, -1, false, TYPALIGN_INT);
            MemoryContextSwitchTo(old);

            if (laplace_relation_type_id("IS_LEMMA_OF", &is_lemma_tid) == 0)
            {
                ArrayType *lemma_type_arr = hash128_array_from_ids(&is_lemma_tid, 1);
                PcLemmaCtx lctx = { tok_h, namer_h, work, nw_datums, nw_owner, n_nw };

                laplace_consensus_candidates_stored(nw_arr, NULL, lemma_type_arr,
                                                    pc_lemma_row_cb, &lctx);
                pfree(lemma_type_arr);
            }
        }
    }

    /* ---- rel_mass: one more indexed read, restricted to the named types. The
     * (subject_id, type_id) index serves it directly. A token never scores
     * itself: the namer must be a DIFFERENT ord. ---- */
    {
        HASH_SEQ_STATUS seq;
        PcTypeEntry    *te;
        Datum          *td_arr = NULL;
        int             n_td = 0;
        long            type_count = hash_get_num_entries(type_h);

        if (type_count < 0 || (uint64) type_count > (uint64) INT_MAX ||
            (Size) type_count > MaxAllocSize / sizeof(Datum))
            ereport(ERROR,
                    (errmsg("prompt_coherence: relation type set exceeds PostgreSQL array capacity")));
        old = MemoryContextSwitchTo(work);
        td_arr = (Datum *) palloc(sizeof(Datum) * Max(type_count, 1));
        MemoryContextSwitchTo(old);

        hash_seq_init(&seq, type_h);
        while ((te = (PcTypeEntry *) hash_seq_search(&seq)) != NULL)
        {
            if (!te->named)
                continue;
            old = MemoryContextSwitchTo(work);
            {
                bytea *b = (bytea *) palloc(VARHDRSZ + 16);

                SET_VARSIZE(b, VARHDRSZ + 16);
                memcpy(VARDATA(b), te->key, 16);
                td_arr[n_td++] = PointerGetDatum(b);
            }
            MemoryContextSwitchTo(old);
        }

        if (n_td > 0)
        {
            ArrayType         *type_arr;
            ArrayType         *sym_types;
            PcEdgeRead         read = {syn_h, type_h, NULL, cands};
            LaplaceEdgeRawRow *raw_out, *raw_in;
            int                n_out = 0, n_in = 0;

            old = MemoryContextSwitchTo(work);
            type_arr = construct_array(td_arr, n_td, BYTEAOID, -1, false, TYPALIGN_INT);
            MemoryContextSwitchTo(old);

            sym_types = laplace_symmetric_relation_types();

            laplace_consensus_candidates_stored(syn_arr, NULL, type_arr,
                                                pc_rel_fwd_cb, &read);
            laplace_consensus_candidates_stored(NULL, syn_arr, sym_types,
                                                pc_rel_rev_cb, &read);

            raw_out = laplace_edges_raw_batch_c(syn_arr, "out", type_arr,
                                                true, 64, true, "edge_rank", &n_out);
            for (int ri = 0; ri < n_out; ri++)
            {
                const LaplaceEdgeRawRow *er = &raw_out[ri];
                if (er->type_null)
                    continue;
                pc_credit_relation_mass_c(&read, &er->subject_id, &er->type_id,
                                          er->rating, er->rd);
            }
            if (raw_out != NULL)
                pfree(raw_out);

            raw_in = laplace_edges_raw_batch_c(syn_arr, "in", sym_types,
                                               true, 64, true, "edge_rank", &n_in);
            for (int ri = 0; ri < n_in; ri++)
            {
                const LaplaceEdgeRawRow *er = &raw_in[ri];
                if (er->type_null || er->neighbour_null ||
                    memcmp(&er->neighbour_id, &er->subject_id, 16) == 0)
                    continue;
                pc_credit_relation_mass_c(&read, &er->subject_id, &er->type_id,
                                          er->rating, er->rd);
            }
            if (raw_in != NULL)
                pfree(raw_in);
        }
    }

emit_winners:
    /* ---- emit one row per token: its elected sense ---- */
    {
        for (int i = 0; i < n_cand; i++)
        {
            PcCand *best = &cands[i];
            bool    is_best = true;

            for (int j = 0; j < n_cand; j++)
            {
                PcCand *o = &cands[j];

                if (j == i || o->ord != best->ord)
                    continue;
                /* Election order: coherence share, raw rel_mass, language
                 * agreement, witness count, denote_mu, total_mass, lower sense
                 * id. The share, not raw coherence, so a high-degree candidate
                 * does not win on edge count alone. Joint evidence from this
                 * observation precedes language agreement, which precedes every
                 * global quantity; an unattested language beats a disagreeing
                 * one and loses to an agreeing one. */
                double o_share    = o->total_mass    > 0.0 ? o->coherence    / o->total_mass    : 0.0;
                double best_share = best->total_mass > 0.0 ? best->coherence / best->total_mass : 0.0;
                if (o->coherence == best->coherence)
                    o_share = best_share;

                if (o_share > best_share
                    || (o_share == best_share && o->rel_mass > best->rel_mass)
                    || (o_share == best_share && o->rel_mass == best->rel_mass
                        && o->lang_agree > best->lang_agree)
                    || (o_share == best_share && o->rel_mass == best->rel_mass
                        && o->lang_agree == best->lang_agree
                        && o->witnesses > best->witnesses)
                    || (o_share == best_share && o->rel_mass == best->rel_mass
                        && o->lang_agree == best->lang_agree
                        && o->witnesses == best->witnesses
                        && o->denote_mu > best->denote_mu)
                    || (o_share == best_share && o->rel_mass == best->rel_mass
                        && o->lang_agree == best->lang_agree
                        && o->witnesses == best->witnesses
                        && o->denote_mu == best->denote_mu
                        && o->total_mass > best->total_mass)
                    || (o_share == best_share && o->rel_mass == best->rel_mass
                        && o->lang_agree == best->lang_agree
                        && o->witnesses == best->witnesses
                        && o->denote_mu == best->denote_mu
                        && o->total_mass == best->total_mass
                        && memcmp(o->syn, best->syn, 16) < 0))
                {
                    is_best = false;
                    break;
                }
            }
            if (!is_best)
                continue;

            {
                Datum values[12];
                bool  nulls[12];
                bytea *tokb = (bytea *) palloc(VARHDRSZ + 16);
                bytea *synb = (bytea *) palloc(VARHDRSZ + 16);

                SET_VARSIZE(tokb, VARHDRSZ + 16);
                memcpy(VARDATA(tokb), best->tok, 16);
                SET_VARSIZE(synb, VARHDRSZ + 16);
                memcpy(VARDATA(synb), best->syn, 16);

                MemSet(nulls, 0, sizeof(nulls));
                values[0] = Int32GetDatum(best->ord);
                values[1] = PointerGetDatum(tokb);
                values[2] = PointerGetDatum(synb);
                values[3] = Float8GetDatum(best->coherence);
                values[4] = DirectFunctionCall1(float8_numeric,
                                                Float8GetDatum(best->denote_mu));
                values[5] = Int64GetDatum(best->peer_count);
                if (best->has_rel_type)
                {
                    bytea *rt = (bytea *) palloc(VARHDRSZ + 16);

                    SET_VARSIZE(rt, VARHDRSZ + 16);
                    memcpy(VARDATA(rt), best->rel_type, 16);
                    values[6] = PointerGetDatum(rt);
                }
                else
                    nulls[6] = true;
                /* rel_mass as a share of the candidate's total mass, scale-free
                 * like specificity; raw mass is share * total_mass. */
                values[7] = Float8GetDatum(
                    best->total_mass > 0.0 ? best->rel_mass / best->total_mass : 0.0);
                /* Specificity: the share of the candidate's own mass that reaches
                 * the rest of the observation, or -1 when its token names a
                 * relation type. A namer is the operator of the observation, not
                 * its subject: it stays orderable but sorts below every
                 * non-namer, including one with no signal. */
                {
                    double share = best->total_mass > 0.0
                                   ? best->coherence / best->total_mass
                                   : 0.0;
                    bool   is_namer = hash_search(
                        namer_h, &best->ord, HASH_FIND, NULL) != NULL;

                    values[8] = Float8GetDatum(
                        is_namer ? -1.0 : share);
                }
                /* The denominator, exposed raw. */
                values[9] = Float8GetDatum(best->total_mass);
                /* Witness count, a key of the election. */
                values[10] = Int64GetDatum(best->witnesses);
                /* +1 agrees / 0 unattested / -1 disagrees. 0 on every token means
                 * no language evidence, not agreement. */
                values[11] = Int32GetDatum(best->lang_agree);

                tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
            }
        }
    }

    MemoryContextDelete(work);
    return (Datum) 0;
}
