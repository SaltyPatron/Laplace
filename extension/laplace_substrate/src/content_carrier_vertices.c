#include "postgres.h"
#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "parser/parse_func.h"
#include "utils/hsearch.h"
#include "utils/syscache.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/trajectory.h"
#include "content_trajectory_read.h"
#include "spi_common.h"
#include "trajectory_wkb.h"

/* One batch enters the canonical partition/PK reader. Stored metadata is
 * returned verbatim; this surface does not normalize a corrupt stored count
 * into the expected one or substitute another physicality for a missing row. */
typedef struct CarrierRead
{
    ReturnSetInfo *result;
    HTAB *selected;
    Oid as_binary;
    Datum parent;
    int32 count;
} CarrierRead;

static int
emit_vertex(void *opaque, size_t ordinal, const hash128_t *child,
    size_t run_length, uint64_t flags)
{
    CarrierRead *read = opaque;
    if (ordinal > PG_INT64_MAX || run_length > PG_INT64_MAX)
        ereport(ERROR, (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
            errmsg("content carrier vertices: logical ordinal/run exceeds int8")));
    Datum id = hash128_to_datum(child);
    Datum values[6] = {read->parent, Int32GetDatum(read->count),
        Int64GetDatum((int64) ordinal), id, Int64GetDatum((int64) run_length),
        Int64GetDatum((int64) flags)};
    bool nulls[6] = {false, false, false, false, false, false};
    tuplestore_putvalues(read->result->setResult, read->result->setDesc, values, nulls);
    pfree(DatumGetPointer(id));
    CHECK_FOR_INTERRUPTS();
    return 0;
}

static void
receive_carrier(Datum physicality, Datum entity, int32 count,
    Datum geometry, void *opaque)
{
    CarrierRead *read = opaque;
    bytea *entity_bytes = DatumGetByteaPP(entity);
    bytea *physicality_bytes = DatumGetByteaPP(physicality);
    if (VARSIZE_ANY_EXHDR(entity_bytes) != sizeof(hash128_t) ||
        VARSIZE_ANY_EXHDR(physicality_bytes) != sizeof(hash128_t))
        ereport(ERROR, (errcode(ERRCODE_DATA_CORRUPTED),
            errmsg("content carrier vertices: stored identities must be 16 bytes")));
    hash128_t parent = datum_to_hash128(entity), expected;
    laplace_physicality_id_compute(parent, 1, &expected);
    if (!hash_search(read->selected, &parent, HASH_FIND, NULL) ||
        memcmp(&expected, VARDATA_ANY(physicality_bytes), sizeof(expected)) != 0)
        ereport(ERROR, (errcode(ERRCODE_DATA_CORRUPTED),
            errmsg("content carrier vertices: stored entity/physicality binding differs")));
    bytea *wkb = DatumGetByteaP(OidFunctionCall1(read->as_binary, geometry));
    uint32 vertices;
    const unsigned char *points = laplace_trajectory_wkb_points(wkb, &vertices);
    if ((Size) vertices > MaxAllocSize / (4 * sizeof(double)))
        elog(ERROR, "content carrier vertices: trajectory exceeds allocation capacity");
    double *aligned = palloc(Max((Size) vertices, 1) * 4 * sizeof(double));
    memcpy(aligned, points, (Size) vertices * 4 * sizeof(double));
    read->parent = entity;
    read->count = count;
    if (trajectory_visit_vertices(aligned, vertices, emit_vertex, read) != 0)
        elog(ERROR, "content carrier vertices: invalid trajectory");
    pfree(aligned);
    pfree(wkb);
}

PG_FUNCTION_INFO_V1(pg_laplace_content_carrier_vertices);
Datum
pg_laplace_content_carrier_vertices(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
            errmsg("content carrier vertices: both identity arrays are required")));
    ArrayType *entities = PG_GETARG_ARRAYTYPE_P(0);
    ArrayType *physicalities = PG_GETARG_ARRAYTYPE_P(1);
    if (ARR_NDIM(entities) > 1 || ARR_NDIM(physicalities) > 1 ||
        ARR_ELEMTYPE(entities) != BYTEAOID || ARR_ELEMTYPE(physicalities) != BYTEAOID)
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
            errmsg("content carrier vertices: expected one-dimensional bytea arrays")));
    Datum *ids, *placements;
    bool *id_nulls, *placement_nulls;
    int count, placement_count;
    deconstruct_array(entities, BYTEAOID, -1, false, TYPALIGN_INT, &ids, &id_nulls, &count);
    deconstruct_array(physicalities, BYTEAOID, -1, false, TYPALIGN_INT,
        &placements, &placement_nulls, &placement_count);
    if (count != placement_count)
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
            errmsg("content carrier vertices: identity array lengths differ")));
    HASHCTL control = {0};
    control.keysize = sizeof(hash128_t);
    control.entrysize = sizeof(hash128_t);
    control.hcxt = CurrentMemoryContext;
    CarrierRead read = {0};
    read.result = (ReturnSetInfo *) fcinfo->resultinfo;
    read.selected = hash_create("selected content carriers", Max(count, 1), &control,
        HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    for (int i = 0; i < count; ++i)
    {
        if (id_nulls[i] || placement_nulls[i] ||
            VARSIZE_ANY_EXHDR(DatumGetByteaPP(ids[i])) != sizeof(hash128_t) ||
            VARSIZE_ANY_EXHDR(DatumGetByteaPP(placements[i])) != sizeof(hash128_t))
            ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                errmsg("content carrier vertices: identities must be non-null 16-byte values")));
        hash128_t id = datum_to_hash128(ids[i]), expected;
        laplace_physicality_id_compute(id, 1, &expected);
        if (memcmp(&expected, VARDATA_ANY(DatumGetByteaPP(placements[i])), sizeof(expected)) != 0)
            ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                errmsg("content carrier vertices: selected physicality is not canonical Content")));
        hash_search(read.selected, &id, HASH_ENTER, NULL);
        CHECK_FOR_INTERRUPTS();
    }
    if (count > 0)
    {
        Oid geometry = GetSysCacheOid2(TYPENAMENSP, Anum_pg_type_oid,
            CStringGetDatum("geometry"), ObjectIdGetDatum(get_namespace_oid("public", false)));
        if (!OidIsValid(geometry)) elog(ERROR, "content carrier vertices: geometry type is missing");
        read.as_binary = LookupFuncName(list_make2(makeString("public"), makeString("st_asbinary")),
            1, &geometry, false);
        laplace_content_carrier_read(entities, receive_carrier, &read);
    }
    hash_destroy(read.selected);
    pfree(ids); pfree(id_nulls); pfree(placements); pfree(placement_nulls);
    return (Datum) 0;
}
