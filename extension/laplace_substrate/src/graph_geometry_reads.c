/*
 * graph_geometry_reads.c - the 4-D geometric reads (coordinate/trajectory
 * distance over PostGIS): nearest_neighbors_4d, structural_cluster,
 * structural_locale and their spi_* distance helpers. The consensus-graph
 * taxonomy/contrast/cascade reads that used to share this file now live in
 * graph_taxonomy.c / graph_contrast.c / graph_cascade.c.
 */
#include "postgres.h"

#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/numeric.h"

#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "spi_common.h"
#include "spi_nested.h"

PG_FUNCTION_INFO_V1(pg_laplace_nearest_neighbors_4d);
PG_FUNCTION_INFO_V1(pg_laplace_structural_cluster);
PG_FUNCTION_INFO_V1(pg_laplace_structural_locale);

/* PostGIS geometry type OID (defined below); used by the geometry-param SPI calls. */
static Oid geom_typeoid(void);

Datum
pg_laplace_nearest_neighbors_4d(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    text          *word_txt;
    int32          k;
    Datum          seed_id, seed_coord, seed_traj;
    bool           seed_traj_null = true;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("nearest_neighbors_4d: p_word must not be NULL")));
    word_txt = PG_GETARG_TEXT_PP(0);
    k = PG_ARGISNULL(1) ? 10 : PG_GETARG_INT32(1);

    InitMaterializedSRF(fcinfo, 0);

    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "nearest_neighbors_4d: SPI_connect failed");

    {
        Oid   argtypes[1] = { TEXTOID };
        Datum args[1] = { PointerGetDatum(word_txt) };
        int   rc = SPI_execute_with_args(
            "SELECT p.entity_id, p.coord, p.trajectory "
            "FROM laplace.physicalities p "
            "JOIN LATERAL (SELECT id FROM laplace.prompt_state($1) LIMIT 1) s "
            "  ON p.entity_id = s.id "
            "WHERE p.type = 1 AND p.coord IS NOT NULL "
            "LIMIT 1",
            1, argtypes, args, NULL, true, 1);
        bool isnull;

        if (rc != SPI_OK_SELECT || SPI_processed == 0)
        {
            laplace_spi_finish(spi_top);
            return (Datum) 0;
        }

        seed_id = copy_bytea_datum(
            SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));
        seed_coord = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 2, &isnull);
        seed_traj = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 3, &seed_traj_null);
        if (seed_traj_null)
            seed_traj = (Datum) 0;
    }

    {
        int   knn_limit = k * 20;
        if (knn_limit < 200)
            knn_limit = 200;
        Oid   argtypes[2] = { geom_typeoid(), INT4OID };
        Datum args[2] = { seed_coord, Int32GetDatum(knn_limit) };
        int   rc = SPI_execute_with_args(
            "SELECT p.entity_id, p.coord, p.trajectory "
            "FROM laplace.physicalities p "
            "WHERE p.type = 1 "
            "ORDER BY p.coord <<->> $1 "
            "LIMIT $2",
            2, argtypes, args, NULL, true, 0);

        if (rc != SPI_OK_SELECT)
            elog(ERROR, "nearest_neighbors_4d: knn query failed");

        typedef struct {
            Datum id;
            Datum coord;
            Datum traj;
            bool  traj_null;
            double geo;
        } NbrCand;

        NbrCand *cands = (NbrCand *) palloc0(sizeof(NbrCand) * SPI_processed);
        int n_cand = 0;

        for (uint64 r = 0; r < SPI_processed; r++)
        {
            HeapTuple tup = SPI_tuptable->vals[r];
            TupleDesc td  = SPI_tuptable->tupdesc;
            bool      isnull;
            Datum     eid, ecoord, etraj;
            bool      etraj_null;
            Oid       gargs[2] = { geom_typeoid(), geom_typeoid() };
            Datum     gargv[2];
            int       grc;
            double    geo;

            eid = SPI_getbinval(tup, td, 1, &isnull);
            if (!isnull && bytea_eq(eid, seed_id))
                continue;

            ecoord = SPI_getbinval(tup, td, 2, &isnull);
            etraj = SPI_getbinval(tup, td, 3, &etraj_null);

            gargv[0] = ecoord;
            gargv[1] = seed_coord;
            grc = SPI_execute_with_args(
                "SELECT public.laplace_angular_distance_4d($1, $2)",
                2, gargs, gargv, NULL, true, 1);
            if (grc != SPI_OK_SELECT || SPI_processed == 0)
                continue;
            geo = DatumGetFloat8(
                SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));

            for (int i = 0; i < n_cand; i++)
            {
                if (bytea_eq(cands[i].id, eid))
                {
                    if (geo < cands[i].geo)
                    {
                        cands[i].geo = geo;
                        cands[i].coord = ecoord;
                        cands[i].traj = etraj;
                        cands[i].traj_null = etraj_null;
                    }
                    eid = (Datum) 0;
                    break;
                }
            }
            if (eid == (Datum) 0)
                continue;

            cands[n_cand].id = copy_bytea_datum(eid);
            cands[n_cand].coord = ecoord;
            cands[n_cand].traj = etraj;
            cands[n_cand].traj_null = etraj_null;
            cands[n_cand].geo = geo;
            n_cand++;
        }

        for (int i = 1; i < n_cand; i++)
        {
            NbrCand tmp = cands[i];
            int j = i;
            while (j > 0 && cands[j - 1].geo > tmp.geo)
            {
                cands[j] = cands[j - 1];
                j--;
            }
            cands[j] = tmp;
        }

        int emitted = 0;
        for (int i = 0; i < n_cand && emitted < k; i++)
        {
            Datum values[3];
            bool  nulls[3] = { false, false, false };
            Oid   rargs[2] = { BYTEAOID, INT4OID };
            Datum rargv[2];
            int   rrc;

            rargv[0] = cands[i].id;
            rargv[1] = Int32GetDatum(24);
            rrc = SPI_execute_with_args(
                "SELECT laplace.render_text($1, $2)",
                2, rargs, rargv, NULL, true, 1);
            if (rrc != SPI_OK_SELECT || SPI_processed == 0)
                continue;

            values[0] = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1,
                                      &nulls[0]);
            values[1] = Float8GetDatum(cands[i].geo);

            if (!seed_traj_null && !cands[i].traj_null)
            {
                Oid   fargs[2] = { geom_typeoid(), geom_typeoid() };
                Datum fargv[2] = { cands[i].traj, seed_traj };
                int   frc = SPI_execute_with_args(
                    "SELECT public.laplace_frechet_4d($1, $2)",
                    2, fargs, fargv, NULL, true, 1);
                if (frc == SPI_OK_SELECT && SPI_processed > 0)
                    values[2] = SPI_getbinval(SPI_tuptable->vals[0],
                                              SPI_tuptable->tupdesc, 1, &nulls[2]);
                else
                    nulls[2] = true;
            }
            else
                nulls[2] = true;

            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
            emitted++;
        }
    }

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}

