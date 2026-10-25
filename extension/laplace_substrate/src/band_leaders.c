#include "postgres.h"
#include "miscadmin.h"
#include "funcapi.h"
#include "utils/array.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "perfcache_native.h"
#include "laplace/core/attestation_engine.h"
#include "laplace/core/sql_catalog.h"
#include "laplace/core/highway_table.h"
#include "laplace/core/relation_law.h"

typedef struct {
    hash128_t subject, type, object;
    int32 band;
    int64 rating, rd, witnesses;
} BandLeader;

static SPIPlanPtr edges_plan, labels_plan;

/* The same declared relation selectors over Native claim trajectories. Aliases
 * select an arena; they never replace a claim ID or rewrite its constituents. */
static SPIPlanPtr claim_leaders_plan;
PG_FUNCTION_INFO_V1(pg_laplace_claim_leaders);
Datum
pg_laplace_claim_leaders(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo, 0);
    ReturnSetInfo *result = (ReturnSetInfo *)fcinfo->resultinfo;
    if (PG_ARGISNULL(0)) return (Datum)0;
    int32 per = PG_ARGISNULL(1) ? 5 : PG_GETARG_INT32(1);
    if (per < 1) ereport(ERROR, (errmsg("claim leaders: page size must be positive")));
    Datum *bands; bool *missing; int count;
    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0), INT4OID, 4, true, TYPALIGN_INT, &bands, &missing, &count);
    if (!count) return (Datum)0;
    bool top = false;
    if (laplace_spi_connect(&top) != SPI_OK_CONNECT) elog(ERROR, "claim leaders: SPI connect failed");
    if (SPI_execute(laplace_sql_query_text("leaders.native_present"), true, 1) != SPI_OK_SELECT)
        elog(ERROR, "claim leaders: cannot identify installed storage");
    bool isnull;
    bool native = DatumGetBool(SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));
    SPI_freetuptable(SPI_tuptable);
    if (!native) { laplace_spi_finish(top); return (Datum)0; }
    if (!laplace_highway_ready()) elog(ERROR, "claim leaders: declared relation selectors are not configured");
    size_t capacity = laplace_relation_table_count + laplace_relation_alias_table_count;
    if (capacity > MaxAllocSize / sizeof(Datum)) elog(ERROR, "claim leaders: relation selectors exceed allocation capacity");
    Datum *surfaces = palloc(Max(capacity, 1) * sizeof(Datum));
    Oid types[] = {TEXTARRAYOID, INT8OID, INT4OID};
    if (!claim_leaders_plan) {
        claim_leaders_plan = SPI_prepare_cursor(laplace_sql_query_text("leaders.native_page"), 3, types,
            CURSOR_OPT_CUSTOM_PLAN | CURSOR_OPT_PARALLEL_OK);
        if (!claim_leaders_plan || SPI_keepplan(claim_leaders_plan) != 0)
            elog(ERROR, "claim leaders: cannot retain trajectory page plan");
    }
    for (int band = 0; band < count; band++) {
        CHECK_FOR_INTERRUPTS();
        if (missing[band]) continue;
        int32 selected = DatumGetInt32(bands[band]);
        int n = 0;
        for (size_t i = 0; i < capacity; i++) {
            const char *surface = i < laplace_relation_table_count
                ? laplace_relation_table[i].canonical
                : laplace_relation_alias_table[i - laplace_relation_table_count].surface;
            hash128_t type; uint8_t actual;
            if (laplace_relation_resolve(surface, &type) != 0
                || highway_table_relation_by_hash(&type, NULL, NULL, &actual) != 0 || actual != selected) continue;
            surfaces[n++] = CStringGetTextDatum(surface);
        }
        if (n) {
            ArrayType *array = construct_array(surfaces, n, TEXTOID, -1, false, TYPALIGN_INT);
            Datum args[] = {PointerGetDatum(array), Int64GetDatum(per), Int32GetDatum(selected)};
            if (SPI_execute_plan(claim_leaders_plan, args, NULL, true, 0) != SPI_OK_SELECT)
                elog(ERROR, "claim leaders: trajectory page failed");
            if (SPI_processed) spi_emit_all_rows(result);
            else {
                Datum values[6] = {bands[band], 0}; bool nulls[6] = {false,true,true,true,true,true};
                tuplestore_putvalues(result->setResult, result->setDesc, values, nulls);
            }
            SPI_freetuptable(SPI_tuptable);
            pfree(array);
        } else {
            Datum values[6] = {bands[band], 0}; bool nulls[6] = {false,true,true,true,true,true};
            tuplestore_putvalues(result->setResult, result->setDesc, values, nulls);
        }
        for (int i = 0; i < n; i++) pfree(DatumGetPointer(surfaces[i]));
    }
    laplace_spi_finish(top);
    return (Datum)0;
}

/* For each requested band, the indexed arena read returns its top `per` consensus
 * cells (subject, type, object, rating, RD, witnesses). Every subject and object
 * across all bands is then labelled in one display batch, and each cell is
 * emitted with its effective mu. */
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
        edges_plan = SPI_prepare_cursor(laplace_sql_query_text("leaders.arena"), 2, edge_types, CURSOR_OPT_PARALLEL_OK);
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
            labels_plan = SPI_prepare_cursor(laplace_sql_query_text("display.labels"), 1, label_types, CURSOR_OPT_PARALLEL_OK);
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
