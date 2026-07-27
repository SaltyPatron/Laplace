/*
 * prompt_coherence — joint orientation + sense resolution, natively.
 *
 * WHY THIS IS C. The SQL form of this read hung chat() for every prompt (>280s,
 * measured 2026-07-27 after the UD ingest) and was reverted off the hot path.
 * It was SQL doing what the substrate law puts in C: set-returning functions as
 * table sources (one senses() per token, one bubble_up per candidate relation
 * type), relation-name identifiers split with string_to_array per row, and an
 * O(n^2) join of 277-315 candidate senses against each other through an OR of
 * both directions that no consensus index can serve. Rewriting it as two indexed
 * joins with a MATERIALIZED fence still measured 82s.
 *
 * What it actually computes is a MEMBERSHIP SCAN: for each candidate sense, how
 * much rated mass connects it to the other tokens' candidates. That is one
 * indexed range read per direction plus an O(1) hash probe per edge -- the shape
 * recall.c and generate_walk.c already use. SQL fetches sets; C does the math.
 *
 * WHAT IT DECIDES. Orientation used to score every token in ISOLATION (best sense
 * by denote_mu, rank tokens by highway popcount). Measured failures that motivated
 * this: "What is a pawn in chess?" ranked the article "a" above "pawn" (breadth
 * tied 13-13, denote_mu 1537 vs 1313) and answered "A is the 1st letter of the
 * Roman alphabet"; "is" resolved to the sense ICE; "chess" ranked LAST. No scalar
 * over one token separates those -- the information is in the graph BETWEEN them.
 *
 * Two signals, both read off the same edge scan:
 *   COHERENCE  rated mass to OTHER tokens' candidate senses. Settles pawn/chess.
 *   REL_MASS   rated mass in a relation type NAMED by another token. Settles the
 *              "what are the X of Y" shape, where X names a relation and not a
 *              peer concept: a car HAS_PART door/wheel/engine and never HAS_PART
 *              "part", so coherence is silent and denote_mu picks TZAR for "car"
 *              (1648, the Slavic spelling) over the vehicle (1567) -- while the
 *              vehicle carries 29 HAS_PART edges and tzar carries none.
 *
 * A relation type is addressed by a name; the name's OBJECT token is a content
 * word with a content id, so a prompt token reaches it by id equality or through
 * an attested IS_LEMMA_OF edge -- which is what makes the PLURAL work ("parts" ->
 * "part" -> HAS_PART). Inflection is a witnessed fact, so this reads the
 * attestation rather than guessing morphology, and stays exact.
 *
 * Only the name's LAST token is used, and only at length >= 3: a canonical name
 * is a VERB_OBJECT identifier (HAS_PART, EVOKES_FRAME, MEMBER_OF_VERBNET_CLASS)
 * whose leading tokens are grammar. Matching every token let the article "a" name
 * IS_A -- whose final token is literally "A" -- handing every candidate with many
 * IS_A edges an enormous mass and selecting a WORSE sense of "car" than denote_mu
 * did. The bound rejects degenerate identifier fragments, not English words.
 *
 * No floors, no caps, no top-k: sums over whatever edges exist, ranked, ties
 * broken on the id for determinism.
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

#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "spi_common.h"
#include "spi_nested.h"

PG_FUNCTION_INFO_V1(pg_laplace_prompt_coherence);

#define PC_MAX_ORD 63          /* peers ride a uint64 mask; ords beyond this still
                                * contribute mass, just not a distinct peer bit. */

typedef struct PcCand
{
    int32   ord;
    uint8   tok[16];
    uint8   syn[16];
    double  denote_mu;
    double  coherence;
    uint64  peer_mask;
    double  rel_mass;
    uint8   rel_type[16];
    double  rel_type_rank;     /* rank of the recorded rel_type, for "best" */
    bool    has_rel_type;
} PcCand;

/* syn -> the candidate rows carrying it (a surface/sense pair can repeat across
 * tokens), plus the ord mask, so an edge hit resolves to peers in O(1). */