/* PostGIS geometry type OID (dynamic — geometry is an extension type). A geometry
 * Datum is gserialized, NOT WKB; passing it through SPI as BYTEAOID makes the planner
 * insert a bytea->geometry cast that WKB-parses the gserialized bytes and fails with
 * "Unknown WKB type" (PostGIS 3.7 dropped the implicit pass-through). Declaring the
 * param with the geometry OID passes the Datum straight through, no cast. */
static Oid
geom_typeoid(void)
{
    static Oid cached = InvalidOid;
    if (cached == InvalidOid)
        cached = TypenameGetTypid("geometry");
    return cached;
}

static double
spi_angular_distance_4d(Datum a, Datum b)
{
    Oid   argtypes[2] = { geom_typeoid(), geom_typeoid() };
    Datum args[2] = { a, b };
    bool  isnull;
    int   rc = SPI_execute_with_args(
        "SELECT public.laplace_angular_distance_4d($1, $2)",
        2, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return -1.0;
    return DatumGetFloat8(
        SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));
}

static Datum
spi_entity_curve(Datum entity_id)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { entity_id };
    bool  isnull;
    int   rc = SPI_execute_with_args(
        "SELECT laplace.entity_curve($1)",
        1, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    return SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
}

static double
spi_frechet_4d(Datum curve_a, Datum curve_b)
{
    Oid   argtypes[2] = { geom_typeoid(), geom_typeoid() };
    Datum args[2] = { curve_a, curve_b };
    bool  isnull;
    int   rc = SPI_execute_with_args(
        "SELECT public.laplace_frechet_4d($1, $2)",
        2, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return -1.0;
    if (SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull), isnull)
        return -1.0;
    return DatumGetFloat8(
        SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));
}

static int64
spi_physicality_count(Datum entity_id)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { entity_id };
    bool  isnull;
    int   rc = SPI_execute_with_args(
        "SELECT count(*) FROM laplace.physicalities pp WHERE pp.entity_id = $1",
        1, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return 0;
    return DatumGetInt64(
        SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));
}

