/* Batch presence reads for ingest's converge step: which candidate ids already
 * stand as entity, physicality or attestation rows, so persist writes only the
 * novel set. Each returns a bitmap over input ordinals. */
#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "executor/spi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "catalog/pg_type.h"
#include "descent_probe.h"

/*
 * Shared validation and SPI wrapper for the id-only presence probes. A bit is
 * set only when the probe positively confirms the id; nothing is presumed
 * present. The entity probes differ in whether the tier-0 perfcache may answer
 * (resolvability) or only a stored row counts (stored-row presence).
 */
static Datum
presence_bitmap_datum(FunctionCallInfo fcinfo, const char* label,
                       int (*probe)(ArrayType*, uint8_t*, int))
{
    ArrayType*  ids_array;
    int         candidate_count;
    Size        bitmap_bytes;
    bytea*      result;
    uint8*      bm;

    if (PG_ARGISNULL(0))
        ereport(ERROR,
            (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
             errmsg("%s: ids array must not be NULL", label)));

    ids_array = PG_GETARG_ARRAYTYPE_P(0);

    if (ARR_NDIM(ids_array) > 1)
        ereport(ERROR,
            (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
             errmsg("%s: ids array must be 1-dimensional", label)));

    if (ARR_ELEMTYPE(ids_array) != BYTEAOID)
        ereport(ERROR,
            (errcode(ERRCODE_DATATYPE_MISMATCH),
             errmsg("%s: ids array element type must be bytea", label)));

    if (ARR_HASNULL(ids_array))
        ereport(ERROR,
            (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
             errmsg("%s: ids array must not contain NULL", label)));

    candidate_count = ARR_NDIM(ids_array) == 0
                      ? 0
                      : ArrayGetNItems(ARR_NDIM(ids_array), ARR_DIMS(ids_array));

    bitmap_bytes = (candidate_count + 7) / 8;

    result = (bytea*) palloc(VARHDRSZ + bitmap_bytes);
    SET_VARSIZE(result, VARHDRSZ + bitmap_bytes);
    if (bitmap_bytes > 0)
        memset(VARDATA(result), 0, bitmap_bytes);
    bm = (uint8*) VARDATA(result);

    if (candidate_count == 0)
        PG_RETURN_BYTEA_P(result);

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
            (errcode(ERRCODE_INTERNAL_ERROR),
             errmsg("%s: SPI_connect failed", label)));

    {
        int rc = probe(ids_array, bm, candidate_count);

        SPI_finish();
        if (rc != SPI_OK_SELECT)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: bulk probe failed (rc=%d)", label, rc)));
    }

    PG_RETURN_BYTEA_P(result);
}

PG_FUNCTION_INFO_V1(pg_laplace_entities_exist_bitmap);

Datum
pg_laplace_entities_exist_bitmap(PG_FUNCTION_ARGS)
{
    return presence_bitmap_datum(fcinfo, "entities_exist_bitmap",
                                  laplace_entities_present_bitmap);
}

PG_FUNCTION_INFO_V1(pg_laplace_tier_batch_existence_probe);

Datum
pg_laplace_tier_batch_existence_probe(PG_FUNCTION_ARGS)
{
    return presence_bitmap_datum(fcinfo, "tier_batch_existence_probe",
                                  laplace_tier_batch_existence_probe);
}

PG_FUNCTION_INFO_V1(pg_laplace_attestations_exist_bitmap);

/*
 * attestations_exist_bitmap(ids, type_ids, subject_ids): attestation ids alone
 * cannot select a partition, so each id travels with the subject it is routed
 * by (and its type). The three arrays are validated for shape, nulls and
 * length parity here so the probe can assume alignment.
 */