typedef struct PcSynEntry
{
    uint8   key[16];
    uint64  ord_mask;
    int    *idx;
    int     n_idx;
    int     cap_idx;
} PcSynEntry;

typedef struct PcTokEntry
{
    uint8   key[16];
    uint64  ord_mask;
} PcTokEntry;

typedef struct PcTypeEntry
{
    uint8   key[16];
    uint64  namer_mask;        /* ords whose token names this relation type */
    bool    named;
} PcTypeEntry;

static void
pc_syn_add(HTAB *h, const uint8 *syn, int32 ord, int idx, MemoryContext cxt)
{
    bool        found;
    PcSynEntry *e = (PcSynEntry *) hash_search(h, syn, HASH_ENTER, &found);

    if (!found)
    {
        MemoryContext old = MemoryContextSwitchTo(cxt);

        e->ord_mask = 0;
        e->cap_idx = 4;
        e->n_idx = 0;
        e->idx = (int *) palloc(sizeof(int) * e->cap_idx);
        MemoryContextSwitchTo(old);
    }
    if (e->n_idx == e->cap_idx)
    {
        MemoryContext old = MemoryContextSwitchTo(cxt);

        e->cap_idx *= 2;
        e->idx = (int *) repalloc(e->idx, sizeof(int) * e->cap_idx);
        MemoryContextSwitchTo(old);
    }
    e->idx[e->n_idx++] = idx;
    if (ord >= 0 && ord <= PC_MAX_ORD)
        e->ord_mask |= (uint64) 1 << ord;
}

/* One direction of the edge scan. `forward` selects which column carries the
 * candidate we are crediting; both are served by a plain index range read
 * (consensus_subject_type_btree / consensus_object_btree), which is the entire
 * point of splitting the OR the SQL form used. */
static void
pc_scan_edges(HTAB *syn_h, HTAB *type_h, PcCand *cands, ArrayType *syn_arr,
              bool forward, MemoryContext cxt)
{
    Oid        argtypes[1] = { BYTEAARRAYOID };
    Datum      args[1];
    Portal     portal;
    const char *sql = forward
        ? "SELECT c.subject_id, c.object_id, c.type_id, c.rating, c.rd "
          "FROM laplace.consensus c WHERE c.subject_id = ANY($1)"
        : "SELECT c.object_id, c.subject_id, c.type_id, c.rating, c.rd "
          "FROM laplace.consensus c WHERE c.object_id = ANY($1)";

    args[0] = PointerGetDatum(syn_arr);
    portal = SPI_cursor_open_with_args("pc_edges", sql, 1, argtypes, args, NULL, true, 0);

    for (;;)
    {
        SPI_cursor_fetch(portal, true, 50000);
        if (SPI_processed == 0)
            break;

        for (uint64 r = 0; r < SPI_processed; r++)
        {
            HeapTuple  tup = SPI_tuptable->vals[r];
            TupleDesc  td = SPI_tuptable->tupdesc;
            bool       isnull;
            bytea     *mine, *other, *tid;
            int64      rating, rd;
            double     eff, rank;
            PcSynEntry *me, *peer;
            PcTypeEntry *te;
            bool        found;
            const laplace_relation_def_t *def = NULL;

            mine = DatumGetByteaPP(SPI_getbinval(tup, td, 1, &isnull));
            if (isnull || VARSIZE_ANY_EXHDR(mine) != 16)
                continue;
            other = DatumGetByteaPP(SPI_getbinval(tup, td, 2, &isnull));
            if (isnull || VARSIZE_ANY_EXHDR(other) != 16)
                continue;
            tid = DatumGetByteaPP(SPI_getbinval(tup, td, 3, &isnull));
            if (isnull || VARSIZE_ANY_EXHDR(tid) != 16)
                continue;
            rating = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));
            if (isnull) continue;
            rd = DatumGetInt64(SPI_getbinval(tup, td, 5, &isnull));
            if (isnull) continue;

            /* Record the type so the relation-naming pass has the bounded set of
             * types the candidates ACTUALLY have edges of -- never a scan of
             * consensus for distinct types. */
            te = (PcTypeEntry *) hash_search(type_h, VARDATA_ANY(tid), HASH_ENTER, &found);
            if (!found)
            {
                te->namer_mask = 0;
                te->named = false;
            }

            peer = (PcSynEntry *) hash_search(syn_h, VARDATA_ANY(other), HASH_FIND, NULL);
            if (peer == NULL)
                continue;               /* edge leaves the candidate set */

            me = (PcSynEntry *) hash_search(syn_h, VARDATA_ANY(mine), HASH_FIND, NULL);
            if (me == NULL)
                continue;

            /* eff_mu = rating - 2*rd, the conservative estimate everything ranks
             * by; relation_rank weights the band. Both native -- no SQL call. */
            eff = (double) (rating - 2 * rd);
            rank = (laplace_relation_lookup((const hash128_t *) VARDATA_ANY(tid), &def) == 0
                    && def != NULL) ? def->rank : 0.0;

            for (int i = 0; i < me->n_idx; i++)
            {
                PcCand *c = &cands[me->idx[i]];
                uint64  others = peer->ord_mask & ~((uint64) 1 << (c->ord & 63));

                if (c->ord <= PC_MAX_ORD && others == 0)
                    continue;           /* same token only: not a peer */
                c->coherence += rank * eff;
                c->peer_mask |= others;
            }
        }
        SPI_freetuptable(SPI_tuptable);
        CHECK_FOR_INTERRUPTS();
    }
    SPI_cursor_close(portal);
}

