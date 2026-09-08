/* Scoped witness support over a batch of exact subjects and one relation.
 * Standing is explicitly pooled; source/context filters select support, never
 * silently claim a source-only Glicko refold. */
#include "postgres.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "consensus_scan.h"
#include "laplace/core/sql_catalog.h"
#include "spi_common.h"
#include "spi_nested.h"

PG_FUNCTION_INFO_V1(pg_laplace_attestation_response_batch);

typedef struct ResponseCell
{
    char key[32]; /* subject, object */
    int sources;
    int64 rating, rd;
    bool standing;
} ResponseCell;
typedef struct ResponseState { HTAB *cells; bool unary; } ResponseState;

static SPIPlanPtr support_plan;

static void
response_standing(const LaplaceConsensusRow *row, void *context)
{
    char key[32];
    ResponseState *state = context;
    if (row->object_is_null != state->unary) return;
    memcpy(key, &row->subject, 16);
    memcpy(key + 16, &row->object, 16);
    ResponseCell *cell = hash_search(state->cells, key, HASH_FIND, NULL);
    if (!cell) return;
    cell->rating = row->rating;
    cell->rd = row->rd;
    cell->standing = true;
}

static int
response_order(const void *left, const void *right)
{
    const ResponseCell *a = left, *b = right;
    int subject = memcmp(a->key, b->key, 16);
    if (subject) return subject;
    __int128 a_eff = (__int128) a->rating - 2 * (__int128) a->rd;
    __int128 b_eff = (__int128) b->rating - 2 * (__int128) b->rd;
    if (a_eff != b_eff) return a_eff > b_eff ? -1 : 1;
    return memcmp(a->key + 16, b->key + 16, 16);
}

static void
validate_array(ArrayType *array)
{
    Datum *values;
    bool *nulls;
    int count;
    if (ARR_NDIM(array) > 1 || ARR_ELEMTYPE(array) != BYTEAOID)
        elog(ERROR, "attestation response requires 1-D bytea arrays");
    deconstruct_array(array, BYTEAOID, -1, false, TYPALIGN_INT, &values, &nulls, &count);
    for (int i = 0; i < count; ++i)
        if (!nulls[i] && VARSIZE_ANY_EXHDR(DatumGetByteaPP(values[i])) != 16)
            elog(ERROR, "attestation response requires 16-byte identities");
    pfree(values);
    pfree(nulls);
}

