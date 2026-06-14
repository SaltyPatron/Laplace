/*
 * consensus_fold_engine.c — the engine lane of the terminal fold
 * (HANDOFF-fold-lane "next rung").
 *
 * One call folds ONE staging partition: read the partition's epoch heaps with
 * the table AM (no SPI on the hot path — SPI costs ~1-2 µs/row, which alone
 * forfeits the ≥1M rel/s target at 10⁹ rows), sort in bounded-memory chunks,
 * k-way merge the sorted runs by (relation identity, epoch), fold each
 * relation's epochs in order through the SAME Glicko-2 math as the SQL-lane
 * aggregate (consensus_fold_math.h — drift breaks the regress parity pin),
 * and land the folded rows in consensus_next via the multi-insert path.
 *
 * Sort key: the 48-byte identity preimage (subject ‖ type ‖ object|zero16) —
 * equal bytes ⇔ equal consensus_id, so BLAKE3 runs once per OUTPUT relation,
 * not once per staged row. Runs spill to BufFile when the chunk arena exceeds
 * laplace.fold_mem_mb (default 8192); the merge then streams with small
 * per-run buffers, so the temp envelope is ≤ one partition's bytes — inside
 * the SQL lane's measured 2× envelope.
 *
 * SQL orchestrates, the engine computes: catalog discovery, DDL of
 * consensus_next, the PK build and the atomic swap all stay in
 * finish_consensus_fold (14_period_fold.sql.in). The PK build remains the
 * loud detector for partition-routing drift.
 *
 * Invariants (identical to the SQL lane, see consensus_fold_step.c):
 *   - THE PERIOD RULE: one fold = one rating period. Staging epochs are flush
 *     quanta (RAM bounds), not time — all of a relation's staged games merge
 *     into ONE Glicko period, like one tournament. Distinct deposits at
 *     distinct times remain distinct periods (they fold against the prior
 *     consensus as seeds). This keeps consensus values independent of
 *     LAPLACE_STAGING_THRESHOLD.
 *   - seed (existing consensus row) initializes the state; one φ per relation
 *     per fold — mixed φ raises;
 *   - the q/rem observation split of pg_laplace_glicko2_accumulate_games;
 *   - witness_count: seed restores it, the period adds its games;
 *   - last_observed_at = max over the seed and every staged row.
 */
#include "postgres.h"

#include "access/heapam.h"
#include "access/table.h"
#include "access/tableam.h"
#include "access/xact.h"
#include "catalog/namespace.h"
#include "executor/spi.h"
#include "executor/tuptable.h"
#include "fmgr.h"
#include "miscadmin.h"
#include "nodes/makefuncs.h"
#include "portability/instr_time.h"
#include "storage/buffile.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/guc.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/rel.h"
#include "utils/snapmgr.h"
#include "utils/timestamp.h"

#include "laplace/core/glicko2.h"
#include "laplace/core/hash128.h"
#include "laplace/core/mantissa.h"
#include "consensus_fold_math.h"

#define FOLD_IDENT_LEN     48      /* subject(16) ‖ type(16) ‖ object|zero(16) */
#define FOLD_ROW_FLAG_OBJ  0x01
#define FOLD_ROW_FLAG_SEED 0x02
#define FOLD_GAMES_BOUND   (INT64CONST(1) << 27)
#define FOLD_RUN_BUF_ROWS  1024    /* per-run merge read buffer               */
#define FOLD_OUT_SLOTS     1024    /* multi-insert batch                      */

typedef struct FoldRow
{
    uint8  ident[FOLD_IDENT_LEN];
    int32  epoch;                  /* 0 = seed                                */
    uint8  flags;
    int64  v1, v2, v3;             /* seed rating/rd/volatility               */
    int64  phi, games, sum_score;  /* partial fields; seed: games=witness_cnt */
    int64  last_ts;                /* TimestampTz raw                         */
} FoldRow;

