#include "postgres.h"
#include "miscadmin.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "trajectory_wkb.h"
#include "laplace/core/sql_catalog.h"
#include "laplace/core/trajectory.h"

/* The GIN projection elects manifests by membership. Only the canonical native
 * ordinal visitor may interpret their order: the index's deduplicated id array
 * cannot represent repeated SPACE, words, or RLE runs. Keep one rolling window
 * per manifest rather than expanding each vertex into SQL executor rows. */
static SPIPlanPtr adjacency_manifests_plan, adjacency_separators_plan;

static SPIPlanPtr
adjacency_plan(SPIPlanPtr *slot, const char *key, int n, Oid *types)
{
    if (*slot == NULL) {
        *slot = SPI_prepare_cursor(laplace_sql_query_text(key), n, types,
                                  CURSOR_OPT_GENERIC_PLAN);
        if (*slot == NULL || SPI_keepplan(*slot) != 0)
            elog(ERROR, "word_adjacency: cannot retain read plan");
    }
    return *slot;
}

static HTAB *
adjacency_ids(ArrayType *array, const char *name)
{
    HASHCTL ctl = {0};
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(hash128_t);
    HTAB *ids = hash_create(name, 128, &ctl, HASH_ELEM | HASH_BLOBS);
    if (array == NULL) return ids;
    Datum *values;
    bool *nulls;
    int n;
    deconstruct_array(array, BYTEAOID, -1, false, TYPALIGN_INT, &values, &nulls, &n);
    for (int i = 0; i < n; ++i) {
        if (nulls[i]) continue;
        bytea *id = DatumGetByteaPP(values[i]);
        if (VARSIZE_ANY_EXHDR(id) != sizeof(hash128_t))
            ereport(ERROR, (errmsg("word_adjacency: ids must be 16 bytes")));
        hash_search(ids, VARDATA_ANY(id), HASH_ENTER, NULL);
    }
    pfree(values);
    pfree(nulls);
    return ids;
}

typedef struct {
    HTAB *vocab, *separators;
    hash128_t *window;
    size_t width, gap;
    hash128_t witness;
    ReturnSetInfo *result;
} AdjacencyVisit;

static void
adjacency_emit(AdjacencyVisit *v, const hash128_t *subject,
               const hash128_t *separator, const hash128_t *object)
{
    if (hash_search(v->vocab, subject, HASH_FIND, NULL) == NULL) return;
    Datum values[] = {hash128_to_datum(subject), (Datum)0,
                      hash128_to_datum(object), hash128_to_datum(&v->witness)};
    bool nulls[] = {false, separator == NULL, false, false};
    if (separator) values[1] = hash128_to_datum(separator);
    tuplestore_putvalues(v->result->setResult, v->result->setDesc, values, nulls);
    for (int i = 0; i < 4; ++i) if (!nulls[i]) pfree(DatumGetPointer(values[i]));
}

static int
adjacency_visit(void *context, size_t ordinal, const hash128_t *id, uint64_t flags)
{
    AdjacencyVisit *v = context;
    (void)flags;
    size_t i = ordinal - 1;
    if ((i & 1023) == 0) CHECK_FOR_INTERRUPTS();
    v->window[i % v->width] = *id;
    if (hash_search(v->vocab, id, HASH_FIND, NULL) == NULL) return 0;
    if (i >= v->gap)
        adjacency_emit(v, &v->window[(i - v->gap) % v->width], NULL, id);
    if (i > v->gap) {
        const hash128_t *separator = &v->window[(i - 1) % v->width];
        if (hash_search(v->separators, separator, HASH_FIND, NULL) != NULL)
            adjacency_emit(v, &v->window[(i - v->gap - 1) % v->width], separator, id);
    }
    return 0;
}

