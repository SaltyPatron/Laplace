




































#include "postgres.h"

#include "access/heapam.h"
#include "access/table.h"
#include "access/tableam.h"
#include "access/xact.h"
#include "catalog/namespace.h"
#include "catalog/pg_inherits.h"
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
#include "consensus_fold_io.h"

#define FOLD_ROW_FLAG_OBJ  0x01
#define FOLD_ROW_FLAG_SEED 0x02
#define FOLD_RUN_BUF_ROWS  1024    

typedef struct FoldRow
{
    uint8  ident[FOLD_IDENT_LEN];
    int32  epoch;                  
    uint8  flags;
    int64  v1, v2, v3;             
    int64  phi, games, sum_score;  
    int64  last_ts;                
} FoldRow;

typedef struct FoldRun
{
    
    FoldRow *mem;                  
    BufFile *file;                 
    int64    remaining;            
    FoldRow *buf;                  
    int      buf_n, buf_pos;
    FoldRow  head;                 
    bool     exhausted;
    int64    mem_pos;
} FoldRun;



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



typedef struct FoldBuild
{
    MemoryContext cxt;             
    FoldRow      *arena;
    int64         arena_cap;       
    int64         arena_alloc;     
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
fold_scan_seed_rel(FoldBuild *b, Relation rel, int32 partition, int32 nparts)
{
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

        slot_getallattrs(slot);

        memset(&row, 0, sizeof(row));
        fold_read_bytea16(slot->tts_values[a_subject - 1], row.ident);
        fold_read_bytea16(slot->tts_values[a_type - 1], row.ident + 16);
        if (!slot->tts_isnull[a_object - 1])
        {
            fold_read_bytea16(slot->tts_values[a_object - 1], row.ident + 32);
            row.flags |= FOLD_ROW_FLAG_OBJ;
        }

        if (fold_route_identity(row.ident, nparts) != partition)
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
}

/* Seed priors from live consensus. The greenfield consensus is LIST/HASH-
 * partitioned — a partitioned parent has NO heap, so table_beginscan on it is
 * fatal. Iterate its leaf relations instead (find_all_inheritors is recursive:
 * hot relations' HASH sub-leaves are included; intermediate 'p' rels skipped).
 * A legacy plain consensus scans as itself. */
static void
fold_scan_seeds(FoldBuild *b, int32 partition, int32 nparts)
{
    RangeVar   *rv = makeRangeVar(NULL, "consensus", -1);
    Oid         parent = RangeVarGetRelid(rv, AccessShareLock, false);
    Relation    prel = table_open(parent, NoLock);

    if (prel->rd_rel->relkind == RELKIND_PARTITIONED_TABLE)
    {
        List     *oids = find_all_inheritors(parent, AccessShareLock, NULL);
        ListCell *lc;

        table_close(prel, NoLock);
        foreach(lc, oids)
        {
            Oid      oid = lfirst_oid(lc);
            Relation leaf = table_open(oid, NoLock);

            if (leaf->rd_rel->relkind == RELKIND_RELATION)
                fold_scan_seed_rel(b, leaf, partition, nparts);
            table_close(leaf, NoLock);
        }
        list_free(oids);
    }
    else
    {
        fold_scan_seed_rel(b, prel, partition, nparts);
        table_close(prel, NoLock);
    }
}



typedef struct FoldGroup
{
    bool   open;
    uint8  ident[FOLD_IDENT_LEN];
    bool   has_obj;
    bool   any;                    
    glicko2_state_t st;
    int64  witness;
    int64  max_ts;

    bool   partial_open;           
    int64  p_phi, p_games, p_sum;
} FoldGroup;

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
    fold_scratch_reserve(scratch, g->p_games);
    consensus_fold_apply_partial(&g->st, g->p_phi, g->p_games, g->p_sum, tau,
                                 scratch->obs);
    g->witness += g->p_games;
    g->partial_open = false;
}



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

    
    if (with_seeds)
        fold_scan_seeds(&build, partition, nparts);

    fold_emit_run(&build);

    
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