typedef struct FoldRun
{
    /* exactly one of mem / file is live */
    FoldRow *mem;                  /* in-memory sorted run                    */
    BufFile *file;                 /* spilled sorted run                      */
    int64    remaining;            /* rows not yet handed to the merge        */
    FoldRow *buf;                  /* read buffer (spilled runs)              */
    int      buf_n, buf_pos;
    FoldRow  head;                 /* current head, valid when !exhausted     */
    bool     exhausted;
    int64    mem_pos;
} FoldRun;

/* ---- chunk sort -------------------------------------------------------- */

static int
fold_row_cmp(const void *pa, const void *pb)
{
    const FoldRow *a = (const FoldRow *) pa;
    const FoldRow *b = (const FoldRow *) pb;
    int c = memcmp(a->ident, b->ident, FOLD_IDENT_LEN);

    if (c != 0)
        return c;
    if (a->epoch != b->epoch)
        return a->epoch < b->epoch ? -1 : 1;
    return 0;
}

/* ---- run access -------------------------------------------------------- */

static void
fold_run_advance(FoldRun *run)
{
    if (run->remaining == 0)
    {
        run->exhausted = true;
        return;
    }
    if (run->mem != NULL)
    {
        run->head = run->mem[run->mem_pos++];
        run->remaining--;
        return;
    }
    if (run->buf_pos >= run->buf_n)
    {
        int want = (int) Min((int64) FOLD_RUN_BUF_ROWS, run->remaining);

        BufFileReadExact(run->file, run->buf, (size_t) want * sizeof(FoldRow));
        run->buf_n = want;
        run->buf_pos = 0;
    }
    run->head = run->buf[run->buf_pos++];
    run->remaining--;
}

/* binary min-heap of run indices keyed by each run's current head */
static int
fold_heap_cmp(FoldRun *runs, int a, int b)
{
    return fold_row_cmp(&runs[a].head, &runs[b].head);
}

static void
fold_heap_sift_down(FoldRun *runs, int *heap, int n, int i)
{
    for (;;)
    {
        int l = 2 * i + 1, r = 2 * i + 2, m = i;

        if (l < n && fold_heap_cmp(runs, heap[l], heap[m]) < 0) m = l;
        if (r < n && fold_heap_cmp(runs, heap[r], heap[m]) < 0) m = r;
        if (m == i)
            return;
        { int t = heap[i]; heap[i] = heap[m]; heap[m] = t; }
        i = m;
    }
}

/* ---- staging / seed readers -------------------------------------------- */

typedef struct FoldBuild
{
    MemoryContext cxt;             /* arena + runs live here                  */
    FoldRow      *arena;
    int64         arena_cap;       /* budget ceiling, rows                    */
    int64         arena_alloc;     /* currently allocated, rows (grows ×2)    */
    int64         arena_n;
    FoldRun      *runs;
    int           n_runs, runs_cap;
    int64         rows_total;
    bool          spilled;
} FoldBuild;

static void
fold_emit_run(FoldBuild *b)
{
    FoldRun *run;

    if (b->arena_n == 0)
        return;
    qsort(b->arena, (size_t) b->arena_n, sizeof(FoldRow), fold_row_cmp);

    if (b->n_runs == b->runs_cap)
    {
        b->runs_cap *= 2;
        b->runs = (FoldRun *) repalloc(b->runs, sizeof(FoldRun) * b->runs_cap);
    }
    run = &b->runs[b->n_runs++];
    memset(run, 0, sizeof(FoldRun));
    run->remaining = b->arena_n;

    if (!b->spilled && b->n_runs == 1)
    {
        /* keep the first run in memory; if a second run arrives this one is
         * spilled retroactively so total memory stays within the budget */
        MemoryContext old = MemoryContextSwitchTo(b->cxt);

        run->mem = b->arena;
        b->arena_alloc = Min(b->arena_alloc, b->arena_cap);
        b->arena = (FoldRow *) palloc_extended(
            sizeof(FoldRow) * (Size) b->arena_alloc, MCXT_ALLOC_HUGE);
        MemoryContextSwitchTo(old);
        b->arena_n = 0;
        return;
    }

    if (b->n_runs == 2 && b->runs[0].mem != NULL)
    {
        FoldRun *r0 = &b->runs[0];

        r0->file = BufFileCreateTemp(false);
        BufFileWrite(r0->file, r0->mem, (size_t) r0->remaining * sizeof(FoldRow));
        pfree(r0->mem);
        r0->mem = NULL;
        b->spilled = true;
    }

    run->file = BufFileCreateTemp(false);
    BufFileWrite(run->file, b->arena, (size_t) b->arena_n * sizeof(FoldRow));
    b->spilled = true;
    b->arena_n = 0;
}

