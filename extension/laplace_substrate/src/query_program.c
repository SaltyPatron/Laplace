/* The query program the Engine compiles from the installed firmware and producer recipes,
 * as jsonb: the chains of Native relation entities a declared-path read walks
 * (public.laplace_chains). */
#include "postgres.h"
#include "fmgr.h"
#include "utils/builtins.h"
#include "utils/jsonb.h"
#include "query_program.h"

PG_FUNCTION_INFO_V1(pg_laplace_query_program);
Datum pg_laplace_query_program(PG_FUNCTION_ARGS)
{
    return DirectFunctionCall1(jsonb_in,CStringGetDatum(laplace_compiled_query_program));
}
