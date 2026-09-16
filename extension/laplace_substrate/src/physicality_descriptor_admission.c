#include "postgres.h"

#include <math.h>
#include "access/detoast.h"
#include "catalog/pg_type_d.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "nodes/parsenodes.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include "utils/snapmgr.h"
#include "utils/timestamp.h"

#include "laplace/core/content_witness_batch.h"
#include "laplace/core/physicality_descriptor_admission.h"
#include "laplace/core/sql_catalog.h"
#include "laplace/core/trajectory.h"
#include "perfcache_native.h"
#include "physicality_descriptor_admission_pg.h"

/* This is a transport/provider boundary. The shared native owners validate
 * tuple framing, body identities, composition, descriptor/view recipes and
 * structural observations. SPI supplies only current Content placements under
 * the caller statement's one registered snapshot. No generated row is a new
 * source observation or a provider for an original reference. */
PG_FUNCTION_INFO_V1(pg_laplace_physicality_descriptor_materialize);

typedef struct stage_list {
    intent_stage_t **items;
    size_t count;
    size_t capacity;
} stage_list;

typedef struct admission_state {
    MemoryContext context;
    MemoryContextCallback cleanup;
    size_t maximum_bytes, bytes, peak_bytes;
    size_t maximum_logical, logical_work;
    size_t source_logical, admitted_logical, current_logical;
    size_t stored_vertices, current_bodies;
    int maximum_operations, operations, rounds;
    stage_list source, admitted, current;
    physicality_descriptor_vocabulary_t *vocabulary;
    physicality_descriptor_capture_t *capture;
    physicality_descriptor_materialization_t *materialization;
    intent_stage_t *output[3];
    hash128_t *missing;
    size_t missing_count;
    Snapshot snapshot;
    SPIPlanPtr metadata_plan, payload_plan;
} admission_state;

typedef struct transport_array {
    Datum *values;
    bool *nulls;
    int count;
} transport_array;

typedef struct provider_key {
    hash128_t placement;
    hash128_t entity;
    bool seen;
} provider_key;

static pg_noreturn void admission_invalid(const char *message)
{
    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                   errmsg("physicality descriptor admission: %s", message)));
}

static size_t admission_add(size_t a, size_t b)
{
    if (b > SIZE_MAX - a)
        admission_invalid("resource size overflow");
    return a + b;
}

static size_t admission_multiply(size_t a, size_t b)
{
    if (a != 0 && b > SIZE_MAX / a)
        admission_invalid("resource size overflow");
    return a * b;
}

static void admission_charge(admission_state *s, size_t bytes)
{
    if (bytes > s->maximum_bytes - s->bytes)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                       errmsg("physicality descriptor admission byte grant exhausted")));
    s->bytes += bytes;
    if (s->bytes > s->peak_bytes) s->peak_bytes = s->bytes;
}

static void admission_native_peak(admission_state *s, size_t bytes)
{
    admission_charge(s, bytes);
    s->bytes -= bytes;
}

static void admission_logical(admission_state *s, size_t count)
{
    if (count > s->maximum_logical - s->logical_work)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                       errmsg("physicality descriptor admission raw logical-work grant exhausted")));
    s->logical_work += count;
}

static void *admission_alloc(admission_state *s, size_t bytes)
{
    if (bytes > MaxAllocSize) admission_invalid("allocation exceeds PostgreSQL datum limit");
    admission_charge(s, bytes);
    return MemoryContextAllocZero(s->context, bytes);
}

static void admission_cleanup(void *arg)
{
    admission_state *s = arg;
    stage_list *lists[3] = {&s->source, &s->admitted, &s->current};
    size_t i, j;
    for (i = 0; i < 3; ++i)
        for (j = 0; j < lists[i]->count; ++j) {
            intent_stage_free(lists[i]->items[j]);
            lists[i]->items[j] = NULL;
        }
    physicality_descriptor_materialization_free(s->materialization);
    s->materialization = NULL;
    physicality_descriptor_capture_free(s->capture);
    s->capture = NULL;
    physicality_descriptor_vocabulary_free(s->vocabulary);
    s->vocabulary = NULL;
    for (i = 0; i < 3; ++i) {
        intent_stage_free(s->output[i]);
        s->output[i] = NULL;
    }
}

static intent_stage_t **admission_stage_slot(admission_state *s, stage_list *list)
{
    if (list->count == list->capacity) {
        size_t capacity = list->capacity == 0 ? 4 : admission_multiply(list->capacity, 2);
        size_t bytes = admission_multiply(capacity, sizeof(*list->items));
        intent_stage_t **items = admission_alloc(s, bytes);
        if (list->count != 0)
            memcpy(items, list->items, list->count * sizeof(*items));
        if (list->items != NULL) {
            s->bytes -= list->capacity * sizeof(*items);
            pfree(list->items);
        }
        list->items = items;
        list->capacity = capacity;
    }
    return &list->items[list->count++];
}

static void admission_status(physicality_descriptor_status_t status, const char *phase)
{
    if (status == PHYSICALITY_DESCRIPTOR_OK) return;
    ereport(ERROR, (errcode(status == PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED ?
                           ERRCODE_PROGRAM_LIMIT_EXCEEDED : ERRCODE_INVALID_PARAMETER_VALUE),
                   errmsg("physicality descriptor admission %s failed (native status %d)",
                          phase, (int)status)));
}

static hash128_t admission_id(Datum value)
{
    bytea *bytes;
    hash128_t result;
    if (toast_raw_datum_size(value) != VARHDRSZ + sizeof(result))
        admission_invalid("identity must contain exactly 16 bytes");
    bytes = DatumGetByteaPP(value);
    if (VARSIZE_ANY_EXHDR(bytes) != sizeof(result))
        admission_invalid("identity must contain exactly 16 bytes");
    memcpy(&result, VARDATA_ANY(bytes), sizeof(result));
    return result;
}