static void
fold_build_add(FoldBuild *b, const FoldRow *row)
{
    if (b->arena_n == b->arena_alloc)
    {
        if (b->arena_alloc < b->arena_cap)
        {
            b->arena_alloc = Min(b->arena_alloc * 2, b->arena_cap);
            b->arena = (FoldRow *) repalloc_huge(
                b->arena, sizeof(FoldRow) * (Size) b->arena_alloc);
        }
        else
            fold_emit_run(b);
    }
    b->arena[b->arena_n++] = *row;
    b->rows_total++;
}

static void
fold_read_bytea16(Datum d, uint8 *out)
{
    bytea *v = DatumGetByteaPP(d);

    if (VARSIZE_ANY_EXHDR(v) != 16)
        ereport(ERROR,
                (errcode(ERRCODE_DATA_EXCEPTION),
                 errmsg("consensus_fold_partition: expected 16-byte id, got %d bytes",
                        (int) VARSIZE_ANY_EXHDR(v))));
    memcpy(out, VARDATA_ANY(v), 16);
}

static uint64
fold_hash_lo(const uint8 *id16)
{
    uint64 lo;

    memcpy(&lo, id16 + 8, 8);      /* bytea image: [0..7]=Hi, [8..15]=Lo, LE */
    return lo;
}

static int
fold_attno(Relation rel, const char *name)
{
    int attno = (int) get_attnum(RelationGetRelid(rel), name);

    if (attno <= 0)
        ereport(ERROR,
                (errcode(ERRCODE_UNDEFINED_COLUMN),
                 errmsg("consensus_fold_partition: relation \"%s\" has no column \"%s\"",
                        RelationGetRelationName(rel), name)));
    return attno;
}

static void
fold_scan_staging(FoldBuild *b, const char *relname, int32 epoch)
{
    RangeVar   *rv = makeRangeVar(NULL, pstrdup(relname), -1);
    Relation    rel = table_open(RangeVarGetRelid(rv, AccessShareLock, false),
                                 NoLock);
    TupleTableSlot *slot = table_slot_create(rel, NULL);
    TableScanDesc scan = table_beginscan(rel, GetActiveSnapshot(), 0, NULL);
    int         a_subject = fold_attno(rel, "subject_id");
    int         a_type    = fold_attno(rel, "type_id");
    int         a_object  = fold_attno(rel, "object_id");
    int         a_phi     = fold_attno(rel, "phi");
    int         a_games   = fold_attno(rel, "games");
    int         a_sum     = fold_attno(rel, "sum_score");
    int         a_ts      = fold_attno(rel, "last_ts");
    int64       nrows = 0;

    while (table_scan_getnextslot(scan, ForwardScanDirection, slot))
    {
        FoldRow row;

        slot_getallattrs(slot);

        memset(&row, 0, sizeof(row));
        fold_read_bytea16(slot->tts_values[a_subject - 1], row.ident);
        fold_read_bytea16(slot->tts_values[a_type - 1], row.ident + 16);
        if (!slot->tts_isnull[a_object - 1])
        {
            fold_read_bytea16(slot->tts_values[a_object - 1], row.ident + 32);
            row.flags |= FOLD_ROW_FLAG_OBJ;
        }
        row.epoch     = epoch;
        row.phi       = DatumGetInt64(slot->tts_values[a_phi - 1]);
        row.games     = DatumGetInt64(slot->tts_values[a_games - 1]);
        row.sum_score = DatumGetInt64(slot->tts_values[a_sum - 1]);
        row.last_ts   = (int64) DatumGetTimestampTz(slot->tts_values[a_ts - 1]);

        fold_build_add(b, &row);
        if ((++nrows & 0xFFFF) == 0)
            CHECK_FOR_INTERRUPTS();
    }

    table_endscan(scan);
    ExecDropSingleTupleTableSlot(slot);
    table_close(rel, NoLock);
}

