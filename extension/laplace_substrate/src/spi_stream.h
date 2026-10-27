#ifndef LAPLACE_SPI_STREAM_H
#define LAPLACE_SPI_STREAM_H

#include "executor/spi.h"
#include "executor/tuptable.h"
#include "miscadmin.h"
#include "tcop/dest.h"
#include "utils/memutils.h"

/* Consume one SELECT in executor order, without portals, fetch loops or a
 * materialized SPI tuple table. Rows are borrowed for one callback. Retained
 * state belongs in the caller's context, outside the reset row context. */
typedef void (*LaplaceSqlRowVisitor)(TupleTableSlot *row, void *context);
typedef struct {
    DestReceiver receiver;
    LaplaceSqlRowVisitor visit;
    void *context;
    MemoryContext row;
    uint64 count;
    bool selected;
} LaplaceSqlReceiver;

static bool laplace_sql_receive(TupleTableSlot *row, DestReceiver *destination)
{
    LaplaceSqlReceiver *receiver = (LaplaceSqlReceiver *)destination;
    CHECK_FOR_INTERRUPTS();
    MemoryContext before = MemoryContextSwitchTo(receiver->row);
    receiver->visit(row, receiver->context);
    receiver->count++;
    MemoryContextSwitchTo(before);
    MemoryContextReset(receiver->row);
    return true;
}
static void laplace_sql_start(DestReceiver *destination, int operation, TupleDesc descriptor)
{
    if (operation != CMD_SELECT) elog(ERROR, "native set reader requires SELECT");
    ((LaplaceSqlReceiver *)destination)->selected = true;
}
static void laplace_sql_end(DestReceiver *destination) { (void)destination; }

static void laplace_spi_stream(SPIPlanPtr plan, int nargs, const Oid *types,
    const Datum *values, const char *nulls, LaplaceSqlRowVisitor visit, void *context)
{
    ParamListInfo params = makeParamList(nargs);
    for (int i = 0; i < nargs; i++) {
        params->params[i].ptype = types[i];
        params->params[i].value = values[i];
        params->params[i].isnull = nulls && nulls[i] == 'n';
    }
    LaplaceSqlReceiver receiver = {
        .receiver = {laplace_sql_receive, laplace_sql_start,
                     laplace_sql_end, laplace_sql_end, DestNone},
        .visit = visit, .context = context,
        .row = AllocSetContextCreate(CurrentMemoryContext, "native SQL row", ALLOCSET_SMALL_SIZES)
    };
    SPIExecuteOptions options = {.params = params, .read_only = true,
        .must_return_tuples = true, .dest = &receiver.receiver};
    int result = SPI_execute_plan_extended(plan, &options);
    /* PostgreSQL reports a SELECT to a custom DestNone receiver as UTILITY. */
    if ((result != SPI_OK_SELECT && result != SPI_OK_UTILITY) || !receiver.selected ||
        receiver.count != SPI_processed)
        elog(ERROR, "native set reader failed: %s", SPI_result_code_string(result));
    MemoryContextDelete(receiver.row);
    pfree(params);
}
#endif