static transport_array admission_array(admission_state *s, Datum value, Oid type)
{
    ArrayType *array;
    transport_array result;
    size_t raw = toast_raw_datum_size(value);
    admission_charge(s, raw);
    array = DatumGetArrayTypeP(value);
    if (ARR_ELEMTYPE(array) != type || ARR_NDIM(array) > 1 ||
        (ARR_NDIM(array) == 1 && ARR_LBOUND(array)[0] != 1))
        admission_invalid("transport requires flat arrays with lower bound one");
    result.count = ArrayGetNItems(ARR_NDIM(array), ARR_DIMS(array));
    admission_charge(s, admission_multiply((size_t)result.count, sizeof(Datum) + sizeof(bool)));
    deconstruct_array(array, type, type == BYTEAOID ? -1 : 8,
                      type == FLOAT8OID, type == BYTEAOID ? 'i' : 'd',
                      &result.values, &result.nulls, &result.count);
    for (int i = 0; i < result.count; ++i)
        if (result.nulls[i]) admission_invalid("transport arrays cannot contain NULL elements");
    return result;
}

static void admission_import(admission_state *s, transport_array *a, stage_list *list)
{
    if (a[0].count != a[1].count || a[0].count != a[2].count)
        admission_invalid("entity/physicality/attestation stage arrays must align");
    for (int i = 0; i < a[0].count; ++i) {
        bytea *parts[3] = {
            DatumGetByteaPP(a[0].values[i]),
            DatumGetByteaPP(a[1].values[i]),
            DatumGetByteaPP(a[2].values[i])
        };
        intent_stage_t **slot = admission_stage_slot(s, list);
        int rc;
        rc = intent_stage_from_tuple_bytes(
            (uint8_t *)VARDATA_ANY(parts[0]), VARSIZE_ANY_EXHDR(parts[0]),
            (uint8_t *)VARDATA_ANY(parts[1]), VARSIZE_ANY_EXHDR(parts[1]),
            (uint8_t *)VARDATA_ANY(parts[2]), VARSIZE_ANY_EXHDR(parts[2]),
            s->maximum_bytes - s->bytes, slot);
        if (rc != 0) admission_status(rc == -2 ? PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED :
                                    PHYSICALITY_DESCRIPTOR_INVALID, "tuple import");
        admission_native_peak(s, intent_stage_memory_peak_bytes(*slot));
        /* Full E/P/A framing was validated above. Descriptor admission uses
         * only physicality observations from this owned copy; original caller
         * stages remain intact for the ordinary writer. Keep the import peak. */
        intent_stage_retain_physicalities(*slot);
        admission_charge(s, intent_stage_memory_bytes(*slot));
    }
}

static size_t admission_preflight(admission_state *s, stage_list *list)
{
    size_t bodies = 0, stored = 0, logical = 0;
    admission_status(physicality_descriptor_stages_preflight(
        (const intent_stage_t *const *)list->items, list->count, s->maximum_logical,
        &bodies, &stored, &logical), "stored-carrier preflight");
    s->stored_vertices = admission_add(s->stored_vertices, stored);
    return logical;
}

static physicality_descriptor_source_observation_t *admission_sources(
    admission_state *s, const transport_array *arrays, size_t count)
{
    physicality_descriptor_source_observation_t *sources;
    if (count != (size_t)arrays[0].count || arrays[0].count != arrays[1].count ||
        arrays[0].count != arrays[2].count)
        admission_invalid("source, source-unit and trust arrays must align with every raw physicality observation");
    sources = admission_alloc(s, admission_multiply(count, sizeof(*sources)));
    for (size_t i = 0; i < count; ++i) {
        sources[i].source_id = admission_id(arrays[0].values[i]);
        sources[i].source_unit_id = admission_id(arrays[1].values[i]);
        sources[i].source_trust = DatumGetFloat8(arrays[2].values[i]);
        if (!isfinite(sources[i].source_trust) || sources[i].source_trust < 0 || sources[i].source_trust > 1)
            admission_invalid("source trust must be an explicit finite registered prior in [0,1]");
    }
    return sources;
}

static ArrayType *admission_ids(admission_state *s, const hash128_t *ids, size_t count)
{
    const size_t item_bytes = INTALIGN(VARHDRSZ + sizeof(hash128_t));
    size_t bytes = admission_add(ARR_OVERHEAD_NONULLS(count ? 1 : 0), admission_multiply(count, item_bytes));
    ArrayType *array = admission_alloc(s, bytes);
    char *cursor;
    if (count > INT_MAX) admission_invalid("identity frontier exceeds PostgreSQL array count");
    SET_VARSIZE(array, bytes);
    ARR_NDIM(array) = count ? 1 : 0;
    ARR_ELEMTYPE(array) = BYTEAOID;
    if (count) { ARR_DIMS(array)[0] = (int)count; ARR_LBOUND(array)[0] = 1; }
    cursor = (char *)array + ARR_OVERHEAD_NONULLS(ARR_NDIM(array));
    for (size_t i = 0; i < count; ++i) {
        SET_VARSIZE(cursor, VARHDRSZ + sizeof(hash128_t));
        memcpy(cursor + VARHDRSZ, &ids[i], sizeof(hash128_t));
        cursor += item_bytes;
    }
    return array;
}

/* The view state and exact missing slice are distinct from immutable D.
 * Array NULL bits express unavailable V; an all-zero id is never published. */
