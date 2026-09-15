#include "postgres.h"

#include "catalog/pg_type_d.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/memutils.h"

#include "laplace/core/content_witness_batch.h"
#include "laplace/core/math4d.h"
#include "laplace/core/tier_tree.h"

#include "perfcache_native.h"

/* One native boundary for the requested complete texts. This is composition
 * inspection, with no entity lookup, evidence election, rendering or deposit.
 * The core owns normalization, decomposition, singleton collapse and placement.
 * Copy its root tuple before crossing back into PostgreSQL allocation/error
 * handling, so no native tree allocation can survive a backend longjmp. */
PG_FUNCTION_INFO_V1(pg_laplace_text_root_placements);

Datum
pg_laplace_text_root_placements(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo;
    ArrayType *inputs;
    ArrayIterator iterator;
    MemoryContext row_context;
    MemoryContext previous;
    Datum item;
    bool item_isnull;
    bool floor_ready = false;
    int ordinal = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0)) return (Datum) 0;
    inputs = PG_GETARG_ARRAYTYPE_P(0);
    if (ARR_NDIM(inputs) > 1)
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                       errmsg("text_root_placements requires a one-dimensional text array")));
    if (ArrayGetNItems(ARR_NDIM(inputs), ARR_DIMS(inputs)) == 0)
    {
        PG_FREE_IF_COPY(inputs, 0);
        return (Datum) 0;
    }
    rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    iterator = array_create_iterator(inputs, 0, NULL);
    row_context = AllocSetContextCreate(CurrentMemoryContext,
        "text root placement row", ALLOCSET_SMALL_SIZES);
    previous = CurrentMemoryContext;

    while (array_iterate(iterator, &item, &item_isnull))
    {
        Datum values[6] = {0};
        bool nulls[6] = {false, true, true, true, true, true};
        text *input;

        CHECK_FOR_INTERRUPTS();
        MemoryContextReset(row_context);
        MemoryContextSwitchTo(row_context);
        values[0] = Int32GetDatum(++ordinal);
        input = item_isnull ? NULL : DatumGetTextPP(item);
        if (input != NULL && VARSIZE_ANY_EXHDR(input) != 0)
        {
            tier_tree_t *tree = NULL;
            tier_node_view_t root;
            Datum components[4];
            bytea *identity;
            bytea *hilbert;
            int rc;

            if (!floor_ready)
            {
                if (!laplace_perfcache_ready())
                    ereport(ERROR,
                        (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                         errmsg("text_root_placements requires the T0 perfcache"),
                         errhint("Configure laplace_substrate.perfcache_path with the installed Unicode cache.")));
                floor_ready = true;
            }
            rc = laplace_content_tree_build_public(
                (const uint8_t *) VARDATA_ANY(input), VARSIZE_ANY_EXHDR(input), &tree);
            if (rc == 0) rc = content_witness_tree_root_node(tree, &root);
            tier_tree_free(tree);
            if (rc != 0)
                ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("text_root_placements: canonical composition failed at input %d (rc=%d)",
                            ordinal, rc)));

            identity = palloc(VARHDRSZ + sizeof(root.id));
            SET_VARSIZE(identity, VARHDRSZ + sizeof(root.id));
            memcpy(VARDATA(identity), &root.id, sizeof(root.id));
            values[1] = PointerGetDatum(identity);
            values[2] = Int16GetDatum(root.tier);
            for (int axis = 0; axis < 4; ++axis)
                components[axis] = Float8GetDatum(root.coord[axis]);
            values[3] = PointerGetDatum(construct_array(components, 4, FLOAT8OID,
                sizeof(float8), FLOAT8PASSBYVAL, TYPALIGN_DOUBLE));
            hilbert = palloc(VARHDRSZ + sizeof(root.hilbert));
            SET_VARSIZE(hilbert, VARHDRSZ + sizeof(root.hilbert));
            memcpy(VARDATA(hilbert), root.hilbert.bytes, sizeof(root.hilbert));
            values[4] = PointerGetDatum(hilbert);
            values[5] = Float8GetDatum(math4d_radius_from_origin(root.coord));
            memset(nulls, 0, sizeof(nulls));
        }
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        MemoryContextSwitchTo(previous);
    }
    array_free_iterator(iterator);
    MemoryContextDelete(row_context);
    PG_FREE_IF_COPY(inputs, 0);
    return (Datum) 0;
}
