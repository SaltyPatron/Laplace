#include "postgres.h"
#include "access/xact.h"
#include "access/detoast.h"
#include "catalog/pg_type_d.h"
#include "common/int.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include "utils/snapmgr.h"
#include "utils/timestamp.h"

#include <math.h>

#include "generated_stage_sink.h"
#include "laplace/core/attestation_engine.h"
#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/physicality_descriptor_admission.h"
#include "laplace/core/sql_catalog.h"
#include "laplace/core/trajectory.h"

typedef struct SinkField { const uint8 *data; int32 length; } SinkField;
typedef struct SinkRow {
    hash128_t id;
    SinkField fields[14];
    size_t ordinal;
    bool duplicate, present, accepted;
} SinkRow;
typedef struct SinkTable { SinkRow *rows; SinkRow **index; size_t count; } SinkTable;
typedef struct SinkState {
    LaplaceGeneratedStageSinkLimits limits;
    LaplaceGeneratedStageSinkReceipt receipt;
    size_t bytes;
    MemoryContextCallback cleanup;
    physicality_descriptor_capture_t *capture;
    SinkTable tables[3];
} SinkState;

static pg_noreturn void sink_invalid(const char *message)
{
    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                   errmsg("generated stage sink: %s", message)));
}

static size_t sink_add(size_t a, size_t b)
{
    if (b > SIZE_MAX - a) sink_invalid("size overflow");
    return a + b;
}

static size_t sink_multiply(size_t a, size_t b)
{
    if (a && b > SIZE_MAX / a) sink_invalid("size overflow");
    return a * b;
}

static void sink_charge(SinkState *s, size_t bytes)
{
    if (bytes > s->limits.maximum_bytes - s->bytes)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                       errmsg("generated stage sink: byte grant exhausted")));
    s->bytes += bytes;
    if (s->bytes > s->receipt.peak_reserved_bytes)
        s->receipt.peak_reserved_bytes = s->bytes;
}

static void *sink_alloc(SinkState *s, size_t bytes)
{
    if (bytes == 0) bytes = 1;
    if (!AllocSizeIsValid(bytes)) sink_invalid("allocation exceeds PostgreSQL limit");
    sink_charge(s, bytes);
    return palloc0(bytes);
}

static void sink_cleanup(void *argument)
{
    SinkState *s = argument;
    physicality_descriptor_capture_free(s->capture);
    s->capture = NULL;
}

static uint64 sink_word(const uint8 *data, size_t width)
{
    uint64 value = 0;
    for (size_t i = 0; i < width; ++i) value = (value << 8) | data[i];
    return value;
}

static int64 sink_integer(const SinkField *field)
{
    uint64 bits = sink_word(field->data, (size_t)field->length);
    int64 result;
    memcpy(&result, &bits, sizeof(result));
    return result;
}

static bool sink_field_equal(const SinkField *a, const SinkField *b)
{
    return a->length == b->length &&
        (a->length < 0 || memcmp(a->data, b->data, (size_t)a->length) == 0);
}

static void sink_read_row(const uint8 *data, size_t size, size_t *offset,
                          unsigned columns, SinkRow *row)
{
    size_t at = *offset;
    if (at > size || size - at < 2 || sink_word(data + at, 2) != columns)
        sink_invalid("invalid native tuple column count");
    at += 2;
    for (unsigned i = 0; i < columns; ++i) {
        if (size - at < 4) sink_invalid("truncated native tuple length");
        uint64 length = sink_word(data + at, 4);
        at += 4;
        row->fields[i].length = length == UINT32_MAX ? -1 : (int32)length;
        if (length != UINT32_MAX) {
            if (length > INT32_MAX || length > size - at)
                sink_invalid("truncated native tuple value");
            row->fields[i].data = data + at;
            at += (size_t)length;
        }
    }
    if (row->fields[0].length != 16) sink_invalid("invalid native row identity");
    memcpy(&row->id, row->fields[0].data, 16);
    *offset = at;
}

static void sink_width(const SinkRow *row, unsigned column, int32 width, bool nullable)
{
    if (row->fields[column].length != width &&
        !(nullable && row->fields[column].length == -1))
        sink_invalid("invalid native tuple field width");
}

static void sink_validate_entity(const SinkRow *row)
{
    sink_width(row, 1, 2, false);
    sink_width(row, 2, 16, false);
    sink_width(row, 3, 16, false);
    uint64 tier = sink_word(row->fields[1].data, 2);
    if (tier > UINT8_MAX) sink_invalid("invalid entity tier");
    hash128_t type = laplace_content_tier_type_id((uint8_t)tier);
    if (memcmp(&type, row->fields[2].data, 16) != 0)
        sink_invalid("generated entity is not declared Content");
}

