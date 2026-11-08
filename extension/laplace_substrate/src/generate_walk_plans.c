/*
 * generate_walk_plans.c — SPI plans for consensus walk batch reads, ordinal
 * continuity probes, ranked edge comparisons, and pg_laplace_walk_strongest.
 */
#include "generate_walk_internal.h"

PG_FUNCTION_INFO_V1(pg_laplace_walk_strongest);

static const char *EDGE_QUERY =
    "SELECT object_id, type_id, rating, rd, witness_count "
    "FROM consensus.walk_edges($1, $2, $3, $4)";

static SPIPlanPtr edge_plan = NULL;

static void
ensure_edge_plan(void)
{
    if (edge_plan == NULL)
    {
        Oid argtypes[4] = { BYTEAOID, BYTEAOID, INT4OID, BYTEAARRAYOID };
        SPIPlanPtr plan = SPI_prepare_cursor(EDGE_QUERY, 4, argtypes, CURSOR_OPT_PARALLEL_OK);
        if (plan == NULL)
            elog(ERROR, "generate walk: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "generate walk: SPI_keepplan failed");
        edge_plan = plan;
    }
}

static const char *WALK_BATCH_QUERY =
    "WITH e AS MATERIALIZED ( "
    "  SELECT f.idx, f.subject_id AS anchor, r.neighbour_id AS neighbor, "
    "         r.type_id, r.rating, r.rd, r.witness_count "
    "    FROM consensus.edges_raw_batch( "
    "        $1::bytea[], 'out', "
    "        CASE WHEN $2::bytea IS NULL THEN NULL ELSE ARRAY[$2::bytea] END, "
    "        64, false, 'edge_rank') r "
    "    JOIN unnest($1::bytea[]) WITH ORDINALITY AS f(subject_id, idx) "
    "      ON f.subject_id = r.subject_id "
    "   WHERE r.neighbour_id IS NOT NULL "
    "     AND r.type_id IS NOT NULL "
    "  UNION ALL "
    "  SELECT f.idx, f.subject_id AS anchor, r.neighbour_id AS neighbor, "
    "         r.type_id, r.rating, r.rd, r.witness_count "
    "    FROM consensus.edges_raw_batch( "
    "        $1::bytea[], 'in', "
    "        CASE WHEN $2::bytea IS NULL THEN $3::bytea[] "
    "             WHEN $2::bytea = ANY($3::bytea[]) THEN ARRAY[$2::bytea] "
    "             ELSE ARRAY[]::bytea[] END, "
    "        64, false, 'edge_rank') r "
    "    JOIN unnest($1::bytea[]) WITH ORDINALITY AS f(subject_id, idx) "
    "      ON f.subject_id = r.subject_id "
    "   WHERE r.neighbour_id IS NOT NULL "
    "     AND r.type_id IS NOT NULL "
    "     AND r.neighbour_id <> f.subject_id "
    "), ids AS MATERIALIZED ( "
    "  SELECT anchor AS id FROM e UNION SELECT neighbor AS id FROM e "
    "), native_coords AS MATERIALIZED ( "
    "  SELECT public.blake3_send(en.id) AS id, en.coord "
    "    FROM public.laplace_entities(ARRAY(SELECT public.blake3_from_bytea(id) FROM ids WHERE id IS NOT NULL)) en "
    "   WHERE en.coord IS NOT NULL "
    "), coords AS MATERIALIZED ( "
    "  SELECT i.id, COALESCE(nc.coord, (SELECT p.coord FROM laplace.physicalities p WHERE p.entity_id = i.id AND p.type = 1 AND p.coord IS NOT NULL LIMIT 1)) AS coord "
    "    FROM ids i LEFT JOIN native_coords nc ON nc.id = i.id "
    ") "
    "SELECT e.idx, e.neighbor, false, "
    "       e.type_id, e.rating, e.rd, e.witness_count, "
    "       (SELECT eo.highway_mask FROM laplace.entities eo WHERE eo.id = e.neighbor), "
    "       ST_X(cs.coord), ST_Y(cs.coord), ST_Z(cs.coord), ST_M(cs.coord), 0::oid, "
    "       ST_X(co.coord), ST_Y(co.coord), ST_Z(co.coord), ST_M(co.coord), 0::oid "
    "FROM e "
    "LEFT JOIN coords cs ON cs.id = e.anchor "
    "LEFT JOIN coords co ON co.id = e.neighbor";

static const char *WALK_BATCH_QUERY_NOMASK =
    "WITH e AS MATERIALIZED ( "
    "  SELECT f.idx, f.subject_id AS anchor, r.neighbour_id AS neighbor, "
    "         r.type_id, r.rating, r.rd, r.witness_count "
    "    FROM consensus.edges_raw_batch( "
    "        $1::bytea[], 'out', "
    "        CASE WHEN $2::bytea IS NULL THEN NULL ELSE ARRAY[$2::bytea] END, "
    "        64, false, 'edge_rank') r "
    "    JOIN unnest($1::bytea[]) WITH ORDINALITY AS f(subject_id, idx) "
    "      ON f.subject_id = r.subject_id "
    "   WHERE r.neighbour_id IS NOT NULL "
    "     AND r.type_id IS NOT NULL "
    "  UNION ALL "
    "  SELECT f.idx, f.subject_id AS anchor, r.neighbour_id AS neighbor, "
    "         r.type_id, r.rating, r.rd, r.witness_count "
    "    FROM consensus.edges_raw_batch( "
    "        $1::bytea[], 'in', "
    "        CASE WHEN $2::bytea IS NULL THEN $3::bytea[] "
    "             WHEN $2::bytea = ANY($3::bytea[]) THEN ARRAY[$2::bytea] "
    "             ELSE ARRAY[]::bytea[] END, "
    "        64, false, 'edge_rank') r "
    "    JOIN unnest($1::bytea[]) WITH ORDINALITY AS f(subject_id, idx) "
    "      ON f.subject_id = r.subject_id "
    "   WHERE r.neighbour_id IS NOT NULL "
    "     AND r.type_id IS NOT NULL "
    "     AND r.neighbour_id <> f.subject_id "
    "), ids AS MATERIALIZED ( "
    "  SELECT anchor AS id FROM e UNION SELECT neighbor AS id FROM e "
    "), native_coords AS MATERIALIZED ( "
    "  SELECT public.blake3_send(en.id) AS id, en.coord "
    "    FROM public.laplace_entities(ARRAY(SELECT public.blake3_from_bytea(id) FROM ids WHERE id IS NOT NULL)) en "
    "   WHERE en.coord IS NOT NULL "
    "), coords AS MATERIALIZED ( "
    "  SELECT i.id, COALESCE(nc.coord, (SELECT p.coord FROM laplace.physicalities p WHERE p.entity_id = i.id AND p.type = 1 AND p.coord IS NOT NULL LIMIT 1)) AS coord "
    "    FROM ids i LEFT JOIN native_coords nc ON nc.id = i.id "
    ") "
    "SELECT e.idx, e.neighbor, false, "
    "       e.type_id, e.rating, e.rd, e.witness_count, "
    "       NULL::bytea AS highway_mask, "
    "       ST_X(cs.coord), ST_Y(cs.coord), ST_Z(cs.coord), ST_M(cs.coord), 0::oid, "
    "       ST_X(co.coord), ST_Y(co.coord), ST_Z(co.coord), ST_M(co.coord), 0::oid "
    "FROM e "
    "LEFT JOIN coords cs ON cs.id = e.anchor "
    "LEFT JOIN coords co ON co.id = e.neighbor";

static const char *WALK_BATCH_QUERY_NOGEO =
    "WITH e AS MATERIALIZED ( "
    "  SELECT f.idx, f.subject_id AS anchor, r.neighbour_id AS neighbor, "
    "         r.type_id, r.rating, r.rd, r.witness_count "
    "    FROM consensus.edges_raw_batch( "
    "        $1::bytea[], 'out', "
    "        CASE WHEN $2::bytea IS NULL THEN NULL ELSE ARRAY[$2::bytea] END, "
    "        64, false, 'edge_rank') r "
    "    JOIN unnest($1::bytea[]) WITH ORDINALITY AS f(subject_id, idx) "
    "      ON f.subject_id = r.subject_id "
    "   WHERE r.neighbour_id IS NOT NULL "
    "     AND r.type_id IS NOT NULL "
    "  UNION ALL "
    "  SELECT f.idx, f.subject_id AS anchor, r.neighbour_id AS neighbor, "
    "         r.type_id, r.rating, r.rd, r.witness_count "
    "    FROM consensus.edges_raw_batch( "
    "        $1::bytea[], 'in', "
    "        CASE WHEN $2::bytea IS NULL THEN $3::bytea[] "
    "             WHEN $2::bytea = ANY($3::bytea[]) THEN ARRAY[$2::bytea] "
    "             ELSE ARRAY[]::bytea[] END, "
    "        64, false, 'edge_rank') r "
    "    JOIN unnest($1::bytea[]) WITH ORDINALITY AS f(subject_id, idx) "
    "      ON f.subject_id = r.subject_id "
    "   WHERE r.neighbour_id IS NOT NULL "
    "     AND r.type_id IS NOT NULL "
    "     AND r.neighbour_id <> f.subject_id "
    ") "
    "SELECT e.idx, e.neighbor, false, "
    "       e.type_id, e.rating, e.rd, e.witness_count, "
    "       (SELECT eo.highway_mask FROM laplace.entities eo WHERE eo.id = e.neighbor) "
    "FROM e";

static const char *WALK_BATCH_QUERY_NOGEO_NOMASK =
    "SELECT f.idx, r.neighbour_id AS neighbor, false, "
    "       r.type_id, r.rating, r.rd, r.witness_count, "
    "       NULL::bytea AS highway_mask "
    "  FROM consensus.edges_raw_batch( "
    "      $1::bytea[], 'out', "
    "      CASE WHEN $2::bytea IS NULL THEN NULL ELSE ARRAY[$2::bytea] END, "
    "      64, false, 'edge_rank') r "
    "  JOIN unnest($1::bytea[]) WITH ORDINALITY AS f(subject_id, idx) "
    "    ON f.subject_id = r.subject_id "
    " WHERE r.neighbour_id IS NOT NULL "
    "   AND r.type_id IS NOT NULL "
    "UNION ALL "
    "SELECT f.idx, r.neighbour_id AS neighbor, false, "
    "       r.type_id, r.rating, r.rd, r.witness_count, "
    "       NULL::bytea AS highway_mask "
    "  FROM consensus.edges_raw_batch( "
    "      $1::bytea[], 'in', "
    "      CASE WHEN $2::bytea IS NULL THEN $3::bytea[] "
    "           WHEN $2::bytea = ANY($3::bytea[]) THEN ARRAY[$2::bytea] "
    "           ELSE ARRAY[]::bytea[] END, "
    "      64, false, 'edge_rank') r "
    "  JOIN unnest($1::bytea[]) WITH ORDINALITY AS f(subject_id, idx) "
    "    ON f.subject_id = r.subject_id "
    " WHERE r.neighbour_id IS NOT NULL "
    "   AND r.type_id IS NOT NULL "
    "   AND r.neighbour_id <> f.subject_id";

static SPIPlanPtr walk_batch_plan = NULL;
static SPIPlanPtr walk_batch_nomask_plan = NULL;
static SPIPlanPtr walk_batch_nogeo_plan = NULL;
static SPIPlanPtr walk_batch_nogeo_nomask_plan = NULL;

SPIPlanPtr
laplace_walk_get_batch_plan(bool use_geometry, bool have_intent_mask)
{
    Oid argtypes[3] = { BYTEAARRAYOID, BYTEAOID, BYTEAARRAYOID };
    int opts = CURSOR_OPT_PARALLEL_OK | CURSOR_OPT_GENERIC_PLAN;
    if (use_geometry)
    {
        if (have_intent_mask)
        {
            if (walk_batch_plan == NULL)
            {
                SPIPlanPtr plan = SPI_prepare_cursor(WALK_BATCH_QUERY, 3, argtypes, opts);
                if (plan == NULL)
                    elog(ERROR, "walk_branches: SPI_prepare(batch) failed: %s",
                         SPI_result_code_string(SPI_result));
                if (SPI_keepplan(plan) != 0)
                    elog(ERROR, "walk_branches: SPI_keepplan(batch) failed");
                walk_batch_plan = plan;
            }
            return walk_batch_plan;
        }
        if (walk_batch_nomask_plan == NULL)
        {
            SPIPlanPtr plan = SPI_prepare_cursor(WALK_BATCH_QUERY_NOMASK, 3, argtypes, opts);
            if (plan == NULL)
                elog(ERROR, "walk_branches: SPI_prepare(batch nomask) failed: %s",
                     SPI_result_code_string(SPI_result));
            if (SPI_keepplan(plan) != 0)
                elog(ERROR, "walk_branches: SPI_keepplan(batch nomask) failed");
            walk_batch_nomask_plan = plan;
        }
        return walk_batch_nomask_plan;
    }
    if (have_intent_mask)
    {
        if (walk_batch_nogeo_plan == NULL)
        {
            SPIPlanPtr plan = SPI_prepare_cursor(WALK_BATCH_QUERY_NOGEO, 3, argtypes, opts);
            if (plan == NULL)
                elog(ERROR, "generate walk: SPI_prepare failed: %s",
                     SPI_result_code_string(SPI_result));
            if (SPI_keepplan(plan) != 0)
                elog(ERROR, "generate walk: SPI_keepplan failed");
            walk_batch_nogeo_plan = plan;
        }
        return walk_batch_nogeo_plan;
    }
    if (walk_batch_nogeo_nomask_plan == NULL)
    {
        SPIPlanPtr plan = SPI_prepare_cursor(WALK_BATCH_QUERY_NOGEO_NOMASK, 3, argtypes, opts);
        if (plan == NULL)
            elog(ERROR, "generate walk: SPI_prepare(nomask) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "generate walk: SPI_keepplan(nomask) failed");
        walk_batch_nogeo_nomask_plan = plan;
    }
    return walk_batch_nogeo_nomask_plan;
}

int
laplace_walk_raw_edge_cmp_idx(const void *a, const void *b)
{
    int32 ia = ((const RawEdge *) a)->idx;
    int32 ib = ((const RawEdge *) b)->idx;
    return (ia > ib) - (ia < ib);
}

int
laplace_walk_ranked_edge_cmp_score_desc(const void *a, const void *b)
{
    const RankedEdge *ea = (const RankedEdge *) a;
    const RankedEdge *eb = (const RankedEdge *) b;
    bytea *oa, *ob;
    int    la, lb, c;

    if (ea->base != eb->base)
        return (ea->base < eb->base) - (ea->base > eb->base);
    if (ea->topic_match != eb->topic_match)
        return ea->topic_match ? -1 : 1;
    if (ea->continuity_known != eb->continuity_known)
        return ea->continuity_known ? -1 : 1;
    if (ea->continuity_known && ea->continuity_distance != eb->continuity_distance)
        return (ea->continuity_distance > eb->continuity_distance) -
               (ea->continuity_distance < eb->continuity_distance);
    if (ea->geometry_known != eb->geometry_known)
        return ea->geometry_known ? -1 : 1;
    if (ea->geometry_known && ea->geometry_distance != eb->geometry_distance)
        return (ea->geometry_distance > eb->geometry_distance) -
               (ea->geometry_distance < eb->geometry_distance);

    oa = DatumGetByteaPP(ea->object);
    ob = DatumGetByteaPP(eb->object);
    la = VARSIZE_ANY_EXHDR(oa);
    lb = VARSIZE_ANY_EXHDR(ob);
    c  = memcmp(VARDATA_ANY(oa), VARDATA_ANY(ob), la < lb ? la : lb);
    if (c != 0)
        return c;
    return (la > lb) - (la < lb);
}

bool
laplace_walk_mask_overlaps(Datum highway_mask_bytea, const laplace_mask256_t *intent_mask)
{
    bytea *b;
    laplace_mask256_t cand, overlap;

    if (highway_mask_bytea == (Datum) 0)
        return true;
    b = DatumGetByteaPP(highway_mask_bytea);
    if (VARSIZE_ANY_EXHDR(b) != (int) sizeof(laplace_mask256_t))
        return true;
    memcpy(&cand, VARDATA_ANY(b), sizeof(laplace_mask256_t));
    overlap = highway_table_mask_and(cand, *intent_mask);
    return highway_table_mask_any(&overlap) != 0;
}

static const char *ORDINAL_CONTINUITY_QUERY =
    "WITH t AS ( "
    "  SELECT p.trajectory FROM laplace.physicalities p "
    "  WHERE p.type = 1 AND p.trajectory IS NOT NULL "
    "    AND public.laplace_trajectory_constituent_ids(p.trajectory) @> ARRAY[$1]::bytea[] "
    "  LIMIT 1 "
    ") "
    "SELECT ST_X(dp.geom), ST_Y(dp.geom), ST_Z(dp.geom), ST_M(dp.geom) "
    "FROM t, ST_DumpPoints(t.trajectory) dp "
    "ORDER BY (dp.path)[1]";

static SPIPlanPtr ordinal_continuity_plan = NULL;

static void
ensure_ordinal_continuity_plan(void)
{
    if (ordinal_continuity_plan == NULL)
    {
        Oid argtypes[1] = { BYTEAOID };
        SPIPlanPtr plan = SPI_prepare_cursor(ORDINAL_CONTINUITY_QUERY, 1, argtypes, CURSOR_OPT_PARALLEL_OK);

        if (plan == NULL)
            elog(ERROR, "walk_branches: SPI_prepare_cursor(ordinal continuity) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "walk_branches: SPI_keepplan(ordinal continuity) failed");
        ordinal_continuity_plan = plan;
    }
}

bool
laplace_walk_ordinal_continuity_distance(Datum subject_entity, Datum object_entity,
                                         uint64 *distance)
{
    Datum  args[1];
    int    rc;
    bool   have_subj = false, have_obj = false;
    int64  subj_ord = 0, obj_ord = 0;
    hash128_t subj_h, obj_h;

    ensure_ordinal_continuity_plan();
    subj_h = datum_to_hash128(subject_entity);
    obj_h  = datum_to_hash128(object_entity);

    args[0] = object_entity;
    rc = SPI_execute_plan(ordinal_continuity_plan, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return false;

    int64 ordinal = 1;

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td  = SPI_tuptable->tupdesc;
        bool isnull[4];
        double vertex[4];
        mantissa_payload_t payload;
        int64 run;

        vertex[0] = DatumGetFloat8(SPI_getbinval(tup, td, 1, &isnull[0]));
        vertex[1] = DatumGetFloat8(SPI_getbinval(tup, td, 2, &isnull[1]));
        vertex[2] = DatumGetFloat8(SPI_getbinval(tup, td, 3, &isnull[2]));
        vertex[3] = DatumGetFloat8(SPI_getbinval(tup, td, 4, &isnull[3]));
        if (isnull[0] || isnull[1] || isnull[2] || isnull[3])
        {
            ordinal += 1;
            continue;
        }

        mantissa_unpack(vertex, &payload);
        run = (payload.run_length < 1) ? 1 : (int64) payload.run_length;

        if (!have_obj && hash128_eq(&payload.entity_id, &obj_h))
        {
            obj_ord = ordinal;
            have_obj = true;
        }
        if (!have_subj && hash128_eq(&payload.entity_id, &subj_h))
        {
            subj_ord = ordinal;
            have_subj = true;
        }
        if (have_obj && have_subj)
            break;

        ordinal += run;
    }
    if (!have_obj || !have_subj)
        return false;
    {
        int64 delta = obj_ord - subj_ord;
        if (delta < 0) delta = -delta;
        *distance = (uint64) delta;
        return true;
    }
}

Datum
pg_laplace_walk_strongest(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    bytea  *prompt;
    Datum   type_datum = 0;
    bool    type_null;
    int32   max_depth;
    Datum   cur;
    Datum  *seen;
    int     n_seen, seen_cap;

    if (PG_ARGISNULL(0))
    {
        InitMaterializedSRF(fcinfo, 0);
        return (Datum) 0;
    }
    prompt    = PG_GETARG_BYTEA_PP(0);
    type_null = PG_ARGISNULL(1);
    if (!type_null)
        type_datum = PG_GETARG_DATUM(1);
    max_depth = PG_ARGISNULL(2) ? PG_INT32_MAX : PG_GETARG_INT32(2);
    if (max_depth < 0)
        ereport(ERROR, (errmsg("walk_strongest: depth must be >= 0")));

    InitMaterializedSRF(fcinfo, 0);

    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "walk_strongest: SPI_connect failed");
    ensure_edge_plan();

    seen_cap = 8;
    seen = (Datum *) palloc(sizeof(Datum) * seen_cap);
    cur = copy_bytea_datum(PointerGetDatum(prompt));
    seen[0] = cur;
    n_seen = 1;

    for (int step = 1; step <= max_depth; step++)
    {
        ArrayType *seen_arr = construct_array(seen, n_seen, BYTEAOID, -1, false, TYPALIGN_INT);
        Datum  args[4];
        char   nulls[5] = "    ";
        int    rc;
        HeapTuple tup;
        TupleDesc td;
        bool   isnull;
        Datum  obj, etype;
        int64  rating, rd;
        Datum  values[4];
        bool   rnulls[4] = { false, false, false, false };

        args[0] = cur;
        args[1] = type_null ? (Datum) 0 : type_datum;
        if (type_null) nulls[1] = 'n';
        args[2] = Int32GetDatum(1);
        args[3] = PointerGetDatum(seen_arr);

        rc = SPI_execute_plan(edge_plan, args, nulls, true, 1);
        if (rc != SPI_OK_SELECT)
            elog(ERROR, "walk_strongest: edge query failed: %s",
                 SPI_result_code_string(rc));
        if (SPI_processed == 0)
        {
            pfree(seen_arr);
            break;
        }

        tup = SPI_tuptable->vals[0];
        td  = SPI_tuptable->tupdesc;
        obj    = copy_bytea_datum(SPI_getbinval(tup, td, 1, &isnull));
        etype  = copy_bytea_datum(SPI_getbinval(tup, td, 2, &isnull));
        rating = DatumGetInt64(SPI_getbinval(tup, td, 3, &isnull));
        rd     = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));

        values[0] = Int32GetDatum(step);
        values[1] = etype;
        values[2] = obj;
        values[3] = eff_mu_display_numeric(rating, rd);
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, rnulls);

        if (n_seen == seen_cap)
        {
            seen_cap *= 2;
            seen = (Datum *) repalloc(seen, sizeof(Datum) * seen_cap);
        }
        seen[n_seen++] = obj;
        cur = obj;
        pfree(seen_arr);
    }

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