Datum
pg_laplace_attestations_exist_bitmap(PG_FUNCTION_ARGS)
{
    const char *label = "attestations_exist_bitmap";
    ArrayType  *arrays[3];
    int         counts[3];
    int         a;
    int         candidate_count;
    int         bitmap_bytes;
    bytea      *result;
    uint8      *bm;

    for (a = 0; a < 3; a++)
    {
        if (PG_ARGISNULL(a))
            ereport(ERROR,
                (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                 errmsg("%s: argument %d must not be NULL", label, a + 1)));
        arrays[a] = PG_GETARG_ARRAYTYPE_P(a);
        if (ARR_NDIM(arrays[a]) > 1)
            ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: argument %d must be 1-dimensional", label, a + 1)));
        if (ARR_ELEMTYPE(arrays[a]) != BYTEAOID)
            ereport(ERROR,
                (errcode(ERRCODE_DATATYPE_MISMATCH),
                 errmsg("%s: argument %d element type must be bytea", label, a + 1)));
        if (ARR_HASNULL(arrays[a]))
            ereport(ERROR,
                (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                 errmsg("%s: argument %d must not contain NULL", label, a + 1)));
        counts[a] = ARR_NDIM(arrays[a]) == 0
                    ? 0
                    : ArrayGetNItems(ARR_NDIM(arrays[a]), ARR_DIMS(arrays[a]));
    }
    if (counts[1] != counts[0] || counts[2] != counts[0])
        ereport(ERROR,
            (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
             errmsg("%s: ids/type_ids/subject_ids must be the same length (%d/%d/%d)",
                    label, counts[0], counts[1], counts[2])));

    candidate_count = counts[0];
    bitmap_bytes = (candidate_count + 7) / 8;

    result = (bytea*) palloc(VARHDRSZ + bitmap_bytes);
    SET_VARSIZE(result, VARHDRSZ + bitmap_bytes);
    if (bitmap_bytes > 0)
        memset(VARDATA(result), 0, bitmap_bytes);
    bm = (uint8*) VARDATA(result);

    if (candidate_count == 0)
        PG_RETURN_BYTEA_P(result);

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
            (errcode(ERRCODE_INTERNAL_ERROR),
             errmsg("%s: SPI_connect failed", label)));

    {
        int rc = laplace_attestations_present_bitmap_keyed(
            arrays[0], arrays[1], arrays[2], bm, candidate_count);

        SPI_finish();
        if (rc != SPI_OK_SELECT)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: bulk probe failed (rc=%d)", label, rc)));
    }

    PG_RETURN_BYTEA_P(result);
}

PG_FUNCTION_INFO_V1(pg_laplace_entities_stored_bitmap);

Datum
pg_laplace_entities_stored_bitmap(PG_FUNCTION_ARGS)
{
    return presence_bitmap_datum(fcinfo, "entities_stored_bitmap",
                                  laplace_entities_stored_bitmap);
}

PG_FUNCTION_INFO_V1(pg_laplace_entities_present_ordinals);

Datum
pg_laplace_entities_present_ordinals(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo, MAT_SRF_USE_EXPECTED_DESC | MAT_SRF_BLESS);
    ReturnSetInfo *result = (ReturnSetInfo *) fcinfo->resultinfo;
    if (PG_ARGISNULL(0)) PG_RETURN_NULL();
    ArrayType *ids = PG_GETARG_ARRAYTYPE_P(0);
    int count = ArrayGetNItems(ARR_NDIM(ids), ARR_DIMS(ids));
    uint8_t *bitmap = palloc0(((Size) count + 7) / 8);
    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "entities_present_ordinals: SPI connection failed");
    int rc = laplace_entities_stored_bitmap(ids, bitmap, count);
    SPI_finish();
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "entities_present_ordinals: identity scan failed (%d)", rc);
    for (int i = 0; i < count; ++i)
    {
        if ((bitmap[i >> 3] & (1u << (i & 7))) == 0) continue;
        Datum value = Int32GetDatum(i);
        bool isnull = false;
        tuplestore_putvalues(result->setResult, result->setDesc, &value, &isnull);
    }
    pfree(bitmap);
    PG_RETURN_NULL();
}

/*
 * Shared validation and SPI wrapper for presence probes that carry the target
 * table's partition key parallel to the ids (tiers for entities), so each id
 * prunes to one index descent instead of one per leaf. Same positive-only
 * semantics as presence_bitmap_datum.
 */