static ArrayType *admission_views(admission_state *s,
    const physicality_descriptor_admitted_form_t *forms,size_t count,size_t missing_count)
{
    size_t present=0;
    if(count>INT_MAX)admission_invalid("view count exceeds PostgreSQL array capacity");
    for(size_t i=0;i<count;++i) {
        if(forms[i].view_state!=PHYSICALITY_DESCRIPTOR_VIEW_AVAILABLE &&
           forms[i].view_state!=PHYSICALITY_DESCRIPTOR_VIEW_MISSING_REFERENCE)
            admission_invalid("native view state is invalid");
        if(forms[i].missing_first>missing_count || forms[i].missing_count>missing_count-forms[i].missing_first ||
           forms[i].missing_first>PG_INT64_MAX || forms[i].missing_count>PG_INT64_MAX ||
           ((forms[i].view_state==PHYSICALITY_DESCRIPTOR_VIEW_AVAILABLE)!=(forms[i].missing_count==0)))
            admission_invalid("native view missing slice is invalid");
        if(forms[i].view_state==PHYSICALITY_DESCRIPTOR_VIEW_AVAILABLE)++present;
    }
    size_t overhead=count?ARR_OVERHEAD_WITHNULLS(1,count):ARR_OVERHEAD_NONULLS(0);
    size_t bytes=admission_add(overhead,admission_multiply(present,INTALIGN(VARHDRSZ+sizeof(hash128_t))));
    ArrayType *array=admission_alloc(s,bytes);SET_VARSIZE(array,bytes);
    ARR_NDIM(array)=count?1:0;ARR_ELEMTYPE(array)=BYTEAOID;
    if(count){ARR_DIMS(array)[0]=(int)count;ARR_LBOUND(array)[0]=1;array->dataoffset=(int32)overhead;}
    char *cursor=(char*)array+overhead;bits8 *bitmap=ARR_NULLBITMAP(array);
    for(size_t i=0;i<count;++i)if(forms[i].view_state==PHYSICALITY_DESCRIPTOR_VIEW_AVAILABLE) {
        bitmap[i/8]|=(bits8)(1u<<(i%8));SET_VARSIZE(cursor,VARHDRSZ+sizeof(hash128_t));
        memcpy(cursor+VARHDRSZ,&forms[i].view_id,sizeof(hash128_t));cursor+=INTALIGN(VARHDRSZ+sizeof(hash128_t));
    }
    return array;
}
static ArrayType *admission_view_field(admission_state *s,
    const physicality_descriptor_admitted_form_t *forms,size_t count,int field)
{
    size_t width=field==0?sizeof(int16):sizeof(int64),overhead=ARR_OVERHEAD_NONULLS(count?1:0);
    size_t bytes=admission_add(overhead,admission_multiply(count,width));
    ArrayType *array=admission_alloc(s,bytes);SET_VARSIZE(array,bytes);ARR_NDIM(array)=count?1:0;
    ARR_ELEMTYPE(array)=field==0?INT2OID:INT8OID;
    if(count){ARR_DIMS(array)[0]=(int)count;ARR_LBOUND(array)[0]=1;}
    char *data=(char*)array+overhead;
    for(size_t i=0;i<count;++i) {
        if(field==0){int16 value=(int16)forms[i].view_state;memcpy(data+i*width,&value,width);}
        else{int64 value=(int64)(field==1?forms[i].missing_first:forms[i].missing_count);memcpy(data+i*width,&value,width);}
    }
    return array;
}

static int admission_key_compare(const void *left, const void *right)
{
    return memcmp(&((const provider_key *)left)->placement,
                  &((const provider_key *)right)->placement, sizeof(hash128_t));
}

static int admission_hash_compare(const void *left, const void *right)
{
    return memcmp(left, right, sizeof(hash128_t));
}

static Datum admission_column(HeapTuple tuple, TupleDesc desc, int column, bool *isnull)
{
    return SPI_getbinval(tuple, desc, column, isnull);
}

static Datum admission_required(HeapTuple tuple, TupleDesc desc, int column)
{
    bool isnull;
    Datum result = admission_column(tuple, desc, column, &isnull);
    if (isnull) admission_invalid("current Content provider returned a NULL required field");
    return result;
}

static provider_key *admission_provider_row(provider_key *keys, size_t count,
                                           HeapTuple tuple, TupleDesc desc)
{
    provider_key key, *found;
    hash128_t entity;
    key.placement = admission_id(admission_required(tuple, desc, 1));
    found = bsearch(&key, keys, count, sizeof(*keys), admission_key_compare);
    entity = admission_id(admission_required(tuple, desc, 2));
    if (found == NULL || found->seen ||
        memcmp(&found->entity, &entity, sizeof(entity)) != 0 ||
        DatumGetInt16(admission_required(tuple, desc, 3)) != 1)
        admission_invalid("current Content provider returned an unexpected or duplicate placement");
    found->seen = true;
    return found;
}

static void admission_execute(admission_state *s, SPIPlanPtr plan, ArrayType *ids)
{
    Datum values[1] = {PointerGetDatum(ids)};
    if (s->operations == s->maximum_operations)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                       errmsg("physicality descriptor admission database-operation grant exhausted")));
    ++s->operations;
    if (SPI_execute_snapshot(plan, values, NULL, s->snapshot, InvalidSnapshot,
                             true, false, 0) != SPI_OK_SELECT)
        ereport(ERROR, (errcode(ERRCODE_INTERNAL_ERROR),
                       errmsg("physicality descriptor current Content set query failed")));
}

/* ST_AsBinary(...,'NDR') is a set projection after raw TOAST size admission.
 * Copy unaligned WKB doubles into aligned memory; the native body owner then
 * validates the actual typed carrier semantics and canonical identity. */