Datum
pg_laplace_prompt_coherence(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    text          *prompt;
    MemoryContext  work, old;
    HASHCTL        ctl;
    HTAB          *syn_h, *tok_h, *type_h;
    PcCand        *cands = NULL;
    int            n_cand = 0, cap_cand = 0;
    Datum         *syn_datums = NULL;
    int            n_syn = 0;
    ArrayType     *syn_arr;
    bool           spi_top = false;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    prompt = PG_GETARG_TEXT_PP(0);

    InitMaterializedSRF(fcinfo, 0);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "prompt_coherence: SPI_connect failed");

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

    /* ---- candidates: ONE query, executed once. The SQL form re-executed this
     * per CTE reference, which is why fencing it MATERIALIZED changed the shape
     * at all. Here it is fetched once into C and never recomputed. ---- */
    {
        Oid    argtypes[1] = { TEXTOID };
        Datum  args[1];
        Portal portal;

        args[0] = PointerGetDatum(prompt);
        portal = SPI_cursor_open_with_args(
            "pc_cand",
            "SELECT p.ord, p.id, s.synset_id, s.eff_mu::float8 "
            "FROM laplace.prompt_state($1) p "
            "CROSS JOIN LATERAL laplace.senses(p.id) s "
            "WHERE p.id IS NOT NULL AND s.synset_id IS NOT NULL",
            1, argtypes, args, NULL, true, 0);

        for (;;)
        {
            SPI_cursor_fetch(portal, true, 4096);
            if (SPI_processed == 0)
                break;

            for (uint64 r = 0; r < SPI_processed; r++)
            {
                HeapTuple tup = SPI_tuptable->vals[r];
                TupleDesc td = SPI_tuptable->tupdesc;
                bool      isnull;
                int32     ord;
                bytea    *tok, *syn;
                double    mu;
                PcTokEntry *tk;
                bool      found;

                ord = DatumGetInt32(SPI_getbinval(tup, td, 1, &isnull));
                if (isnull) continue;
                tok = DatumGetByteaPP(SPI_getbinval(tup, td, 2, &isnull));
                if (isnull || VARSIZE_ANY_EXHDR(tok) != 16) continue;
                syn = DatumGetByteaPP(SPI_getbinval(tup, td, 3, &isnull));
                if (isnull || VARSIZE_ANY_EXHDR(syn) != 16) continue;
                mu = DatumGetFloat8(SPI_getbinval(tup, td, 4, &isnull));
                if (isnull) mu = 0.0;

                old = MemoryContextSwitchTo(work);
                if (n_cand == cap_cand)
                {
                    cap_cand = cap_cand ? cap_cand * 2 : 512;
                    cands = cands ? (PcCand *) repalloc(cands, sizeof(PcCand) * cap_cand)
                                  : (PcCand *) palloc(sizeof(PcCand) * cap_cand);
                }
                MemoryContextSwitchTo(old);

                MemSet(&cands[n_cand], 0, sizeof(PcCand));
                cands[n_cand].ord = ord;
                memcpy(cands[n_cand].tok, VARDATA_ANY(tok), 16);
                memcpy(cands[n_cand].syn, VARDATA_ANY(syn), 16);
                cands[n_cand].denote_mu = mu;

                if (hash_search(syn_h, VARDATA_ANY(syn), HASH_FIND, NULL) == NULL)
                {
                    old = MemoryContextSwitchTo(work);
                    if (syn_datums == NULL)
                        syn_datums = (Datum *) palloc(sizeof(Datum) * 4096);
                    if (n_syn < 4096)
                    {
                        bytea *cp = (bytea *) palloc(VARSIZE_ANY(syn));

                        memcpy(cp, syn, VARSIZE_ANY(syn));
                        syn_datums[n_syn++] = PointerGetDatum(cp);
                    }
                    MemoryContextSwitchTo(old);
                }
                pc_syn_add(syn_h, (const uint8 *) VARDATA_ANY(syn), ord, n_cand, work);

                tk = (PcTokEntry *) hash_search(tok_h, VARDATA_ANY(tok), HASH_ENTER, &found);
                if (!found)
                    tk->ord_mask = 0;
                if (ord >= 0 && ord <= PC_MAX_ORD)
                    tk->ord_mask |= (uint64) 1 << ord;

                n_cand++;
            }
            SPI_freetuptable(SPI_tuptable);
            CHECK_FOR_INTERRUPTS();
        }
        SPI_cursor_close(portal);
    }

    if (n_cand == 0 || n_syn == 0)
    {
        MemoryContextDelete(work);
        laplace_spi_finish(spi_top);
        return (Datum) 0;
    }

    old = MemoryContextSwitchTo(work);
    syn_arr = construct_array(syn_datums, n_syn, BYTEAOID, -1, false, TYPALIGN_INT);
    MemoryContextSwitchTo(old);

    /* ---- coherence: two indexed range reads, O(1) probe per edge ---- */
    pc_scan_edges(syn_h, type_h, cands, syn_arr, true, work);
    pc_scan_edges(syn_h, type_h, cands, syn_arr, false, work);

    /* ---- which of those types does a prompt token NAME? Canonical name and
     * rank come from the manifest in C; the name's object token becomes a
     * content id through the same hash the substrate uses everywhere, so the
     * match is exact id equality -- or an attested IS_LEMMA_OF edge, which is
     * the hop that makes an inflected prompt word ("parts") reach its lemma. ---- */
    {
        HASH_SEQ_STATUS  seq;
        PcTypeEntry     *te;
        Datum           *nw_datums = NULL;
        int              n_nw = 0;
        PcTypeEntry    **nw_owner = NULL;

        old = MemoryContextSwitchTo(work);
        nw_datums = (Datum *) palloc(sizeof(Datum) * 1024);
        nw_owner = (PcTypeEntry **) palloc(sizeof(PcTypeEntry *) * 1024);
        MemoryContextSwitchTo(old);

        hash_seq_init(&seq, type_h);
        while ((te = (PcTypeEntry *) hash_seq_search(&seq)) != NULL)
        {
            const laplace_relation_def_t *def = NULL;
            const char *name, *last;
            size_t      len;
            hash128_t   wid;
            PcTokEntry *tk;

            if (laplace_relation_lookup((const hash128_t *) te->key, &def) != 0 || def == NULL)
                continue;
            name = def->canonical;
            if (name == NULL)
                continue;

            last = strrchr(name, '_');
            last = last ? last + 1 : name;
            len = strlen(last);
            if (len < 3)
                continue;               /* identifier fragment, not a concept */

            {
                char *lower = pnstrdup(last, len);

                for (size_t i = 0; i < len; i++)
                    lower[i] = pg_ascii_tolower(lower[i]);
                if (laplace_content_root_id((const uint8_t *) lower, len, &wid) != 0)
                {
                    pfree(lower);
                    continue;
                }
                pfree(lower);
            }

            tk = (PcTokEntry *) hash_search(tok_h, &wid, HASH_FIND, NULL);
            if (tk != NULL)
            {
                te->named = true;
                te->namer_mask |= tk->ord_mask;
                continue;
            }
            /* No direct hit: queue the name id for the lemma probe. */
            if (n_nw < 1024)
            {
                bytea *b;

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
            Oid        argtypes[1] = { BYTEAARRAYOID };
            Datum      args[1];
            ArrayType *nw_arr;
            int        rc;

            old = MemoryContextSwitchTo(work);
            nw_arr = construct_array(nw_datums, n_nw, BYTEAOID, -1, false, TYPALIGN_INT);
            MemoryContextSwitchTo(old);

            args[0] = PointerGetDatum(nw_arr);
            rc = SPI_execute_with_args(
                "SELECT l.subject_id, l.object_id FROM laplace.consensus l "
                "WHERE l.subject_id = ANY($1) "
                "  AND l.type_id = laplace.relation_type_id('IS_LEMMA_OF')",
                1, argtypes, args, NULL, true, 0);
            if (rc == SPI_OK_SELECT)
            {
                for (uint64 r = 0; r < SPI_processed; r++)
                {
                    HeapTuple tup = SPI_tuptable->vals[r];
                    TupleDesc td = SPI_tuptable->tupdesc;
                    bool      isnull;
                    bytea    *lemma, *form;
                    PcTokEntry *tk;

                    lemma = DatumGetByteaPP(SPI_getbinval(tup, td, 1, &isnull));
                    if (isnull || VARSIZE_ANY_EXHDR(lemma) != 16) continue;
                    form = DatumGetByteaPP(SPI_getbinval(tup, td, 2, &isnull));
                    if (isnull || VARSIZE_ANY_EXHDR(form) != 16) continue;

                    tk = (PcTokEntry *) hash_search(tok_h, VARDATA_ANY(form), HASH_FIND, NULL);
                    if (tk == NULL)
                        continue;
                    for (int i = 0; i < n_nw; i++)
                    {
                        bytea *nb = DatumGetByteaPP(nw_datums[i]);

                        if (memcmp(VARDATA_ANY(nb), VARDATA_ANY(lemma), 16) == 0)
                        {
                            nw_owner[i]->named = true;
                            nw_owner[i]->namer_mask |= tk->ord_mask;
                        }
                    }
                }
                SPI_freetuptable(SPI_tuptable);
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

        old = MemoryContextSwitchTo(work);
        td_arr = (Datum *) palloc(sizeof(Datum) * 1024);
        MemoryContextSwitchTo(old);

        hash_seq_init(&seq, type_h);
        while ((te = (PcTypeEntry *) hash_seq_search(&seq)) != NULL)
        {
            if (!te->named || n_td >= 1024)
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
            Oid        argtypes[2] = { BYTEAARRAYOID, BYTEAARRAYOID };
            Datum      args[2];
            ArrayType *type_arr;
            Portal     portal;

            old = MemoryContextSwitchTo(work);
            type_arr = construct_array(td_arr, n_td, BYTEAOID, -1, false, TYPALIGN_INT);
            MemoryContextSwitchTo(old);

            args[0] = PointerGetDatum(syn_arr);
            args[1] = PointerGetDatum(type_arr);
            portal = SPI_cursor_open_with_args(
                "pc_rel",
                "SELECT c.subject_id, c.type_id, c.rating, c.rd "
                "FROM laplace.consensus c "
                "WHERE c.subject_id = ANY($1) AND c.type_id = ANY($2)",
                2, argtypes, args, NULL, true, 0);

            for (;;)
            {
                SPI_cursor_fetch(portal, true, 50000);
                if (SPI_processed == 0)
                    break;

                for (uint64 r = 0; r < SPI_processed; r++)
                {
                    HeapTuple tup = SPI_tuptable->vals[r];
                    TupleDesc td = SPI_tuptable->tupdesc;
                    bool      isnull;
                    bytea    *subj, *tid;
                    int64     rating, rd;
                    double    eff, rank;
                    PcSynEntry  *me;
                    PcTypeEntry *te2;
                    const laplace_relation_def_t *def = NULL;

                    subj = DatumGetByteaPP(SPI_getbinval(tup, td, 1, &isnull));
                    if (isnull || VARSIZE_ANY_EXHDR(subj) != 16) continue;
                    tid = DatumGetByteaPP(SPI_getbinval(tup, td, 2, &isnull));
                    if (isnull || VARSIZE_ANY_EXHDR(tid) != 16) continue;
                    rating = DatumGetInt64(SPI_getbinval(tup, td, 3, &isnull));
                    if (isnull) continue;
                    rd = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));
                    if (isnull) continue;

                    te2 = (PcTypeEntry *) hash_search(type_h, VARDATA_ANY(tid), HASH_FIND, NULL);
                    if (te2 == NULL || !te2->named)
                        continue;
                    me = (PcSynEntry *) hash_search(syn_h, VARDATA_ANY(subj), HASH_FIND, NULL);
                    if (me == NULL)
                        continue;

                    eff = (double) (rating - 2 * rd);
                    rank = (laplace_relation_lookup((const hash128_t *) VARDATA_ANY(tid), &def) == 0
                            && def != NULL) ? def->rank : 0.0;

                    for (int i = 0; i < me->n_idx; i++)
                    {
                        PcCand *c = &cands[me->idx[i]];
                        uint64  namers = te2->namer_mask & ~((uint64) 1 << (c->ord & 63));

                        if (namers == 0)
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
                SPI_freetuptable(SPI_tuptable);
                CHECK_FOR_INTERRUPTS();
            }
            SPI_cursor_close(portal);
        }
    }

    /* ---- emit one row per (token, best sense). WHICH SENSE: the one that
     * coheres with the rest of the prompt, then the one participating in the
     * relation the prompt named; denote_mu breaks a tie in both, and carries a
     * prompt with no joint signal at all -- it is what picked TZAR and ICE when
     * it led. ---- */
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
                if (o->coherence > best->coherence
                    || (o->coherence == best->coherence && o->rel_mass > best->rel_mass)
                    || (o->coherence == best->coherence && o->rel_mass == best->rel_mass
                        && o->denote_mu > best->denote_mu)
                    || (o->coherence == best->coherence && o->rel_mass == best->rel_mass
                        && o->denote_mu == best->denote_mu
                        && memcmp(o->syn, best->syn, 16) < 0))
                {
                    is_best = false;
                    break;
                }
            }
            if (!is_best)
                continue;

            {
                Datum values[8];
                bool  nulls[8];
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
                values[5] = Int64GetDatum((int64) pg_popcount64(best->peer_mask));
                if (best->has_rel_type)
                {
                    bytea *rt = (bytea *) palloc(VARHDRSZ + 16);

                    SET_VARSIZE(rt, VARHDRSZ + 16);
                    memcpy(VARDATA(rt), best->rel_type, 16);
                    values[6] = PointerGetDatum(rt);
                }
                else
                    nulls[6] = true;
                values[7] = Float8GetDatum(best->rel_mass);

                tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
            }
        }
    }

    MemoryContextDelete(work);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
