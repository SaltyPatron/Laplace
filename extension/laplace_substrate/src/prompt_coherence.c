/*
 * prompt_coherence — joint orientation + sense resolution, natively.
 *
 * WHY THIS IS C. The SQL form of this read hung converse.chat() for every prompt (>280s,
 * measured 2026-07-27 after the UD ingest) and was reverted off the hot path.
 * It was SQL doing what the substrate law puts in C: set-returning functions as
 * table sources (one lexical.senses() per token, one bubble_up per candidate relation
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
                                * contribute mass and still RECEIVE peer credit
                                * from trackable tokens, but cannot BE a peer for
                                * anyone — they never set a bit. Two tokens both
                                * past 63 score no coherence between them. That is
                                * a stated ceiling, not a silent degradation; see
                                * the self-bit comment in pc_scan_edges. */

typedef struct PcCand
{
    int32   ord;
    uint8   tok[16];
    uint8   syn[16];
    double  denote_mu;
    int64   witnesses;         /* evidence behind this sense, from lexical.senses() */
    int32   lang_agree;        /* +1 agrees with the token's language, 0 unknown,
                                * -1 disagrees. Tri-state on purpose: an
                                * unattested language is NOT a mismatch. */
    double  coherence;
    double  total_mass;        /* ALL rated mass on this candidate, peers or not */
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
    double  icf;               /* inverse container frequency; see pc_load_icf */
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

            me = (PcSynEntry *) hash_search(syn_h, VARDATA_ANY(mine), HASH_FIND, NULL);
            if (me == NULL)
                continue;

            /* eff_mu = rating - 2*rd, the conservative estimate everything ranks
             * by; relation_rank weights the band. Both native -- no SQL call. */
            eff = (double) (rating - 2 * rd);
            rank = (laplace_relation_lookup((const hash128_t *) VARDATA_ANY(tid), &def) == 0
                    && def != NULL) ? def->rank : 0.0;

            /* TOTAL first, and for EVERY edge -- this is the denominator that
             * makes the coherence sum mean something. Forward pass only, so an
             * edge with both endpoints in the candidate set is not counted
             * twice. Free: these rows are already being read. */
            if (forward)
                for (int i = 0; i < me->n_idx; i++)
                    cands[me->idx[i]].total_mass += rank * eff;

            peer = (PcSynEntry *) hash_search(syn_h, VARDATA_ANY(other), HASH_FIND, NULL);
            if (peer == NULL)
                continue;               /* edge leaves the candidate set */

            for (int i = 0; i < me->n_idx; i++)
            {
                PcCand *c = &cands[me->idx[i]];
                uint64  self_bit;
                uint64  others;

                /* Self-exclusion must clear THIS token's bit or nothing at all.
                 * `1 << (c->ord & 63)` wrapped: ord 64 cleared bit 0, i.e. some
                 * OTHER token's bit, silently removing a real peer. And the
                 * `others == 0` guard below was itself gated on ord <= PC_MAX_ORD,
                 * so past 63 coherence was credited unconditionally — including
                 * when the peer had no attesting token at all.
                 *
                 * A token beyond PC_MAX_ORD never sets a bit (the ord_mask write
                 * is guarded at load), so it has no self-bit to clear and clearing
                 * anything for it is always wrong. Clearing nothing is correct.
                 *
                 * Stated limit, not a silent one: past ord 63 a token still
                 * contributes and receives mass, and is still credited whenever
                 * ANY trackable token attests the same peer. What it cannot do is
                 * be counted as a peer FOR another token. Two tokens both beyond
                 * 63 therefore see each other as no peer and score 0 coherence
                 * between them — conservative and wrong-in-one-direction only,
                 * where the previous behaviour was wrong in both. Widening the
                 * mask past 64 ords is the real fix; GH #23 in the task list. */
                self_bit = (c->ord >= 0 && c->ord <= PC_MAX_ORD)
                           ? ((uint64) 1 << c->ord)
                           : 0;
                others = peer->ord_mask & ~self_bit;

                if (others == 0)
                    continue;           /* no OTHER token attests this peer */
                c->coherence += rank * eff;
                c->peer_mask |= others;
            }
        }
        SPI_freetuptable(SPI_tuptable);
        CHECK_FOR_INTERRUPTS();
    }
    SPI_cursor_close(portal);
}