static Datum
presence_bitmap_datum_keyed(FunctionCallInfo fcinfo, const char* label,
                             Oid key_elem_oid, const char* key_desc,
                             int (*probe)(ArrayType*, ArrayType*, uint8_t*, int))
{
    ArrayType*  ids_array;
    ArrayType*  keys_array;
    int         candidate_count;
    int         key_count;
    int         bitmap_bytes;
    bytea*      result;
    uint8*      bm;

    if (PG_ARGISNULL(0))
        ereport(ERROR,
            (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
             errmsg("%s: ids array must not be NULL", label)));
    if (PG_ARGISNULL(1))
        ereport(ERROR,
            (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
             errmsg("%s: %s array must not be NULL", label, key_desc)));

    ids_array = PG_GETARG_ARRAYTYPE_P(0);
    keys_array = PG_GETARG_ARRAYTYPE_P(1);

    if (ARR_NDIM(ids_array) > 1 || ARR_NDIM(keys_array) > 1)
        ereport(ERROR,
            (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
             errmsg("%s: ids and %s arrays must be 1-dimensional", label, key_desc)));

    if (ARR_ELEMTYPE(ids_array) != BYTEAOID)
        ereport(ERROR,
            (errcode(ERRCODE_DATATYPE_MISMATCH),
             errmsg("%s: ids array element type must be bytea", label)));
    if (ARR_ELEMTYPE(keys_array) != key_elem_oid)
        ereport(ERROR,
            (errcode(ERRCODE_DATATYPE_MISMATCH),
             errmsg("%s: %s array element type mismatch", label, key_desc)));

    if (ARR_HASNULL(ids_array) || ARR_HASNULL(keys_array))
        ereport(ERROR,
            (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
             errmsg("%s: ids and %s arrays must not contain NULL", label, key_desc)));

    candidate_count = ARR_NDIM(ids_array) == 0
                      ? 0
                      : ArrayGetNItems(ARR_NDIM(ids_array), ARR_DIMS(ids_array));
    key_count = ARR_NDIM(keys_array) == 0
                ? 0
                : ArrayGetNItems(ARR_NDIM(keys_array), ARR_DIMS(keys_array));
    if (key_count != candidate_count)
        ereport(ERROR,
            (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
             errmsg("%s: ids and %s must be the same length (%d/%d)",
                    label, key_desc, candidate_count, key_count)));

    bitmap_bytes = (candidate_count + 7) / 8;

    result = (bytea*) palloc(VARHDRSZ + bitmap_bytes);
    SET_VARSIZE(result, VARHDRSZ + bitmap_bytes);
    if (bitmap_bytes > 0)
        memset(VARDATA(result), 0, bitmap_bytes);
    bm = (uint8*) VARDATA(result);

    if (candidate_count == 0)
        PG_RETURN_BYTEA_P(result);

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
            (errcode(ERRCODE_INTERNAL_ERROR),
             errmsg("%s: SPI_connect failed", label)));

    {
        int rc = probe(ids_array, keys_array, bm, candidate_count);

        SPI_finish();
        if (rc != SPI_OK_SELECT)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: bulk probe failed (rc=%d)", label, rc)));
    }

    PG_RETURN_BYTEA_P(result);
}

PG_FUNCTION_INFO_V1(pg_laplace_entities_stored_bitmap_keyed);

Datum
pg_laplace_entities_stored_bitmap_keyed(PG_FUNCTION_ARGS)
{
    return presence_bitmap_datum_keyed(fcinfo, "entities_stored_bitmap",
                                        INT2OID, "tiers",
                                        laplace_entities_stored_bitmap_keyed);
}

PG_FUNCTION_INFO_V1(pg_laplace_tier_batch_existence_probe_keyed);

Datum
pg_laplace_tier_batch_existence_probe_keyed(PG_FUNCTION_ARGS)
{
    return presence_bitmap_datum_keyed(fcinfo, "tier_batch_existence_probe",
                                        INT2OID, "tiers",
                                        laplace_tier_batch_existence_probe_keyed);
}

/* physicalities is HASH(id): the id alone routes the probe. */

PG_FUNCTION_INFO_V1(pg_laplace_physicalities_exist_bitmap);

Datum
pg_laplace_physicalities_exist_bitmap(PG_FUNCTION_ARGS)
{
    return presence_bitmap_datum(fcinfo, "physicalities_exist_bitmap",
                                  laplace_physicalities_present_bitmap);
}

PG_FUNCTION_INFO_V1(pg_laplace_content_descent_bitmap);

/*
 * content_descent_bitmap(ids, parents): the same positive-only resolvability
 * probe as tier_batch_existence_probe over the flat id set. `parents` is
 * validated for shape and length but does not prune; no id is presumed present
 * because an ancestor is.
 */
Datum
pg_laplace_content_descent_bitmap(PG_FUNCTION_ARGS)
{
    ArrayType*  ids_array;
    ArrayType*  par_array;
    int         candidate_count;
    int         parent_count;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR,
            (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
             errmsg("content_descent_bitmap: ids and parents must not be NULL")));

    ids_array = PG_GETARG_ARRAYTYPE_P(0);
    par_array = PG_GETARG_ARRAYTYPE_P(1);

    if (ARR_NDIM(par_array) > 1)
        ereport(ERROR,
            (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
             errmsg("content_descent_bitmap: parents array must be 1-dimensional")));
    if (ARR_ELEMTYPE(par_array) != INT4OID)
        ereport(ERROR,
            (errcode(ERRCODE_DATATYPE_MISMATCH),
             errmsg("content_descent_bitmap: parents element type must be int4")));

    candidate_count = ARR_NDIM(ids_array) == 0
                      ? 0 : ArrayGetNItems(ARR_NDIM(ids_array), ARR_DIMS(ids_array));
    parent_count    = ARR_NDIM(par_array) == 0
                      ? 0 : ArrayGetNItems(ARR_NDIM(par_array), ARR_DIMS(par_array));
    if (candidate_count != parent_count)
        ereport(ERROR,
            (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
             errmsg("content_descent_bitmap: ids and parents length mismatch (%d vs %d)",
                    candidate_count, parent_count)));

    return presence_bitmap_datum(fcinfo, "content_descent_bitmap",
                                  laplace_tier_batch_existence_probe);
}

