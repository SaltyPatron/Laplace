/* Support and standing of the cells of one relation type over a batch of
 * subjects. The evidence read counts distinct supporting sources per
 * (subject, object) cell, optionally narrowed by filters; the consensus scan
 * then attaches each cell's one pooled rating and RD. Filters narrow the support
 * count only, never the standing. Cells without standing are dropped; each
 * subject's cells are ordered by rating - 2 RD and limited per subject. */
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
#include "observation_read.h"

PG_FUNCTION_INFO_V1(pg_laplace_attestation_response_batch);

typedef struct ResponseCell
{
    char key[32]; /* subject, object */
    int sources;
    int64 rating, rd;
    bool standing;
} ResponseCell;
typedef struct ResponseState { HTAB *cells; bool unary; } ResponseState;

typedef struct {
    HTAB *cells, *sources;
    bool unary, context_filtered;
    hash128_t context;
} ResponseSupport;

static void
response_support(int ordinal, int16 role, const LaplaceObservation *row, void *context)
{
    ResponseSupport *support = context;
    char key[48] = {0};
    bool found;
    (void) ordinal;
    if (role != 1 || row->object_null != support->unary) return;
    if (support->context_filtered && (row->context_null ||
        memcmp(&row->context, &support->context, sizeof(hash128_t)) != 0)) return;
    if (row->source_null) elog(ERROR, "attestation response: missing supporting source");
    memcpy(key, &row->subject, 16);
    if (!row->object_null) memcpy(key + 16, &row->object, 16);
    memcpy(key + 32, &row->source, 16);
    hash_search(support->sources, key, HASH_ENTER, &found);
    if (found) return;
    ResponseCell *cell = hash_search(support->cells, key, HASH_ENTER, &found);
    if (!found) { cell->sources = 0; cell->standing = false; }
    if (cell->sources == INT_MAX) elog(ERROR, "attestation response source count overflow");
    ++cell->sources;
}

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
    Datum type_datum = PointerGetDatum(relation);
    ArrayType *types = construct_array(&type_datum, 1, BYTEAOID, -1, false, TYPALIGN_INT);
    ResponseSupport support = {.cells=cells, .sources=sources, .unary=unary,
        .context_filtered=!PG_ARGISNULL(3)};
    if (support.context_filtered)
        support.context=datum_to_hash128(PG_GETARG_DATUM(3));
    laplace_observation_read(subjects, PG_ARGISNULL(2) ? NULL : PG_GETARG_ARRAYTYPE_P(2),
        types, 1, response_support, &support);
    MemoryContextSwitchTo(work);
    ResponseState state = {cells, unary};
    if (hash_get_num_entries(cells) > 0)
        laplace_consensus_candidates(subjects, NULL, types, response_standing, &state, NULL);
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