static void sink_validate_attestation(const SinkRow *row, const hash128_t *relation)
{
    for (unsigned i = 1; i < 6; ++i)
        sink_width(row, i, 16, false);
    sink_width(row, 6, 2, false);
    for (unsigned i = 7; i <= 11; ++i) sink_width(row, i, 8, false);
    sink_width(row, 12, 1, false);
    sink_width(row, 13, 32, true);
    if (row->fields[12].data[0] != 1)
        sink_invalid("non-replayable evidence is outside generated-stage scope");
    if (memcmp(row->fields[2].data, relation, 16) != 0)
        sink_invalid("only generated HAS_PHYSICALITY evidence is supported");
    hash128_t values[5], id;
    memset(values, 0, sizeof(values));
    for (unsigned i = 0; i < 5; ++i)
        if (row->fields[i + 1].length == 16) memcpy(&values[i], row->fields[i + 1].data, 16);
    laplace_attestation_id_compute(&values[0], &values[1], &values[2],
        row->fields[3].length == -1, &values[3], &values[4], row->fields[5].length == -1, &id);
    if (!hash128_equals(&id, &row->id)) sink_invalid("attestation identity disagrees with its five-tuple");
    int64 games = sink_integer(&row->fields[8]);
    int64 sum = sink_integer(&row->fields[9]);
    int16 outcome;
    if (games <= 0 || sum < 0 || sum / games > INT64_C(1000000000) ||
        (sum / games == INT64_C(1000000000) && sum % games != 0) ||
        sink_integer(&row->fields[10]) <= 0 || sink_integer(&row->fields[11]) <= 0)
        sink_invalid("invalid exact witness aggregates");
    if (laplace_attestation_outcome_from_totals_fp(games, sum, &outcome) != 0 ||
        sink_word(row->fields[6].data, 2) != (uint64)outcome)
        sink_invalid("witness outcome disagrees with its exact aggregate");
}

static int sink_compare_ids(const void *a, const void *b)
{
    const SinkRow *left = *(SinkRow *const *)a, *right = *(SinkRow *const *)b;
    int result = memcmp(&left->id, &right->id, 16);
    return result ? result : (left->ordinal > right->ordinal) - (left->ordinal < right->ordinal);
}

static SinkRow *sink_find(const SinkTable *table, const hash128_t *id)
{
    size_t lo = 0, hi = table->count;
    while (lo < hi) {
        size_t middle = lo + (hi - lo) / 2;
        if (memcmp(&table->index[middle]->id, id, 16) < 0) lo = middle + 1;
        else hi = middle;
    }
    return lo < table->count && hash128_equals(&table->index[lo]->id, id)
        ? table->index[lo] : NULL;
}

static void sink_deduplicate(SinkState *s, unsigned table)
{
    SinkTable *t = &s->tables[table];
    qsort(t->index, t->count, sizeof(*t->index), sink_compare_ids);
    SinkRow *first = NULL;
    const unsigned columns[] = {4, 10, 14};
    for (size_t i = 0; i < t->count; ++i) {
        SinkRow *row = t->index[i];
        if (first == NULL || !hash128_equals(&first->id, &row->id)) {
            first = row;
            ++s->receipt.distinct_rows[table];
            continue;
        }
        for (unsigned field = 0; field < columns[table]; ++field) {
            /* E creation retains its first source. Repeated generated bodies
             * and source-unit witnesses may carry a later observation time. */
            if ((table == 0 && field >= 1) || (table == 1 && field == 9) ||
                (table == 2 && field == 7)) continue;
            if (!sink_field_equal(&first->fields[field], &row->fields[field]))
                sink_invalid("duplicate generated identity has conflicting fields");
        }
        if (table == 0 && sink_word(row->fields[1].data,2) < sink_word(first->fields[1].data,2)) {
            first->fields[1] = row->fields[1];
            first->fields[2] = row->fields[2];
        }
        if (table == 2 && sink_integer(&row->fields[7]) > sink_integer(&first->fields[7]))
            first->fields[7] = row->fields[7];
        row->duplicate = true;
    }
}

static void sink_parse(SinkState *s, const intent_stage_t *const *stages, size_t stage_count)
{
    const unsigned columns[] = {4, 10, 14};
    hash128_t relation;
    if (laplace_relation_resolve("HAS_PHYSICALITY", &relation) != 0)
        sink_invalid("missing governed HAS_PHYSICALITY relation");
    for (unsigned table = 0; table < 3; ++table) {
        SinkTable *t = &s->tables[table];
        t->count = s->receipt.input_rows[table];
        t->rows = sink_alloc(s, sink_multiply(t->count, sizeof(*t->rows)));
        t->index = sink_alloc(s, sink_multiply(t->count, sizeof(*t->index)));
        size_t at = 0;
        for (size_t stage = 0; stage < stage_count; ++stage) {
            size_t bytes, offset = 0;
            const uint8 *data = intent_stage_tuple_ptr(stages[stage], (intent_stage_table_t)(table + 1), &bytes);
            while (offset < bytes) {
                if (at == t->count) sink_invalid("native tuple count mismatch");
                SinkRow *row = &t->rows[at];
                row->ordinal = at;
                t->index[at++] = row;
                sink_read_row(data, bytes, &offset, columns[table], row);
                if (table == 0) sink_validate_entity(row);
                if (table == 2) sink_validate_attestation(row, &relation);
            }
        }
        if (at != t->count) sink_invalid("native tuple count mismatch");
        sink_deduplicate(s, table);
    }
}

/* These are the only database statements owned by this sink. Plans are fixed
 * catalog entries and retained per backend; no statement depends on row data. */
enum SinkQuery { SQ_LOCK, SQ_EPOCH, SQ_PRESENCE, SQ_ENTITIES, SQ_PHYSICALITIES,
                 SQ_ATTESTATIONS, SQ_FOLD, SQ_MASKS, SQ_COUNT };
