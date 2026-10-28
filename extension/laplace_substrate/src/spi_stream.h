#ifndef LAPLACE_SPI_STREAM_H
#define LAPLACE_SPI_STREAM_H

#include "laplace_pg/executor.h"

/* Consume one SELECT in executor order, without portals, fetch loops or a
 * materialized SPI tuple table. Rows are borrowed for one callback. Retained
 * state belongs in the caller's context, outside the reset row context. */
typedef lp_pg_executor_visit LaplaceSqlRowVisitor;

static void laplace_spi_stream(SPIPlanPtr plan, int nargs, const Oid *types,
    const Datum *values, const char *nulls, LaplaceSqlRowVisitor visit, void *context)
{
    ParamListInfo params = makeParamList(nargs);
    for (int i = 0; i < nargs; i++) {
        params->params[i].ptype = types[i];
        params->params[i].value = values[i];
        params->params[i].isnull = nulls && nulls[i] == 'n';
    }
    lp_pg_execute_stream(plan, params, true, visit, context);
    pfree(params);
}
#endif
