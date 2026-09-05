#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"

#include "laplace/core/hash128.h"
#include "spi_common.h"
#include "spi_nested.h"

/* Reverse composition traversal: C owns one frontier and a visited set; PostgreSQL
 * resolves each complete frontier through one fixed typed membership read. Keep
 * the prepared plan across calls rather than planning all partition joins at
 * every hop. PostgreSQL invalidates retained plans when their dependencies change.
 * LIMIT stays in the candidate query so it constrains work before hydration. */

/* Materialize indexed membership before entity hydration. Joining v_word_points
 * inside the bounded candidate read let the planner price the LIMIT against an
 * inflated entity join cardinality, selecting a full physicality btree walk.
 * Retained Morse file: 1 parent, 1.15M buffer hits / 563 ms before; the identical
 * candidate set through GIN used 1,806 hits / 19 ms including hydration.
 * The C frontier/deduplication and caller's bound are unchanged. */
static const char *CONTAINERS_QUERY =
    "WITH candidates AS MATERIALIZED ("
    " SELECT p.entity_id FROM laplace.physicalities p "
    " WHERE p.type = 1 AND p.trajectory IS NOT NULL "
    " AND public.laplace_trajectory_constituent_ids(p.trajectory) && $1::bytea[] "
    " LIMIT $2) "
    "SELECT e.id, e.tier, e.type_id "
    "FROM candidates c JOIN laplace.entities e ON e.id = c.entity_id";

static SPIPlanPtr containers_plan = NULL;

static void
ensure_containers_plan(void)
{
    if (containers_plan != NULL)
        return;
    Oid argtypes[2] = { BYTEAARRAYOID, INT4OID };
    SPIPlanPtr plan = SPI_prepare_cursor(CONTAINERS_QUERY, 2, argtypes,
                                         CURSOR_OPT_GENERIC_PLAN);
    if (plan == NULL)
        elog(ERROR, "containers_of: SPI_prepare failed: %s",
             SPI_result_code_string(SPI_result));
    if (SPI_keepplan(plan) != 0)
        elog(ERROR, "containers_of: SPI_keepplan failed");
    containers_plan = plan;
}

PG_FUNCTION_INFO_V1(pg_laplace_containers_of);