static SPIPlanPtr sink_plans[SQ_COUNT];
static const char *const sink_keys[SQ_COUNT] = {
    "ingest.generated_stage_sink.lock", "ingest.generated_stage_sink.epoch",
    "ingest.generated_stage_sink.presence", "ingest.generated_stage_sink.entities",
    "ingest.generated_stage_sink.physicalities", "ingest.generated_stage_sink.attestations",
    "ingest.generated_stage_sink.fold", "ingest.generated_stage_sink.masks"};
static const unsigned sink_column_counts[3] = {4,10,14};
static const Oid sink_types[3][14] = {
    {BYTEAOID,INT2OID,BYTEAOID,BYTEAOID},
    {BYTEAOID,BYTEAOID,INT2OID,BYTEAOID,BYTEAOID,BYTEAOID,INT4OID,FLOAT8OID,INT4OID,TIMESTAMPTZOID},
    {BYTEAOID,BYTEAOID,BYTEAOID,BYTEAOID,BYTEAOID,BYTEAOID,INT2OID,TIMESTAMPTZOID,
     INT8OID,INT8OID,INT8OID,INT8OID,BOOLOID,BYTEAOID}};

static void sink_operation(SinkState *s)
{
    if (s->receipt.operations == s->limits.maximum_operations)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                       errmsg("generated stage sink: database operation grant exhausted")));
    ++s->receipt.operations;
}

static void sink_execute(SinkState *s, enum SinkQuery query, int nargs,
                         Oid *types, Datum *values, int expected)
{
    if (sink_plans[query] == NULL) {
        const char *sql = laplace_sql_query_text(sink_keys[query]);
        if (sql == NULL) sink_invalid("missing governed query");
        /* Grant includes actual prepare and execute operations, including the
         * cold backend path. SPI_keepplan does not execute a database query. */
        sink_operation(s);
        SPIPlanPtr plan = SPI_prepare(sql, nargs, types);
        if (plan == NULL || SPI_keepplan(plan) != 0)
            elog(ERROR, "generated stage sink: cannot retain query %s", sink_keys[query]);
        sink_plans[query] = plan;
    }
    sink_operation(s);
    int result = SPI_execute_plan(sink_plans[query], values, NULL, false, 0);
    if (result != expected)
        elog(ERROR, "generated stage sink: %s failed (%s)", sink_keys[query],
             SPI_result_code_string(result));
}

static void sink_clear_result(void)
{
    if (SPI_tuptable != NULL) SPI_freetuptable(SPI_tuptable);
}

static int64 sink_scalar(void)
{
    bool is_null;
    if (SPI_processed != 1 || SPI_tuptable == NULL)
        sink_invalid("expected one database result");
    Datum value = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &is_null);
    if (is_null) sink_invalid("unexpected null database result");
    int64 result = DatumGetInt64(value);
    sink_clear_result();
    return result;
}

static Oid sink_array_type(Oid type)
{
    switch (type) {
    case BYTEAOID: return BYTEAARRAYOID;
    case INT2OID: return INT2ARRAYOID;
    case INT4OID: return INT4ARRAYOID;
    case INT8OID: return INT8ARRAYOID;
    case FLOAT8OID: return FLOAT8ARRAYOID;
    case TIMESTAMPTZOID: return TIMESTAMPTZARRAYOID;
    case BOOLOID: return BOOLARRAYOID;
    default: sink_invalid("unsupported array type");
    }
}

static Datum sink_bytea(SinkState *s, const void *value, size_t length)
{
    size_t bytes = sink_add(VARHDRSZ,length);
    bytea *out = sink_alloc(s,bytes);
    SET_VARSIZE(out,bytes);
    if (length) memcpy(VARDATA(out),value,length);
    return PointerGetDatum(out);
}

static Datum sink_array(SinkState *s, Datum *values, bool *nulls, size_t count, Oid type)
{
    int16 width;
    bool byval;
    char align;
    size_t payload = 0;
    if (count > INT_MAX) sink_invalid("array element limit exceeded");
    if (type == BYTEAOID) { width=-1; byval=false; align=TYPALIGN_INT; }
    else if (type == INT2OID) { width=2; byval=true; align=TYPALIGN_SHORT; }
    else if (type == INT4OID) { width=4; byval=true; align=TYPALIGN_INT; }
    else if (type == BOOLOID) { width=1; byval=true; align=TYPALIGN_CHAR; }
    else { width=8; byval=true; align=TYPALIGN_DOUBLE; }
    for (size_t i=0;i<count;++i) {
        if (nulls != NULL && nulls[i]) continue;
        size_t item = width < 0 ? VARSIZE_ANY(DatumGetPointer(values[i])) : (size_t)width;
        /* Include worst-case MAXALIGN padding, independent of host alignment. */
        payload = sink_add(payload,sink_add(item,MAXIMUM_ALIGNOF-1));
    }
    size_t bytes = sink_add(ARR_OVERHEAD_WITHNULLS(1,(int)count),payload);
    sink_charge(s,bytes);
    int dims[1]={(int)count}, lbs[1]={1};
    return PointerGetDatum(construct_md_array(values,nulls,1,dims,lbs,type,width,byval,align));
}

static Datum sink_field_value(SinkState *s, const SinkField *field, Oid type)
{
    if (type == BYTEAOID) return sink_bytea(s,field->data,(size_t)field->length);
    if (type == FLOAT8OID) {
        uint64 bits=sink_word(field->data,8);
        double value;
        memcpy(&value,&bits,8);
        return Float8GetDatum(value);
    }
    if (type == BOOLOID) return BoolGetDatum(field->data[0] != 0);
    if (type == INT2OID) return Int16GetDatum((int16)sink_word(field->data,2));
    if (type == INT4OID) return Int32GetDatum((int32)sink_word(field->data,4));
    return Int64GetDatum(sink_integer(field));
}

