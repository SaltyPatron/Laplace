#include "postgres.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "tcop/dest.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "observation_read.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "laplace/core/sql_catalog.h"
#include "laplace/core/qualifier_law.h"

PG_FUNCTION_INFO_V1(pg_laplace_observation_bindings);

typedef struct OperandEntry
{
    hash128_t id;
    int first;
} OperandEntry;

typedef struct ObservationReceiver
{
    DestReceiver receiver;
    HTAB *operands;
    int *next;
    int roles;
    LaplaceObservationVisitor visit;
    void *context;
    bool native;
} ObservationReceiver;

static SPIPlanPtr observation_plan;
static SPIPlanPtr observation_cell_plan;
static SPIPlanPtr native_observation_plan;
static SPIPlanPtr native_observation_cell_plan;

static void
read_id(Datum value, hash128_t *id)
{
    bytea *bytes = DatumGetByteaPP(value);
    if (VARSIZE_ANY_EXHDR(bytes) != sizeof(*id))
        ereport(ERROR, (errmsg("observation bindings: ids must be 16 bytes")));
    memcpy(id, VARDATA_ANY(bytes), sizeof(*id));
}

/* Digest of one attestation row: row id plus source, context, outcome and
 * occurrence count encoded explicitly, so two rows sharing an id but differing
 * in testimony remain distinct provenance members of the channel's Merkle
 * root. */
hash128_t
laplace_observation_provenance(const LaplaceObservation *row)
{
    unsigned char bytes[16 * 5 + 12 + 1 + 40 + 32];
    hash128_t domain;
    hash128_t zero;
    hash128_t result;
    const hash128_t *source;
    const hash128_t *context;
    Size at = 0;
    uint16 outcome = (uint16) row->outcome;
    uint64 occurrences = (uint64) row->occurrences;

    hash128_blake3_str(row->native?"laplace:native-testimony-witness:v1":"laplace:query-provenance-witness:v1", &domain);
    hash128_zero(&zero);
    source = row->source_null ? &zero : &row->source;
    context = row->context_null ? &zero : &row->context;

#define PROVENANCE_APPEND(value) \
    do { memcpy(bytes + at, &(value), sizeof(value)); at += sizeof(value); } while (0)
    PROVENANCE_APPEND(domain);
    if(row->native){ PROVENANCE_APPEND(row->claim); } else { PROVENANCE_APPEND(row->id); }
    PROVENANCE_APPEND(*source);
    PROVENANCE_APPEND(*context);
#undef PROVENANCE_APPEND
    bytes[at++] = row->source_null ? 0 : 1;
    bytes[at++] = row->context_null ? 0 : 1;
    bytes[at++] = (unsigned char) (outcome >> 8);
    bytes[at++] = (unsigned char) outcome;
    bytes[at++] = (unsigned char) (occurrences >> 56);
    bytes[at++] = (unsigned char) (occurrences >> 48);
    bytes[at++] = (unsigned char) (occurrences >> 40);
    bytes[at++] = (unsigned char) (occurrences >> 32);
    bytes[at++] = (unsigned char) (occurrences >> 24);
    bytes[at++] = (unsigned char) (occurrences >> 16);
    bytes[at++] = (unsigned char) (occurrences >> 8);
    bytes[at++] = (unsigned char) occurrences;

    if(row->native){
        bytes[at++]=row->dependence_null?0:1;
        memcpy(bytes+at,row->dependence_null?&zero:&row->dependence,sizeof(hash128_t));at+=sizeof(hash128_t);
        uint64 score,trust;memcpy(&score,&row->score,sizeof(score));memcpy(&trust,&row->trust,sizeof(trust));
        uint64 fields[]={(uint64)row->ordinal,(uint64)row->tokens,score,trust,(uint64)row->position};
        for(int i=0;i<lengthof(fields);i++)for(int shift=56;shift>=0;shift-=8)
            bytes[at++]=(unsigned char)(fields[i]>>shift);
        memcpy(bytes+at,row->qualifiers,sizeof(row->qualifiers));at+=sizeof(row->qualifiers);
    }

    hash128_blake3(bytes, at, &result);
    return result;
}

static void
validate_filter(ArrayType *filter)
{
    ArrayIterator iterator;
    Datum value;
    bool isnull;
    if (!filter) return;
    if (ARR_NDIM(filter) > 1 || ARR_ELEMTYPE(filter) != BYTEAOID)
        ereport(ERROR, (errmsg("observation bindings: ids must be a 1-D bytea array")));
    iterator = array_create_iterator(filter, 0, NULL);
    while (array_iterate(iterator, &value, &isnull))
    {
        hash128_t id;
        if (!isnull) read_id(value, &id);
    }
    array_free_iterator(iterator);
}