Datum
pg_laplace_structural_cluster(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum          seed_id;
    double         eps;
    int32          lim;
    Datum          seed_coord;
    Datum          seed_curve;
    bool           seed_curve_null;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("structural_cluster: p_seed must not be NULL")));
    seed_id = PG_GETARG_DATUM(0);
    eps = PG_ARGISNULL(1) ? 0.05 : PG_GETARG_FLOAT8(1);
    lim = PG_ARGISNULL(2) ? 200 : PG_GETARG_INT32(2);

    InitMaterializedSRF(fcinfo, 0);

    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "structural_cluster: SPI_connect failed");

    {
        Oid   argtypes[1] = { BYTEAOID };
        Datum args[1] = { seed_id };
        bool  isnull;
        int   rc = SPI_execute_with_args(
            "SELECT p.coord FROM laplace.physicalities p "
            "WHERE p.entity_id = $1 AND p.type = 1 AND p.coord IS NOT NULL "
            "LIMIT 1",
            1, argtypes, args, NULL, true, 1);

        if (rc != SPI_OK_SELECT || SPI_processed == 0)
        {
            laplace_spi_finish(spi_top);
            return (Datum) 0;
        }
        seed_coord = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
        if (isnull)
        {
            laplace_spi_finish(spi_top);
            return (Datum) 0;
        }
        /* seed_coord points INTO this tuptable; the next SPI call (spi_entity_curve)
         * frees it, so $1 in the KNN would read freed memory ("Unknown WKB type").
         * Copy it into the caller context to survive every later SPI call. */
        seed_coord = copy_bytea_datum(seed_coord);
    }

    seed_curve = spi_entity_curve(seed_id);
    seed_curve_null = (seed_curve == (Datum) 0);
    if (seed_curve_null)
    {
        laplace_spi_finish(spi_top);
        return (Datum) 0;
    }
    /* seed_curve points into the entity_curve tuptable; copy it into the caller
     * context so it survives the KNN query and every per-candidate frechet call. */
    seed_curve = copy_bytea_datum(seed_curve);

    {
        int   knn_limit = lim * 20;
        if (knn_limit < 2000)
            knn_limit = 2000;
        Oid   argtypes[3] = { geom_typeoid(), BYTEAOID, INT4OID };
        Datum args[3] = { seed_coord, seed_id, Int32GetDatum(knn_limit) };
        int   rc = SPI_execute_with_args(
            "SELECT p.entity_id FROM laplace.physicalities p "
            "WHERE p.type = 1 AND p.coord IS NOT NULL AND p.entity_id <> $2 "
            "ORDER BY p.coord <<->> $1 "
            "LIMIT $3",
            3, argtypes, args, NULL, true, 0);

        if (rc != SPI_OK_SELECT)
            elog(ERROR, "structural_cluster: knn query failed");

        typedef struct {
            Datum  entity_id;
            double fr;
        } ScoredCand;

        /* Snapshot every KNN id into the caller context BEFORE any inner SPI call.
         * spi_entity_curve / spi_frechet_4d each run their own SPI query, which
         * reassigns the global SPI_tuptable; iterating SPI_tuptable->vals[r] across
         * those calls reads rows out of the wrong (inner) result set -> garbage past
         * r=0, which is why this only ever returned a single neighbor. */
        uint64  n_cand      = SPI_processed;
        Datum  *cand        = (Datum *) palloc0(sizeof(Datum) * (n_cand ? n_cand : 1));
        int     n_cand_real = 0;
        for (uint64 r = 0; r < n_cand; r++)
        {
            bool  isnull;
            Datum eid = SPI_getbinval(SPI_tuptable->vals[r], SPI_tuptable->tupdesc,
                                      1, &isnull);
            if (isnull)
                continue;
            cand[n_cand_real++] = copy_bytea_datum(eid);
        }

        ScoredCand *scored = (ScoredCand *)
            palloc0(sizeof(ScoredCand) * (n_cand_real ? n_cand_real : 1));
        int n_scored = 0;

        for (int r = 0; r < n_cand_real; r++)
        {
            Datum  eid = cand[r];
            double fr;
            int    idx;

            fr = spi_frechet_4d(spi_entity_curve(eid), seed_curve);
            if (fr < 0.0)
                continue;

            for (idx = 0; idx < n_scored; idx++)
            {
                if (bytea_eq(scored[idx].entity_id, eid))
                    break;
            }
            if (idx < n_scored)
                continue;

            scored[n_scored].entity_id = eid;  /* already copied into caller ctx */
            scored[n_scored].fr = fr;
            n_scored++;
        }

        for (int i = 1; i < n_scored; i++)
        {
            ScoredCand tmp = scored[i];
            int j = i;
            while (j > 0 && scored[j - 1].fr > tmp.fr)
            {
                scored[j] = scored[j - 1];
                j--;
            }
            scored[j] = tmp;
        }

        int emitted = 0;
        for (int i = 0; i < n_scored && emitted < lim; i++)
        {
            Datum values[4];
            bool  nulls[4] = { false, false, false, false };
            Oid   rargs[2] = { BYTEAOID, INT4OID };
            Datum rargv[2];
            int   rrc;

            if (scored[i].fr > eps)
                continue;

            rargv[0] = scored[i].entity_id;
            rargv[1] = Int32GetDatum(48);
            rrc = SPI_execute_with_args(
                "SELECT laplace.render_text($1, $2)",
                2, rargs, rargv, NULL, true, 1);
            if (rrc != SPI_OK_SELECT || SPI_processed == 0)
                continue;

            values[0] = scored[i].entity_id;
            values[1] = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1,
                                      &nulls[1]);
            values[2] = Float8GetDatum(scored[i].fr);
            values[3] = Int64GetDatum(spi_physicality_count(scored[i].entity_id));

            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
            emitted++;
        }
    }

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}