/* ---- inverse container frequency: the specificity prior ----
 *
 * coherence is rated mass between one token's candidates and the OTHER tokens'
 * candidates. It requires a DIRECT consensus edge to exist between two
 * candidate sets, and at one hop the graph is sparse: measured 2026-08-04 on
 * the full foundation ladder, coherence was 0 on every token of "hot", "dog"
 * and "Water is made of", and fired on only 2 of 4 tokens of "What is a pawn
 * in chess". specificity is coherence/total_mass, so it was 0 too, and the
 * election fell through to ord DESC — a subject-verb-object word-order prior
 * sitting inside a substrate that is language- and modality-agnostic. It is
 * right for "What is a glacier?" and wrong for "Water is made of", for SOV and
 * VSO languages, for a chess position, and for an image.
 *
 * Containment is the signal that is always defined, because it is structural:
 * how many higher-tier entities contain this id. A token inside nearly every
 * trajectory says almost nothing about which trajectory you meant. This is IDF
 * derived from the composition hierarchy rather than from a stop-word list, so
 * it carries across languages and modalities by construction.
 *
 * One batched call to the installed structural.entity_container_degree() operation — the
 * same body diagnostics use, never a second copy of the read. Bounded by its
 * p_cap, so cost does not scale with how ubiquitous the floor is.
 *
 * Rejected alternatives, all measured on the same probe set rather than
 * reasoned about: highway popcount (the tier-0 floor scores HIGHEST — 'a' at 19
 * bands — because an atom participates in every band; that is degree, not
 * specificity), total_mass (elects 'chess' over 'pawn'; rewards volume), and
 * band-rank sum (better, but still a single hand-picked scalar). */
static void
pc_load_icf(HTAB *tok_h, MemoryContext work)
{
    HASH_SEQ_STATUS seq;
    PcTokEntry     *tk;
    Datum          *ids;
    int             n = 0;
    MemoryContext   old;
    Oid             argtypes[1] = { BYTEAARRAYOID };
    Datum           args[1];
    ArrayType      *arr;
    int             rc;

    old = MemoryContextSwitchTo(work);
    ids = (Datum *) palloc(sizeof(Datum) * (PC_MAX_ORD + 1));
    MemoryContextSwitchTo(old);

    hash_seq_init(&seq, tok_h);
    while ((tk = (PcTokEntry *) hash_seq_search(&seq)) != NULL)
    {
        /* Neutral default set for EVERY token before anything can fail: an
         * unmeasured id must not be silently ranked as maximally specific. */
        tk->icf = 1.0;
        if (n > PC_MAX_ORD)
            continue;
        old = MemoryContextSwitchTo(work);
        {
            bytea *b = (bytea *) palloc(VARHDRSZ + 16);

            SET_VARSIZE(b, VARHDRSZ + 16);
            memcpy(VARDATA(b), tk->key, 16);
            ids[n++] = PointerGetDatum(b);
        }
        MemoryContextSwitchTo(old);
    }
    if (n == 0)
        return;

    old = MemoryContextSwitchTo(work);
    arr = construct_array(ids, n, BYTEAOID, -1, false, TYPALIGN_INT);
    MemoryContextSwitchTo(old);
    args[0] = PointerGetDatum(arr);

    rc = SPI_execute_with_args(
        "SELECT entity_id, icf FROM structural.entity_container_degree($1)",
        1, argtypes, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        return;                 /* neutral priors already in place */

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple   tup = SPI_tuptable->vals[r];
        TupleDesc   td = SPI_tuptable->tupdesc;
        bool        isnull;
        bytea      *eid;
        double      icf;
        PcTokEntry *e;

        eid = DatumGetByteaPP(SPI_getbinval(tup, td, 1, &isnull));
        if (isnull || VARSIZE_ANY_EXHDR(eid) != 16) continue;
        icf = DatumGetFloat8(SPI_getbinval(tup, td, 2, &isnull));
        if (isnull) continue;
        e = (PcTokEntry *) hash_search(tok_h, VARDATA_ANY(eid), HASH_FIND, NULL);
        if (e != NULL)
            e->icf = icf;
    }
    SPI_freetuptable(SPI_tuptable);
}