static void
fold_scan_seeds(FoldBuild *b, int32 partition, int32 nparts)
{
    RangeVar   *rv = makeRangeVar(NULL, "consensus", -1);
    Relation    rel = table_open(RangeVarGetRelid(rv, AccessShareLock, false),
                                 NoLock);
    TupleTableSlot *slot = table_slot_create(rel, NULL);
    TableScanDesc scan = table_beginscan(rel, GetActiveSnapshot(), 0, NULL);
    int         a_subject = fold_attno(rel, "subject_id");
    int         a_type    = fold_attno(rel, "type_id");
    int         a_object  = fold_attno(rel, "object_id");
    int         a_rating  = fold_attno(rel, "rating");
    int         a_rd      = fold_attno(rel, "rd");
    int         a_vol     = fold_attno(rel, "volatility");
    int         a_wc      = fold_attno(rel, "witness_count");
    int         a_ts      = fold_attno(rel, "last_observed_at");
    int64       nrows = 0;

    while (table_scan_getnextslot(scan, ForwardScanDirection, slot))
    {
        FoldRow row;
        uint64  route;

        slot_getallattrs(slot);

        memset(&row, 0, sizeof(row));
        fold_read_bytea16(slot->tts_values[a_subject - 1], row.ident);
        fold_read_bytea16(slot->tts_values[a_type - 1], row.ident + 16);
        if (!slot->tts_isnull[a_object - 1])
        {
            fold_read_bytea16(slot->tts_values[a_object - 1], row.ident + 32);
            row.flags |= FOLD_ROW_FLAG_OBJ;
        }

        /* the routing twin of consensus_partition_of (SQL) and
         * ConsensusAccumulatingWriter.PartitionOf (C#) */
        route = (fold_hash_lo(row.ident)
                 ^ fold_hash_lo(row.ident + 16)
                 ^ fold_hash_lo(row.ident + 32)) % (uint64) nparts;
        if ((int32) route != partition)
            continue;

        row.epoch   = 0;
        row.flags  |= FOLD_ROW_FLAG_SEED;
        row.v1      = DatumGetInt64(slot->tts_values[a_rating - 1]);
        row.v2      = DatumGetInt64(slot->tts_values[a_rd - 1]);
        row.v3      = DatumGetInt64(slot->tts_values[a_vol - 1]);
        row.games   = DatumGetInt64(slot->tts_values[a_wc - 1]);
        row.last_ts = (int64) DatumGetTimestampTz(slot->tts_values[a_ts - 1]);

        fold_build_add(b, &row);
        if ((++nrows & 0xFFFF) == 0)
            CHECK_FOR_INTERRUPTS();
    }

    table_endscan(scan);
    ExecDropSingleTupleTableSlot(slot);
    table_close(rel, NoLock);
}

/* ---- output ------------------------------------------------------------ */

typedef struct FoldOut
{
    Relation         rel;
    TupleTableSlot **slots;
    int              n_filled;
    CommandId        cid;
    BulkInsertState  bistate;
    MemoryContext    batch_cxt;
} FoldOut;

static void
fold_out_flush(FoldOut *o)
{
    if (o->n_filled == 0)
        return;
    table_multi_insert(o->rel, o->slots, o->n_filled, o->cid,
                       TABLE_INSERT_SKIP_FSM, o->bistate);
    o->n_filled = 0;
    MemoryContextReset(o->batch_cxt);
}

