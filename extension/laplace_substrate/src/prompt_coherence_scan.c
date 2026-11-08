#include "prompt_coherence_internal.h"

static SPIPlanPtr pc_edges_fwd_plan = NULL;
static SPIPlanPtr pc_edges_rev_plan = NULL;

void
pc_int32_add(int32 **values, int *n, int *cap, int32 value,
             MemoryContext cxt, const char *what)
{
    MemoryContext old;
    int             next;

    for (int i = 0; i < *n; i++)
        if ((*values)[i] == value)
            return;

    if (*n < *cap)
    {
        (*values)[(*n)++] = value;
        return;
    }

    if (*cap > INT_MAX / 2)
        ereport(ERROR, (errmsg("prompt_coherence: %s cardinality exceeds integer capacity", what)));
    next = *cap == 0 ? 1 : *cap * 2;
    if ((Size) next > MaxAllocSize / sizeof(int32))
        ereport(ERROR, (errmsg("prompt_coherence: %s exceeds PostgreSQL allocation capacity", what)));

    old = MemoryContextSwitchTo(cxt);
    *values = *values == NULL
        ? (int32 *) palloc(sizeof(int32) * next)
        : (int32 *) repalloc(*values, sizeof(int32) * next);
    MemoryContextSwitchTo(old);
    *cap = next;
    (*values)[(*n)++] = value;
}

void
pc_type_add_namers(PcTypeEntry *te, const PcTokEntry *tk, HTAB *namer_h,
                   MemoryContext cxt)
{
    for (int i = 0; i < tk->n_ords; i++)
    {
        bool found;

        pc_int32_add(&te->namer_ords, &te->n_namers, &te->cap_namers,
                     tk->ords[i], cxt, "relation namers");
        (void) hash_search(namer_h, &tk->ords[i], HASH_ENTER, &found);
    }
    te->named = te->n_namers > 0;
}

bool
pc_type_has_other_namer(const PcTypeEntry *te, int32 ord)
{
    return te->n_namers > 1 ||
           (te->n_namers == 1 && te->namer_ords[0] != ord);
}

void
pc_syn_add(HTAB *h, const uint8 *syn, int idx, MemoryContext cxt)
{
    bool        found;
    PcSynEntry *e = (PcSynEntry *) hash_search(h, syn, HASH_ENTER, &found);

    if (!found)
    {
        MemoryContext old = MemoryContextSwitchTo(cxt);

        e->cap_idx = 4;
        e->n_idx = 0;
        e->idx = (int *) palloc(sizeof(int) * e->cap_idx);
        MemoryContextSwitchTo(old);
    }
    for (int i = 0; i < e->n_idx; i++)
        if (e->idx[i] == idx)
            return;
    if (e->n_idx == e->cap_idx)
    {
        MemoryContext old = MemoryContextSwitchTo(cxt);

        if (e->cap_idx > INT_MAX / 2 ||
            (Size) e->cap_idx * 2 > MaxAllocSize / sizeof(int))
            ereport(ERROR,
                    (errmsg("prompt_coherence: candidate membership exceeds PostgreSQL allocation capacity")));
        e->cap_idx *= 2;
        e->idx = (int *) repalloc(e->idx, sizeof(int) * e->cap_idx);
        MemoryContextSwitchTo(old);
    }
    e->idx[e->n_idx++] = idx;
}

SPIPlanPtr
ensure_pc_plan(SPIPlanPtr *slot, const char *sql, int nargs, Oid *argtypes, const char *label)
{
    if (*slot == NULL)
    {
        SPIPlanPtr p = SPI_prepare_cursor(sql, nargs, argtypes, CURSOR_OPT_PARALLEL_OK);

        if (p == NULL)
            elog(ERROR, "prompt_coherence: SPI_prepare(%s) failed: %s",
                 label, SPI_result_code_string(SPI_result));
        if (SPI_keepplan(p) != 0)
            elog(ERROR, "prompt_coherence: SPI_keepplan(%s) failed", label);
        *slot = p;
    }
    return *slot;
}

/* One direction of the edge scan. `forward` selects which column carries the
 * candidate being credited. Each direction is a plain index range read on the
 * subject or object btree; one OR over both directions would not be. */