Datum
pg_laplace_prompt_coherence(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    text          *prompt;
    MemoryContext  work, old;
    HASHCTL        ctl;
    HTAB          *syn_h, *tok_h, *type_h;
    /* Ords whose token NAMES a relation. A namer is the operator the prompt is
     * asking with, not the subject it is asking about — spec 37 OP3. The scan
     * already refuses to let a namer score itself (pc_scan_edges' namer_mask
     * exclusion); this carries the same principle to topic candidacy. */
    uint64         namer_ords = 0;
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
            /* p.language is computed by prompt_state and was DISCARDED here --
             * W14 G-B. The substrate resolved the prompt's language correctly at
             * fetch time and the elector could not see it, which is how a Polish
             * prompt gets answered from English senses while 87,985 Polish senses
             * sit in the substrate (W14 G-A, measured). Selecting both sides of
             * the comparison; neither is ever named in code. */
            "SELECT p.ord, p.id, s.synset_id, s.eff_mu::float8, "
            "       s.witnesses::bigint, p.language, "
            "       converse.word_language(s.sense_id) "
            "FROM converse.prompt_state($1) p "
            "CROSS JOIN LATERAL lexical.senses(p.id) s "
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
                int64     wit;
                int32     lang_agree;
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
                wit = DatumGetInt64(SPI_getbinval(tup, td, 5, &isnull));
                if (isnull) wit = 0;
                {
                    Datum  d_tl, d_sl;
                    bool   tl_null, sl_null;

                    d_tl = SPI_getbinval(tup, td, 6, &tl_null);
                    d_sl = SPI_getbinval(tup, td, 7, &sl_null);
                    /* Absence law: either side unattested leaves this 0. Only two
                     * ATTESTED languages can agree or disagree. */
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
                cands[n_cand].witnesses = wit;
                cands[n_cand].lang_agree = lang_agree;

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

                /* Word-level subjects too. IS_ANTONYM_OF on this seed hangs on
                 * word_id('hot'), not its synsets (0 syn-subject edges, 2 word-
                 * subject). Without the surface id in the membership set,
                 * naming IS_ANTONYM_OF still left rel_mass at 0 — GH #864. */
                if (hash_search(syn_h, VARDATA_ANY(tok), HASH_FIND, NULL) == NULL)
                {
                    old = MemoryContextSwitchTo(work);
                    if (syn_datums == NULL)
                        syn_datums = (Datum *) palloc(sizeof(Datum) * 4096);
                    if (n_syn < 4096)
                    {
                        bytea *cp = (bytea *) palloc(VARSIZE_ANY(tok));

                        memcpy(cp, tok, VARSIZE_ANY(tok));
                        syn_datums[n_syn++] = PointerGetDatum(cp);
                    }
                    MemoryContextSwitchTo(old);
                }
                pc_syn_add(syn_h, (const uint8 *) VARDATA_ANY(tok), ord, n_cand, work);

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

    /* The specificity prior. One batched call; see pc_load_icf for why the
     * graph alone cannot carry this key. */
    pc_load_icf(tok_h, work);

    /* ---- which of those types does a prompt token NAME? Canonical name and
     * rank come from the manifest in C; the name's object token becomes a
     * content id through the same hash the substrate uses everywhere, so the
     * match is exact id equality -- or an attested IS_LEMMA_OF edge, which is
     * the hop that makes an inflected prompt word ("parts") reach its lemma. ---- */
    {
        PcTypeEntry     *te;
        Datum           *nw_datums = NULL;
        int              n_nw = 0;
        PcTypeEntry    **nw_owner = NULL;

        old = MemoryContextSwitchTo(work);
        nw_datums = (Datum *) palloc(sizeof(Datum) * 1024);
        nw_owner = (PcTypeEntry **) palloc(sizeof(PcTypeEntry *) * 1024);
        MemoryContextSwitchTo(old);

        /* Iterate the WHOLE MANIFEST, not type_h.
         *
         * type_h is populated by pc_scan_edges from the relation types the
         * CANDIDATES ALREADY CARRY EDGES OF. Walking it here asked "which of the
         * types already present does a token name" — so naming was conditional on
         * the answer being present, which inverts the intent: naming exists to
         * SELECT which relation to traverse.
         *
         * MEASURED 2026-08-04: 'synonym of dog' fired (dog's candidates carry
         * IS_SYNONYM_OF) while 'The opposite of hot is' returned rel_type_id NULL
         * and rel_mass 0 on every token, because nothing in that prompt's
         * candidate set happened to carry an oppositional edge. The prompt named
         * the relation and the elector could not see it.
         *
         * The manifest is bounded and static — laplace_relation_table_count is
         * the relation count, not a function of graph degree — so this is a fixed
         * cost per call, not a scan. Types the candidates do not carry simply
         * find no rel_mass in the scan below; they are no longer invisible to it.
         *
         * A type first seen here is ENTERED into type_h so the rel_mass pass can
         * reach it. GH #864. */
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

            /* def->type_id in the static table is always zero; real ids live in
             * k_relation_type_id_cache and are filled by relation_ids_ensure().
             * HASH_ENTER on &def->type_id collapsed every relation into one
             * all-zero key, so naming set namer_ords (specificity=-1) while
             * the rel_mass query asked for type_id = 0x00… and returned nothing.
             * Measured post-a1ec6ed1: synonym-of-dog and opposite-of-hot both
             * demoted the namer and still left rel_mass=0 / rel_type_id NULL. */
            if (laplace_relation_type_id(name, &type_id) < 0)
                continue;
            te = (PcTypeEntry *) hash_search(type_h, &type_id, HASH_ENTER, &found);
            if (!found)
            {
                te->namer_mask = 0;
                te->named = false;
            }

            /* Pick the LONGEST underscore-delimited segment, not the last one.
             * `strrchr(name, '_') + 1` grabs the trailing preposition for every
             * *_OF / *_TO / *_ON / *_BY name: "IS_SYNONYM_OF" yields "OF",
             * which the len < 3 guard then discarded, leaving the relation
             * unnameable from a prompt that names it in so many words.
             *
             * MEASURED 2026-08-04 against engine/manifest/relation_types.toml:
             * 56 of 233 canonical names lost their concept that way, including
             * IS_A, IS_SYNONYM_OF, IS_ANTONYM_OF, IS_TRANSLATION_OF,
             * IS_INSTANCE_OF, MADE_UP_OF, RELATED_TO, CAPABLE_OF and
             * DEPENDS_ON — i.e. most of the relations a question actually
             * names. rel_mass was measured 0 and rel_type_id NULL on every
             * token of every probe, so the elector's only discriminating key
             * was inert and every election fell through to denote_mu, the
             * single-token scalar spec 37 OP3 forbids ranking on.
             *
             * The longest segment is the concept in every manifest name; ties
             * keep the earlier segment. Names with no segment >= 3 chars
             * (IS_A -> "IS"/"A") stay skipped: those are stopwords, and a
             * prompt token matching them would name nothing. */
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
                /* Floor 2, not 3. The 3 existed because "A" named IS_A and wrecked
                 * every election (W7:41-44) -- a ONE-character segment matches a
                 * token that is in essentially every prompt. Two characters does
                 * not have that property, and 3 excluded the single most damaging
                 * function word in the set.
                 *
                 * MEASURED 2026-08-05, after the OMW seed: "is" segments IS_A to
                 * IS/A, longest "IS" at 2, so it named nothing, stayed a full topic
                 * candidate, and won four probes outright -- glacier, france, hot
                 * and water all elected "ice", the Danish/Norwegian/Dutch synonym
                 * that OMW attaches to the surface "is" with 9 witnesses against
                 * English "is" with 1 (GH #867). Election correctness 5/6 -> 2/6.
                 *
                 * The demotion is precise, not a stopword list: longest-segment
                 * means "is" names IS_A and nothing else (IS_PART_OF yields PART,
                 * IS_SENSE_OF yields SENSE), and "a" stays excluded at length 1,
                 * which is what the original incident requires. It also fixes
                 * "parts of a car" for the same reason PART is a segment of
                 * HAS_PART -- the header at prompt_coherence.sql.in:45 records that
                 * probe failing because `parts` outmassed `car`. */
                if (best_seg == NULL || best_len < 2)
                    continue;           /* identifier fragment, not a concept */
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
                /* GH #864: concept-segment aliases. IS_ANTONYM_OF's longest
                 * segment is ANTONYM; prompts say "opposite". They share a
                 * WordNet synset but a full senses×tokens join hung the
                 * elector — keep a closed alias list of measured misses. */
                {
                    static const struct { const char *from; const char *to; } aliases[] = {
                        {"antonym", "opposite"},
                        /* HAS_DEFINITION's manifest segment is the noun. A user
                         * supplies the verb when invoking that relation as an
                         * operator ("define whale"). Without this content-id
                         * alias, DEFINE remains a topic candidate and its ICF
                         * prior beats WHALE even though it names the requested
                         * traversal. The alias only marks its role; it does not
                         * parse or dispatch an English shape. */
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
                te->named = true;
                te->namer_mask |= tk->ord_mask;
                namer_ords |= tk->ord_mask;
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
                            namer_ords |= tk->ord_mask;
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
                /* Select on the SHARE, the same scale-free quantity values[8]
                 * reports -- not on raw coherence. This comparator DROPS every
                 * non-winner for the ord (`if (!is_best) continue` below), so
                 * whatever it selects on is the real election; computing the
                 * share afterwards only describes a candidate raw sum already
                 * picked. Selecting on the sum and reporting the share meant the
                 * two disagreed, and the better candidate was gone before the
                 * share was ever consulted.
                 *
                 * Raw coherence is a TOTAL, and a total rewards degree. Glicko-2
                 * exists so a rating does not improve by playing more games;
                 * summing rank*eff across edges puts that back. MEASURED on this
                 * substrate for "what is a tree?": et carries 36,202 edges to
                 * tree's 296 and loses on every quality axis -- rank 0.239 vs
                 * 0.784, eff_mu 1.18e12 vs 1.26e12, rd 262 vs 232 -- yet wins the
                 * raw sum 122x on volume alone. Rank-weighting alone only brings
                 * it to a TIE (0.3 vs 0.3); the share separates them 3.3x. et is a
                 * player who beat 36,000 weak opponents, and the fold already said
                 * so in mu and rd.
                 *
                 * Ties still fall through to rel_mass, then denote_mu, then id --
                 * unchanged. */
                double o_share    = o->total_mass    > 0.0 ? o->coherence    / o->total_mass    : 0.0;
                double best_share = best->total_mass > 0.0 ? best->coherence / best->total_mass : 0.0;

                /* total_mass DESC between denote_mu and the id: on a
                 * foundation-only seed every DENOTES edge has ONE witness, so
                 * denote_mu is the same constant (~1169.73) for nearly every
                 * sense and the id memcmp was the real elector -- measured
                 * 2026-08-01: "What is a dog?" answered the derogatory sense
                 * ("a dull unattractive unpleasant girl") and "car" the
                 * railway carriage, both by id order. The sense that carries
                 * more witnessed mass is the dominant sense, and mass is
                 * fold-produced; the id stays only as the determinism anchor. */
                /* EVIDENCE BEFORE ADJUDICATED STRENGTH, third site. lexical.senses() and
                 * taxonomy.bubble_up() were corrected the same way on 2026-08-05; this
                 * comparator is a SEPARATE election and did not inherit either
                 * fix, because it re-ranks the rows lexical.senses() returns rather than
                 * taking its order. Correcting only the SQL left the C picking
                 * `urine` for the word "water" in "Water is made of" -- the exact
                 * row the SQL fix had just demoted.
                 *
                 * Placement: after the two PROMPT-LOCAL signals (share, rel_mass),
                 * which are joint evidence from this prompt and outrank any global
                 * count; before denote_mu, which is eff_mu = rating - 2*rd and
                 * therefore orders substantially by how little rd has shrunk (W16
                 * 3.2: mean |rating - neutral| 192.85 vs mean rd 262.24 across
                 * 447,145 rows -- 2*rd runs ~2.7x the signal). Two witnesses
                 * losing to one on 1.28 of eff_mu is reading the evidence
                 * backwards through the uncertainty term.
                 *
                 * Nothing is excluded and no threshold is introduced; the sort key
                 * order changes. Spec 37 L6 untouched. */
                /* LANGUAGE AGREEMENT, ranked above every global quantity.
                 *
                 * A sense in a language the prompt is not written in is the wrong
                 * sense however well attested it is -- W14 G-A measured
                 * "Kot spi na stole w domu" answered in English while 87,985
                 * Polish senses sat in the substrate. That is an ADDRESSING
                 * failure, and no amount of witness count fixes it, so agreement
                 * has to outrank witnesses rather than break ties under them.
                 *
                 * It sits BELOW share and rel_mass because those are joint
                 * evidence from this prompt -- a direct edge between two
                 * candidates outranks a provenance match.
                 *
                 * Tri-state, not boolean: an unattested language scores 0 and
                 * therefore beats a DISAGREEING one while losing to an agreeing
                 * one. `EXISTS`-style collapse of unattested into false is the
                 * exact defect the read laws name; a sense whose language nobody
                 * recorded has not been shown to be foreign.
                 *
                 * No language is named anywhere in this comparison. Both sides are
                 * ids the substrate resolved. */
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
                /* SPECIFICITY -- the rank key. A summed mass is meaningless on a
                 * high-degree id: an article is wired to everything, so its edges
                 * to the prompt are unremarkable, while a chess pawn's edge to
                 * chess is most of what it has. Measured on this substrate,
                 * "What is a pawn in chess?": total mass a = 4.28e16, chess =
                 * 5.28e14, pawn = 2.63e13 -- three orders between the article and
                 * the topic, which is exactly the correction. Not a floor or a
                 * knob: it is the share of a candidate's OWN witnessed mass that
                 * reaches the rest of the prompt, and it is scale-free, so it
                 * does not drift as seeds land. */
                /* ...and the containment prior, which is what makes this key
                 * DEFINED when the graph is silent. The share above needs a
                 * direct edge between two candidate sets to be non-zero, and at
                 * one hop the graph is sparse enough that it was 0 on every
                 * token of most prompts — which is what pushed the election onto
                 * ord DESC, an SVO word-order assumption in a substrate that is
                 * language- and modality-agnostic. Additive because both terms
                 * are already normalized to (0, 1]: share is the fraction of a
                 * candidate's own mass that reaches the prompt, icf is the
                 * inverse containment frequency of its token. Neither can
                 * annihilate the other, and ties still fall through to rel_mass
                 * exactly as before.
                 *
                 * WEIGHED AGAINST THE FOLD (GH #865). ICF alone elected
                 * `opposite` over `hot` (rarer wins) — 5/6 → 4/6. Witness sat
                 * alone fixed that pair but elected capital→great over France
                 * (more sense witnesses, far less total_mass). The prior that
                 * clears both is ICF × mass sat, where mass sat =
                 * total_mass/(total_mass+HALFMAX_MASS) and HALFMAX_MASS (~1e13)
                 * is the sparse-concept scale on this substrate — same class of
                 * saturation constant as foundry_witness_sat's half-max, not a
                 * per-probe knob. total_mass is the fold's own volume on the
                 * elected sense. share still leads when the graph speaks. */
                /* A NAMER IS NOT A TOPIC (2026-08-05).
                 *
                 * Spec 37 OP3: a token that names a relation selects WHICH
                 * relation to traverse. It is the operator the question is asked
                 * with, not the subject it is asked about. pc_scan_edges already
                 * acts on this — a type's namer_mask excludes the naming ord from
                 * receiving its own rel_mass — but the namer stayed a full topic
                 * candidate, and with every discriminating key at 0 it won on
                 * `ord DESC` whenever it sat later in the prompt.
                 *
                 * MEASURED: "Water is made of" elected `made`. Both tokens carry
                 * specificity 0, rel_mass 0, coherence 0, peers 0, so ord DESC
                 * decided and `made` is later. `made` is the longest segment of
                 * MADE_UP_OF, so it names a relation; `Water` names none. The
                 * question is what water is made OF, and the elector answered
                 * with the preposition's relation.
                 *
                 * -1 rather than exclusion: a namer stays in the result and stays
                 * orderable, so a prompt made entirely of relation words still
                 * elects something instead of returning nothing. It sorts below
                 * any non-namer, including one with no signal at all — which is
                 * correct, because "I have nothing to say about this token" still
                 * beats "this token is the verb".
                 *
                 * Deliberately NOT symmetric with the ICF prior below: this is a
                 * ROLE distinction the manifest already encodes, not a frequency
                 * heuristic. "The opposite of hot is" is the load-bearing case:
                 * the antonym→opposite alias + real type_id lookup make
                 * `opposite` a namer (specificity -1) and let hot take the
                 * IS_ANTONYM_OF rel_mass (GH #864). */
                {
                    double share = best->total_mass > 0.0
                                   ? best->coherence / best->total_mass
                                   : 0.0;
                    bool   is_namer = (best->ord >= 0 && best->ord <= PC_MAX_ORD)
                                      && ((namer_ords >> best->ord) & 1) != 0;
                    /* ~1e13: sparsely-wired concept mass on foundation seed. */
                    double mass_sat = best->total_mass <= 0.0
                                          ? 0.0
                                          : (best->total_mass
                                             / (best->total_mass + 1.0e13));
                    double icf = 1.0;
                    PcTokEntry *te = (PcTokEntry *) hash_search(
                        tok_h, best->tok, HASH_FIND, NULL);

                    if (te != NULL)
                        icf = te->icf;

                    values[8] = Float8GetDatum(
                        is_namer ? -1.0 : (share + icf * mass_sat));
                }
                /* The denominator, exposed raw. Computed here all along and
                 * discarded; returning it is what let the electors' first
                 * fallback premise (inverse own-mass) be refuted by
                 * measurement on the foundation-only seed -- see
                 * prompt_coherence.sql.in for the ledger. */
                values[9] = Float8GetDatum(best->total_mass);
                /* The evidence behind the elected sense, exposed for the same
                 * reason total_mass is: it is now a SORT KEY in the comparator
                 * above, and a key that cannot be read cannot be refuted. The
                 * comparator's first version was debugged by guessing at this
                 * value; that is the cost of an unobservable rank key. */
                values[10] = Int64GetDatum(best->witnesses);
                /* +1 agrees / 0 unattested / -1 disagrees. Exposed so the
                 * abstention is visible: 0 across every token means the substrate
                 * has no language for this prompt, which is a different statement
                 * from "the languages matched" and must not read as one. */
                values[11] = Int32GetDatum(best->lang_agree);

                tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
            }
        }
    }

    MemoryContextDelete(work);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
