#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"

#include "spi_common.h"
#include "spi_nested.h"

/*
 * structural.containers_of(entity, max_hops, limit) -- the reverse of realize.constituents():
 * given an entity, walk UP the composition hierarchy and return every entity
 * of a higher tier whose trajectory contains it (directly, hop=1, or
 * transitively through intermediate tiers, hop>1) -- e.g. a word -> the
 * sentences containing that word (hop 1) -> the documents containing those
 * sentences (hop 2).
 *
 * Why this is native and not a single SQL statement: physicalities_constituents_gin
 * (GIN index on laplace_trajectory_constituent_ids(trajectory)) resolves a
 * single-key `@> ARRAY[id]` probe in ~2ms regardless of table size (confirmed
 * via EXPLAIN ANALYZE -- Bitmap Index Scan, not a sequential/BRIN scan). But
 * Postgres's GIN cost model badly mis-estimates a *multi-key* `&&`/`@>` probe
 * against a large bound array (order-1000+ elements, as a single hop's
 * frontier commonly is) -- confirmed live: the same containment check
 * reformulated as one `trajectory && $1::bytea[]` query against a
 * ~1200-element array made the planner abandon the GIN index entirely for a
 * Parallel Bitmap Heap Scan over ALL candidate-tier entities with a per-row
 * recheck filter (850ms for what should be a sub-millisecond lookup; 873366
 * rows discarded by index recheck alone). A SQL-level LATERAL rewrite (one
 * probe per frontier element, expressed as a correlated subquery) fared
 * worse still under concurrent load.
 *
 * The fix is the "native C/SPI does the heavy lifting" pattern used elsewhere
 * in this file family (see generate_walk.c's edge_plan): drive the frontier
 * expansion as a tight C loop -- one bound execution of the proven-fast
 * single-key query per frontier element, in-process. UNLIKE that family, the
 * plan is deliberately NOT kept (no SPI_prepare/SPI_keepplan): a process-wide
 * kept plan is planned once and never revisited, so a backend that planned it
 * against since-rebuilt indexes (the partitioned index cycle drops and
 * recreates them), or that settled into a bad generic plan, executes the
 * stale shape for the life of the backend. MEASURED 2026-08-13: 527ms per
 * call from a backend holding the stale plan vs 17ms planned fresh against
 * the live GIN. Each call plans custom via SPI_execute_with_args -- parsing
 * this one-liner is microseconds against a 17ms execution, and the planner
 * sees the live indexes and the actual bound values every time. This is not
 * "batching for its own sake" (word_shape_peers_fast's array-unnest batching
 * measurably did NOT help once its real bottleneck was fixed) -- it's the
 * specific response to a specific, confirmed planner cost-estimation defect
 * for this query shape.
 */

/* LIMIT $2 is IN THE QUERY TEXT, and it has to be. SPI_execute_plan's count
 * argument stops the executor FETCHING, which is not the same thing: the plan is
 * a Bitmap Index Scan, and the bitmap for every match is built before the first
 * tuple is produced. Capping the fetch skips no bitmap work at all.
 *
 * MEASURED on 'water' at 37.4M entities, same rows returned:
 *   SPI count = limit_rows (no LIMIT in text)   3,245 ms
 *   LIMIT $2 as a bound parameter                  40.4 ms
 * 80x. (Measured on the since-removed kept plan's generic shape; the custom
 * plan only improves on it.)
 *
 * The limit stays a PARAMETER, not interpolated into the string:
 * SPI_execute_with_args plans with the bound value visible, so a parameter
 * plans exactly like a literal would -- and the constant query text keeps
 * this one entry in pg_stat_statements instead of one per distinct limit. */
static const char *CONTAINERS_QUERY =
    "SELECT w.id, w.tier, w.type_id "
    "FROM laplace.v_word_points w "
    "WHERE public.laplace_trajectory_constituent_ids(w.trajectory) @> ARRAY[$1]::bytea[] "
    "LIMIT $2";

PG_FUNCTION_INFO_V1(pg_laplace_containers_of);

Datum
pg_laplace_containers_of(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    bytea  *prompt;
    int32   max_hops, limit_rows;
    Datum  *frontier;
    int     n_frontier;
    Datum  *seen;
    int     n_seen, seen_cap;
    int     n_output = 0;
    bool    spi_top = false;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("containers_of: entity must not be NULL")));
    prompt     = PG_GETARG_BYTEA_PP(0);
    max_hops   = PG_ARGISNULL(1) ? 1 : PG_GETARG_INT32(1);
    limit_rows = PG_ARGISNULL(2) ? 1000 : PG_GETARG_INT32(2);
    if (max_hops < 1)
        ereport(ERROR, (errmsg("containers_of: max_hops must be >= 1")));
    if (limit_rows < 1)
        ereport(ERROR, (errmsg("containers_of: limit must be >= 1")));

    InitMaterializedSRF(fcinfo, 0);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "containers_of: SPI_connect failed");

    frontier = (Datum *) palloc(sizeof(Datum));
    frontier[0] = copy_bytea_datum(PointerGetDatum(prompt));
    n_frontier = 1;

    seen_cap = 64;
    seen = (Datum *) palloc(sizeof(Datum) * seen_cap);
    seen[0] = frontier[0];
    n_seen = 1;

    for (int hop = 1; hop <= max_hops && n_frontier > 0 && n_output < limit_rows; hop++)
    {
        int    next_cap = 64, n_next = 0;
        Datum *next_frontier = (Datum *) palloc(sizeof(Datum) * next_cap);

        for (int f = 0; f < n_frontier && n_output < limit_rows; f++)
        {
            Oid   argtypes[2] = { BYTEAOID, INT4OID };
            Datum args[2];
            int   rc;

            args[0] = frontier[f];
            args[1] = Int32GetDatum(limit_rows);
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
            rc = SPI_execute_with_args(CONTAINERS_QUERY, 2, argtypes,
                                       args, NULL, true, limit_rows);
            if (rc != SPI_OK_SELECT)
                elog(ERROR, "containers_of: probe query failed: %s",
                     SPI_result_code_string(rc));

            for (uint64 r = 0; r < SPI_processed && n_output < limit_rows; r++)
            {
                HeapTuple tup = SPI_tuptable->vals[r];
                TupleDesc td  = SPI_tuptable->tupdesc;
                bool   isnull;
                Datum  hit_id   = SPI_getbinval(tup, td, 1, &isnull);
                Datum  hit_tier = SPI_getbinval(tup, td, 2, &isnull);
                Datum  hit_type = SPI_getbinval(tup, td, 3, &isnull);
                bool   dup = false;
                Datum  values[4];
                bool   rnulls[4] = { false, false, false, false };

                for (int s = 0; s < n_seen; s++)
                {
                    if (bytea_eq(seen[s], hit_id)) { dup = true; break; }
                }
                if (dup)
                    continue;

                hit_id   = copy_bytea_datum(hit_id);
                hit_type = copy_bytea_datum(hit_type);

                if (n_seen == seen_cap)
                {
                    seen_cap *= 2;
                    seen = (Datum *) repalloc(seen, sizeof(Datum) * seen_cap);
                }
                seen[n_seen++] = hit_id;

                if (n_next == next_cap)
                {
                    next_cap *= 2;
                    next_frontier = (Datum *) repalloc(next_frontier, sizeof(Datum) * next_cap);
                }
                next_frontier[n_next++] = hit_id;

                values[0] = hit_id;
                values[1] = hit_tier;
                values[2] = hit_type;
                values[3] = Int32GetDatum(hop);
                tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, rnulls);
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
