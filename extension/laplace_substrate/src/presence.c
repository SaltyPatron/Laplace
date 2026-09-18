/* SQL presence contracts share the native set scan. Kept in the versioned
 * execution module so updates do not replace the preloaded host library. */
#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "executor/spi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "catalog/pg_type.h"
#include "descent_probe.h"

/*
 * Shared validation + SPI-wrapped invocation for the flat batch-presence
 * primitives (entities_exist_bitmap, tier_batch_existence_probe). Both are
 * driven by the exact same underlying batch_presence_core() in
 * descent_probe.c (perfcache fast-path + one SPI batch query) -- a bit in
 * the returned bitmap is set iff that id was POSITIVELY confirmed to have a
 * committed entities row. Nothing here ever assumes presence by default.
 * The two SQL entry points exist separately (rather than one function under
 * two names) so ingest-descent call sites are self-documenting about *why*
 * they're calling it -- tier_batch_existence_probe denotes one round of the
 * C#-driven tier-by-tier trunk-to-leaf probe (TierTreeDescent.cs), while
 * entities_exist_bitmap is the general-purpose "do these ids exist" check.
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
 * attestations_exist_bitmap(ids, type_ids, subject_ids) -- KEYED probe.
 * Attestations are partitioned LIST(type_id) -> HASH(subject_id); id alone
 * cannot prune, so the caller (which computed every id FROM these keys)
 * passes them alongside. Three parallel bytea[] arrays, validated for
 * shape/nulls/length parity here so descent_probe.c can assume alignment.
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
 * Shared validation + SPI-wrapped invocation for the single-key batch-
 * presence primitives (entities_stored_bitmap(ids, tiers),
 * tier_batch_existence_probe(ids, tiers),
 * physicalities_exist_bitmap(ids, hilberts)). Same positive-confirmation
 * semantics as presence_bitmap_datum; the second argument carries the
 * target table's partition key parallel to the ids so the ordinals probe
 * prunes to one index descent per id instead of one per leaf
 * (entities: LIST(tier); physicalities: RANGE(hilbert_index)).
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

/*
 * pg_laplace_physicalities_exist_bitmap_keyed is GONE (2026-08-04).
 *
 * It took a parallel "hilberts" array and pruned each probe to the
 * RANGE(hilbert_index) band that held the row. physicalities is now
 * HASH(id): a hilbert key selects no partition, so the keyed probe would
 * answer "absent" for stored rows -- and COPY has no ON CONFLICT, so that
 * aborts the ingest rather than slowing it. The id-only entry point below
 * needs no partition hint; HASH(id) routes on the id it already has.
 */

PG_FUNCTION_INFO_V1(pg_laplace_physicalities_exist_bitmap);

Datum
pg_laplace_physicalities_exist_bitmap(PG_FUNCTION_ARGS)
{
    return presence_bitmap_datum(fcinfo, "physicalities_exist_bitmap",
                                  laplace_physicalities_present_bitmap);
}

PG_FUNCTION_INFO_V1(pg_laplace_content_descent_bitmap);

/*
 * Legacy/back-compat entry point (content_descent_bitmap(ids, parents)).
 *
 * The real trunk-to-leaf, tier-by-tier descent algorithm is now driven
 * entirely by the C# orchestrator (TierTreeDescent.cs), which calls
 * tier_batch_existence_probe() once per tier/round with only the
 * candidates that round actually needs to check -- already filtered to
 * exclude descendants of nodes proven present in an earlier round, per the
 * content-addressing guarantee that a present node's whole subtree is
 * present too. This function is kept only so callers that still pass a
 * flat (ids, parents) pair in one shot keep working; `parents` is
 * validated for shape and otherwise UNUSED -- there is no tree-walk, no
 * default-present assumption, and no shortcircuiting here anymore. This
 * replaces the previous laplace_content_descent_bitmap_core()
 * implementation, which memset the whole bitmap to 0xFF ("assume every id
 * present") and only ever cleared bits it could positively disprove,
 * stopping descent at the first node it (correctly or incorrectly)
 * believed present -- that "assume-present, clear-on-disproof,
 * single-shortcircuit-per-branch" scheme is exactly the shape of bug that
 * independently existed on the C# side (TierTreeDescent.cs's previously
 * unconditional MarkProven() call); neither should ever assume presence
 * without a real, positive confirmation. Every bit returned here is set
 * iff a real batch presence query (or perfcache codepoint match) actually
 * confirmed that id has a committed entities row -- identical semantics to
 * tier_batch_existence_probe / entities_exist_bitmap, since all three
 * ultimately call the same batch_presence_core().
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