static double *admission_wkb(Datum value, uint32_t *vertices)
{
    bytea *bytes = DatumGetByteaPP(value);
    const unsigned char *data = (const unsigned char *)VARDATA_ANY(bytes);
    size_t length = VARSIZE_ANY_EXHDR(bytes), offset;
    uint32_t type, count;
    double *xyzm;
    if (length < 5 || data[0] != 1) admission_invalid("provider geometry requires little-endian WKB");
    type = (uint32_t)data[1] | ((uint32_t)data[2] << 8) |
           ((uint32_t)data[3] << 16) | ((uint32_t)data[4] << 24);
    if (type == 3001) { count = 1; offset = 5; }
    else if (type == 3002 && length >= 9) {
        count = (uint32_t)data[5] | ((uint32_t)data[6] << 8) |
                ((uint32_t)data[7] << 16) | ((uint32_t)data[8] << 24);
        offset = 9;
    } else { admission_invalid("provider geometry must be PointZM or LineStringZM"); return NULL; }
    if (count == 0 || admission_add(offset, admission_multiply(count, 32)) != length)
        admission_invalid("provider geometry has invalid or trailing WKB bytes");
    xyzm = palloc(admission_multiply(count, 32));
    for (size_t i = 0; i < (size_t)count * 4; ++i) {
        uint64_t bits = 0;
        for (int j = 0; j < 8; ++j) bits |= (uint64_t)data[offset + i * 8 + j] << (8 * j);
        memcpy(&xyzm[i], &bits, sizeof(bits));
    }
    *vertices = count;
    return xyzm;
}

static void admission_hydrate(admission_state *s, const hash128_t *pending, size_t count)
{
    size_t before = s->bytes, metadata_rows, temporary, raw = 0;
    provider_key *keys = admission_alloc(s, admission_multiply(count, sizeof(*keys)));
    hash128_t *ids = admission_alloc(s, admission_multiply(count, sizeof(*ids)));
    ArrayType *array;
    intent_stage_t **slot;
    size_t stage_bytes = 0, new_missing = 0;
    hash128_t *missing;

    if (count == 0) admission_invalid("native provider requested an empty frontier");
    if (s->maximum_operations - s->operations < 2)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                       errmsg("physicality descriptor admission requires two operations for the next provider frontier")));
    for (size_t i = 0; i < count; ++i) {
        if (s->missing_count && bsearch(&pending[i], s->missing, s->missing_count,
                                       sizeof(hash128_t), admission_hash_compare))
            admission_invalid("native provider repeated an already checked absent entity");
        keys[i].entity = pending[i];
        laplace_physicality_id_compute(pending[i], 1, &keys[i].placement);
        ids[i] = keys[i].placement;
    }
    qsort(keys, count, sizeof(*keys), admission_key_compare);
    for (size_t i = 1; i < count; ++i)
        if (admission_key_compare(&keys[i-1], &keys[i]) == 0)
            admission_invalid("native provider requested duplicate placement identities");
    array = admission_ids(s, ids, count);
    /* Heap tuple/Datum/TOAST-pointer reservation precedes either SPI result.
     * Expanded geometry is charged separately before ST_AsBinary executes. */
    /* toast_tuple_target can keep geometries inline above its usual 2 KiB.
     * Reserve from the configured heap block width, not that tuning default. */
    admission_charge(s, admission_multiply(count, 2 * (size_t)BLCKSZ + 2048));
    admission_execute(s, s->metadata_plan, array);
    metadata_rows = (size_t)SPI_processed;
    if (metadata_rows > count) admission_invalid("provider exceeded requested frontier");
    for (size_t i = 0; i < metadata_rows; ++i) {
        HeapTuple tuple = SPI_tuptable->vals[i];
        TupleDesc desc = SPI_tuptable->tupdesc;
        bool isnull;
        Datum trajectory;
        (void)admission_provider_row(keys, count, tuple, desc);
        raw = admission_add(raw, toast_raw_datum_size(admission_required(tuple, desc, 4)));
        trajectory = admission_column(tuple, desc, 6, &isnull);
        if (!isnull) raw = admission_add(raw, toast_raw_datum_size(trajectory));
    }
    for (size_t i = 0; i < count; ++i) { if (!keys[i].seen) ++new_missing; keys[i].seen = false; }
    SPI_freetuptable(SPI_tuptable);
    admission_charge(s, admission_add(admission_multiply(raw, 4), admission_multiply(metadata_rows, 1024)));
    temporary = s->bytes - before;

    /* The full missing set is retained independently from each temporary
     * frontier. Native admission distinguishes explicit absence from no read. */
    missing = admission_alloc(s, admission_multiply(admission_add(s->missing_count, new_missing), sizeof(*missing)));
    if (s->missing_count) memcpy(missing, s->missing, s->missing_count * sizeof(*missing));
    if (s->missing) { s->bytes -= s->missing_count * sizeof(*missing); pfree(s->missing); }
    s->missing = missing;

    admission_execute(s, s->payload_plan, array);
    if ((size_t)SPI_processed != metadata_rows)
        admission_invalid("current Content metadata and payload disagree under the pinned snapshot");
    slot = admission_stage_slot(s, &s->current);
    *slot = intent_stage_new_bounded(metadata_rows, s->maximum_bytes - s->bytes);
    if (*slot == NULL) admission_status(PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED, "provider stage allocation");
    stage_bytes = intent_stage_memory_bytes(*slot);
    admission_charge(s, stage_bytes);
    for (size_t i = 0; i < metadata_rows; ++i) {
        HeapTuple tuple = SPI_tuptable->vals[i];
        TupleDesc desc = SPI_tuptable->tupdesc;
        provider_key *key = admission_provider_row(keys, count, tuple, desc);
        uint32_t coord_vertices = 0, vertices = 0;
        double *coord = admission_wkb(admission_required(tuple, desc, 4), &coord_vertices);
        double *trajectory = NULL, alignment = 0;
        hilbert128_t hilbert;
        hash128_t hilbert_bits = admission_id(admission_required(tuple, desc, 5));
        bool trajectory_null, alignment_null, dimension_null;
        Datum value = admission_column(tuple, desc, 6, &trajectory_null);
        int32_t constituents = DatumGetInt32(admission_required(tuple, desc, 7)), dimension = 0;
        int64_t observed = DatumGetTimestampTz(admission_required(tuple, desc, 10));
        size_t logical = 0, actual;
        int rc;
        CHECK_FOR_INTERRUPTS();
        if (coord_vertices != 1) admission_invalid("provider coordinate must be one PointZM");
        if (!trajectory_null) trajectory = admission_wkb(value, &vertices);
        if (trajectory && trajectory_constituent_count(trajectory, vertices, &logical) != 0)
            admission_invalid("provider trajectory has invalid stored carriers");
        if (constituents < 0 || logical != (size_t)constituents)
            admission_invalid("provider logical count disagrees with its stored carriers");
        admission_logical(s, logical); /* stage-add hashes this exact logical body */
        s->current_logical = admission_add(s->current_logical, logical);
        s->stored_vertices = admission_add(s->stored_vertices, vertices);
        value = admission_column(tuple, desc, 8, &alignment_null);
        if (!alignment_null) alignment = DatumGetFloat8(value);
        value = admission_column(tuple, desc, 9, &dimension_null);
        if (!dimension_null) dimension = DatumGetInt32(value);
        if (TIMESTAMP_NOT_FINITE(observed) || observed > INT64_MAX - INTENT_STAGE_PG_EPOCH_UNIX_US)
            admission_invalid("provider observation timestamp is not finite");
        observed += INTENT_STAGE_PG_EPOCH_UNIX_US;
        memcpy(&hilbert, &hilbert_bits, sizeof(hilbert));
        rc = intent_stage_add_physicality(*slot, &key->placement, &key->entity, 1,
            coord, &hilbert, trajectory, vertices, constituents,
            alignment_null, alignment, dimension_null, dimension, observed);
        pfree(coord);
        if (trajectory) pfree(trajectory);
        if (rc != 0) admission_status(intent_stage_allocation_failed(*slot) ?
            PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED : PHYSICALITY_DESCRIPTOR_INVALID_BODY,
            "current Content stage");
        actual = intent_stage_memory_bytes(*slot);
        admission_native_peak(s, intent_stage_memory_peak_bytes(*slot) - stage_bytes);
        admission_charge(s, actual - stage_bytes);
        stage_bytes = actual;
    }
    for (size_t i = 0; i < count; ++i)
        if (!keys[i].seen) s->missing[s->missing_count++] = keys[i].entity;
    if (s->missing_count > 1)
        qsort(s->missing, s->missing_count, sizeof(hash128_t), admission_hash_compare);
    s->current_bodies = admission_add(s->current_bodies, metadata_rows);
    SPI_freetuptable(SPI_tuptable);
    pfree(keys); pfree(ids); pfree(array);
    s->bytes -= temporary;
    ++s->rounds;
}

