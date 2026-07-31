/*
 * steer_candidates.c — S7 STEER (docs/specs/36 §3).
 *
 *   steer_candidates(candidates bytea[], frontier bytea[], kappa float8)
 *     -> TABLE(candidate bytea, steer float8, edges bigint)
 *
 * THE STAGE THE SPEC SAYS NOBODY WROTE. S6 proposes continuations from sequence
 * (physicalities.trajectory); S7 re-ranks them by rated consensus mass reaching
 * the LIVE frontier S4 is standing on; S8 samples with RD as temperature.
 * "Sequence proposes, meaning steers" is already in converse_walk's header, but
 * that lane steers by a weight fixed BEFORE the walk begins — every token of a
 * gathered sentence carries its source sentence's constant (50 for the concept's
 * own gloss, 40 for containers of the topic word). A constant per source is not
 * steering; it is a prior. This makes it live: the frontier is passed in per
 * emitted token, so the same candidate scores differently depending on where the
 * walk has arrived.
 *
 * ONE SET-BASED READ, NOT A PROBE PER CANDIDATE. Both directions are needed
 * (consensus is stored once per (subject, type, object), and a candidate may sit
 * on either end), and a both-directions OR join is the shape the read-side law
 * sends to C. It is done here as two indexed arms UNION ALL'd in a single
 * prepared statement — |cands| x |frontier| is bounded by the beam, so this is
 * one round trip per emitted token, not |cands| of them.
 *
 * SCORING IS walk_score.h, SHARED WITH walk_branches. S7 must not invent a second
 * ranking: if steering used a different weight than retrieval, the two halves of
 * the forward pass would disagree about what the graph says, which is the exact
 * condition that lets generate() and chat() answer differently today.
 *
 * ZERO IS NOT ABSENCE. A candidate with no consensus edge to the frontier returns
 * steer 0.0 with edges 0 — unattested, distinct from a candidate whose edges sum
 * to zero (edges > 0). Collapsing those is the EXISTS error the substrate law
 * calls out, and the caller needs the distinction to decide between backing off
 * and dead-ending.
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

#include "laplace/core/hash128.h"
#include "spi_common.h"
#include "walk_score.h"

#include <math.h>       /* log1p — the coverage fold below */

PG_FUNCTION_INFO_V1(pg_laplace_steer_candidates);

typedef struct SteerEntry
{
    char   key[16];      /* candidate id — HASH_BLOBS keysize */
    double steer;        /* coverage-weighted: sum of log1p over DISTINCT frontier members */
    int64  edges;        /* raw edge count — 0 still means "no attested path", see header */
    int64  covered;      /* distinct frontier members reached */
} SteerEntry;

/*
 * One row per (candidate, frontier member). Edge scores accumulate here FIRST so that
 * many edges into a single frontier member fold into one member-level score before the
 * candidate total is formed. Without this stage `edges` is the only breadth signal, and
 * it counts EDGES not MEMBERS — five edges into `france` alone is indistinguishable from
 * edges spread across the whole frontier, which is the distinction the ranking needs.
 */
typedef struct PairEntry
{
    char   key[32];      /* candidate id ‖ frontier id */
    double score;
} PairEntry;

static SPIPlanPtr steer_plan = NULL;

/*
 * Prepared once per backend and kept (same idiom as generate_walk.c's edge_plan):
 * the un-prepared path re-planned this on every emitted token of every walk.
 */