static void
fold_out_emit(FoldOut *o,
              const uint8 *ident, bool has_object,
              const glicko2_state_t *st, int64 witness_count, int64 last_ts)
{
    TupleTableSlot *slot;
    MemoryContext   old;
    hash128_t       cid;

    if (o->n_filled == FOLD_OUT_SLOTS)
        fold_out_flush(o);

    slot = o->slots[o->n_filled];
    ExecClearTuple(slot);

    old = MemoryContextSwitchTo(o->batch_cxt);
    hash128_blake3(ident, FOLD_IDENT_LEN, &cid);

    {
        bytea *idv = (bytea *) palloc(VARHDRSZ + 16);
        bytea *sv  = (bytea *) palloc(VARHDRSZ + 16);
        bytea *tv  = (bytea *) palloc(VARHDRSZ + 16);

        SET_VARSIZE(idv, VARHDRSZ + 16);
        memcpy(VARDATA(idv), &cid, 16);
        SET_VARSIZE(sv, VARHDRSZ + 16);
        memcpy(VARDATA(sv), ident, 16);
        SET_VARSIZE(tv, VARHDRSZ + 16);
        memcpy(VARDATA(tv), ident + 16, 16);

        slot->tts_values[0] = PointerGetDatum(idv);
        slot->tts_values[1] = PointerGetDatum(sv);
        slot->tts_values[2] = PointerGetDatum(tv);
        slot->tts_isnull[0] = slot->tts_isnull[1] = slot->tts_isnull[2] = false;
        if (has_object)
        {
            bytea *ov = (bytea *) palloc(VARHDRSZ + 16);

            SET_VARSIZE(ov, VARHDRSZ + 16);
            memcpy(VARDATA(ov), ident + 32, 16);
            slot->tts_values[3] = PointerGetDatum(ov);
            slot->tts_isnull[3] = false;
        }
        else
        {
            slot->tts_values[3] = (Datum) 0;
            slot->tts_isnull[3] = true;
        }
    }
    MemoryContextSwitchTo(old);

    slot->tts_values[4] = Int64GetDatum(st->rating);
    slot->tts_values[5] = Int64GetDatum(st->rd);
    slot->tts_values[6] = Int64GetDatum(st->volatility);
    slot->tts_values[7] = Int64GetDatum(witness_count);
    slot->tts_values[8] = TimestampTzGetDatum((TimestampTz) last_ts);
    slot->tts_isnull[4] = slot->tts_isnull[5] = slot->tts_isnull[6] = false;
    slot->tts_isnull[7] = slot->tts_isnull[8] = false;

    ExecStoreVirtualTuple(slot);
    o->n_filled++;
}

/* ---- group fold state --------------------------------------------------- */

typedef struct FoldGroup
{
    bool   open;
    uint8  ident[FOLD_IDENT_LEN];
    bool   has_obj;
    bool   any;                    /* glicko state initialized               */
    glicko2_state_t st;
    int64  witness;
    int64  max_ts;

    bool   partial_open;           /* the fold's ONE period, accumulating      */
    int64  p_phi, p_games, p_sum;
} FoldGroup;

typedef struct FoldScratch
{
    MemoryContext          cxt;
    glicko2_observation_t *obs;
    int64                  cap;
} FoldScratch;

static void
fold_close_partial(FoldGroup *g, int64 tau, FoldScratch *scratch)
{
    if (!g->partial_open)
        return;
    if (!g->any)
    {
        glicko2_init(&g->st, CONSENSUS_FOLD_NEUTRAL_MU,
                     CONSENSUS_FOLD_INITIAL_RD,
                     CONSENSUS_FOLD_INITIAL_VOLATILITY);
        g->any = true;
    }
    if (g->p_games > scratch->cap)
    {
        scratch->cap = g->p_games * 2;
        scratch->obs = scratch->obs
            ? (glicko2_observation_t *)
              repalloc(scratch->obs, sizeof(*scratch->obs) * scratch->cap)
            : (glicko2_observation_t *)
              MemoryContextAlloc(scratch->cxt, sizeof(*scratch->obs) * scratch->cap);
    }
    consensus_fold_apply_partial(&g->st, g->p_phi, g->p_games, g->p_sum, tau,
                                 scratch->obs);
    g->witness += g->p_games;
    g->partial_open = false;
}