static ArrayType *admission_output(admission_state *s, intent_stage_table_t table, size_t *tuple_bytes)
{
    size_t sizes[3], bytes = ARR_OVERHEAD_NONULLS(1);
    const uint8_t *parts[3];
    ArrayType *array;
    char *cursor;
    for (size_t i = 0; i < 3; ++i) {
        parts[i] = intent_stage_tuple_ptr(s->output[i], table, &sizes[i]);
        *tuple_bytes = admission_add(*tuple_bytes, sizes[i]);
        bytes = admission_add(bytes, INTALIGN(admission_add(VARHDRSZ, sizes[i])));
    }
    array = admission_alloc(s, bytes);
    SET_VARSIZE(array, bytes); ARR_NDIM(array) = 1; ARR_ELEMTYPE(array) = BYTEAOID;
    ARR_DIMS(array)[0] = 3; ARR_LBOUND(array)[0] = 1;
    cursor = (char *)array + ARR_OVERHEAD_NONULLS(1);
    for (size_t i = 0; i < 3; ++i) {
        SET_VARSIZE(cursor, VARHDRSZ + sizes[i]);
        if (sizes[i]) memcpy(cursor + VARHDRSZ, parts[i], sizes[i]);
        cursor += INTALIGN(VARHDRSZ + sizes[i]);
    }
    return array;
}

/* Serialize the object actually passed to SPI, including command visibility
 * and recovery subtransactions. This is operation evidence, not a content id
 * or a call that might obtain a different transaction/statement snapshot. */
static text *admission_snapshot_text(admission_state *s)
{
    Snapshot snapshot = s->snapshot;
    size_t capacity = admission_add(192, admission_multiply(
        admission_add(snapshot->xcnt, snapshot->subxcnt), 12));
    char *buffer = admission_alloc(s, capacity);
    size_t used = (size_t)snprintf(buffer, capacity,
        "active-mvcc-v1;xmin=%u;xmax=%u;cid=%u;recovery=%d;suboverflow=%d;xip=",
        snapshot->xmin, snapshot->xmax, snapshot->curcid,
        snapshot->takenDuringRecovery, snapshot->suboverflowed);
    text *result;
    for (uint32 i = 0; i < snapshot->xcnt; ++i)
        used += (size_t)snprintf(buffer + used, capacity - used, "%s%u", i ? "," : "", snapshot->xip[i]);
    used += (size_t)snprintf(buffer + used, capacity - used, ";subxip=");
    for (int32 i = 0; i < snapshot->subxcnt; ++i)
        used += (size_t)snprintf(buffer + used, capacity - used, "%s%u", i ? "," : "", snapshot->subxip[i]);
    if (used >= capacity) admission_invalid("snapshot receipt exceeded its reserved buffer");
    result = admission_alloc(s, VARHDRSZ + used);
    SET_VARSIZE(result, VARHDRSZ + used);
    memcpy(VARDATA(result), buffer, used);
    pfree(buffer); s->bytes -= capacity;
    return result;
}

