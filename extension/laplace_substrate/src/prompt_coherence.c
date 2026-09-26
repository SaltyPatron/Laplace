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
#include "relation_symmetry.h"
#include "spi_nested.h"

PG_FUNCTION_INFO_V1(pg_laplace_prompt_coherence);

typedef struct PcCand
{
    int32   ord;
    uint8   tok[16];
    uint8   syn[16];
    double  denote_mu;
    int64   witnesses;         /* witnesses behind this sense, from bubble_up_batch */
    int32   lang_agree;        /* +1 agrees with the token's language, 0 unknown,
                                * -1 disagrees. Tri-state on purpose: an
                                * unattested language is NOT a mismatch. */
    double  coherence;
    double  total_mass;        /* ALL rated mass on this candidate, peers or not */
    int64   peer_count;
    double  rel_mass;
    uint8   rel_type[16];
    double  rel_type_rank;     /* rank of the recorded rel_type, for "best" */
    bool    has_rel_type;
} PcCand;

/* syn -> the candidate rows carrying it (a surface/sense pair can repeat across
 * tokens). */
typedef struct PcSynEntry
{
    uint8   key[16];
    int    *idx;
    int     n_idx;
    int     cap_idx;
} PcSynEntry;

typedef struct PcTokEntry
{
    uint8   key[16];
    int32  *ords;
    int     n_ords;
    int     cap_ords;
} PcTokEntry;

typedef struct PcTypeEntry
{
    uint8   key[16];
    int32  *namer_ords;        /* ords whose token names this relation type */
    int     n_namers;
    int     cap_namers;
    bool    named;
} PcTypeEntry;

typedef struct PcPeerKey
{
    int32 cand_idx;
    int32 peer_ord;
} PcPeerKey;

typedef struct PcPeerEntry
{
    PcPeerKey key;
} PcPeerEntry;

typedef struct PcOrdEntry
{
    int32 key;
} PcOrdEntry;

static void
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

static void
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

static bool
pc_type_has_other_namer(const PcTypeEntry *te, int32 ord)
{
    return te->n_namers > 1 ||
           (te->n_namers == 1 && te->namer_ords[0] != ord);
}

static void
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
pc_scan_edges(HTAB *syn_h, HTAB *type_h, HTAB *peer_h, PcCand *cands,
              ArrayType *syn_arr, bool forward)
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
    portal = SPI_cursor_open_with_args("pc_edges", sql, 1, argtypes, args, NULL, true, CURSOR_OPT_PARALLEL_OK);

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
                continue;

            /* Effective mu (rating - 2*rd) weighted by the relation's manifest rank. */
            eff = (double) (rating - 2 * rd);
            rank = (laplace_relation_lookup((const hash128_t *) VARDATA_ANY(tid), &def) == 0
                    && def != NULL) ? def->rank : 0.0;

            /* Total mass counts every cell incident to the candidate: outgoing
             * from the forward scan, incoming from the reverse. A cell between
             * two candidates belongs once to each endpoint, so coherence and its
             * denominator rest on the same directional support. */
            for (int i = 0; i < me->n_idx; i++)
                cands[me->idx[i]].total_mass += rank * eff;

            peer = (PcSynEntry *) hash_search(syn_h, VARDATA_ANY(other), HASH_FIND, NULL);
            if (peer == NULL)
                continue;               /* edge leaves the candidate set */

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
    HTAB          *syn_h, *tok_h, *type_h, *peer_h, *namer_h;
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

    /* ---- candidates: one statement, fetched once into C ---- */
    {
        Oid    argtypes[1] = { TEXTOID };
        Datum  args[1];
        Portal portal;

        args[0] = PointerGetDatum(prompt);
        portal = SPI_cursor_open_with_args(
            "pc_cand",
            /* bubble_up_batch runs once over every resolved id, with a NULL cut
             * that keeps the complete candidate field. The token's resolved
             * language and the sense's language are read for comparison by id.
             * Row order carries no weight: each row is appended and indexed by
             * token. */
            "WITH p AS MATERIALIZED (SELECT * FROM converse.prompt_state($1)), "
            "     b AS MATERIALIZED (SELECT * FROM taxonomy.bubble_up_batch("
            "         ARRAY(SELECT id FROM p WHERE id IS NOT NULL), ARRAY[]::bytea[], NULL)) "
            "SELECT p.ord, p.id, b.synset_id, b.base_eff_mu::float8, "
            "       b.witnesses::bigint, p.language, "
            "       converse.word_language(b.sense_id) "
            "FROM p JOIN b ON b.term = p.id "
            "WHERE p.id IS NOT NULL AND b.synset_id IS NOT NULL",
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
            }
            SPI_freetuptable(SPI_tuptable);
            CHECK_FOR_INTERRUPTS();
        }
        SPI_cursor_close(portal);
    }

    if (n_cand == 0)
    {
        MemoryContextDelete(work);
        laplace_spi_finish(spi_top);
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
                            pc_type_add_namers(nw_owner[i], tk, namer_h, work);
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
            Oid        argtypes[3] = { BYTEAARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID };
            Datum      args[3];
            ArrayType *type_arr;
            Portal     portal;

            old = MemoryContextSwitchTo(work);
            type_arr = construct_array(td_arr, n_td, BYTEAOID, -1, false, TYPALIGN_INT);
            MemoryContextSwitchTo(old);

            args[0] = PointerGetDatum(syn_arr);
            args[1] = PointerGetDatum(type_arr);
            args[2] = PointerGetDatum(laplace_symmetric_relation_types());
            portal = SPI_cursor_open_with_args(
                "pc_rel",
                /* A symmetric type is stored once in canonical orientation, so
                 * the candidate may be its object; both ends are read. Column 1
                 * is the candidate endpoint in both arms. Asymmetric types are
                 * read from the subject only. */
                "SELECT c.subject_id, c.type_id, c.rating, c.rd "
                "FROM laplace.consensus c "
                "WHERE c.subject_id = ANY($1) AND c.type_id = ANY($2) "
                "UNION ALL "
                "SELECT c.object_id, c.type_id, c.rating, c.rd "
                "FROM laplace.consensus c "
                "WHERE c.object_id = ANY($1) AND c.type_id = ANY($2) "
                "  AND c.object_id IS NOT NULL "
                "  AND c.subject_id <> c.object_id "
                "  AND c.type_id = ANY($3)",
                3, argtypes, args, NULL, true, CURSOR_OPT_PARALLEL_OK);

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
                SPI_freetuptable(SPI_tuptable);
                CHECK_FOR_INTERRUPTS();
            }
            SPI_cursor_close(portal);
        }
    }

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
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