Datum
pg_laplace_attestation_response_batch(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo, 0);
    ReturnSetInfo *result = (ReturnSetInfo *) fcinfo->resultinfo;
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1)) return (Datum) 0;
    int limit = PG_ARGISNULL(4) ? INT_MAX : PG_GETARG_INT32(4);
    bool unary = PG_NARGS() > 5 && !PG_ARGISNULL(5) && PG_GETARG_BOOL(5);
    if (limit <= 0) return (Datum) 0;
    ArrayType *subjects = PG_GETARG_ARRAYTYPE_P(0);
    bytea *relation = PG_GETARG_BYTEA_PP(1);
    validate_array(subjects);
    if (VARSIZE_ANY_EXHDR(relation) != 16 ||
        (!PG_ARGISNULL(3) && VARSIZE_ANY_EXHDR(PG_GETARG_BYTEA_PP(3)) != 16))
        elog(ERROR, "attestation response requires 16-byte identities");
    if (!PG_ARGISNULL(2)) validate_array(PG_GETARG_ARRAYTYPE_P(2));
    if (ArrayGetNItems(ARR_NDIM(subjects), ARR_DIMS(subjects)) == 0)
        return (Datum) 0;

    MemoryContext work = AllocSetContextCreate(CurrentMemoryContext,
        "attestation response batch", ALLOCSET_DEFAULT_SIZES);
    MemoryContext previous = MemoryContextSwitchTo(work);
    HASHCTL ctl = {0};
    ctl.keysize = 32;
    ctl.entrysize = sizeof(ResponseCell);
    ctl.hcxt = work;
    HTAB *cells = hash_create("attestation response cells", 128, &ctl,
                              HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    ctl.keysize = ctl.entrysize = 48;
    HTAB *sources = hash_create("attestation response sources", 256, &ctl,
                                HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "attestation response: SPI_connect failed");
    if (!support_plan)
    {
        Oid types[5] = {BYTEAARRAYOID, BYTEAOID, BYTEAARRAYOID, BYTEAOID, BOOLOID};
        SPIPlanPtr plan = SPI_prepare_cursor(laplace_sql_query_text("evidence.response_support"),
                                             5, types, CURSOR_OPT_PARALLEL_OK);
        if (!plan || SPI_keepplan(plan) != 0)
            elog(ERROR, "attestation response: preparing support query failed");
        support_plan = plan;
    }
    Datum args[5] = {PointerGetDatum(subjects), PointerGetDatum(relation),
                    PG_ARGISNULL(2) ? (Datum) 0 : PG_GETARG_DATUM(2),
                    PG_ARGISNULL(3) ? (Datum) 0 : PG_GETARG_DATUM(3), BoolGetDatum(unary)};
    char nulls[5] = {' ', ' ', PG_ARGISNULL(2) ? 'n' : ' ', PG_ARGISNULL(3) ? 'n' : ' ', ' '};
    Portal portal = SPI_cursor_open(NULL, support_plan, args, nulls, true);
    if (!portal) elog(ERROR, "attestation response: opening support cursor failed");
    for (;;)
    {
        SPI_cursor_fetch(portal, true, 4096);
        uint64 rows = SPI_processed;
        for (uint64 i = 0; i < rows; ++i)
        {
            char key[48];
            bool found;
            for (int col = 1; col <= 3; ++col)
            {
                bool isnull;
                Datum datum = SPI_getbinval(SPI_tuptable->vals[i], SPI_tuptable->tupdesc, col, &isnull);
                if (isnull && col == 2 && unary) { memset(key + 16, 0, 16); continue; }
                if (isnull || VARSIZE_ANY_EXHDR(DatumGetByteaPP(datum)) != 16)
                    elog(ERROR, "attestation response: malformed stored evidence identity");
                memcpy(key + (col - 1) * 16, VARDATA_ANY(DatumGetByteaPP(datum)), 16);
            }
            hash_search(sources, key, HASH_ENTER, &found);
            if (found) continue;
            ResponseCell *cell = hash_search(cells, key, HASH_ENTER, &found);
            if (!found) { cell->sources = 0; cell->standing = false; }
            if (cell->sources == INT_MAX) elog(ERROR, "attestation response source count overflow");
            ++cell->sources;
        }
        if (SPI_tuptable) { SPI_freetuptable(SPI_tuptable); SPI_tuptable = NULL; }
        if (!rows) break;
        CHECK_FOR_INTERRUPTS();
    }
    SPI_cursor_close(portal);
    MemoryContextSwitchTo(work);
    Datum type_datum = PointerGetDatum(relation);
    ArrayType *types = construct_array(&type_datum, 1, BYTEAOID, -1, false, TYPALIGN_INT);
    ResponseState state = {cells, unary};
    if (hash_get_num_entries(cells) > 0)
        laplace_consensus_scan(subjects, NULL, types, response_standing, &state, NULL);
    long capacity = hash_get_num_entries(cells);
    if ((uint64) capacity > MaxAllocSize / sizeof(ResponseCell))
        elog(ERROR, "attestation response exceeds allocation capacity");
    ResponseCell *ordered = palloc(sizeof(*ordered) * Max(capacity, 1));
    HASH_SEQ_STATUS sequence;
    ResponseCell *cell;
    long count = 0;
    hash_seq_init(&sequence, cells);
    while ((cell = hash_seq_search(&sequence)) != NULL)
        if (cell->standing) ordered[count++] = *cell;
    qsort(ordered, count, sizeof(*ordered), response_order);
    int emitted = 0;
    for (long i = 0; i < count; ++i)
    {
        cell = ordered + i;
        if (i == 0 || memcmp(cell->key, ordered[i-1].key, 16) != 0) emitted = 0;
        if (emitted >= limit) continue;
        ++emitted;
        Datum values[7] = {
            hash128_to_datum((const hash128_t *) cell->key),
            unary ? (Datum) 0 : hash128_to_datum((const hash128_t *) (cell->key + 16)),
            Float8GetDatum((double) ((__int128) cell->rating - 2 * (__int128) cell->rd) / 1e9),
            Int32GetDatum(cell->sources), Int64GetDatum(cell->rating), Int64GetDatum(cell->rd),
            CStringGetTextDatum("pooled")};
        bool output_nulls[7] = {false};
        output_nulls[1] = unary;
        tuplestore_putvalues(result->setResult, result->setDesc, values, output_nulls);
        pfree(DatumGetPointer(values[0]));
        if (!unary) pfree(DatumGetPointer(values[1]));
        pfree(DatumGetPointer(values[6]));
    }
    laplace_spi_finish(spi_top);
    MemoryContextSwitchTo(previous);
    MemoryContextDelete(work);
    return (Datum) 0;
}