static admission_state *admission_state_create(size_t maximum_bytes,
    int maximum_operations, size_t maximum_logical)
{
    admission_state *s;
    if (maximum_bytes == 0 || maximum_logical == 0 || maximum_operations <= 0)
        admission_invalid("positive finite byte, operation and logical-work grants are required");
    if (!ActiveSnapshotSet()) admission_invalid("an active statement snapshot is required");
    s = palloc0(sizeof(*s));
    s->context = CurrentMemoryContext;
    s->maximum_bytes = maximum_bytes;
    s->maximum_logical = maximum_logical;
    s->maximum_operations = maximum_operations;
    s->cleanup.func = admission_cleanup; s->cleanup.arg = s;
    MemoryContextRegisterResetCallback(s->context, &s->cleanup);
    admission_charge(s, sizeof(*s));
    return s;
}

static void admission_prepare_provider_plans(admission_state *s,
    const char *metadata_sql, const char *payload_sql)
{
    Oid query_types[1] = {BYTEAARRAYOID};
    if (s->maximum_operations - s->operations < 2)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
            errmsg("physicality descriptor admission provider plans require two database operations")));
    ++s->operations;
    s->metadata_plan = SPI_prepare_cursor(metadata_sql, 1, query_types,
        CURSOR_OPT_PARALLEL_OK);
    ++s->operations;
    s->payload_plan = SPI_prepare_cursor(payload_sql, 1, query_types,
        CURSOR_OPT_PARALLEL_OK);
    if (!s->metadata_plan || !s->payload_plan) admission_invalid("could not prepare provider set queries");
}

static void admission_materialize(admission_state *s,
    const physicality_descriptor_source_observation_t *sources, size_t source_count,
    int64_t generated_at, laplace_physicality_pg_admission_result *result)
{
    physicality_descriptor_limits_t limits;
    hash128_t generated_source, floor;
    size_t form_count = 0, floor_bytes, source_peak = 0, actual_count = 0;
    Datum snapshot_text;
    const char *metadata_sql, *payload_sql;
    s->source_logical = admission_preflight(s, &s->source);
    s->admitted_logical = admission_preflight(s, &s->admitted);
    for (size_t i = 0; i < s->source.count; ++i)
        actual_count = admission_add(actual_count, intent_stage_physicality_count(s->source.items[i]));
    if (actual_count != source_count || (source_count != 0 && sources == NULL))
        admission_invalid("source metadata must align with every raw physicality observation");
    for (size_t i = 0; i < source_count; ++i)
        if (!isfinite(sources[i].source_trust) || sources[i].source_trust < 0 || sources[i].source_trust > 1)
            admission_invalid("source trust must be an explicit finite registered prior in [0,1]");
    if (!laplace_perfcache_ready())
        ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                       errmsg("physicality descriptor admission requires the configured Unicode perfcache")));
    admission_status(physicality_descriptor_generated_source_create(
        s->maximum_bytes - s->bytes, &generated_source, &s->output[0], &source_peak),
        "generated source declaration");
    admission_native_peak(s, source_peak);
    admission_charge(s, intent_stage_memory_bytes(s->output[0]));
    admission_status(physicality_descriptor_vocabulary_create(&generated_source,
        s->maximum_bytes - s->bytes, &s->vocabulary), "vocabulary creation");
    admission_native_peak(s, physicality_descriptor_vocabulary_peak_bytes(s->vocabulary));
    admission_charge(s, physicality_descriptor_vocabulary_bytes(s->vocabulary));
    floor_bytes = physicality_descriptor_vocabulary_floor_index_added_bytes(s->vocabulary);
    admission_charge(s, floor_bytes);
    floor = *physicality_descriptor_vocabulary_floor_receipt(s->vocabulary);
    admission_logical(s, s->source_logical);
    limits.maximum_plan_bytes = s->maximum_bytes - s->bytes;
    {
        physicality_descriptor_status_t status = physicality_descriptor_capture_stages(
            (const intent_stage_t *const *)s->source.items, s->source.count,
            physicality_descriptor_vocabulary_basis(s->vocabulary), &limits,
            s->maximum_bytes - s->bytes, &s->capture);
        if (status != PHYSICALITY_DESCRIPTOR_OK)
            ereport(ERROR, (errcode(status == PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED ?
                                   ERRCODE_PROGRAM_LIMIT_EXCEEDED : ERRCODE_INVALID_PARAMETER_VALUE),
                errmsg("physicality descriptor admission original-form capture failed (native status %d)",
                       (int)status),
                errdetail("grant_bytes=%zu retained_bytes=%zu remaining_bytes=%zu "
                          "source_forms=%zu preflight_stored_vertices=%zu",
                          s->maximum_bytes, s->bytes, s->maximum_bytes - s->bytes,
                          source_count, s->stored_vertices)));
    }
    admission_native_peak(s, physicality_descriptor_capture_peak_bytes(s->capture));
    /* This owner retains the fully validated source rows for materialization,
     * not their preliminary plan. Preserve the observed capture peak and all
     * logical-work charges; only its retained allocation is reduced. */
    physicality_descriptor_capture_release_plan(s->capture);
    admission_charge(s, physicality_descriptor_capture_bytes(s->capture));

    s->snapshot = RegisterSnapshot(GetActiveSnapshot());
    snapshot_text = PointerGetDatum(admission_snapshot_text(s));
    metadata_sql = laplace_sql_query_text("ingest.physicality_descriptor_provider_metadata");
    payload_sql = laplace_sql_query_text("ingest.physicality_descriptor_provider_payload");
    if (metadata_sql == NULL || payload_sql == NULL) admission_invalid("immutable provider SQL is unavailable");
    if (SPI_connect() != SPI_OK_CONNECT) admission_invalid("could not connect to SPI");
    admission_prepare_provider_plans(s, metadata_sql, payload_sql);

    for (;;) {
        physicality_descriptor_status_t status;
        size_t pending_count = 0, retained;
        const hash128_t *pending;
        CHECK_FOR_INTERRUPTS();
        /* Source capture hashes once; each invocation validates source once,
         * and current/admitted provider bodies twice. This grant counts raw
         * expanded carrier work, separate from finite generated plan work. */
        admission_logical(s, admission_add(s->source_logical,
            admission_multiply(2, admission_add(s->current_logical, s->admitted_logical))));
        status = physicality_descriptor_materialize(s->capture, s->vocabulary,
            (const intent_stage_t *const *)s->current.items, s->current.count,
            (const intent_stage_t *const *)s->admitted.items, s->admitted.count,
            s->missing, s->missing_count, sources, source_count, &generated_source,
            generated_at, s->maximum_bytes - s->bytes, &s->materialization);
        if (status != PHYSICALITY_DESCRIPTOR_OK && status != PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER)
            admission_status(status, "native materialization");
        admission_native_peak(s, physicality_descriptor_materialization_peak_bytes(s->materialization));
        retained = physicality_descriptor_materialization_bytes(s->materialization);
        admission_charge(s, retained);
        if (status == PHYSICALITY_DESCRIPTOR_OK) break;
        pending = physicality_descriptor_materialization_pending(s->materialization, &pending_count);
        admission_hydrate(s, pending, pending_count);
        physicality_descriptor_materialization_free(s->materialization);
        s->materialization = NULL;
        s->bytes -= retained;
    }
    SPI_finish();
    UnregisterSnapshot(s->snapshot); s->snapshot = InvalidSnapshot;
    MemoryContextSwitchTo(s->context);
    result->forms = physicality_descriptor_materialization_forms(s->materialization, &form_count);
    if (form_count != source_count) admission_invalid("native output form count differs from original observation count");
    s->output[1] = physicality_descriptor_vocabulary_take_stage(s->vocabulary);
    s->output[2] = physicality_descriptor_materialization_take_stage(s->materialization);
    for (size_t i = 0; i < 3; ++i) result->stages[i] = s->output[i];
    result->owner = s->context;
    result->form_count = form_count;
    result->view_missing_ids = physicality_descriptor_materialization_missing(s->materialization, &result->view_missing_count);
    result->floor_receipt = floor;
    result->generated_source_id = generated_source;
    result->snapshot_receipt = snapshot_text;
    result->current_bodies = s->current_bodies;
    result->missing_bodies = s->missing_count;
    result->floor_index_added_bytes = floor_bytes;
    result->retained_bytes = s->bytes;
    result->peak_bytes = s->peak_bytes;
    result->logical_work = s->logical_work;
    result->stored_vertices = s->stored_vertices;
    result->provider_rounds = s->rounds;
    result->database_operations = s->operations;
}

