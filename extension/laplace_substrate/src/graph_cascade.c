




#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/numeric.h"

#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "spi_common.h"

/* Step-edge lookup plan, prepared once and kept: the query runs once per path
 * step inside the realization loop — re-planning it per step was pure overhead. */
static SPIPlanPtr step_edge_plan = NULL;

static SPIPlanPtr
ensure_step_edge_plan(void)
{
    if (step_edge_plan == NULL)
    {
        Oid        argtypes[2] = { BYTEAOID, BYTEAOID };
        SPIPlanPtr plan = SPI_prepare(
            "SELECT type_id, "
            "       CASE WHEN subject_id = $1 THEN 1 ELSE -1 END AS dir "
            "FROM consensus.step_edge($1, $2)",
            2, argtypes);

        if (plan == NULL)
            elog(ERROR, "cascade: SPI_prepare(step edge) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "cascade: SPI_keepplan(step edge) failed");
        step_edge_plan = plan;
    }
    return step_edge_plan;
}
#include "spi_nested.h"

PG_FUNCTION_INFO_V1(pg_laplace_cascade);

Datum
pg_laplace_cascade(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    text          *x_txt, *y_txt;
    int32          max_depth;
    Datum          x_id, y_id;
    Datum         *goals;
    int            n_goals = 0;
    int            n_steps = 0;
    Datum         *steps = NULL;
    double        *costs = NULL;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("cascade: endpoints must not be NULL")));
    x_txt = PG_GETARG_TEXT_PP(0);
    y_txt = PG_GETARG_TEXT_PP(1);
    max_depth = PG_ARGISNULL(2) ? PG_INT32_MAX : PG_GETARG_INT32(2);

    InitMaterializedSRF(fcinfo, 0);

    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "cascade: SPI_connect failed");

    {
        Oid   argtypes[1] = { TEXTOID };
        Datum args[1];
        bool  isnull;
        int   rc;

        args[0] = PointerGetDatum(x_txt);
        rc = SPI_execute_with_args(
            "SELECT converse.resolve_last_word($1)", 1, argtypes, args, NULL, true, 1);
        if (rc != SPI_OK_SELECT || SPI_processed == 0)
        {
            laplace_spi_finish(spi_top);
            return (Datum) 0;
        }
        x_id = copy_bytea_datum(
            SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));
        if (isnull)
        {
            laplace_spi_finish(spi_top);
            return (Datum) 0;
        }

        args[0] = PointerGetDatum(y_txt);
        rc = SPI_execute_with_args(
            "SELECT converse.resolve_last_word($1)", 1, argtypes, args, NULL, true, 1);
        if (rc != SPI_OK_SELECT || SPI_processed == 0)
        {
            laplace_spi_finish(spi_top);
            return (Datum) 0;
        }
        y_id = copy_bytea_datum(
            SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));
        if (isnull)
        {
            laplace_spi_finish(spi_top);
            return (Datum) 0;
        }
    }

    {
        Datum *synsets;
        int    ns;

        spi_fetch_synset_ids(y_id, &synsets, &ns);
        if (ns < 0 || (Size) ns >= MaxAllocSize / sizeof(Datum))
            ereport(ERROR,
                    (errmsg("cascade: goal set exceeds PostgreSQL allocation capacity"),
                     errdetail("Requested one endpoint plus %d synset goals.", ns)));

        goals = (Datum *) palloc(sizeof(Datum) * ((Size) ns + 1));
        goals[n_goals++] = y_id;
        for (int i = 0; i < ns; i++)
            goals[n_goals++] = copy_bytea_datum(synsets[i]);
    }

    {
        Oid     argtypes[3] = { BYTEAOID, BYTEAARRAYOID, INT4OID };
        Datum   args[3];
        int     rc;

        args[0] = x_id;
        args[1] = PointerGetDatum(construct_array(goals, n_goals, BYTEAOID,
                                                  -1, false, TYPALIGN_INT));
        args[2] = Int32GetDatum(max_depth);

        rc = SPI_execute_with_args(
            "SELECT ap.step, ap.entity_id, ap.g "
            "FROM converse.astar_path($1, $2, $3) ap "
            "ORDER BY ap.step",
            3, argtypes, args, NULL, true, 0);
        pfree(DatumGetPointer(args[1]));

        if (rc != SPI_OK_SELECT || SPI_processed <= 1)
        {
            laplace_spi_finish(spi_top);
            return (Datum) 0;
        }

        n_steps = (int) SPI_processed;
        steps = (Datum *) palloc(sizeof(Datum) * n_steps);
        costs = (double *) palloc(sizeof(double) * n_steps);
        for (uint64 r = 0; r < SPI_processed; r++)
        {
            HeapTuple tup = SPI_tuptable->vals[r];
            TupleDesc td  = SPI_tuptable->tupdesc;
            bool      isnull;
            steps[r] = copy_bytea_datum(
                SPI_getbinval(tup, td, 2, &isnull));
            costs[r] = DatumGetFloat8(
                SPI_getbinval(tup, td, 3, &isnull));
        }
    }

    {
        Datum     *via_types;
        bool      *via_null;
        int       *via_dirs;
        Datum      lang;
        Oid        argtypes[4];
        Datum      args[4];
        char       nulls[5] = "    ";
        int        rc;
        double     max_cost = costs[0];

        /* via_types[s] stays 0 for any step whose edge lookup misses. A zero
         * Datum is a NULL pointer, and construct_array() with elmlen=-1 /
         * elmbyval=false dereferences every element to read its varlena header
         * — so one missing edge segfaults the backend and takes the whole
         * cluster into recovery. construct_array() has no nulls argument and
         * cannot represent a missing element; construct_md_array() can.
         *
         * MEASURED: `SELECT * FROM converse.cascade('dog','dog',4)` on a
         * substrate with 0 consensus rows. Every step-edge lookup misses, so it
         * dereferences NULL on the first element. Server log 2026-08-10
         * 10:23:33 UTC and again at 03:01:56 UTC — "client backend was
         * terminated by signal 11: Segmentation fault", followed by
         * "terminating any other active server processes". Reproducible.
         */
        via_types = (Datum *) palloc0(sizeof(Datum) * n_steps);
        via_null = (bool *) palloc0(sizeof(bool) * n_steps);
        for (int s = 0; s < n_steps; s++)
            via_null[s] = true;          /* until an edge is actually found */
        via_dirs = (int *) palloc0(sizeof(int) * n_steps);

        for (int s = 1; s < n_steps; s++)
        {
            Datum eargv[2] = { steps[s - 1], steps[s] };
            int   erc = SPI_execute_plan(ensure_step_edge_plan(), eargv,
                                         NULL, true, 1);
            if (erc == SPI_OK_SELECT && SPI_processed > 0)
            {
                HeapTuple tup = SPI_tuptable->vals[0];
                TupleDesc td  = SPI_tuptable->tupdesc;
                bool      isnull;
                /* isnull FIRST. copy_bytea_datum() calls DatumGetByteaPP()
                 * then VARSIZE_ANY() with no null check, so copying before
                 * testing the flag dereferences the zero Datum that
                 * SPI_getbinval returns for a NULL column — the same fault
                 * this function was fixed for. */
                Datum via = SPI_getbinval(tup, td, 1, &isnull);
                via_null[s] = isnull;
                if (!isnull)
                    via_types[s] = copy_bytea_datum(via);
                bool dir_isnull;
                Datum dir = SPI_getbinval(tup, td, 2, &dir_isnull);
                via_dirs[s] = dir_isnull ? 0 : DatumGetInt32(dir);
            }
            if (costs[s] > max_cost)
                max_cost = costs[s];
        }

        lang = spi_word_language(x_id);

        argtypes[0] = BYTEAARRAYOID;
        argtypes[1] = BYTEAARRAYOID;
        argtypes[2] = INT4ARRAYOID;
        argtypes[3] = BYTEAOID;
        args[0] = PointerGetDatum(construct_array(steps, n_steps, BYTEAOID,
                                                  -1, false, TYPALIGN_INT));
        {
            /* construct_md_array, not construct_array: a step with no edge is a
             * genuine SQL NULL, not a zero Datum to dereference. */
            int md_dims[1] = { n_steps - 1 };
            int md_lbs[1]  = { 1 };
            args[1] = PointerGetDatum(
                construct_md_array(via_types + 1, via_null + 1, 1,
                                   md_dims, md_lbs,
                                   BYTEAOID, -1, false, TYPALIGN_INT));
        }
        {
            Datum *dir_datums = (Datum *) palloc(sizeof(Datum) * (n_steps - 1));
            for (int di = 1; di < n_steps; di++)
                dir_datums[di - 1] = Int32GetDatum(via_dirs[di]);
            args[2] = PointerGetDatum(construct_array(dir_datums, n_steps - 1,
                                                      INT4OID, sizeof(int32),
                                                      true, TYPALIGN_INT));
            pfree(dir_datums);
        }
        args[3] = lang;
        if (lang == (Datum) 0)
            nulls[3] = 'n';

        rc = SPI_execute_with_args(
            "SELECT realize.path($1::bytea[], $2::bytea[], $3::int[], $4)",
            4, argtypes, args, nulls, true, 1);

        if (rc == SPI_OK_SELECT && SPI_processed > 0)
        {
            bool   isnull;
            Datum  chain = SPI_getbinval(SPI_tuptable->vals[0],
                                         SPI_tuptable->tupdesc, 1, &isnull);
            Datum  values[2];
            bool   rnulls[2] = { false, false };

            if (!isnull)
            {
                values[0] = chain;
                values[1] = Float8GetDatum(max_cost);
                tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc,
                                     values, rnulls);
            }
        }

        pfree(DatumGetPointer(args[0]));
        pfree(DatumGetPointer(args[1]));
        pfree(DatumGetPointer(args[2]));
        pfree(via_types);
        pfree(via_null);
        pfree(via_dirs);
    }

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