PG_FUNCTION_INFO_V1(pg_laplace_word_adjacency);
Datum
pg_laplace_word_adjacency(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(2)) return (Datum)0;
    int gap = PG_GETARG_INT32(2);
    if (gap < 1) ereport(ERROR, (errmsg("word_adjacency: gap must be positive")));
    if (!PG_ARGISNULL(1) && PG_GETARG_INT32(1) < 0)
        ereport(ERROR, (errmsg("word_adjacency: witness limit must not be negative")));
    ArrayType *vocab = PG_GETARG_ARRAYTYPE_P(0);
    AdjacencyVisit visit = {0};
    visit.result = (ReturnSetInfo *)fcinfo->resultinfo;
    visit.gap = (size_t)gap;
    visit.vocab = adjacency_ids(vocab, "word adjacency vocabulary");
    if (hash_get_num_entries(visit.vocab) == 0 ||
        (!PG_ARGISNULL(1) && PG_GETARG_INT32(1) == 0)) return (Datum)0;
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "word_adjacency: SPI connect failed");
    int rc = SPI_execute_plan(adjacency_plan(&adjacency_separators_plan,
        "generation.separators", 0, NULL), NULL, NULL, true, 1);
    if (rc != SPI_OK_SELECT) elog(ERROR, "word_adjacency: separator read failed");
    ArrayType *separators = NULL;
    if (SPI_processed) {
        bool isnull;
        Datum value = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
        if (!isnull) separators = DatumGetArrayTypeP(value);
    }
    visit.separators = adjacency_ids(separators, "word adjacency separators");
    SPI_freetuptable(SPI_tuptable);
    Oid types[] = {BYTEAARRAYOID, INT4OID};
    Datum args[] = {PointerGetDatum(vocab), PG_ARGISNULL(1) ? (Datum)0 : PG_GETARG_DATUM(1)};
    char nulls[] = {' ', PG_ARGISNULL(1) ? 'n' : ' '};
    Portal cursor = SPI_cursor_open(NULL, adjacency_plan(&adjacency_manifests_plan,
        "generation.adjacency_manifests", 2, types), args, nulls, true);
    if (cursor == NULL) elog(ERROR, "word_adjacency: cannot open manifest read");
    for (;;) {
        CHECK_FOR_INTERRUPTS();
        SPI_cursor_fetch(cursor, true, 128);
        uint64 fetched = SPI_processed;
        for (uint64 row = 0; row < fetched; ++row) {
            bool isnull;
            HeapTuple tuple = SPI_tuptable->vals[row];
            TupleDesc desc = SPI_tuptable->tupdesc;
            Datum entity = SPI_getbinval(tuple, desc, 1, &isnull);
            if (isnull) continue;
            visit.witness = datum_to_hash128(entity);
            Datum manifest = SPI_getbinval(tuple, desc, 2, &isnull);
            if (isnull) continue;
            uint32 points;
            const unsigned char *raw = laplace_trajectory_wkb_points(DatumGetByteaPP(manifest), &points);
            if (points == 0) continue;
            double *aligned = palloc((Size)points * 4 * sizeof(double));
            memcpy(aligned, raw, (Size)points * 4 * sizeof(double));
            size_t count;
            if (trajectory_constituent_count(aligned, points, &count) != 0)
                elog(ERROR, "word_adjacency: invalid trajectory");
            visit.width = Min(count, visit.gap + 2);
            if (count > visit.gap) {
                if (visit.width > MaxAllocSize / sizeof(hash128_t))
                    elog(ERROR, "word_adjacency: ordinal window exceeds allocation capacity");
                visit.window = palloc(visit.width * sizeof(hash128_t));
                if (trajectory_visit_constituents(aligned, points, adjacency_visit, &visit) != 0)
                    elog(ERROR, "word_adjacency: constituent visit failed");
                pfree(visit.window);
            }
            pfree(aligned);
        }
        SPI_freetuptable(SPI_tuptable);
        if (fetched == 0) break;
    }
    SPI_cursor_close(cursor);
    laplace_spi_finish(spi_top);
    return (Datum)0;
}