Datum
pg_laplace_containers_of(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    bytea  *prompt;
    int32   max_hops, limit_rows;
    bool    unlimited;
    Datum  *frontier;
    int     n_frontier;
    HTAB   *seen;
    int     n_output = 0;
    bool    spi_top = false;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("containers_of: entity must not be NULL")));
    prompt     = PG_GETARG_BYTEA_PP(0);
    max_hops   = PG_ARGISNULL(1) ? 1 : PG_GETARG_INT32(1);
    unlimited  = PG_ARGISNULL(2);
    limit_rows = unlimited ? 0 : PG_GETARG_INT32(2);
    if (max_hops < 0)
        ereport(ERROR, (errmsg("containers_of: max_hops must be >= 0")));
    if (limit_rows < 0)
        ereport(ERROR, (errmsg("containers_of: limit must be >= 0 or NULL for all rows")));

    InitMaterializedSRF(fcinfo, 0);
    if (max_hops == 0 || (!unlimited && limit_rows == 0))
        return (Datum) 0;

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "containers_of: SPI_connect failed");

    ensure_containers_plan();

    frontier = (Datum *) palloc(sizeof(Datum));
    frontier[0] = copy_bytea_datum(PointerGetDatum(prompt));
    n_frontier = 1;

    {
        HASHCTL ctl;
        hash128_t root_id = datum_to_hash128(frontier[0]);
        bool found;

        memset(&ctl, 0, sizeof(ctl));
        ctl.keysize = sizeof(hash128_t);
        ctl.entrysize = sizeof(hash128_t);
        seen = hash_create("containers_of seen", 1024, &ctl,
                           HASH_ELEM | HASH_BLOBS);
        hash_search(seen, &root_id, HASH_ENTER, &found);
    }

    for (int hop = 1;
         hop <= max_hops && n_frontier > 0
         && (unlimited || n_output < limit_rows);
         hop++)
    {
        int    next_cap, n_next = 0;
        Datum *next_frontier;

        /* ONE PROBE PER HOP, NOT ONE PER FRONTIER ELEMENT.
         *
         * This ran a separate SPI query for every id in the frontier, each with its
         * own limit_rows budget, and then the dedup below discarded everything past
         * limit_rows TOTAL. Measured over an 84-id frontier: 84 per-element probes
         * cost 1,559 ms and fetched 3,356 rows to keep 400, while one overlap probe
         * costs 192 ms and fetches exactly 400 -- 8x, and it stops over-fetching.
         *
         * `&&` (array overlap) is the batched form of `@> ARRAY[$1]` and uses the
         * same GIN index on laplace_trajectory_constituent_ids. For a one-element
         * frontier the two are the same query.
         *
         * The contract is unchanged: the caller is promised at most limit_rows rows
         * and CONTAINERS_QUERY has no ORDER BY, so which rows come back was already
         * an arbitrary subset of the matches. */
        {
            ArrayType *fr_arr = construct_array(frontier, n_frontier, BYTEAOID, -1,
                                                false, TYPALIGN_INT);
            Datum args[2];
            char  nulls[2] = {' ', ' '};
            int   rc;

            args[0] = PointerGetDatum(fr_arr);
            args[1] = unlimited ? (Datum) 0 : Int32GetDatum(limit_rows);
            if (unlimited) nulls[1] = 'n';
            /* Cap the FETCH at the caller's budget. The 0 that was here means
             * "unlimited" to SPI, so every probe materialized every container of
             * the entity -- an estimated 187,223 rows across every physicality
             * partition -- and then the dedup loop below threw away all but
             * limit_rows of them.
             *
             * MEASURED on 'water': structural.containers_of(word_id('water'), 1, 400) ran
             * 7,987ms, while the IDENTICAL query with LIMIT 400 pushed into SQL
             * planned as an early-terminating Gather and returned in 97.7ms. Same
             * rows, same index (physicalities_*_laplace_trajectory_constituent_ids
             * _idx1), 80x. It was over half the cost of converse_walk.
             *
             * Passing limit_rows rather than (limit_rows - n_output) leaves a
             * probe room to fill the whole budget on its own when an earlier
             * frontier node contributed duplicates.
             *
             * This does not narrow the contract: CONTAINERS_QUERY has no ORDER BY,
             * so "the first limit_rows containers" was already an arbitrary subset
             * of the matches. Capping the fetch changes WHICH arbitrary rows come
             * back, not how many the caller is promised. */
            rc = SPI_execute_plan(containers_plan, args, nulls, true,
                                       unlimited ? 0 : limit_rows);
            if (rc != SPI_OK_SELECT)
                elog(ERROR, "containers_of: probe query failed: %s",
                     SPI_result_code_string(rc));

            if (SPI_processed > (uint64) PG_INT32_MAX
                || SPI_processed > (uint64) (MaxAllocSize / sizeof(Datum)))
                ereport(ERROR, (errmsg(
                    "containers_of: frontier result exceeds PostgreSQL allocation capacity")));
            next_cap = SPI_processed > 0 ? (int) SPI_processed : 1;
            next_frontier = (Datum *) palloc(sizeof(Datum) * next_cap);

            for (uint64 r = 0;
                 r < SPI_processed && (unlimited || n_output < limit_rows);
                 r++)
            {
                HeapTuple tup = SPI_tuptable->vals[r];
                TupleDesc td  = SPI_tuptable->tupdesc;
                bool   isnull;
                Datum  hit_id   = SPI_getbinval(tup, td, 1, &isnull);
                Datum  hit_tier = SPI_getbinval(tup, td, 2, &isnull);
                Datum  hit_type = SPI_getbinval(tup, td, 3, &isnull);
                hash128_t hit_hash;
                bool   found;
                Datum  values[4];
                bool   rnulls[4] = { false, false, false, false };

                hit_hash = datum_to_hash128(hit_id);
                hash_search(seen, &hit_hash, HASH_ENTER, &found);
                if (found)
                    continue;

                hit_id   = copy_bytea_datum(hit_id);
                hit_type = copy_bytea_datum(hit_type);

                next_frontier[n_next++] = hit_id;

                values[0] = hit_id;
                values[1] = hit_tier;
                values[2] = hit_type;
                values[3] = Int32GetDatum(hop);
                tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, rnulls);
                if (n_output == PG_INT32_MAX)
                    ereport(ERROR, (errmsg(
                        "containers_of: output exceeds integer representation")));
                n_output++;
            }
            /* hit_id/hit_type copied out; hit_tier already materialized into
             * the tuplestore. Free before the next frontier element's probe. */
            SPI_freetuptable(SPI_tuptable);
        }

        pfree(frontier);
        frontier = next_frontier;
        n_frontier = n_next;
    }

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