static bool
receive_observation(TupleTableSlot *slot, DestReceiver *destination)
{
    ObservationReceiver *receiver = (ObservationReceiver *) destination;
    LaplaceObservation row = {0};
    hash128_t *ids[] = {&row.id, &row.subject, &row.type,
                       &row.object, &row.source, &row.context};
    bool nulls[16];
    Datum values[16];
    OperandEntry *operand;
    for (int i = 0; i < (receiver->native?16:9); ++i)
        values[i] = slot_getattr(slot, i + 1, &nulls[i]);
    if (nulls[0] || nulls[1] || nulls[2] || (!receiver->native && nulls[6]) || nulls[7])
        elog(ERROR, "observation bindings: missing witness identity or outcome");
    for (int i = 0; i < 6; ++i)
        if (!nulls[i]) read_id(values[i], ids[i]);
    row.object_null = nulls[3];
    row.source_null = nulls[4];
    row.context_null = nulls[5];
    row.outcome = nulls[6]?LAPLACE_OBSERVATION_SCORED:DatumGetInt16(values[6]);
    row.occurrences = DatumGetInt64(values[7]);
    row.native=receiver->native;row.dependence_null=true;
    row.score=(double)row.outcome/2.0;row.tokens=row.occurrences;
    if(receiver->native){
        for(int i=9;i<16;i++)if(i!=13 && nulls[i])
            elog(ERROR,"Native observation has a missing series field");
        row.score=DatumGetFloat8(values[9]);row.tokens=DatumGetInt64(values[10]);
        row.ordinal=DatumGetInt64(values[11]);row.trust=DatumGetFloat8(values[12]);
        row.position=DatumGetInt32(values[15]);
        row.dependence_null=nulls[13];if(!nulls[13])read_id(values[13],&row.dependence);
        if(DatumGetBool(values[14])){
            int bit=laplace_qualifier_bit("derivation","calculation");
            if(bit<0 || bit>=256)elog(ERROR,"Native calculation qualifier is undeclared");
            row.qualifiers[bit/8]|=1u<<(bit%8);
        }
    }
    if (!nulls[8])
    {
        bytea *mask = DatumGetByteaPP(values[8]);
        if (VARSIZE_ANY_EXHDR(mask) != sizeof(row.qualifiers))
            elog(ERROR, "observation bindings: qualifier masks must be 32 bytes");
        memcpy(row.qualifiers, VARDATA_ANY(mask), sizeof(row.qualifiers));
    }
    if(row.native){row.claim=row.id;row.id=laplace_observation_provenance(&row);}
    operand = (receiver->roles & 1)
        ? hash_search(receiver->operands, &row.subject, HASH_FIND, NULL) : NULL;
    if (operand)
        for (int i = operand->first; i >= 0; i = receiver->next[i])
            receiver->visit(i + 1, 1, &row, receiver->context);
    if (!row.object_null && (receiver->roles & 2))
    {
        OperandEntry *object = hash_search(receiver->operands, &row.object, HASH_FIND, NULL);
        if (!operand && !object)
            elog(ERROR, "observation bindings: witness escaped the operand set");
        if (object)
            for (int i = object->first; i >= 0; i = receiver->next[i])
                receiver->visit(i + 1, 2, &row, receiver->context);
    }
    else if (!operand)
        elog(ERROR, "observation bindings: witness escaped the operand set");
    CHECK_FOR_INTERRUPTS();
    return true;
}

static void
observation_startup(DestReceiver *destination, int operation, TupleDesc descriptor)
{
    const Oid types[] = {BYTEAOID, BYTEAOID, BYTEAOID, BYTEAOID,
                         BYTEAOID, BYTEAOID, INT2OID, INT8OID, BYTEAOID,
                         FLOAT8OID,INT8OID,INT8OID,FLOAT8OID,BYTEAOID,BOOLOID,INT4OID};
    ObservationReceiver *receiver=(ObservationReceiver *)destination;
    int columns=receiver->native?16:9;
    if (operation != CMD_SELECT || descriptor->natts != columns)
        elog(ERROR, "observation bindings: invalid result shape");
    for (int i = 0; i < columns; ++i)
        if (TupleDescAttr(descriptor, i)->atttypid != types[i])
            elog(ERROR, "observation bindings: invalid result column type");
}

