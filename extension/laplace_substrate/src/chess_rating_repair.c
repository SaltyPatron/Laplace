#include "postgres.h"
#include "miscadmin.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/timestamp.h"
#include "spi_common.h"
#include "laplace/core/sql_catalog.h"
#include "spi_nested.h"

/* Exceptional reconstruction of explicitly selected player cells. Evidence
 * selection and bulk persistence are PostgreSQL set operations; native C owns
 * event ordering, cell grouping and the canonical Glicko fold. Source Elo
 * testimony is never queried or rewritten as Laplace standing. */
typedef struct {
    hash128_t subject, type, object, id;
    bool object_null;
    int64 games, score, rating, rd, time;
} RepairEvidence;

typedef struct {
    RepairEvidence key;
    glicko2_state_t state;
} RepairCell;

static int
cell_compare(const RepairEvidence *a, const RepairEvidence *b)
{
    int c = memcmp(&a->subject, &b->subject, sizeof(hash128_t));
    if (c) return c;
    c = memcmp(&a->type, &b->type, sizeof(hash128_t));
    if (c) return c;
    if (a->object_null != b->object_null) return a->object_null ? 1 : -1;
    return a->object_null ? 0 : memcmp(&a->object, &b->object, sizeof(hash128_t));
}

static int
evidence_compare(const void *x, const void *y)
{
    const RepairEvidence *a = x, *b = y;
    int c = cell_compare(a, b);
    if (c) return c;
    if (a->time != b->time) return a->time < b->time ? -1 : 1;
    return memcmp(&a->id, &b->id, sizeof(hash128_t));
}

/* Replays retained evidence in observed order, one retained record per period.
 * This cannot recover original period grouping that was not recorded. */
