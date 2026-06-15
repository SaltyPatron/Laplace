/*
 * consensus_fold_walks.c — the walk lane of the terminal fold (the trajectory
 * journal needs no sort). Split out of consensus_fold_engine.c; the staging/seed
 * reads, partition routing, the consensus_next writer and the Glicko scratch it
 * shares with the partition lane live in consensus_fold_io.h.
 *
 * Invariants are identical to the partition lane (see consensus_fold_engine.c and
 * consensus_fold_math.h); parity with the SQL lane is pinned by
 * tests/sql/consensus_fold.sql.
 */
#include "postgres.h"

#include "access/heapam.h"
#include "access/table.h"
#include "access/tableam.h"
#include "catalog/namespace.h"
#include "executor/spi.h"
#include "executor/tuptable.h"
#include "fmgr.h"
#include "miscadmin.h"
#include "nodes/makefuncs.h"
#include "portability/instr_time.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/rel.h"
#include "utils/snapmgr.h"
#include "utils/timestamp.h"

#include "laplace/core/glicko2.h"
#include "laplace/core/hash128.h"
#include "laplace/core/mantissa.h"
#include "consensus_fold_math.h"
#include "consensus_fold_io.h"
/* ═══ the walk fold: the trajectory journal needs no sort ══════════════════════
 *
 * The journal is subject-grouped by construction (one row per subject × type ×
 * layer-context, vertices = testimony-packed object references). The fold:
 * bucket the partition's walks by subject in memory, then per subject merge
 * its walks' vertices in a distinct-entities-bounded map — ONE Glicko period per relation
 * (the period rule) — and emit. Seeds (existing consensus rows) route by the
 * same subject hash and initialize their relation's state. The conservation
 * receipt is in the LOG line: games read == games folded.
 */

typedef struct WalkRow
{
    uint8   subject[16];
    uint8   type[16];
    uint8   context[16];           /* unused by identity; provenance only */
    int64   phi;
    int64   last_ts;
    int32   n_vertices;
    double *vertices;              /* 4 doubles per vertex, palloc'd        */
} WalkRow;

typedef struct WalkMergeKey
{
    uint8 type[16];
    uint8 object[16];
} WalkMergeKey;

typedef struct WalkMergeEntry
{
    WalkMergeKey key;              /* dynahash requires key first */
    int64        games;
    int64        sum_score;
} WalkMergeEntry;

typedef struct SeedRow
{
    uint8 ident[FOLD_IDENT_LEN];   /* subject ‖ type ‖ object|zero16 */
    uint8 has_object;
    int64 rating, rd, volatility, witness_count, last_ts;
} SeedRow;

static int
walk_row_cmp(const void *pa, const void *pb)
{
    const WalkRow *a = *(const WalkRow *const *) pa;
    const WalkRow *b = *(const WalkRow *const *) pb;

    return memcmp(a->subject, b->subject, 16);
}

static int
seed_row_cmp(const void *pa, const void *pb)
{
    const SeedRow *a = (const SeedRow *) pa;
    const SeedRow *b = (const SeedRow *) pb;

    return memcmp(a->ident, b->ident, FOLD_IDENT_LEN);
}

PG_FUNCTION_INFO_V1(pg_laplace_consensus_fold_walks);