Datum
pg_laplace_structural_locale(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    text          *word_txt;
    double         near_thresh;
    Datum          seed_id;
    Datum          seed_coord;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("structural_locale: p_word must not be NULL")));
    word_txt = PG_GETARG_TEXT_PP(0);
    near_thresh = PG_ARGISNULL(1) ? 0.05 : PG_GETARG_FLOAT8(1);

    InitMaterializedSRF(fcinfo, 0);

    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "structural_locale: SPI_connect failed");

    {
        Oid   argtypes[1] = { TEXTOID };
        Datum args[1] = { PointerGetDatum(word_txt) };
        bool  isnull;
        int   rc = SPI_execute_with_args(
            "SELECT p.entity_id, p.coord "
            "FROM laplace.physicalities p "
            "JOIN LATERAL (SELECT id FROM laplace.prompt_state($1) LIMIT 1) s "
            "  ON p.entity_id = s.id "
            "WHERE p.type = 1 AND p.coord IS NOT NULL "
            "LIMIT 1",
            1, argtypes, args, NULL, true, 1);

        if (rc != SPI_OK_SELECT || SPI_processed == 0)
        {
            laplace_spi_finish(spi_top);
            return (Datum) 0;
        }

        seed_id = copy_bytea_datum(
            SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));
        seed_coord = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 2, &isnull);
        if (isnull)
        {
            laplace_spi_finish(spi_top);
            return (Datum) 0;
        }
    }

    {
        Oid   argtypes[2] = { geom_typeoid(), INT4OID };
        Datum args[2] = { seed_coord, Int32GetDatum(3000) };
        int   rc = SPI_execute_with_args(
            "SELECT p.entity_id, p.coord "
            "FROM laplace.physicalities p "
            "WHERE p.type = 1 "
            "ORDER BY p.coord <<->> $1 "
            "LIMIT $2",
            2, argtypes, args, NULL, true, 0);

        if (rc != SPI_OK_SELECT)
            elog(ERROR, "structural_locale: knn query failed");

        typedef struct {
            Datum  entity_id;
            double d;
        } GeoCand;

        GeoCand *cands = (GeoCand *) palloc0(sizeof(GeoCand) * SPI_processed);
        int n_cand = 0;
        double min_d = -1.0;
        int64 within_near = 0;
        int64 within_2x = 0;
        int64 within_5x = 0;

        for (uint64 r = 0; r < SPI_processed; r++)
        {
            HeapTuple tup = SPI_tuptable->vals[r];
            TupleDesc td  = SPI_tuptable->tupdesc;
            bool      isnull;
            Datum     eid;
            Datum     ecoord;
            double    d;
            int       idx;

            eid = SPI_getbinval(tup, td, 1, &isnull);
            ecoord = SPI_getbinval(tup, td, 2, &isnull);
            if (isnull || bytea_eq(eid, seed_id))
                continue;

            d = spi_angular_distance_4d(ecoord, seed_coord);
            if (d < 0.0)
                continue;

            for (idx = 0; idx < n_cand; idx++)
            {
                if (bytea_eq(cands[idx].entity_id, eid))
                {
                    if (d < cands[idx].d)
                        cands[idx].d = d;
                    eid = (Datum) 0;
                    break;
                }
            }
            if (eid == (Datum) 0)
                continue;

            cands[n_cand].entity_id = copy_bytea_datum(eid);
            cands[n_cand].d = d;
            n_cand++;
        }

        for (int i = 0; i < n_cand; i++)
        {
            double d = cands[i].d;
            if (min_d < 0.0 || d < min_d)
                min_d = d;
            if (d <= near_thresh)
                within_near++;
            if (d <= 2.0 * near_thresh)
                within_2x++;
            if (d <= 5.0 * near_thresh)
                within_5x++;
        }

        {
            Datum values[5];
            bool  nulls[5] = { false, false, false, false, false };

            if (min_d < 0.0)
            {
                nulls[0] = true;
                values[4] = BoolGetDatum(true);
            }
            else
            {
                values[0] = Float8GetDatum(min_d);
                values[4] = BoolGetDatum(min_d > near_thresh);
            }
            values[1] = Int64GetDatum(within_near);
            values[2] = Int64GetDatum(within_2x);
            values[3] = Int64GetDatum(within_5x);

            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        }
    }

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}