bool
laplace_observation_qualified(const LaplaceObservation *row, const char *family, const char *value)
{
    const int bit = laplace_qualifier_bit(family, value);
    if (bit < 0)
        elog(ERROR, "observation bindings: undeclared qualifier %s/%s", family, value);
    return (row->qualifiers[bit / 8] & (1u << (bit % 8))) != 0;
}

static void
observation_shutdown(DestReceiver *destination)
{
    (void) destination;
}

static void
observation_read(ArrayType *operands, ArrayType *sources,
    ArrayType *types, int roles, const LaplaceObservationCell *cells, int cell_count,
    LaplaceObservationVisitor visitor, void *context)
{
    MemoryContext work, previous;
    HASHCTL ctl = {0};
    Datum *values;
    bool *nulls;
    int count;
    bool spi_top = false;
    hash128_t *unique;
    int unique_count = 0;
    ObservationReceiver receiver = {
        .receiver = {receive_observation, observation_startup,
                     observation_shutdown, observation_shutdown, DestNone},
        .roles = roles, .visit = visitor, .context = context
    };
    validate_filter(operands);
    validate_filter(sources);
    validate_filter(types);
    if (!operands || !visitor)
        elog(ERROR, "observation bindings: operands and visitor are required");
    if (roles < 0 || roles > 3)
        elog(ERROR, "observation bindings: roles must be 0 (none), 1 (subject), 2 (object), or 3 (both)");
    if (roles == 0) return;
    if (ArrayGetNItems(ARR_NDIM(operands), ARR_DIMS(operands)) == 0 ||
        (sources && ArrayGetNItems(ARR_NDIM(sources), ARR_DIMS(sources)) == 0) ||
        (types && ArrayGetNItems(ARR_NDIM(types), ARR_DIMS(types)) == 0)) return;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "observation bindings: SPI_connect failed");
    work = AllocSetContextCreate(CurrentMemoryContext, "observation bindings", ALLOCSET_DEFAULT_SIZES);
    previous = MemoryContextSwitchTo(work);
    deconstruct_array(operands, BYTEAOID, -1, false, TYPALIGN_INT, &values, &nulls, &count);
    unique = palloc(sizeof(hash128_t) * count);
    receiver.next = palloc(sizeof(int) * count);
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(OperandEntry);
    receiver.operands = hash_create("observation operands", count, &ctl, HASH_ELEM | HASH_BLOBS);
    for (int i = count - 1; i >= 0; --i)
    {
        hash128_t id;
        OperandEntry *entry;
        bool found;
        if (nulls[i]) continue;
        read_id(values[i], &id);
        entry = hash_search(receiver.operands, &id, HASH_ENTER, &found);
        receiver.next[i] = found ? entry->first : -1;
        entry->first = i;
        if (!found) unique[unique_count++] = id;
    }
    if (unique_count > 0)
    {
        ParamListInfo params = makeParamList(4);
        SPIExecuteOptions options = {.params = params, .read_only = true,
            .must_return_tuples = true, .dest = &receiver.receiver};
        int result;
        params->params[0].value = PointerGetDatum(hash128_array_from_ids(unique, unique_count));
        params->params[1].value = sources ? PointerGetDatum(sources) : (Datum) 0;
        params->params[2].value = types ? PointerGetDatum(types) : (Datum) 0;
        for (int i = 0; i < 3; ++i)
        {
            params->params[i].isnull = i == 1 ? sources == NULL : i == 2 && types == NULL;
            params->params[i].pflags = PARAM_FLAG_CONST;
            params->params[i].ptype = BYTEAARRAYOID;
        }
        params->params[3].value = Int32GetDatum(roles);
        params->params[3].isnull = false;
        params->params[3].pflags = PARAM_FLAG_CONST;
        params->params[3].ptype = INT4OID;
        if (cells)
        {
            HASHCTL cell_ctl = {0};
            cell_ctl.keysize = cell_ctl.entrysize = sizeof(LaplaceObservationCell);
            HTAB *seen = hash_create("observation exact cells", Max(cell_count,1),
                                     &cell_ctl, HASH_ELEM | HASH_BLOBS);
            ArrayBuildState *subjects = NULL, *relations = NULL, *objects = NULL;
            for (int i = 0; i < cell_count; ++i)
            {
                bool found;
                hash_search(seen, &cells[i], HASH_ENTER, &found);
                if (found) continue;
                subjects = accumArrayResult(subjects, hash128_to_datum(&cells[i].subject),
                                              false, BYTEAOID, work);
                relations = accumArrayResult(relations, hash128_to_datum(&cells[i].type),
                                               false, BYTEAOID, work);
                objects = accumArrayResult(objects, hash128_to_datum(&cells[i].object),
                                             false, BYTEAOID, work);
            }
            params->params[0].value = makeArrayResult(subjects, work);
            params->params[1].value = makeArrayResult(relations, work);
            params->params[2].value = makeArrayResult(objects, work);
            for (int i = 0; i < 3; ++i) params->params[i].isnull = false;
            params->params[3].value = sources ? PointerGetDatum(sources) : (Datum)0;
            params->params[3].isnull = sources == NULL;
            params->params[3].ptype = BYTEAARRAYOID;
        }
        SPIPlanPtr *plans[]={cells?&observation_cell_plan:&observation_plan,
            cells?&native_observation_cell_plan:&native_observation_plan};
        const char *names[]={cells?"evidence.observation_cells":"evidence.observation_bindings",
            cells?"native.observation_cells":"native.observation_bindings"};
        for(int face=0;face<2;face++){
            SPIPlanPtr *plan=plans[face];receiver.native=face==1;
            if(!*plan){
                Oid parameter_types[]={BYTEAARRAYOID,BYTEAARRAYOID,BYTEAARRAYOID,cells?BYTEAARRAYOID:INT4OID};
                const char *query=laplace_sql_query_text(names[face]);
                if(!query)elog(ERROR,"observation bindings: required static query is unavailable");
                *plan=SPI_prepare_cursor(query,4,parameter_types,CURSOR_OPT_PARALLEL_OK);
                if(!*plan || SPI_keepplan(*plan)!=0)elog(ERROR,"observation bindings: preparing static read failed");
            }
            result=SPI_execute_plan_extended(*plan,&options);
            /* SELECT to DestNone reports SPI_OK_UTILITY; startup checks shape. */
            if(result!=SPI_OK_SELECT && result!=SPI_OK_UTILITY)
                elog(ERROR,"observation bindings: read failed: %s",SPI_result_code_string(result));
        }
    }
    MemoryContextSwitchTo(previous);
    MemoryContextDelete(work);
    laplace_spi_finish(spi_top);
}