static void
pc_receive_edge(TupleTableSlot *row, void *opaque)
{
    PcEdgeRead *read = opaque;
    HTAB *syn_h = read->syn_h, *type_h = read->type_h, *peer_h = read->peer_h;
    PcCand *cands = read->cands;
    bool       isnull;
    bytea     *mine, *other, *tid;
    int64      rating, rd;
    double     eff, rank;
    PcSynEntry *me, *peer;
    PcTypeEntry *te;
    bool        found;
    const laplace_relation_def_t *def = NULL;

    mine = spi_slot_bytea(row, 1);
    if (!mine || VARSIZE_ANY_EXHDR(mine) != 16)
        return;
    other = spi_slot_bytea(row, 2);
    if (!other || VARSIZE_ANY_EXHDR(other) != 16)
        return;
    tid = spi_slot_bytea(row, 3);
    if (!tid || VARSIZE_ANY_EXHDR(tid) != 16)
        return;
    rating = DatumGetInt64(slot_getattr(row, 4, &isnull));
    if (isnull) return;
    rd = DatumGetInt64(slot_getattr(row, 5, &isnull));
    if (isnull) return;

    /* Every type a candidate carries enters type_h, the set the
     * rel_mass pass draws its named types from. */
    te = (PcTypeEntry *) hash_search(type_h, VARDATA_ANY(tid), HASH_ENTER, &found);
    if (!found)
    {
        te->namer_ords = NULL;
        te->n_namers = 0;
        te->cap_namers = 0;
        te->named = false;
    }

    me = (PcSynEntry *) hash_search(syn_h, VARDATA_ANY(mine), HASH_FIND, NULL);
    if (me == NULL)
        return;

    /* Effective mu (rating - 2*rd) weighted by the relation's manifest rank. */
    eff = (double) laplace_effective_mu_fp(rating, rd);
    rank = (laplace_relation_lookup((const hash128_t *) VARDATA_ANY(tid), &def) == 0
            && def != NULL && def->rank >= laplace_firmware_default()->salience_floor)
               ? def->rank : 0.0;
    if (rank <= 0.0)
        return;

    /* Total mass counts every cell incident to the candidate: outgoing
     * from the forward scan, incoming from the reverse. A cell between
     * two candidates belongs once to each endpoint, so coherence and its
     * denominator rest on the same directional support. */
    for (int i = 0; i < me->n_idx; i++)
        cands[me->idx[i]].total_mass += rank * eff;

    peer = (PcSynEntry *) hash_search(syn_h, VARDATA_ANY(other), HASH_FIND, NULL);
    if (peer == NULL)
        return;                 /* edge leaves the candidate set */

    for (int i = 0; i < me->n_idx; i++)
    {
        PcCand *c = &cands[me->idx[i]];
        bool     has_other = false;

        for (int j = 0; j < peer->n_idx; j++)
        {
            int32     peer_ord = cands[peer->idx[j]].ord;
            PcPeerKey key;
            bool      seen;

            if (peer_ord == c->ord)
                continue;
            has_other = true;
            key.cand_idx = me->idx[i];
            key.peer_ord = peer_ord;
            (void) hash_search(peer_h, &key, HASH_ENTER, &seen);
            if (!seen)
                c->peer_count++;
        }
        if (!has_other)
            continue;           /* no OTHER token attests this peer */
        c->coherence += rank * eff;
    }
}

void
pc_scan_edges(HTAB *syn_h, HTAB *type_h, HTAB *peer_h, PcCand *cands,
              ArrayType *syn_arr, bool forward)
{
    Oid types[] = {BYTEAARRAYOID};
    Datum args[] = {PointerGetDatum(syn_arr)};
    SPIPlanPtr *slot = forward ? &pc_edges_fwd_plan : &pc_edges_rev_plan;
    const char *key = forward ? "prompt.coherence_edges_forward" : "prompt.coherence_edges_reverse";
    PcEdgeRead read = {syn_h, type_h, peer_h, cands};
    laplace_spi_stream(ensure_pc_plan(slot, laplace_sql_query_text(key), 1, types, key),
        1, types, args, NULL, pc_receive_edge, &read);
}