static Datum sink_column(SinkState *s, unsigned table, unsigned column, size_t count)
{
    Datum *values = sink_alloc(s,sink_multiply(count,sizeof(*values)));
    bool *nulls = sink_alloc(s,sink_multiply(count,sizeof(*nulls)));
    size_t at=0;
    SinkTable *t=&s->tables[table];
    for (size_t i=0;i<t->count;++i) {
        SinkRow *row=t->index[i];
        if (row->duplicate || (table == 0 && row->present)) continue;
        if (at == count) sink_invalid("column count mismatch");
        const SinkField *field=&row->fields[column];
        nulls[at]=field->length < 0;
        if (!nulls[at]) values[at]=sink_field_value(s,field,sink_types[table][column]);
        ++at;
    }
    if (at != count) sink_invalid("column count mismatch");
    return sink_array(s,values,nulls,count,sink_types[table][column]);
}

static void sink_insert(SinkState *s, unsigned table)
{
    size_t count=0;
    SinkTable *t=&s->tables[table];
    for (size_t i=0;i<t->count;++i)
        if (!t->rows[i].duplicate && !(table == 0 && t->rows[i].present)) ++count;
    if (count == 0) return;
    Datum values[14];
    Oid types[14];
    for (unsigned i=0;i<sink_column_counts[table];++i) {
        values[i]=sink_column(s,table,i,count);
        types[i]=sink_array_type(sink_types[table][i]);
    }
    /* Reserve returned heap tuples and ID detoast copies before execution.
     * Query executor/shared buffers remain PostgreSQL-owned (see header). */
    sink_charge(s,sink_multiply(count,sizeof(HeapTupleData)+128));
    sink_execute(s,(enum SinkQuery)(SQ_ENTITIES+table),(int)sink_column_counts[table],
                 types,values,SPI_OK_INSERT_RETURNING);
    if (SPI_processed > count || (SPI_processed && SPI_tuptable == NULL))
        sink_invalid("invalid INSERT RETURNING cardinality");
    for (uint64 i=0;i<SPI_processed;++i) {
        bool is_null;
        Datum value=SPI_getbinval(SPI_tuptable->vals[i],SPI_tuptable->tupdesc,1,&is_null);
        if (is_null) sink_invalid("null inserted identity");
        bytea *bytes=DatumGetByteaPP(value);
        if (VARSIZE_ANY_EXHDR(bytes) != 16) sink_invalid("invalid inserted identity");
        hash128_t id;
        memcpy(&id,VARDATA_ANY(bytes),16);
        SinkRow *row=sink_find(t,&id);
        if (row == NULL || row->accepted || row->duplicate || (table == 0 && row->present))
            sink_invalid("unexpected inserted identity");
        row->accepted=true;
        ++s->receipt.inserted_rows[table];
    }
    sink_clear_result();
}

typedef struct SinkReferences {
    SinkState *state;
    hash128_t *ids;
    size_t count,capacity;
} SinkReferences;

static void sink_reference(SinkReferences *references,const hash128_t *id)
{
    if (references->count == references->capacity) sink_invalid("reference count mismatch");
    references->ids[references->count++]=*id;
}

static int sink_reference_vertex(void *context,size_t ordinal,const hash128_t *id,
                                 size_t run,uint64 flags)
{
    (void)ordinal; (void)run; (void)flags;
    sink_reference(context,id);
    return 0;
}

static int sink_compare_hash(const void *a,const void *b) { return memcmp(a,b,16); }

static bool sink_has_hash(const hash128_t *ids,size_t count,const hash128_t *id)
{
    return count != 0 && bsearch(id,ids,count,sizeof(*ids),sink_compare_hash) != NULL;
}