/* ---- the fold ----------------------------------------------------------- */

PG_FUNCTION_INFO_V1(pg_laplace_consensus_fold_partition);

Datum
pg_laplace_consensus_fold_partition(PG_FUNCTION_ARGS)
{
    ArrayType  *tables_arr = PG_GETARG_ARRAYTYPE_P(0);
    ArrayType  *epochs_arr = PG_GETARG_ARRAYTYPE_P(1);
    int32       partition  = PG_GETARG_INT32(2);
    int32       nparts     = PG_GETARG_INT32(3);
    bool        with_seeds = PG_GETARG_BOOL(4);

    Datum      *table_datums;
    bool       *table_nulls;
    int         n_tables;
    Datum      *epoch_datums;
    bool       *epoch_nulls;
    int         n_epochs;

    MemoryContext fold_cxt, oldcxt;
    FoldBuild   build;
    FoldOut     out;
    FoldGroup   g;
    FoldScratch scratch;
    int        *heap;
    int         heap_n;
    int64       tau;
    int64       budget_mb = 8192;
    int64       groups = 0;
    instr_time  t0, t1;
    int         i;

    INSTR_TIME_SET_CURRENT(t0);

    if (nparts <= 0 || partition < 0 || partition >= nparts)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("consensus_fold_partition: partition %d of %d out of range",
                        partition, nparts)));

    deconstruct_array(tables_arr, TEXTOID, -1, false, TYPALIGN_INT,
                      &table_datums, &table_nulls, &n_tables);
    deconstruct_array(epochs_arr, INT4OID, sizeof(int32), true, TYPALIGN_INT,
                      &epoch_datums, &epoch_nulls, &n_epochs);
    if (n_tables != n_epochs)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("consensus_fold_partition: %d tables but %d epochs",
                        n_tables, n_epochs)));

    /* tau once, from the same SQL constant the SQL lane folds with */
    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "consensus_fold_partition: SPI_connect failed");
    {
        bool isnull;
        int rc = SPI_execute("SELECT laplace.glicko2_tau()", true, 1);

        if (rc != SPI_OK_SELECT || SPI_processed != 1)
            elog(ERROR, "consensus_fold_partition: glicko2_tau() read failed");
        tau = DatumGetInt64(SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 1, &isnull));
    }
    SPI_finish();

    {
        const char *s = GetConfigOption("laplace.fold_mem_mb", true, false);

        if (s != NULL && *s != '\0')
        {
            budget_mb = strtol(s, NULL, 10);
            if (budget_mb < 1)
                budget_mb = 1;
        }
    }

    fold_cxt = AllocSetContextCreate(CurrentMemoryContext,
                                     "consensus_fold_engine",
                                     ALLOCSET_DEFAULT_SIZES);
    oldcxt = MemoryContextSwitchTo(fold_cxt);

    memset(&build, 0, sizeof(build));
    build.cxt = fold_cxt;
    build.arena_cap = Max((int64) 1024,
                          budget_mb * INT64CONST(1048576) / (int64) sizeof(FoldRow));
    build.arena_alloc = Min(build.arena_cap, (int64) 65536);
    build.arena = (FoldRow *) palloc_extended(
        sizeof(FoldRow) * (Size) build.arena_alloc, MCXT_ALLOC_HUGE);
    build.runs_cap = 16;
    build.runs = (FoldRun *) palloc(sizeof(FoldRun) * build.runs_cap);

    /* 1) staging heaps (epoch order only matters for locality — the sort key
     *    carries the epoch) */
    for (i = 0; i < n_tables; i++)
    {
        int32 epoch;

        if (table_nulls[i] || epoch_nulls[i])
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("consensus_fold_partition: NULL table/epoch at %d", i)));
        epoch = DatumGetInt32(epoch_datums[i]);
        if (epoch < 1)
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("consensus_fold_partition: epoch %d < 1 (0 is the seed)",
                            epoch)));
        fold_scan_staging(&build, TextDatumGetCString(table_datums[i]), epoch);
    }

    /* 2) existing consensus rows as epoch-0 seeds, routed to this partition */
    if (with_seeds)
        fold_scan_seeds(&build, partition, nparts);

    fold_emit_run(&build);

    /* 3) prime the merge */
    heap = (int *) palloc(sizeof(int) * Max(build.n_runs, 1));
    heap_n = 0;
    for (i = 0; i < build.n_runs; i++)
    {
        FoldRun *run = &build.runs[i];

        if (run->file != NULL)
        {
            if (BufFileSeek(run->file, 0, 0L, SEEK_SET) != 0)
                elog(ERROR, "consensus_fold_partition: BufFileSeek failed");
            run->buf = (FoldRow *) palloc(sizeof(FoldRow) * FOLD_RUN_BUF_ROWS);
            run->buf_n = run->buf_pos = 0;
        }
        fold_run_advance(run);
        if (!run->exhausted)
            heap[heap_n++] = i;
    }
    for (i = heap_n / 2 - 1; i >= 0; i--)
        fold_heap_sift_down(build.runs, heap, heap_n, i);

    /* 4) output relation (created by the SQL wrapper before this call) */
    memset(&out, 0, sizeof(out));
    {
        RangeVar *rv = makeRangeVar(NULL, "consensus_next", -1);

        out.rel = table_open(RangeVarGetRelid(rv, RowExclusiveLock, false),
                             NoLock);
        out.slots = (TupleTableSlot **) palloc(sizeof(TupleTableSlot *) * FOLD_OUT_SLOTS);
        for (i = 0; i < FOLD_OUT_SLOTS; i++)
            out.slots[i] = table_slot_create(out.rel, NULL);
        out.cid = GetCurrentCommandId(true);
        out.bistate = GetBulkInsertState();
        out.batch_cxt = AllocSetContextCreate(fold_cxt,
                                              "consensus_fold_engine batch",
                                              ALLOCSET_DEFAULT_SIZES);
    }

    /* 5) consume groups: equal identity = one relation; equal (identity,
     *    epoch) heads pre-merge exactly like the SQL lane's `partial` CTE;
     *    epochs then fold in order through the shared math. */
    memset(&g, 0, sizeof(g));
    memset(&scratch, 0, sizeof(scratch));
    scratch.cxt = fold_cxt;

    while (heap_n > 0)
    {
        FoldRun *run = &build.runs[heap[0]];
        FoldRow  row = run->head;

        fold_run_advance(run);
        if (run->exhausted)
        {
            heap[0] = heap[--heap_n];
            if (heap_n > 0)
                fold_heap_sift_down(build.runs, heap, heap_n, 0);
        }
        else
            fold_heap_sift_down(build.runs, heap, heap_n, 0);

        if (g.open && memcmp(g.ident, row.ident, FOLD_IDENT_LEN) != 0)
        {
            fold_close_partial(&g, tau, &scratch);
            fold_out_emit(&out, g.ident, g.has_obj, &g.st, g.witness, g.max_ts);
            groups++;
            g.open = false;
            if ((groups & 0xFFFF) == 0)
                CHECK_FOR_INTERRUPTS();
        }

        if (!g.open)
        {
            memcpy(g.ident, row.ident, FOLD_IDENT_LEN);
            g.has_obj = (row.flags & FOLD_ROW_FLAG_OBJ) != 0;
            g.any = false;
            g.witness = 0;
            g.max_ts = PG_INT64_MIN;
            g.open = true;
            g.partial_open = false;
        }

        if (row.last_ts > g.max_ts)
            g.max_ts = row.last_ts;

        if (row.flags & FOLD_ROW_FLAG_SEED)
        {
            if (g.any || g.partial_open)
                ereport(ERROR,
                        (errcode(ERRCODE_DATA_EXCEPTION),
                         errmsg("consensus_fold_partition: seed row arrived after "
                                "period partials (merge order violated)")));
            glicko2_init(&g.st, row.v1, row.v2, row.v3);
            g.witness = row.games;
            g.any = true;
            continue;
        }

        if (row.games <= 0)
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("consensus_fold_partition: games must be > 0 (got "
                            INT64_FORMAT ")", row.games)));

        /* the period rule: every staged row of this relation joins the ONE
         * period of this fold, regardless of which flush epoch journaled it */
        if (g.partial_open)
        {
            if (g.p_phi != row.phi)
                ereport(ERROR,
                        (errcode(ERRCODE_DATA_EXCEPTION),
                         errmsg("accumulation invariant violated: relation "
                                "observed with mixed phi within one period")));
            g.p_games += row.games;
            g.p_sum   += row.sum_score;
        }
        else
        {
            g.partial_open = true;
            g.p_phi   = row.phi;
            g.p_games = row.games;
            g.p_sum   = row.sum_score;
        }

        if (g.p_games > FOLD_GAMES_BOUND)
            ereport(ERROR,
                    (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                     errmsg("consensus_fold_partition: " INT64_FORMAT " games in "
                            "one period exceeds the per-relation bound", g.p_games)));
    }

    if (g.open)
    {
        fold_close_partial(&g, tau, &scratch);
        fold_out_emit(&out, g.ident, g.has_obj, &g.st, g.witness, g.max_ts);
        groups++;
    }

    fold_out_flush(&out);
    table_finish_bulk_insert(out.rel, TABLE_INSERT_SKIP_FSM);
    FreeBulkInsertState(out.bistate);

    for (i = 0; i < FOLD_OUT_SLOTS; i++)
        ExecDropSingleTupleTableSlot(out.slots[i]);
    for (i = 0; i < build.n_runs; i++)
        if (build.runs[i].file != NULL)
            BufFileClose(build.runs[i].file);
    table_close(out.rel, NoLock);

    INSTR_TIME_SET_CURRENT(t1);
    INSTR_TIME_SUBTRACT(t1, t0);
    ereport(LOG,
            (errmsg("terminal fold (engine): partition %d/%d: " INT64_FORMAT
                    " rows in, " INT64_FORMAT " relations out, %d run(s)%s, %.1f s",
                    partition + 1, nparts, build.rows_total, groups,
                    build.n_runs, build.spilled ? " (spilled)" : "",
                    INSTR_TIME_GET_DOUBLE(t1))));

    MemoryContextSwitchTo(oldcxt);
    MemoryContextDelete(fold_cxt);

    PG_RETURN_INT64(groups);
}

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
                if ((int32) (fold_hash_lo(s.ident) % (uint64) nparts) != partition)
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
                        if (ent->games > scratch.cap)
                        {
                            scratch.cap = ent->games * 2;
                            scratch.obs = scratch.obs
                                ? (glicko2_observation_t *)
                                  repalloc(scratch.obs,
                                           sizeof(*scratch.obs) * scratch.cap)
                                : (glicko2_observation_t *)
                                  MemoryContextAlloc(scratch.cxt,
                                                     sizeof(*scratch.obs) * scratch.cap);
                        }
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
                        if (e->games > scratch.cap)
                        {
                            scratch.cap = e->games * 2;
                            scratch.obs = scratch.obs
                                ? (glicko2_observation_t *)
                                  repalloc(scratch.obs,
                                           sizeof(*scratch.obs) * scratch.cap)
                                : (glicko2_observation_t *)
                                  MemoryContextAlloc(scratch.cxt,
                                                     sizeof(*scratch.obs) * scratch.cap);
                        }
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