static void
ensure_steer_plan(void)
{
    if (steer_plan == NULL)
    {
        Oid        argtypes[2] = { BYTEAARRAYOID, BYTEAARRAYOID };
        /*
         * The FRONTIER-side id comes back too (e.front). Summing edge scores without it
         * cannot tell "reaches one frontier member very strongly" from "sits where several
         * frontier members overlap" — and the second is what a composed prefix means. For
         * "the capital of france is", `Lyon` carries one strong edge to `france` while
         * `Paris` carries moderate edges to `france` AND `capital`; a bare sum lets Lyon win.
         */
        SPIPlanPtr plan = SPI_prepare(
            "SELECT e.cand, e.front, e.type_id, e.rating, e.rd, e.witness_count FROM ("
            "  SELECT c.subject_id AS cand, c.object_id AS front,"
            "         c.type_id, c.rating, c.rd, c.witness_count"
            "    FROM laplace.consensus c"
            "   WHERE c.subject_id = ANY($1) AND c.object_id = ANY($2)"
            "  UNION ALL"
            "  SELECT c.object_id, c.subject_id,"
            "         c.type_id, c.rating, c.rd, c.witness_count"
            "    FROM laplace.consensus c"
            "   WHERE c.object_id = ANY($1) AND c.subject_id = ANY($2)"
            ") e",
            2, argtypes);

        if (plan == NULL)
            elog(ERROR, "steer_candidates: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "steer_candidates: SPI_keepplan failed");
        steer_plan = plan;
    }
}

Datum
pg_laplace_steer_candidates(PG_FUNCTION_ARGS)
{
    ArrayType   *cand_arr, *front_arr;
    double       kappa;
    HASHCTL      hctl, phctl;
    HTAB        *acc, *pairs;
    Datum       *cand_elems;
    bool        *cand_nulls;
    int          n_cand;
    Datum        args[2];
    int          rc;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("steer_candidates: candidates and frontier must not be NULL")));

    cand_arr  = PG_GETARG_ARRAYTYPE_P(0);
    front_arr = PG_GETARG_ARRAYTYPE_P(1);

    if (ARR_NDIM(cand_arr) != 1 || ARR_ELEMTYPE(cand_arr) != BYTEAOID ||
        ARR_NDIM(front_arr) != 1 || ARR_ELEMTYPE(front_arr) != BYTEAOID)
        ereport(ERROR, (errmsg("steer_candidates: both arguments must be 1-D bytea arrays")));

    InitMaterializedSRF(fcinfo, 0);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "steer_candidates: SPI_connect failed");

    /* Same kappa the retrieval half uses; never a second constant. */
    kappa = PG_ARGISNULL(2) ? spi_fetch_rd_kappa() : PG_GETARG_FLOAT8(2);

    memset(&hctl, 0, sizeof(hctl));
    hctl.keysize   = 16;
    hctl.entrysize = sizeof(SteerEntry);
    hctl.hcxt      = CurrentMemoryContext;
    acc = hash_create("steer_candidates acc", 256, &hctl,
                      HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    memset(&phctl, 0, sizeof(phctl));
    phctl.keysize   = 32;          /* candidate ‖ frontier member */
    phctl.entrysize = sizeof(PairEntry);
    phctl.hcxt      = CurrentMemoryContext;
    pairs = hash_create("steer_candidates pairs", 1024, &phctl,
                        HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    /*
     * Seed every candidate at zero FIRST, so a candidate the frontier never
     * reaches is reported as steer 0.0 / edges 0 rather than omitted. An omitted
     * row would make "unreachable" indistinguishable from "not asked about".
     */
    deconstruct_array(cand_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &cand_elems, &cand_nulls, &n_cand);
    for (int i = 0; i < n_cand; i++)
    {
        bytea      *b;
        SteerEntry *e;
        bool        found;

        if (cand_nulls[i])
            continue;
        b = DatumGetByteaPP(cand_elems[i]);
        if (VARSIZE_ANY_EXHDR(b) != 16)
            ereport(ERROR, (errmsg("steer_candidates: candidate ids must be 16 bytes")));
        e = (SteerEntry *) hash_search(acc, VARDATA_ANY(b), HASH_ENTER, &found);
        if (!found)
        {
            e->steer = 0.0;
            e->edges = 0;
            e->covered = 0;
        }
    }

    ensure_steer_plan();
    args[0] = PointerGetDatum(cand_arr);
    args[1] = PointerGetDatum(front_arr);
    rc = SPI_execute_plan(steer_plan, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "steer_candidates: edge read failed: %s", SPI_result_code_string(rc));

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple   tup = SPI_tuptable->vals[r];
        TupleDesc   td  = SPI_tuptable->tupdesc;
        bool        isnull;
        bytea      *cb  = DatumGetByteaPP(SPI_getbinval(tup, td, 1, &isnull));
        bytea      *fb  = DatumGetByteaPP(SPI_getbinval(tup, td, 2, &isnull));
        bytea      *tb  = DatumGetByteaPP(SPI_getbinval(tup, td, 3, &isnull));
        int64       rating = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));
        int64       rd     = DatumGetInt64(SPI_getbinval(tup, td, 5, &isnull));
        int64       wit    = DatumGetInt64(SPI_getbinval(tup, td, 6, &isnull));
        hash128_t   type_id;
        SteerEntry *e;
        PairEntry  *p;
        char        pairkey[32];
        bool        found;

        if (VARSIZE_ANY_EXHDR(cb) != 16 || VARSIZE_ANY_EXHDR(fb) != 16
            || VARSIZE_ANY_EXHDR(tb) != 16)
            continue;

        memcpy(&type_id, VARDATA_ANY(tb), 16);
        e = (SteerEntry *) hash_search(acc, VARDATA_ANY(cb), HASH_FIND, &found);
        if (!found)
            continue;   /* not one of ours — the arms cannot produce this, but be strict */

        e->edges += 1;

        memcpy(pairkey, VARDATA_ANY(cb), 16);
        memcpy(pairkey + 16, VARDATA_ANY(fb), 16);
        p = (PairEntry *) hash_search(pairs, pairkey, HASH_ENTER, &found);
        if (!found)
        {
            p->score = 0.0;
            e->covered += 1;
        }
        p->score += walk_edge_score(type_id, rating, rd, wit, kappa);

        if ((r & 0xFFFF) == 0)
            CHECK_FOR_INTERRUPTS();
    }

    /*
     * Fold member scores into candidate totals. log1p is CONCAVE, so reaching a SECOND
     * frontier member is worth more than piling further mass onto one already reached —
     * that is the intersection preference stated numerically instead of asserted. Negative
     * member mass (refutation) passes through LINEARLY: a refuted candidate has to stay
     * able to sink, and running it through the same curve would flatten it toward zero.
     *
     * acc is keyed on 16 bytes and pairkey's first 16 ARE the candidate id, so HASH_BLOBS
     * compares exactly the candidate half — no separate key buffer needed.
     */
    {
        HASH_SEQ_STATUS pseq;
        PairEntry      *p;
        bool            hit;

        hash_seq_init(&pseq, pairs);
        while ((p = (PairEntry *) hash_seq_search(&pseq)) != NULL)
        {
            SteerEntry *ce = (SteerEntry *) hash_search(acc, p->key, HASH_FIND, &hit);

            if (!hit)
                continue;
            ce->steer += (p->score >= 0.0) ? log1p(p->score) : p->score;
        }
    }

    {
        ReturnSetInfo   *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
        HASH_SEQ_STATUS  seq;
        SteerEntry      *e;

        hash_seq_init(&seq, acc);
        while ((e = (SteerEntry *) hash_seq_search(&seq)) != NULL)
        {
            Datum  values[4];
            bool   nulls[4] = { false, false, false, false };
            bytea *idb = (bytea *) palloc(VARHDRSZ + 16);

            SET_VARSIZE(idb, VARHDRSZ + 16);
            memcpy(VARDATA(idb), e->key, 16);
            values[0] = PointerGetDatum(idb);
            values[1] = Float8GetDatum(e->steer);
            values[2] = Int64GetDatum(e->edges);
            values[3] = Int64GetDatum(e->covered);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        }
    }

    hash_destroy(pairs);
    hash_destroy(acc);
    SPI_finish();
    return (Datum) 0;
}