void
pc_receive_candidate(TupleTableSlot *row, void *opaque)
{
    PcCandidateRead *read = opaque;
    HTAB *syn_h = read->syn_h, *tok_h = read->tok_h;
    PcCand *cands = read->cands;
    int n_cand = read->count, cap_cand = read->capacity;
    MemoryContext work = read->work, old;
    bool      isnull;
    int32     ord;
    bytea    *tok, *syn;
    double    mu;
    int64     wit;
    int32     lang_agree;
    PcTokEntry *tk;
    bool      found;

    ord = DatumGetInt32(slot_getattr(row, 1, &isnull));
    if (isnull) return;
    tok = spi_slot_bytea(row, 2);
    if (!tok || VARSIZE_ANY_EXHDR(tok) != 16) return;
    syn = spi_slot_bytea(row, 3);
    if (!syn || VARSIZE_ANY_EXHDR(syn) != 16) return;
    mu = DatumGetFloat8(slot_getattr(row, 4, &isnull));
    if (isnull) mu = 0.0;
    wit = DatumGetInt64(slot_getattr(row, 5, &isnull));
    if (isnull) wit = 0;
    {
        Datum  d_tl, d_sl;
        bool   tl_null, sl_null;

        d_tl = slot_getattr(row, 6, &tl_null);
        d_sl = slot_getattr(row, 7, &sl_null);
        /* Either side unattested leaves this 0. Only two attested
         * languages can agree or disagree. */
        if (tl_null || sl_null)
            lang_agree = 0;
        else
        {
            bytea *tl = DatumGetByteaPP(d_tl);
            bytea *sl = DatumGetByteaPP(d_sl);

            if (VARSIZE_ANY_EXHDR(tl) != 16 || VARSIZE_ANY_EXHDR(sl) != 16)
                lang_agree = 0;
            else
                lang_agree = (memcmp(VARDATA_ANY(tl), VARDATA_ANY(sl), 16) == 0)
                             ? 1 : -1;
        }
    }

    old = MemoryContextSwitchTo(work);
    if (n_cand == cap_cand)
    {
        if (cap_cand > INT_MAX / 2 ||
            (Size) (cap_cand ? cap_cand * 2 : 1) >
                MaxAllocSize / sizeof(PcCand))
            ereport(ERROR,
                    (errmsg("prompt_coherence: candidate set exceeds PostgreSQL allocation capacity")));
        cap_cand = cap_cand ? cap_cand * 2 : 1;
        cands = cands ? (PcCand *) repalloc(cands, sizeof(PcCand) * cap_cand)
                      : (PcCand *) palloc(sizeof(PcCand) * cap_cand);
    }
    MemoryContextSwitchTo(old);

    MemSet(&cands[n_cand], 0, sizeof(PcCand));
    cands[n_cand].ord = ord;
    memcpy(cands[n_cand].tok, VARDATA_ANY(tok), 16);
    memcpy(cands[n_cand].syn, VARDATA_ANY(syn), 16);
    cands[n_cand].denote_mu = mu;
    cands[n_cand].witnesses = wit;
    cands[n_cand].lang_agree = lang_agree;

    pc_syn_add(syn_h, (const uint8 *) VARDATA_ANY(syn), n_cand, work);

    /* The token's own id joins the membership set, so cells that
     * hang on the surface rather than on a sense credit its
     * candidates too. */
    pc_syn_add(syn_h, (const uint8 *) VARDATA_ANY(tok), n_cand, work);

    tk = (PcTokEntry *) hash_search(tok_h, VARDATA_ANY(tok), HASH_ENTER, &found);
    if (!found)
    {
        tk->ords = NULL;
        tk->n_ords = 0;
        tk->cap_ords = 0;
    }
    pc_int32_add(&tk->ords, &tk->n_ords, &tk->cap_ords,
                 ord, work, "token ordinals");

    n_cand++;
    read->cands = cands;
    read->count = n_cand;
    read->capacity = cap_cand;
}

void
pc_receive_relation_mass(TupleTableSlot *row, void *opaque)
{
    PcEdgeRead *read = opaque;
    HTAB *syn_h = read->syn_h, *type_h = read->type_h;
    PcCand *cands = read->cands;
    bool      isnull;
    bytea    *subj, *tid;
    int64     rating, rd;
    double    eff, rank;
    PcSynEntry  *me;
    PcTypeEntry *te2;
    const laplace_relation_def_t *def = NULL;

    subj = spi_slot_bytea(row, 1);
    if (!subj || VARSIZE_ANY_EXHDR(subj) != 16) return;
    tid = spi_slot_bytea(row, 2);
    if (!tid || VARSIZE_ANY_EXHDR(tid) != 16) return;
    rating = DatumGetInt64(slot_getattr(row, 3, &isnull));
    if (isnull) return;
    rd = DatumGetInt64(slot_getattr(row, 4, &isnull));
    if (isnull) return;

    te2 = (PcTypeEntry *) hash_search(type_h, VARDATA_ANY(tid), HASH_FIND, NULL);
    if (te2 == NULL || !te2->named)
        return;
    me = (PcSynEntry *) hash_search(syn_h, VARDATA_ANY(subj), HASH_FIND, NULL);
    if (me == NULL)
        return;

    eff = (double) laplace_effective_mu_fp(rating, rd);
    rank = (laplace_relation_lookup((const hash128_t *) VARDATA_ANY(tid), &def) == 0
            && def != NULL) ? def->rank : 0.0;

    for (int i = 0; i < me->n_idx; i++)
    {
        PcCand *c = &cands[me->idx[i]];

        if (!pc_type_has_other_namer(te2, c->ord))
            continue;   /* only this token named it: no credit */
        c->rel_mass += rank * eff;
        if (!c->has_rel_type || rank > c->rel_type_rank)
        {
            memcpy(c->rel_type, VARDATA_ANY(tid), 16);
            c->rel_type_rank = rank;
            c->has_rel_type = true;
        }
    }
}