static void sink_presence(SinkState *s,const physicality_descriptor_input_t *bodies,size_t count)
{
    size_t capacity=sink_add(sink_multiply(s->tables[0].count,2),
                    sink_add(count,sink_add(s->receipt.stored_vertices,
                                           sink_multiply(s->tables[2].count,4))));
    SinkReferences refs={s,sink_alloc(s,sink_multiply(capacity,sizeof(hash128_t))),0,capacity};
    for (size_t i=0;i<s->tables[0].count;++i) {
        SinkRow *row=&s->tables[0].rows[i];
        hash128_t source;
        memcpy(&source,row->fields[3].data,16);
        sink_reference(&refs,&row->id);
        sink_reference(&refs,&source);
        /* Every generated E has an authenticated ordinary Content body or an
         * exact descriptor-retention manifest. Metadata is not a naked E. */
        hash128_t placement;
        laplace_physicality_id_compute(row->id,1,&placement);
        if (sink_find(&s->tables[1],&placement) == NULL) {
            laplace_physicality_id_compute(row->id,PHYSICALITY_DESCRIPTOR_RETENTION_TYPE,&placement);
            if (sink_find(&s->tables[1],&placement) == NULL)
                sink_invalid("generated entity has no supplied Content or retention body");
        }
    }
    for (size_t i=0;i<count;++i) {
        if (bodies[i].type != 1 && bodies[i].type != PHYSICALITY_DESCRIPTOR_RETENTION_TYPE)
            sink_invalid("generated stage contains unsupported physicality");
        sink_reference(&refs,&bodies[i].entity_id);
        if (trajectory_visit_vertices(bodies[i].trajectory_xyzm,bodies[i].trajectory_vertices,
                                      sink_reference_vertex,&refs) != 0)
            sink_invalid("invalid generated reference manifest");
    }
    const unsigned reference_columns[4]={1,3,4,5};
    for (size_t i=0;i<s->tables[2].count;++i) {
        const SinkRow *row=&s->tables[2].rows[i];
        for (unsigned j=0;j<4;++j) {
            hash128_t id;
            memcpy(&id,row->fields[reference_columns[j]].data,16);
            sink_reference(&refs,&id);
        }
    }
    qsort(refs.ids,refs.count,sizeof(hash128_t),sink_compare_hash);
    size_t unique=0;
    for (size_t i=0;i<refs.count;++i)
        if (unique == 0 || !hash128_equals(&refs.ids[unique-1],&refs.ids[i]))
            refs.ids[unique++]=refs.ids[i];
    if (unique == 0) return;
    Datum *values=sink_alloc(s,sink_multiply(unique,sizeof(Datum)));
    for (size_t i=0;i<unique;++i) values[i]=sink_bytea(s,&refs.ids[i],16);
    Datum array=sink_array(s,values,NULL,unique,BYTEAOID);
    Oid type=BYTEAARRAYOID;
    sink_charge(s,sink_multiply(unique,sizeof(HeapTupleData)+128));
    sink_execute(s,SQ_PRESENCE,1,&type,&array,SPI_OK_SELECT);
    if (SPI_processed > unique || (SPI_processed && SPI_tuptable == NULL))
        sink_invalid("invalid entity presence cardinality");
    hash128_t *present=sink_alloc(s,sink_multiply((size_t)SPI_processed,sizeof(hash128_t)));
    size_t present_count=(size_t)SPI_processed;
    for (size_t i=0;i<present_count;++i) {
        bool is_null;
        Datum value=SPI_getbinval(SPI_tuptable->vals[i],SPI_tuptable->tupdesc,1,&is_null);
        if (is_null) sink_invalid("null present identity");
        bytea *bytes=DatumGetByteaPP(value);
        if (VARSIZE_ANY_EXHDR(bytes) != 16) sink_invalid("invalid present identity");
        memcpy(&present[i],VARDATA_ANY(bytes),16);
        if (!sink_has_hash(refs.ids,unique,&present[i])) sink_invalid("unrequested entity presence");
        SinkRow *row=sink_find(&s->tables[0],&present[i]);
        if (row != NULL) row->present=true;
    }
    sink_clear_result();
    qsort(present,present_count,sizeof(hash128_t),sink_compare_hash);
    for (size_t i=0;i<unique;++i) {
        if (sink_find(&s->tables[0],&refs.ids[i]) != NULL ||
            sink_has_hash(present,present_count,&refs.ids[i])) continue;
        ereport(ERROR,(errcode(ERRCODE_FOREIGN_KEY_VIOLATION),
                       errmsg("generated stage sink: referenced entity is not admitted")));
    }
}

/* Group exact accepted observations, never average opponent ratings or RD.
 * The canonical consensus owner consumes the entire rating-period group list. */
static int sink_compare_cells(const void *a,const void *b)
{
    const SinkRow *left=*(SinkRow *const *)a,*right=*(SinkRow *const *)b;
    const unsigned columns[]={2,1,3,11,10,0};
    for (unsigned i=0;i<lengthof(columns);++i) {
        unsigned column=columns[i];
        int result=memcmp(left->fields[column].data,right->fields[column].data,
                          (size_t)left->fields[column].length);
        if (result) return result;
    }
    return 0;
}

static bool sink_same_cell(const SinkRow *a,const SinkRow *b)
{
    return sink_field_equal(&a->fields[1],&b->fields[1]) &&
           sink_field_equal(&a->fields[2],&b->fields[2]) &&
           sink_field_equal(&a->fields[3],&b->fields[3]);
}

static int64 sink_sum(int64 a,int64 b)
{
    int64 out;
    if (pg_add_s64_overflow(a,b,&out)) sink_invalid("exact evidence aggregate overflow");
    return out;
}