/* Reuse the tuple import owner so backend callers get the same full framing,
 * identity and finite-body validation as the SQL transport. Borrowed caller
 * stages are never retained beyond this copy. */
static void admission_clone_stages(admission_state *s,
    const intent_stage_t *const *stages, size_t count, stage_list *list)
{
    if (count != 0 && stages == NULL) admission_invalid("stage array is missing");
    for (size_t i = 0; i < count; ++i) {
        size_t sizes[3];
        const uint8_t *parts[3];
        intent_stage_t **slot;
        int rc;
        if (stages[i] == NULL) admission_invalid("stage array contains a missing stage");
        for (size_t j = 0; j < 3; ++j)
            parts[j] = intent_stage_tuple_ptr(stages[i], (intent_stage_table_t)(j + 1), &sizes[j]);
        slot = admission_stage_slot(s, list);
        rc = intent_stage_from_tuple_bytes(parts[0], sizes[0], parts[1], sizes[1],
            parts[2], sizes[2], s->maximum_bytes - s->bytes, slot);
        if (rc != 0) admission_status(rc == -2 ? PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED :
                                    PHYSICALITY_DESCRIPTOR_INVALID, "caller stage import");
        admission_native_peak(s, intent_stage_memory_peak_bytes(*slot));
        /* Full E/P/A framing was validated above. Descriptor admission uses
         * only physicality observations from this owned copy; original caller
         * stages remain intact for the ordinary writer. Keep the import peak. */
        intent_stage_retain_physicalities(*slot);
        admission_charge(s, intent_stage_memory_bytes(*slot));
    }
}

laplace_physicality_pg_admission_result *laplace_physicality_pg_materialize(
    const intent_stage_t *const *source_stages, size_t source_stage_count,
    const intent_stage_t *const *admitted_stages, size_t admitted_stage_count,
    const physicality_descriptor_source_observation_t *observations, size_t observation_count,
    int64 generated_at_unix_us, size_t maximum_bytes,
    int maximum_database_operations, size_t maximum_logical_occurrences)
{
    MemoryContext owner = AllocSetContextCreate(CurrentMemoryContext,
        "Laplace physicality admission", ALLOCSET_DEFAULT_SIZES);
    MemoryContext previous = MemoryContextSwitchTo(owner);
    admission_state *s = admission_state_create(maximum_bytes,
        maximum_database_operations, maximum_logical_occurrences);
    laplace_physicality_pg_admission_result *result = admission_alloc(s, sizeof(*result));
    physicality_descriptor_source_observation_t *sources;
    if (observation_count != 0 && observations == NULL)
        admission_invalid("source observations are missing");
    sources = admission_alloc(s, admission_multiply(observation_count, sizeof(*sources)));
    if (observation_count != 0) memcpy(sources, observations, observation_count * sizeof(*sources));
    admission_clone_stages(s, source_stages, source_stage_count, &s->source);
    admission_clone_stages(s, admitted_stages, admitted_stage_count, &s->admitted);
    admission_materialize(s, sources, observation_count, generated_at_unix_us, result);
    MemoryContextSwitchTo(previous);
    return result;
}