Datum
pg_laplace_consensus_fold_walks(PG_FUNCTION_ARGS)
{
    int32 partition = PG_GETARG_INT32(0);
    int32 nparts    = PG_GETARG_INT32(1);
    bool  with_seeds = PG_GETARG_BOOL(2);

    MemoryContext fold_cxt, oldcxt;
    FoldOut     out;
    FoldScratch scratch;
    int64       tau;
    int64       groups = 0;
    int64       games_in = 0, games_folded = 0;
    int64       walks_in = 0;
    instr_time  t0, t1;
    char        relname[64];

    WalkRow   **walks = NULL;
    int64       n_walks = 0, walks_cap = 8192;

    INSTR_TIME_SET_CURRENT(t0);

    if (nparts <= 0 || partition < 0 || partition >= nparts)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("consensus_fold_walks: partition %d of %d out of range",
                        partition, nparts)));

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "consensus_fold_walks: SPI_connect failed");
    {
        bool isnull;
        int rc = SPI_execute("SELECT laplace.glicko2_tau()", true, 1);

        if (rc != SPI_OK_SELECT || SPI_processed != 1)
            elog(ERROR, "consensus_fold_walks: glicko2_tau() read failed");
        tau = DatumGetInt64(SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 1, &isnull));
    }
    SPI_finish();

    fold_cxt = AllocSetContextCreate(CurrentMemoryContext,
                                     "consensus_fold_walks",
                                     ALLOCSET_DEFAULT_SIZES);
    oldcxt = MemoryContextSwitchTo(fold_cxt);

    walks = (WalkRow **) palloc(sizeof(WalkRow *) * walks_cap);

    /* 1) read this partition's walk rows (already routed by the writer) */
    snprintf(relname, sizeof(relname), "consensus_walk_staging_%d", partition);
    {
        RangeVar   *rv = makeRangeVar(NULL, relname, -1);
        Relation    rel = table_open(RangeVarGetRelid(rv, AccessShareLock, false),
                                     NoLock);
        TupleTableSlot *slot = table_slot_create(rel, NULL);
        TableScanDesc scan = table_beginscan(rel, GetActiveSnapshot(), 0, NULL);
        int a_subject = fold_attno(rel, "subject_id");
        int a_type    = fold_attno(rel, "type_id");
        int a_ctx     = fold_attno(rel, "context_id");
        int a_phi     = fold_attno(rel, "phi");
        int a_nv      = fold_attno(rel, "n_vertices");
        int a_ts      = fold_attno(rel, "last_ts");
        int a_walk    = fold_attno(rel, "walk");

        while (table_scan_getnextslot(scan, ForwardScanDirection, slot))
        {
            WalkRow *w;
            bytea   *wb;
            Size     blen;

            slot_getallattrs(slot);

            w = (WalkRow *) palloc(sizeof(WalkRow));
            fold_read_bytea16(slot->tts_values[a_subject - 1], w->subject);
            fold_read_bytea16(slot->tts_values[a_type - 1], w->type);
            if (!slot->tts_isnull[a_ctx - 1])
                fold_read_bytea16(slot->tts_values[a_ctx - 1], w->context);
            else
                memset(w->context, 0, 16);
            w->phi     = DatumGetInt64(slot->tts_values[a_phi - 1]);
            w->last_ts = (int64) DatumGetTimestampTz(slot->tts_values[a_ts - 1]);
            w->n_vertices = DatumGetInt32(slot->tts_values[a_nv - 1]);

            wb = DatumGetByteaPP(slot->tts_values[a_walk - 1]);
            blen = VARSIZE_ANY_EXHDR(wb);
            if (blen != (Size) w->n_vertices * 4 * sizeof(double))
                ereport(ERROR,
                        (errcode(ERRCODE_DATA_EXCEPTION),
                         errmsg("consensus_fold_walks: walk byte length %zu != %d vertices",
                                blen, w->n_vertices)));
            w->vertices = (double *) palloc(blen);
            memcpy(w->vertices, VARDATA_ANY(wb), blen);

            if (n_walks == walks_cap)
            {
                walks_cap *= 2;
                walks = (WalkRow **) repalloc(walks, sizeof(WalkRow *) * walks_cap);
            }
            walks[n_walks++] = w;
            if ((n_walks & 0xFFF) == 0)
                CHECK_FOR_INTERRUPTS();
        }
        table_endscan(scan);
        ExecDropSingleTupleTableSlot(slot);
        table_close(rel, NoLock);
    }
    walks_in = n_walks;

    /* 2) subject-major order: a sort over walk ROWS (~thousands), never pairs */
    if (n_walks > 1)
        qsort(walks, (size_t) n_walks, sizeof(WalkRow *), walk_row_cmp);

    /* 3) output + scratch */
    memset(&out, 0, sizeof(out));
    {
        RangeVar *rv = makeRangeVar(NULL, "consensus_next", -1);
        int       i;

        out.rel = table_open(RangeVarGetRelid(rv, RowExclusiveLock, false),
                             NoLock);
        out.slots = (TupleTableSlot **) palloc(sizeof(TupleTableSlot *) * FOLD_OUT_SLOTS);
        for (i = 0; i < FOLD_OUT_SLOTS; i++)
            out.slots[i] = table_slot_create(out.rel, NULL);
        out.cid = GetCurrentCommandId(true);
        out.bistate = GetBulkInsertState();
        out.batch_cxt = AllocSetContextCreate(fold_cxt,
                                              "consensus_fold_walks batch",
                                              ALLOCSET_DEFAULT_SIZES);
    }
    memset(&scratch, 0, sizeof(scratch));
    scratch.cxt = fold_cxt;

    /* 3.5) seeds: EVERY consensus row whose subject routes to this partition —
     *      including subjects with no walks, which must pass through the swap
     *      unchanged (losing them would truncate the arena) */
    {
        SeedRow *seeds = NULL;
        int64    n_seeds = 0, seeds_cap = 8192;

        if (with_seeds)
        {
            RangeVar   *rv = makeRangeVar(NULL, "consensus", -1);
            Relation    rel = table_open(RangeVarGetRelid(rv, AccessShareLock, false),
                                         NoLock);
            TupleTableSlot *slot = table_slot_create(rel, NULL);
            TableScanDesc scan = table_beginscan(rel, GetActiveSnapshot(), 0, NULL);
            int a_subject = fold_attno(rel, "subject_id");
            int a_type    = fold_attno(rel, "type_id");
            int a_object  = fold_attno(rel, "object_id");
            int a_rating  = fold_attno(rel, "rating");
            int a_rd      = fold_attno(rel, "rd");
            int a_vol     = fold_attno(rel, "volatility");
            int a_wc      = fold_attno(rel, "witness_count");
            int a_ts      = fold_attno(rel, "last_observed_at");

            seeds = (SeedRow *) palloc(sizeof(SeedRow) * seeds_cap);
            while (table_scan_getnextslot(scan, ForwardScanDirection, slot))
            {
                SeedRow s;

                slot_getallattrs(slot);
                memset(&s, 0, sizeof(s));
                fold_read_bytea16(slot->tts_values[a_subject - 1], s.ident);
                if (fold_route_subject(s.ident, nparts) != partition)
                    continue;
                fold_read_bytea16(slot->tts_values[a_type - 1], s.ident + 16);
                if (!slot->tts_isnull[a_object - 1])
                {
                    fold_read_bytea16(slot->tts_values[a_object - 1], s.ident + 32);
                    s.has_object = 1;
                }
                s.rating        = DatumGetInt64(slot->tts_values[a_rating - 1]);
                s.rd            = DatumGetInt64(slot->tts_values[a_rd - 1]);
                s.volatility    = DatumGetInt64(slot->tts_values[a_vol - 1]);
                s.witness_count = DatumGetInt64(slot->tts_values[a_wc - 1]);
                s.last_ts       = (int64) DatumGetTimestampTz(slot->tts_values[a_ts - 1]);

                if (n_seeds == seeds_cap)
                {
                    seeds_cap *= 2;
                    seeds = (SeedRow *) repalloc(seeds, sizeof(SeedRow) * seeds_cap);
                }
                seeds[n_seeds++] = s;
                if ((n_seeds & 0xFFFF) == 0)
                    CHECK_FOR_INTERRUPTS();
            }
            table_endscan(scan);
            ExecDropSingleTupleTableSlot(slot);
            table_close(rel, NoLock);

            if (n_seeds > 1)
                qsort(seeds, (size_t) n_seeds, sizeof(SeedRow), seed_row_cmp);
        }

        /* 4) lockstep subject-major merge: walks fold their ONE period onto
         *    seeds where present, onto the neutral prior otherwise; seeds
         *    without walks pass through unchanged */
        {
            HASHCTL hctl;
            int64   iw = 0, is = 0;

            memset(&hctl, 0, sizeof(hctl));
            hctl.keysize = sizeof(WalkMergeKey);
            hctl.entrysize = sizeof(WalkMergeEntry);
            hctl.hcxt = fold_cxt;

            while (iw < n_walks || is < n_seeds)
            {
                uint8  subject[16];
                bool   have_walks;
                HTAB  *merge = NULL;
                int64  subj_phi = 0;
                int64  subj_max_ts = PG_INT64_MIN;

                /* the next subject in identity order across both streams */
                if (iw < n_walks && is < n_seeds)
                {
                    int c = memcmp(walks[iw]->subject, seeds[is].ident, 16);

                    memcpy(subject, c <= 0 ? walks[iw]->subject : seeds[is].ident, 16);
                    have_walks = (c <= 0);
                }
                else if (iw < n_walks)
                {
                    memcpy(subject, walks[iw]->subject, 16);
                    have_walks = true;
                }
                else
                {
                    memcpy(subject, seeds[is].ident, 16);
                    have_walks = false;
                }

                if (have_walks)
                {
                    int64 j = iw;

                    merge = hash_create("walk merge", 4096, &hctl,
                                        HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
                    subj_phi = walks[iw]->phi;
                    for (; j < n_walks && memcmp(walks[j]->subject, subject, 16) == 0; j++)
                    {
                        WalkRow *w = walks[j];

                        if (w->phi != subj_phi)
                            ereport(ERROR,
                                    (errcode(ERRCODE_DATA_EXCEPTION),
                                     errmsg("accumulation invariant violated: relation "
                                            "observed with mixed phi within one period")));
                        if (w->last_ts > subj_max_ts)
                            subj_max_ts = w->last_ts;

                        for (int v = 0; v < w->n_vertices; v++)
                        {
                            hash128_t oid;
                            int64     score;
                            uint16    g;
                            WalkMergeKey key;
                            WalkMergeEntry *ent;
                            bool      found;

                            if (laplace_testimony_unpack_vertex(
                                    w->vertices + (Size) v * 4,
                                    &oid, &score, &g, NULL) != 0)
                                ereport(ERROR,
                                        (errcode(ERRCODE_DATA_EXCEPTION),
                                         errmsg("consensus_fold_walks: vertex %d is "
                                                "not testimony-packed", v)));

                            memset(&key, 0, sizeof(key));
                            memcpy(key.type, w->type, 16);
                            memcpy(key.object, &oid, 16);
                            ent = (WalkMergeEntry *)
                                hash_search(merge, &key, HASH_ENTER, &found);
                            if (!found)
                            {
                                ent->games = 0;
                                ent->sum_score = 0;
                            }
                            ent->games += g;
                            ent->sum_score += score * (int64) g;
                            games_in += g;
                        }
                    }
                    /* free this subject's walks as consumed */
                    for (; iw < j; iw++)
                    {
                        pfree(walks[iw]->vertices);
                        pfree(walks[iw]);
                    }
                }

                /* this subject's seeds: fold-onto or pass-through */
                for (; is < n_seeds && memcmp(seeds[is].ident, subject, 16) == 0; is++)
                {
                    SeedRow *s = &seeds[is];
                    glicko2_state_t st;
                    WalkMergeEntry *ent = NULL;

                    glicko2_init(&st, s->rating, s->rd, s->volatility);
                    if (merge != NULL)
                    {
                        WalkMergeKey key;
                        bool found;

                        memset(&key, 0, sizeof(key));
                        memcpy(key.type, s->ident + 16, 16);
                        memcpy(key.object, s->ident + 32, 16);
                        ent = (WalkMergeEntry *)
                            hash_search(merge, &key, HASH_FIND, &found);
                        if (!found)
                            ent = NULL;
                    }
                    if (ent != NULL && ent->games > 0)
                    {
                        if (ent->games > FOLD_GAMES_BOUND)
                            ereport(ERROR,
                                    (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                                     errmsg("consensus_fold_walks: " INT64_FORMAT
                                            " games exceed the per-period bound",
                                            ent->games)));
                        fold_scratch_reserve(&scratch, ent->games);
                        consensus_fold_apply_partial(&st, subj_phi, ent->games,
                                                     ent->sum_score, tau, scratch.obs);
                        games_folded += ent->games;
                        fold_out_emit(&out, s->ident, s->has_object != 0, &st,
                                      s->witness_count + ent->games,
                                      Max(s->last_ts, subj_max_ts));
                        ent->games = -1;   /* consumed */
                    }
                    else
                    {
                        fold_out_emit(&out, s->ident, s->has_object != 0, &st,
                                      s->witness_count, s->last_ts);
                    }
                    groups++;
                }

                /* unseeded relations: the neutral prior gains the period */
                if (merge != NULL)
                {
                    HASH_SEQ_STATUS hs;
                    WalkMergeEntry *e;

                    hash_seq_init(&hs, merge);
                    while ((e = (WalkMergeEntry *) hash_seq_search(&hs)) != NULL)
                    {
                        glicko2_state_t st;
                        uint8 ident[FOLD_IDENT_LEN];
                        bool  has_obj;

                        if (e->games < 0)
                            continue;
                        if (e->games > FOLD_GAMES_BOUND)
                            ereport(ERROR,
                                    (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                                     errmsg("consensus_fold_walks: " INT64_FORMAT
                                            " games exceed the per-period bound",
                                            e->games)));
                        glicko2_init(&st, CONSENSUS_FOLD_NEUTRAL_MU,
                                     CONSENSUS_FOLD_INITIAL_RD,
                                     CONSENSUS_FOLD_INITIAL_VOLATILITY);
                        fold_scratch_reserve(&scratch, e->games);
                        consensus_fold_apply_partial(&st, subj_phi, e->games,
                                                     e->sum_score, tau, scratch.obs);
                        games_folded += e->games;

                        memcpy(ident, subject, 16);
                        memcpy(ident + 16, e->key.type, 16);
                        memcpy(ident + 32, e->key.object, 16);
                        /* the identity-preimage law carried into the vertex:
                         * a zero16 object id IS the NULL-object relation (the
                         * writer journals NULL-object partials as zero16) */
                        {
                            static const uint8 zero16[16] = {0};

                            has_obj = memcmp(e->key.object, zero16, 16) != 0;
                        }
                        fold_out_emit(&out, ident, has_obj, &st, e->games, subj_max_ts);
                        groups++;
                    }
                    hash_destroy(merge);
                }

                CHECK_FOR_INTERRUPTS();
            }
        }
    }

    fold_out_flush(&out);
    table_finish_bulk_insert(out.rel, TABLE_INSERT_SKIP_FSM);
    FreeBulkInsertState(out.bistate);
    {
        int i;

        for (i = 0; i < FOLD_OUT_SLOTS; i++)
            ExecDropSingleTupleTableSlot(out.slots[i]);
    }
    table_close(out.rel, NoLock);

    INSTR_TIME_SET_CURRENT(t1);
    INSTR_TIME_SUBTRACT(t1, t0);
    ereport(LOG,
            (errmsg("terminal fold (walks): partition %d/%d: " INT64_FORMAT
                    " walks in, " INT64_FORMAT " games in, " INT64_FORMAT
                    " games folded, " INT64_FORMAT " relations out, %.1f s"
                    "%s",
                    partition + 1, nparts, walks_in, games_in, games_folded,
                    groups, INSTR_TIME_GET_DOUBLE(t1),
                    games_in == games_folded
                        ? " — conservation holds"
                        : " — CONSERVATION VIOLATION")));
    if (games_in != games_folded)
        ereport(ERROR,
                (errcode(ERRCODE_DATA_EXCEPTION),
                 errmsg("consensus_fold_walks: games in (" INT64_FORMAT
                        ") != games folded (" INT64_FORMAT ")",
                        games_in, games_folded)));

    MemoryContextSwitchTo(oldcxt);
    MemoryContextDelete(fold_cxt);

    PG_RETURN_INT64(groups);
}