static void
repair_cells(ArrayType *subjects, ArrayType *relations, bool normalize)
{
    bool spi_top = false;
    Datum *subject_values; bool *subject_nulls; int nsubjects;
    deconstruct_array(subjects, BYTEAOID, -1, false, TYPALIGN_INT,
                      &subject_values, &subject_nulls, &nsubjects);
    if (nsubjects == 0) ereport(ERROR, (errmsg("repair_player_ratings_batch: empty subject batch")));
    for (int i = 0; i < nsubjects; ++i)
        if (subject_nulls[i] || VARSIZE_ANY_EXHDR(DatumGetByteaPP(subject_values[i])) != 16)
            ereport(ERROR, (errmsg("repair_player_ratings_batch: invalid subject id")));
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT) elog(ERROR, "chess repair: SPI connect failed");
    Oid types[] = {BYTEAARRAYOID, BYTEAARRAYOID};
    Datum args[] = {PointerGetDatum(subjects), PointerGetDatum(relations)};
    uint64 corrected = 0;
    /* Only the explicit chess-player repair corrects source-Elo calibration.
     * General cell reconstruction retains the recorded opponent state. */
    if (normalize) {
        if (SPI_execute_with_args(laplace_sql_query_text("chess.repair_normalize"),
            2, types, args, NULL, false, 0) != SPI_OK_UPDATE)
            elog(ERROR, "chess repair: calibration correction failed");
        corrected = SPI_processed;
    }
    const char *read = laplace_sql_query_text("consensus.repair_evidence");
    SPIPlanPtr p = SPI_prepare(read, 2, types);
    if (p == NULL) elog(ERROR, "chess repair: cannot prepare evidence read");
    Portal cursor = SPI_cursor_open(NULL, p, args, NULL, false);
    if (cursor == NULL) elog(ERROR, "chess repair: cannot open evidence read");
    RepairEvidence *evidence = NULL;
    Size n = 0, cap = 0;
    for (;;) {
        CHECK_FOR_INTERRUPTS();
        SPI_cursor_fetch(cursor, true, 1024);
        uint64 fetched = SPI_processed;
        if (n + fetched > MaxAllocSize / sizeof(RepairEvidence))
            elog(ERROR, "chess repair: selected evidence exceeds batch allocation capacity");
        if (n + fetched > cap) {
            cap = Min(Max(n + fetched, cap ? cap * 2 : 1024), MaxAllocSize / sizeof(RepairEvidence));
            evidence = evidence ? repalloc(evidence, cap * sizeof(RepairEvidence)) : palloc(cap * sizeof(RepairEvidence));
        }
        for (uint64 i = 0; i < fetched; ++i) {
            HeapTuple t = SPI_tuptable->vals[i]; TupleDesc d = SPI_tuptable->tupdesc;
            bool isnull;
            RepairEvidence *e = &evidence[n++]; memset(e, 0, sizeof(*e));
            e->subject = datum_to_hash128(SPI_getbinval(t,d,1,&isnull));
            e->type = datum_to_hash128(SPI_getbinval(t,d,2,&isnull));
            Datum object = SPI_getbinval(t,d,3,&e->object_null);
            if (!e->object_null) e->object = datum_to_hash128(object);
            e->id = datum_to_hash128(SPI_getbinval(t,d,4,&isnull));
            e->games = DatumGetInt64(SPI_getbinval(t,d,5,&isnull));
            e->score = DatumGetInt64(SPI_getbinval(t,d,6,&isnull));
            e->rating = DatumGetInt64(SPI_getbinval(t,d,10,&isnull));
            e->rd = DatumGetInt64(SPI_getbinval(t,d,7,&isnull));
            e->time = DatumGetTimestampTz(SPI_getbinval(t,d,8,&isnull));
            if (!DatumGetBool(SPI_getbinval(t,d,9,&isnull)))
                ereport(ERROR, (errmsg("chess repair: transient calibration evidence is not replayable")));
        }
        SPI_freetuptable(SPI_tuptable);
        if (fetched == 0) break;
    }
    SPI_cursor_close(cursor); SPI_freeplan(p);
    if (n == 0) { laplace_spi_finish(spi_top); return; }
    qsort(evidence, n, sizeof(RepairEvidence), evidence_compare);
    if (n > MaxAllocSize / sizeof(RepairCell)) elog(ERROR, "chess repair: cell batch exceeds allocation capacity");
    RepairCell *cells = palloc(n * sizeof(RepairCell));
    Size ncells = 0;
    for (Size i = 0; i < n; ++i) {
        CHECK_FOR_INTERRUPTS();
        RepairEvidence *e = &evidence[i];
        if (ncells == 0 || cell_compare(&cells[ncells-1].key, e) != 0) {
            RepairCell *cell = &cells[ncells++]; cell->key = *e;
            glicko2_init(&cell->state, LAPLACE_GLICKO2_NEUTRAL_MU_FP, 350000000000LL, 60000000LL);
        }
        RepairCell *cell = &cells[ncells-1];
        if (glicko2_fold_uniform_period(&cell->state, e->rating,
            e->rd, e->games, e->score, LAPLACE_GLICKO2_DEFAULT_TAU, 0) != 0)
            ereport(ERROR, (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                errmsg("chess repair: chronological evidence cannot be represented by the native fold")));
        cell->key.time = e->time;
    }
    pfree(evidence);
    Datum *columns[8]; bool *object_nulls = palloc0(ncells * sizeof(bool));
    for (int j = 0; j < 8; ++j) columns[j] = palloc(ncells * sizeof(Datum));
    for (Size i = 0; i < ncells; ++i) {
        RepairCell *c = &cells[i];
        columns[0][i] = hash128_to_datum(&c->key.subject);
        columns[1][i] = hash128_to_datum(&c->key.type);
        object_nulls[i] = c->key.object_null;
        columns[2][i] = c->key.object_null ? (Datum)0 : hash128_to_datum(&c->key.object);
        columns[3][i] = Int64GetDatum(c->state.rating); columns[4][i] = Int64GetDatum(c->state.rd);
        columns[5][i] = Int64GetDatum(c->state.volatility); columns[6][i] = Int64GetDatum(c->state.observation_count);
        columns[7][i] = TimestampTzGetDatum(c->key.time);
    }
    Datum values[8]; Oid argtypes[8]; int dims[] = {(int)ncells}, lbs[] = {1};
    for (int j = 0; j < 8; ++j) {
        Oid element = j < 3 ? BYTEAOID : j == 7 ? TIMESTAMPTZOID : INT8OID;
        argtypes[j] = j < 3 ? BYTEAARRAYOID : j == 7 ? TIMESTAMPTZARRAYOID : INT8ARRAYOID;
        values[j] = PointerGetDatum(construct_md_array(columns[j], j == 2 ? object_nulls : NULL,
            1, dims, lbs, element, j < 3 ? -1 : 8, j >= 3, j < 3 ? TYPALIGN_INT : TYPALIGN_DOUBLE));
    }
    const char *write = laplace_sql_query_text("consensus.repair_write");
    if (SPI_execute_with_args(write, 8, argtypes, values, NULL, false, 0) != SPI_OK_INSERT)
        elog(ERROR, "chess repair: bulk standing write failed");
    ereport(NOTICE, (errmsg("chess repair: %d subjects, %zu evidence rows, %zu cells, %llu calibrations corrected, %llu standings updated",
        nsubjects,n,ncells,(unsigned long long)corrected,(unsigned long long)SPI_processed)));
    laplace_spi_finish(spi_top);
    return;
}

PG_FUNCTION_INFO_V1(pg_laplace_repair_player_ratings_batch);
Datum
pg_laplace_repair_player_ratings_batch(PG_FUNCTION_ARGS)
{
    for (int i = 0; i < 4; ++i)
        if (PG_ARGISNULL(i)) ereport(ERROR, (errmsg("repair_player_ratings_batch: NULL scope")));
    Datum types[] = {PG_GETARG_DATUM(0), PG_GETARG_DATUM(1)};
    repair_cells(PG_GETARG_ARRAYTYPE_P(3), construct_array(types, 2, BYTEAOID, -1, false, TYPALIGN_INT), true);
    PG_RETURN_VOID();
}

PG_FUNCTION_INFO_V1(pg_laplace_repair_consensus_cells_batch);
Datum
pg_laplace_repair_consensus_cells_batch(PG_FUNCTION_ARGS)
{
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1)) ereport(ERROR, (errmsg("repair_cells_batch: NULL scope")));
    ArrayType *types = PG_GETARG_ARRAYTYPE_P(1);
    if (ArrayGetNItems(ARR_NDIM(types), ARR_DIMS(types)) == 0)
        ereport(ERROR, (errmsg("repair_cells_batch: empty relation scope")));
    repair_cells(PG_GETARG_ARRAYTYPE_P(0), types, false);
    PG_RETURN_VOID();
}
