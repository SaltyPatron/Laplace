#include "postgres.h"
#include "miscadmin.h"
#include "funcapi.h"
#include "utils/array.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "laplace/core/attestation_engine.h"
#include "laplace/core/sql_catalog.h"

typedef struct {
    hash128_t subject, type, object;
    int32 band;
    int64 rating, rd, witnesses;
} BandLeader;

static SPIPlanPtr edges_plan, labels_plan;

/* Each arena uses the canonical indexed band operation. The selected pages are
 * retained as packed ids, then share one lazy display batch. There are no SQL
 * row-expansion joins or separate render calls for subjects and objects. */
PG_FUNCTION_INFO_V1(pg_laplace_band_leaders);
Datum
pg_laplace_band_leaders(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo, 0);
    ReturnSetInfo *r = (ReturnSetInfo *)fcinfo->resultinfo;
    if (PG_ARGISNULL(0)) return (Datum)0;
    int32 per = PG_ARGISNULL(1) ? 5 : PG_GETARG_INT32(1);
    if (per < 1) ereport(ERROR, (errmsg("band leaders: page size must be positive")));
    Datum *bands; bool *band_nulls; int n_bands;
    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0), INT4OID, 4, true, TYPALIGN_INT, &bands, &band_nulls, &n_bands);
    if (n_bands == 0) return (Datum)0;
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT) elog(ERROR, "band leaders: SPI connect failed");
    Oid edge_types[] = {INT4OID, INT8OID};
    if (!edges_plan) {
        edges_plan = SPI_prepare(laplace_sql_query_text("leaders.arena"), 2, edge_types);
        if (!edges_plan || SPI_keepplan(edges_plan) != 0) elog(ERROR, "band leaders: cannot retain arena read");
    }
    BandLeader *rows = NULL; Size count = 0;
    for (int band = 0; band < n_bands; ++band) {
        CHECK_FOR_INTERRUPTS();
        if (band_nulls[band]) continue;
        Datum args[] = {bands[band], Int64GetDatum(per)};
        if (SPI_execute_plan(edges_plan, args, NULL, true, 0) != SPI_OK_SELECT)
            elog(ERROR, "band leaders: arena read failed");
        Size total = count + SPI_processed;
        if (total > MaxAllocSize / sizeof(BandLeader) || total > MaxAllocSize / (2 * sizeof(Datum)))
            elog(ERROR, "band leaders: selected pages exceed allocation capacity");
        rows = rows ? repalloc(rows, Max(total, 1) * sizeof(BandLeader)) : palloc(Max(total, 1) * sizeof(BandLeader));
        for (uint64 i = 0; i < SPI_processed; ++i) {
            HeapTuple t = SPI_tuptable->vals[i]; TupleDesc d = SPI_tuptable->tupdesc; bool isnull;
            BandLeader *row = &rows[count++];
            row->band = DatumGetInt32(bands[band]);
            row->subject = datum_to_hash128(SPI_getbinval(t,d,1,&isnull));
            row->type = datum_to_hash128(SPI_getbinval(t,d,2,&isnull));
            row->object = datum_to_hash128(SPI_getbinval(t,d,3,&isnull));
            row->rating = DatumGetInt64(SPI_getbinval(t,d,4,&isnull));
            row->rd = DatumGetInt64(SPI_getbinval(t,d,5,&isnull));
            row->witnesses = DatumGetInt64(SPI_getbinval(t,d,6,&isnull));
        }
        SPI_freetuptable(SPI_tuptable);
    }
    if (count) {
        Datum *ids = palloc(2 * count * sizeof(Datum));
        for (Size i = 0; i < count; ++i) {
            ids[2*i] = hash128_to_datum(&rows[i].subject);
            ids[2*i+1] = hash128_to_datum(&rows[i].object);
        }
        Oid label_types[] = {BYTEAARRAYOID};
        if (!labels_plan) {
            labels_plan = SPI_prepare(laplace_sql_query_text("display.labels"), 1, label_types);
            if (!labels_plan || SPI_keepplan(labels_plan) != 0) elog(ERROR, "band leaders: cannot retain display read");
        }
        Datum args[] = {PointerGetDatum(construct_array(ids, 2 * count, BYTEAOID, -1, false, TYPALIGN_INT))};
        if (SPI_execute_plan(labels_plan,args,NULL,true,0) != SPI_OK_SELECT || SPI_processed != 2 * count)
            elog(ERROR, "band leaders: display lost selected positions");
        for (Size i = 0; i < count; ++i) {
            bool isnull;
            Datum subject = SPI_getbinval(SPI_tuptable->vals[2*i], SPI_tuptable->tupdesc, 2, &isnull);
            if (isnull) subject = CStringGetTextDatum("Entity");
            Datum object = SPI_getbinval(SPI_tuptable->vals[2*i+1], SPI_tuptable->tupdesc, 2, &isnull);
            if (isnull) object = CStringGetTextDatum("Entity");
            const char *relation = laplace_relation_canonical_for_type_id(&rows[i].type);
            Datum v[] = {Int32GetDatum(rows[i].band), ids[2*i], subject,
                CStringGetTextDatum(relation ? relation : "Relation"), ids[2*i+1], object,
                eff_mu_display_numeric(rows[i].rating, rows[i].rd), Int64GetDatum(rows[i].witnesses)};
            bool nulls[8] = {false};
            tuplestore_putvalues(r->setResult,r->setDesc,v,nulls);
        }
        SPI_freetuptable(SPI_tuptable);
    }
    laplace_spi_finish(spi_top);
    return (Datum)0;
}
