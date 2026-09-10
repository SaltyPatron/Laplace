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
} ObservationReceiver;

static SPIPlanPtr observation_plan;
static SPIPlanPtr observation_cell_plan;

static void
read_id(Datum value, hash128_t *id)
{
    bytea *bytes = DatumGetByteaPP(value);
    if (VARSIZE_ANY_EXHDR(bytes) != sizeof(*id))
        ereport(ERROR, (errmsg("observation bindings: ids must be 16 bytes")));
    memcpy(id, VARDATA_ANY(bytes), sizeof(*id));
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
    bool nulls[8];
    Datum values[8];
    OperandEntry *operand;
    for (int i = 0; i < 8; ++i)
        values[i] = slot_getattr(slot, i + 1, &nulls[i]);
    if (nulls[0] || nulls[1] || nulls[2] || nulls[6] || nulls[7])
        elog(ERROR, "observation bindings: missing witness identity or outcome");
    for (int i = 0; i < 6; ++i)
        if (!nulls[i]) read_id(values[i], ids[i]);
    row.object_null = nulls[3];
    row.source_null = nulls[4];
    row.context_null = nulls[5];
    row.outcome = DatumGetInt16(values[6]);
    row.occurrences = DatumGetInt64(values[7]);
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
                         BYTEAOID, BYTEAOID, INT2OID, INT8OID};
    (void) destination;
    if (operation != CMD_SELECT || descriptor->natts != lengthof(types))
        elog(ERROR, "observation bindings: invalid result shape");
    for (int i = 0; i < lengthof(types); ++i)
        if (TupleDescAttr(descriptor, i)->atttypid != types[i])
            elog(ERROR, "observation bindings: invalid result column type");
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
        SPIPlanPtr *plan = cells ? &observation_cell_plan : &observation_plan;
        if (!*plan)
        {
            Oid parameter_types[] = {BYTEAARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID, INT4OID};
            if (cells) parameter_types[3] = BYTEAARRAYOID;
            const char *query = laplace_sql_query_text(cells
                ? "evidence.observation_cells" : "evidence.observation_bindings");
            if (!query)
                elog(ERROR, "observation bindings: required static query is unavailable");
            *plan = SPI_prepare_cursor(query,
                4, parameter_types, CURSOR_OPT_PARALLEL_OK);
            if (!*plan || SPI_keepplan(*plan) != 0)
                elog(ERROR, "observation bindings: preparing static read failed");
        }
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
        result = SPI_execute_plan_extended(*plan, &options);
        /* SELECT to DestNone reports SPI_OK_UTILITY; startup checks its shape. */
        if (result != SPI_OK_SELECT && result != SPI_OK_UTILITY)
            elog(ERROR, "observation bindings: read failed: %s", SPI_result_code_string(result));
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
        row->source_null, row->context_null, false, false};
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