void
laplace_observation_read(ArrayType *operands, ArrayType *sources,
    ArrayType *types, int roles, LaplaceObservationVisitor visitor, void *context)
{
    observation_read(operands, sources, types, roles, NULL, 0, visitor, context);
}

void
laplace_observation_read_cells(ArrayType *operands, ArrayType *sources,
    const LaplaceObservationCell *cells, int cell_count,
    LaplaceObservationVisitor visitor, void *context)
{
    if (cell_count < 0 || (cell_count > 0 && !cells))
        elog(ERROR, "observation bindings: invalid exact cell set");
    if (cell_count == 0) return;
    observation_read(operands, sources, NULL, 3, cells, cell_count, visitor, context);
}

static void
emit_observation(int ordinal, int16 role, const LaplaceObservation *row, void *context)
{
    ReturnSetInfo *rsinfo = context;
    Datum values[] = {Int32GetDatum(ordinal), Int16GetDatum(role), hash128_to_datum(&row->id),
        hash128_to_datum(&row->subject), hash128_to_datum(&row->type),
        hash128_to_datum(&row->object), hash128_to_datum(&row->source),
        hash128_to_datum(&row->context), Int16GetDatum(row->outcome),
        Int64GetDatum(row->occurrences)};
    bool nulls[] = {false, false, false, false, false, row->object_null,
        row->source_null, row->context_null, row->outcome==LAPLACE_OBSERVATION_SCORED, false};
    tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
    for (int i = 2; i <= 7; ++i) pfree(DatumGetPointer(values[i]));
}

Datum
pg_laplace_observation_bindings(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0)) return (Datum) 0;
    laplace_observation_read(PG_GETARG_ARRAYTYPE_P(0),
        PG_ARGISNULL(1) ? NULL : PG_GETARG_ARRAYTYPE_P(1),
        PG_ARGISNULL(2) ? NULL : PG_GETARG_ARRAYTYPE_P(2),
        PG_ARGISNULL(3) ? 3 : PG_GETARG_INT32(3), emit_observation, fcinfo->resultinfo);
    return (Datum) 0;
}
