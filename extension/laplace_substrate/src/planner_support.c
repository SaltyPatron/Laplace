#include "postgres.h"
#include "fmgr.h"
#include "nodes/primnodes.h"
#include "nodes/supportnodes.h"
#include "utils/selfuncs.h"

PG_FUNCTION_INFO_V1(pg_laplace_array_length_rows_support);

Datum
pg_laplace_array_length_rows_support(PG_FUNCTION_ARGS)
{
    Node *request = (Node *) PG_GETARG_POINTER(0);

    if (IsA(request, SupportRequestRows))
    {
        SupportRequestRows *rows_request = (SupportRequestRows *) request;
        List *arguments = NIL;

        if (IsA(rows_request->node, FuncExpr))
            arguments = ((FuncExpr *) rows_request->node)->args;
        if (arguments != NIL)
        {
            rows_request->rows = estimate_array_length(
                rows_request->root, (Node *) linitial(arguments));
            PG_RETURN_POINTER(rows_request);
        }
    }

    PG_RETURN_POINTER(NULL);
}