static void sink_fold(SinkState *s)
{
    size_t count=s->receipt.inserted_rows[2];
    if (count == 0) return;
    SinkRow **rows=sink_alloc(s,sink_multiply(count,sizeof(*rows)));
    size_t n=0;
    for (size_t i=0;i<s->tables[2].count;++i)
        if (s->tables[2].rows[i].accepted) rows[n++]=&s->tables[2].rows[i];
    if (n != count) sink_invalid("accepted witness count mismatch");
    qsort(rows,count,sizeof(*rows),sink_compare_cells);
    Datum *columns[13];
    for (unsigned i=0;i<13;++i)
        columns[i]=sink_alloc(s,sink_multiply(count+1,sizeof(Datum)));
    size_t cells=0,groups=0;
    int64 observations=0;
    for (size_t start=0;start<count;) {
        size_t end=start+1;
        while (end<count && sink_same_cell(rows[start],rows[end])) ++end;
        SinkRow *first=rows[start];
        columns[0][cells]=sink_field_value(s,&first->fields[1],BYTEAOID);
        columns[1][cells]=sink_field_value(s,&first->fields[2],BYTEAOID);
        columns[2][cells]=sink_field_value(s,&first->fields[3],BYTEAOID);
        columns[3][cells]=Int64GetDatum(sink_integer(&first->fields[10]));
        columns[7][cells]=Int64GetDatum(sink_integer(&first->fields[11]));
        columns[8][cells]=Int64GetDatum((int64)groups);
        int64 games=0,sum=0,time=sink_integer(&first->fields[7]);
        for (size_t at=start;at<end;) {
            size_t next=at+1;
            int64 group_games=sink_integer(&rows[at]->fields[8]);
            int64 group_sum=sink_integer(&rows[at]->fields[9]);
            int64 group_time=sink_integer(&rows[at]->fields[7]);
            while (next<end && sink_field_equal(&rows[at]->fields[10],&rows[next]->fields[10]) &&
                              sink_field_equal(&rows[at]->fields[11],&rows[next]->fields[11])) {
                group_games=sink_sum(group_games,sink_integer(&rows[next]->fields[8]));
                group_sum=sink_sum(group_sum,sink_integer(&rows[next]->fields[9]));
                int64 observed=sink_integer(&rows[next]->fields[7]);
                if (observed>group_time) group_time=observed;
                ++next;
            }
            columns[9][groups]=Int64GetDatum(sink_integer(&rows[at]->fields[11]));
            columns[10][groups]=Int64GetDatum(sink_integer(&rows[at]->fields[10]));
            columns[11][groups]=Int64GetDatum(group_games);
            columns[12][groups]=Int64GetDatum(group_sum);
            ++groups;
            games=sink_sum(games,group_games); sum=sink_sum(sum,group_sum);
            if (group_time>time) time=group_time;
            at=next;
        }
        columns[4][cells]=Int64GetDatum(games);
        columns[5][cells]=Int64GetDatum(sum);
        columns[6][cells]=TimestampTzGetDatum(time);
        observations=sink_sum(observations,games);
        ++cells; start=end;
    }
    columns[8][cells]=Int64GetDatum((int64)groups);
    Datum values[13]; Oid types[13];
    for (unsigned i=0;i<13;++i) {
        Oid type=i<3?BYTEAOID:(i==6?TIMESTAMPTZOID:INT8OID);
        size_t elements=i<8?cells:(i==8?cells+1:groups);
        types[i]=sink_array_type(type);
        values[i]=sink_array(s,columns[i],NULL,elements,type);
    }
    sink_execute(s,SQ_FOLD,13,types,values,SPI_OK_SELECT);
    int64 affected=sink_scalar();
    if (affected<0 || (uint64)affected!=cells) sink_invalid("consensus owner returned wrong cell count");
    s->receipt.folded_cells=cells;
    s->receipt.folded_observations=observations;
    /* Both endpoints respond to this typed relation. Native mask owner dedups
     * identical pairs and deposits actual masks using permission-aware writes. */
    Datum *entities=sink_alloc(s,sink_multiply(cells,2*sizeof(Datum)));
    Datum *relations=sink_alloc(s,sink_multiply(cells,2*sizeof(Datum)));
    for (size_t i=0;i<cells;++i) {
        entities[2*i]=columns[0][i]; entities[2*i+1]=columns[2][i];
        relations[2*i]=columns[1][i]; relations[2*i+1]=columns[1][i];
    }
    Datum mask_values[2]={sink_array(s,entities,NULL,2*cells,BYTEAOID),
                          sink_array(s,relations,NULL,2*cells,BYTEAOID)};
    Oid mask_types[2]={BYTEAARRAYOID,BYTEAARRAYOID};
    sink_execute(s,SQ_MASKS,2,mask_types,mask_values,SPI_OK_SELECT);
    s->receipt.mask_rows=sink_scalar();
    if (s->receipt.mask_rows<0) sink_invalid("negative native mask receipt");
    s->receipt.mask_pairs=2*cells;
}

static void sink_require_isolation(void)
{
    if (XactIsoLevel != XACT_READ_COMMITTED)
        ereport(ERROR,(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                       errmsg("generated stage sink requires READ COMMITTED")));
}

void laplace_generated_stage_sink_lock(void)
{
    sink_require_isolation();
    SinkState state;
    memset(&state,0,sizeof(state));
    state.limits.maximum_operations=2;
    if (SPI_connect()!=SPI_OK_CONNECT) elog(ERROR,"generated stage sink: SPI_connect failed");
    sink_execute(&state,SQ_LOCK,0,NULL,NULL,SPI_OK_SELECT);
    sink_clear_result();
    if (SPI_finish()!=SPI_OK_FINISH) elog(ERROR,"generated stage sink: SPI_finish failed");
}

