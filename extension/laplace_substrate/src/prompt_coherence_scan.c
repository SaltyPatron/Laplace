#include "prompt_coherence_internal.h"

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

/* One direction of the edge scan. `forward` selects which column carries the
 * candidate being credited. Each direction is a plain index range read on the
 * subject or object btree; one OR over both directions would not be. */
static void
pc_credit_edge_c(PcEdgeRead *read, const hash128_t *mine,
                 const hash128_t *other, const hash128_t *tid,
                 int64 rating, int64 rd)
{
    HTAB *syn_h = read->syn_h, *type_h = read->type_h, *peer_h = read->peer_h;
    PcCand *cands = read->cands;
    double     eff, rank;
    PcSynEntry *me, *peer;
    PcTypeEntry *te;
    bool        found;
    const laplace_relation_def_t *def = NULL;

    /* Every type a candidate carries enters type_h, the set the
     * rel_mass pass draws its named types from. */
    te = (PcTypeEntry *) hash_search(type_h, tid, HASH_ENTER, &found);
    if (!found)
    {
        te->namer_ords = NULL;
        te->n_namers = 0;
        te->cap_namers = 0;
        te->named = false;
    }

    me = (PcSynEntry *) hash_search(syn_h, mine, HASH_FIND, NULL);
    if (me == NULL)
        return;

    /* Effective mu (rating - 2*rd) weighted by the relation's manifest rank. */
    eff = (double) laplace_effective_mu_fp(rating, rd);
    rank = (laplace_relation_lookup(tid, &def) == 0
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

    peer = (PcSynEntry *) hash_search(syn_h, other, HASH_FIND, NULL);
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

static void
pc_stored_fwd_cb(const LaplaceConsensusRow *crow, void *context)
{
    if (crow->object_is_null)
        return;
    pc_credit_edge_c((PcEdgeRead *) context, &crow->subject, &crow->object,
                     &crow->type, crow->rating, crow->rd);
}

static void
pc_stored_rev_cb(const LaplaceConsensusRow *crow, void *context)
{
    if (crow->object_is_null)
        return;
    pc_credit_edge_c((PcEdgeRead *) context, &crow->object, &crow->subject,
                     &crow->type, crow->rating, crow->rd);
}

void
pc_scan_edges(HTAB *syn_h, HTAB *type_h, HTAB *peer_h, PcCand *cands,
              ArrayType *syn_arr, bool forward)
{
    PcEdgeRead         read = {syn_h, type_h, peer_h, cands};
    LaplaceEdgeRawRow *raw_rows;
    int                n_raw = 0;

    laplace_consensus_candidates_stored(
        forward ? syn_arr : NULL,
        forward ? NULL : syn_arr,
        NULL,
        forward ? pc_stored_fwd_cb : pc_stored_rev_cb,
        &read);

    raw_rows = laplace_edges_raw_batch_c(
        syn_arr, forward ? "out" : "in", NULL,
        true, 128, true, "edge_rank", &n_raw);
    for (int i = 0; i < n_raw; ++i)
    {
        const LaplaceEdgeRawRow *r = &raw_rows[i];
        if (r->neighbour_null || r->type_null)
            continue;
        pc_credit_edge_c(&read, &r->subject_id, &r->neighbour_id,
                         &r->type_id, r->rating, r->rd);
    }
    if (raw_rows != NULL)
        pfree(raw_rows);
}

void
pc_add_candidate_c(PcCandidateRead *read, int32 ord, const hash128_t *tok,
                   const hash128_t *syn, double mu, int64 wit,
                   int32 lang_agree)
{
    HTAB         *syn_h = read->syn_h, *tok_h = read->tok_h;
    PcCand       *cands = read->cands;
    int           n_cand = read->count, cap_cand = read->capacity;
    MemoryContext work = read->work, old;
    PcTokEntry   *tk;
    bool          found;

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
    memcpy(cands[n_cand].tok, tok, 16);
    memcpy(cands[n_cand].syn, syn, 16);
    cands[n_cand].denote_mu = mu;
    cands[n_cand].witnesses = wit;
    cands[n_cand].lang_agree = lang_agree;

    pc_syn_add(syn_h, (const uint8 *) syn, n_cand, work);

    /* The token's own id joins the membership set, so cells that
     * hang on the surface rather than on a sense credit its
     * candidates too. */
    pc_syn_add(syn_h, (const uint8 *) tok, n_cand, work);

    tk = (PcTokEntry *) hash_search(tok_h, tok, HASH_ENTER, &found);
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
pc_credit_relation_mass_c(PcEdgeRead *read, const hash128_t *subj,
                          const hash128_t *tid, int64 rating, int64 rd)
{
    HTAB        *syn_h = read->syn_h, *type_h = read->type_h;
    PcCand      *cands = read->cands;
    double       eff, rank;
    PcSynEntry  *me;
    PcTypeEntry *te2;
    const laplace_relation_def_t *def = NULL;

    te2 = (PcTypeEntry *) hash_search(type_h, tid, HASH_FIND, NULL);
    if (te2 == NULL || !te2->named)
        return;
    me = (PcSynEntry *) hash_search(syn_h, subj, HASH_FIND, NULL);
    if (me == NULL)
        return;

    eff = (double) laplace_effective_mu_fp(rating, rd);
    rank = (laplace_relation_lookup(tid, &def) == 0 && def != NULL) ? def->rank : 0.0;

    for (int i = 0; i < me->n_idx; i++)
    {
        PcCand *c = &cands[me->idx[i]];

        if (!pc_type_has_other_namer(te2, c->ord))
            continue;   /* only this token named it: no credit */
        c->rel_mass += rank * eff;
        if (!c->has_rel_type || rank > c->rel_type_rank)
        {
            memcpy(c->rel_type, tid, 16);
            c->rel_type_rank = rank;
            c->has_rel_type = true;
        }
    }
}