void laplace_physicality_pg_admission_release(laplace_physicality_pg_admission_result *result)
{
    if (result != NULL) MemoryContextDelete(result->owner);
}

Datum pg_laplace_physicality_descriptor_materialize(PG_FUNCTION_ARGS)
{
    admission_state *s;
    transport_array arrays[9];
    physicality_descriptor_source_observation_t *sources;
    laplace_physicality_pg_admission_result materialized = {0};
    const physicality_descriptor_admitted_form_t *forms;
    hash128_t generated_source, floor, *form_ids;
    size_t source_count = 0, form_count = 0, tuple_bytes = 0, floor_bytes;
    int64_t maximum_bytes = PG_GETARG_INT64(10), maximum_logical = PG_GETARG_INT64(12);
    int32_t maximum_operations = PG_GETARG_INT32(11);
    int64_t generated_at = PG_GETARG_INT64(9);
    Datum snapshot_text, values[22] = {0};
    bool nulls[22] = {false};
    ReturnSetInfo *result;

    if (maximum_bytes <= 0 || maximum_logical <= 0 || maximum_operations <= 0 ||
        (uint64_t)maximum_bytes > SIZE_MAX || (uint64_t)maximum_logical > SIZE_MAX)
        admission_invalid("positive finite byte, operation and logical-work grants are required");
    s = admission_state_create((size_t)maximum_bytes, maximum_operations, (size_t)maximum_logical);
    for (int i = 0; i < 9; ++i)
        arrays[i] = admission_array(s, PG_GETARG_DATUM(i), i == 8 ? FLOAT8OID : BYTEAOID);
    admission_import(s, arrays, &s->source);
    admission_import(s, arrays + 3, &s->admitted);
    for (size_t i = 0; i < s->source.count; ++i)
        source_count = admission_add(source_count, intent_stage_physicality_count(s->source.items[i]));
    sources = admission_sources(s, arrays + 6, source_count);
    admission_materialize(s, sources, source_count, generated_at, &materialized);
    forms = materialized.forms;
    form_count = materialized.form_count;
    floor = materialized.floor_receipt;
    generated_source = materialized.generated_source_id;
    floor_bytes = materialized.floor_index_added_bytes;
    snapshot_text = materialized.snapshot_receipt;
    form_ids = admission_alloc(s, admission_multiply(form_count, sizeof(*form_ids)));
    for (size_t i = 0; i < form_count; ++i) form_ids[i] = forms[i].descriptor_id;
    values[3] = PointerGetDatum(admission_ids(s, form_ids, form_count));
    values[4] = PointerGetDatum(admission_views(s, forms, form_count, materialized.view_missing_count));
    for(int i=0;i<3;++i)
        values[18+i]=PointerGetDatum(admission_view_field(s,forms,form_count,i));
    values[21]=PointerGetDatum(admission_ids(s,materialized.view_missing_ids,materialized.view_missing_count));
    for (int i = 0; i < 3; ++i)
        values[i] = PointerGetDatum(admission_output(s, (intent_stage_table_t)(i + 1), &tuple_bytes));
    {
        bytea *floor_id = admission_alloc(s, VARHDRSZ + sizeof(floor));
        SET_VARSIZE(floor_id, VARHDRSZ + sizeof(floor));
        memcpy(VARDATA(floor_id), &floor, sizeof(floor));
        values[5] = PointerGetDatum(floor_id);
        bytea *source_id = admission_alloc(s, VARHDRSZ + sizeof(generated_source));
        SET_VARSIZE(source_id, VARHDRSZ + sizeof(generated_source));
        memcpy(VARDATA(source_id), &generated_source, sizeof(generated_source));
        values[17] = PointerGetDatum(source_id);
    }
    values[6] = snapshot_text;
    values[7] = Int64GetDatum((int64_t)source_count);
    values[8] = Int64GetDatum((int64_t)s->current_bodies);
    values[9] = Int64GetDatum((int64_t)s->missing_count);
    values[10] = Int32GetDatum(s->rounds);
    values[11] = Int32GetDatum(s->operations);
    /* The tuplestore receives another serialized result; reserve its payload
     * copy before constructing it. Backend allocator/SPI executor bookkeeping
     * and the separately reported floor-owned index are not process RSS. */
    {
        size_t result_bytes = 256 + VARHDRSZ + sizeof(generated_source);
        for (int i = 0; i < 7; ++i)
            result_bytes = admission_add(result_bytes, toast_raw_datum_size(values[i]));
        for (int i = 18; i < 22; ++i)
            result_bytes = admission_add(result_bytes, toast_raw_datum_size(values[i]));
        admission_charge(s, result_bytes);
    }
    values[12] = Int64GetDatum((int64_t)s->peak_bytes);
    values[13] = Int64GetDatum((int64_t)tuple_bytes);
    values[14] = Int64GetDatum((int64_t)floor_bytes);
    values[15] = Int64GetDatum((int64_t)s->logical_work);
    values[16] = Int64GetDatum((int64_t)s->stored_vertices);
    InitMaterializedSRF(fcinfo, 0);
    result = (ReturnSetInfo *)fcinfo->resultinfo;
    if (result->setDesc->natts != 22) admission_invalid("SQL result contract does not match native provider");
    tuplestore_putvalues(result->setResult, result->setDesc, values, nulls);
    admission_cleanup(s);
    PG_RETURN_NULL();
}