static void sink_validate_bodies(SinkState *s,const intent_stage_t *const *stages,size_t stage_count)
{
    size_t count=0,vertices=0,logical=0;
    physicality_descriptor_status_t status=physicality_descriptor_stages_preflight(
        stages,stage_count,s->limits.maximum_logical_occurrences,&count,&vertices,&logical);
    if (status == PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED)
        ereport(ERROR,(errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                       errmsg("generated stage sink: logical work grant exhausted")));
    if (status != PHYSICALITY_DESCRIPTOR_OK || count != s->tables[1].count)
        sink_invalid("invalid generated physicality preflight");
    s->receipt.logical_work=logical;
    s->receipt.stored_vertices=vertices;
    status=physicality_descriptor_capture_stage_rows(stages,stage_count,
        s->limits.maximum_bytes-s->bytes,&s->capture);
    if (status == PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED)
        ereport(ERROR,(errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                       errmsg("generated stage sink: native export byte grant exhausted")));
    if (status != PHYSICALITY_DESCRIPTOR_OK) sink_invalid("invalid generated physicality export");
    sink_charge(s,physicality_descriptor_capture_bytes(s->capture));
    const physicality_descriptor_input_t *bodies=physicality_descriptor_capture_inputs(s->capture,&count);
    for (size_t i=0;i<count;++i) {
        const physicality_descriptor_input_t *body=&bodies[i];
        if ((body->type != 1 && body->type != PHYSICALITY_DESCRIPTOR_RETENTION_TYPE) || (!body->alignment_residual_is_null &&
            (!isfinite(body->alignment_residual) || body->alignment_residual < 0)) ||
            (!body->source_dim_is_null && body->source_dim <= 0))
            sink_invalid("invalid generated Content or retention metadata");
        for (unsigned axis=0;axis<4;++axis)
            if (!isfinite(body->coord[axis])) sink_invalid("nonfinite generated coordinate");
        /* One bounded canonical identity pass, charged by preflight before any
         * expanded-RLE hash work. Other scans visit stored vertices only. */
        if (laplace_physicality_manifest_validate(&body->entity_id,body->type,
            body->trajectory_xyzm,body->trajectory_vertices,body->n_constituents) != 0)
            sink_invalid("generated Content identity or retention identity disagrees with its exact manifest");
        if (body->trajectory_vertices == 0) {
            uint32_t atom;
            hash128_t id;
            double coord[4]; hilbert128_t hilbert;
            if (!codepoint_table_id_index_ready() ||
                codepoint_table_lookup_id(&body->entity_id,&atom) != 0 ||
                codepoint_table_resolve_atom(atom,&id,coord,&hilbert) != 0 ||
                !hash128_equals(&id,&body->entity_id) || memcmp(coord,body->coord,sizeof(coord)) != 0 ||
                memcmp(&hilbert,&body->hilbert_index,sizeof(hilbert)) != 0 ||
                !body->alignment_residual_is_null || !body->source_dim_is_null)
                sink_invalid("empty Content body is not the exact loaded floor atom");
        }
    }
    sink_presence(s,bodies,count);
}

void laplace_generated_stage_sink(const intent_stage_t *const *stages,
    size_t stage_count,const LaplaceGeneratedStageSinkLimits *limits,
    LaplaceGeneratedStageSinkReceipt *out_receipt)
{
    if (limits == NULL || out_receipt == NULL || (stage_count && stages == NULL))
        sink_invalid("missing sink argument");
    if (stage_count > LAPLACE_GENERATED_STAGE_SINK_MAX_STAGES)
        sink_invalid("generated sink accepts at most four source declaration, vocabulary and generated stages");
    /* Reentrant: session callers acquire this same lock before their row lock.
     * Other callers still get the common writer ordering and isolation guard. */
    sink_require_isolation();
    MemoryContext caller=CurrentMemoryContext;
    MemoryContext owner=AllocSetContextCreate(caller,"Generated stage sink",ALLOCSET_DEFAULT_SIZES);
    PG_TRY();
    {
        MemoryContextSwitchTo(owner);
        SinkState *s=palloc0(sizeof(*s));
        s->limits=*limits;
        sink_charge(s,sizeof(*s));
        s->cleanup.func=sink_cleanup; s->cleanup.arg=s;
        MemoryContextRegisterResetCallback(owner,&s->cleanup);
        size_t total=0;
        for (size_t i=0;i<stage_count;++i) {
            if (stages[i] == NULL || intent_stage_allocation_failed(stages[i]))
                sink_invalid("missing or allocation-failed native stage");
            size_t counts[3]={intent_stage_entity_count(stages[i]),intent_stage_physicality_count(stages[i]),
                              intent_stage_attestation_count(stages[i])};
            for (unsigned t=0;t<3;++t) {
                s->receipt.input_rows[t]=sink_add(s->receipt.input_rows[t],counts[t]);
                total=sink_add(total,counts[t]);
                size_t bytes;
                (void)intent_stage_tuple_ptr(stages[i],(intent_stage_table_t)(t+1),&bytes);
                s->receipt.tuple_bytes=sink_add(s->receipt.tuple_bytes,bytes);
            }
        }
        if (total>limits->maximum_rows)
            ereport(ERROR,(errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                           errmsg("generated stage sink: row grant exhausted")));
        sink_parse(s,stages,stage_count);
        if (SPI_connect()!=SPI_OK_CONNECT) elog(ERROR,"generated stage sink: SPI_connect failed");
        sink_execute(s,SQ_LOCK,0,NULL,NULL,SPI_OK_SELECT);
        sink_clear_result();
        /* SPI-owned temporary allocations remain under owner until SPI_finish;
         * exported native allocations are covered by the reset callback. */
        sink_validate_bodies(s,stages,stage_count);
        if (total != 0) {
            sink_execute(s,SQ_EPOCH,0,NULL,NULL,SPI_OK_SELECT);
            (void)sink_scalar();
            for (unsigned t=0;t<3;++t) sink_insert(s,t);
            sink_fold(s);
        }
        if (SPI_finish()!=SPI_OK_FINISH) elog(ERROR,"generated stage sink: SPI_finish failed");
        *out_receipt=s->receipt;
        MemoryContextSwitchTo(caller);
        MemoryContextDelete(owner);
    }
    PG_CATCH();
    {
        MemoryContextSwitchTo(caller);
        MemoryContextDelete(owner);
        PG_RE_THROW();
    }
    PG_END_TRY();
}

#ifdef LAPLACE_SUBSTRATE_TESTING
/* No installed SQL entry. Regression fixtures may bind this adapter in pg_temp
 * to the current execution library. It only imports the actual materializer's
 * three tuple stages and delegates to the same production sink. */
typedef struct SinkTestStages { intent_stage_t *items[3]; } SinkTestStages;
static void sink_test_stages_free(void *argument)
{
    SinkTestStages *stages=argument;
    for (unsigned i=0;i<3;++i) { intent_stage_free(stages->items[i]); stages->items[i]=NULL; }
}

PG_FUNCTION_INFO_V1(pg_laplace_generated_stage_sink_test);
Datum pg_laplace_generated_stage_sink_test(PG_FUNCTION_ARGS)
{
    for (int i=0;i<7;++i) if (PG_ARGISNULL(i)) sink_invalid("null regression adapter argument");
    int64 rows=PG_GETARG_INT64(3),bytes=PG_GETARG_INT64(4),logical=PG_GETARG_INT64(5);
    int32 operations=PG_GETARG_INT32(6);
    if (rows<0 || bytes<=0 || logical<0 || operations<0 ||
        (uint64)bytes>SIZE_MAX || (uint64)rows>SIZE_MAX || (uint64)logical>SIZE_MAX)
        sink_invalid("invalid regression adapter grant");
    SinkState budget;
    memset(&budget,0,sizeof(budget)); budget.limits.maximum_bytes=(size_t)bytes;
    SinkTestStages *stages=sink_alloc(&budget,sizeof(*stages));
    MemoryContextCallback *cleanup=sink_alloc(&budget,sizeof(*cleanup));
    cleanup->func=sink_test_stages_free; cleanup->arg=stages;
    MemoryContextRegisterResetCallback(CurrentMemoryContext,cleanup);
    Datum *columns[3]; bool *nulls[3]; int lengths[3];
    for (unsigned i=0;i<3;++i) {
        /* Bound raw array detoast before materialization. Metadata allocations
         * have a fixed 3-element upper bound after the shape check. */
        sink_charge(&budget,toast_raw_datum_size(PG_GETARG_DATUM(i)));
        ArrayType *array=PG_GETARG_ARRAYTYPE_P(i);
        if (ARR_NDIM(array)!=1 || ARR_DIMS(array)[0]!=3 || ARR_ELEMTYPE(array)!=BYTEAOID)
            sink_invalid("regression adapter requires three tuple stages");
        sink_charge(&budget,3*(sizeof(Datum)+sizeof(bool)));
        deconstruct_array(array,BYTEAOID,-1,false,TYPALIGN_INT,&columns[i],&nulls[i],&lengths[i]);
    }
    for (unsigned i=0;i<3;++i) {
        bytea *parts[3];
        for (unsigned t=0;t<3;++t) {
            if (nulls[t][i]) sink_invalid("null regression stage tuple blob");
            sink_charge(&budget,toast_raw_datum_size(columns[t][i]));
            parts[t]=DatumGetByteaPP(columns[t][i]);
        }
        int status=intent_stage_from_tuple_bytes(
            (const uint8 *)VARDATA_ANY(parts[0]),VARSIZE_ANY_EXHDR(parts[0]),
            (const uint8 *)VARDATA_ANY(parts[1]),VARSIZE_ANY_EXHDR(parts[1]),
            (const uint8 *)VARDATA_ANY(parts[2]),VARSIZE_ANY_EXHDR(parts[2]),
            budget.limits.maximum_bytes-budget.bytes,&stages->items[i]);
        if (status!=0) sink_invalid("invalid or over-budget regression tuple stage");
        size_t peak=intent_stage_memory_peak_bytes(stages->items[i]);
        if (peak>budget.limits.maximum_bytes-budget.bytes) sink_invalid("native import peak exceeds grant");
        sink_charge(&budget,intent_stage_memory_bytes(stages->items[i]));
    }
    LaplaceGeneratedStageSinkLimits limits={(size_t)rows,budget.limits.maximum_bytes-budget.bytes,
                                           (size_t)logical,(uint32)operations};
    LaplaceGeneratedStageSinkReceipt receipt;
    laplace_generated_stage_sink((const intent_stage_t *const *)stages->items,3,&limits,&receipt);
    sink_test_stages_free(stages);
    Datum result[12]={Int64GetDatum((int64)receipt.inserted_rows[0]),
        Int64GetDatum((int64)receipt.inserted_rows[1]),Int64GetDatum((int64)receipt.inserted_rows[2]),
        Int64GetDatum((int64)receipt.folded_cells),Int64GetDatum(receipt.folded_observations),
        Int64GetDatum((int64)receipt.mask_pairs),Int64GetDatum(receipt.mask_rows),
        Int64GetDatum((int64)receipt.tuple_bytes),Int64GetDatum((int64)receipt.logical_work),
        Int64GetDatum((int64)receipt.stored_vertices),Int64GetDatum((int64)receipt.peak_reserved_bytes),
        Int64GetDatum(receipt.operations)};
    PG_RETURN_ARRAYTYPE_P(construct_array(result,12,INT8OID,8,true,TYPALIGN_DOUBLE));
}
#endif
