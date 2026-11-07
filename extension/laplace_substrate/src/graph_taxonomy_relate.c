#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/numeric.h"

#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "graph_taxonomy_internal.h"

PG_FUNCTION_INFO_V1(pg_laplace_relate_path_raw);

typedef struct RelateSeed
{
    hash128_t id;
    int64_t   mu;
    bool      mu_valid;
} RelateSeed;

typedef struct RelateDirect
{
    bool      found;
    hash128_t x;
    hash128_t y;
    hash128_t type;
    int       dir;
    int64_t   mu;
} RelateDirect;

static SPIPlanPtr relate_senses_plan = NULL;

static void
ensure_relate_plans(void)
{
    if (relate_senses_plan == NULL)
    {
        Oid argtypes[1] = { BYTEAOID };

        relate_senses_plan = SPI_prepare_cursor(
            "SELECT synset_id, (eff_mu * 1000000000)::bigint "
            "FROM lexical.senses($1) WHERE synset_id IS NOT NULL",
            1, argtypes, CURSOR_OPT_PARALLEL_OK);
        if (relate_senses_plan == NULL || SPI_keepplan(relate_senses_plan) != 0)
            elog(ERROR, "relate_path_raw: could not prepare lexical sense plan");
    }
}

static int
relate_seed_find(const RelateSeed *seeds, int n, const hash128_t *id)
{
    for (int i = 0; i < n; i++)
        if (hash128_eq(&seeds[i].id, id))
            return i;
    return -1;
}

static void
relate_seed_add(RelateSeed **seeds, int *n, int *cap,
                const hash128_t *id, bool mu_valid, int64 mu)
{
    int existing = relate_seed_find(*seeds, *n, id);

    if (existing >= 0)
    {
        if (mu_valid && (!(*seeds)[existing].mu_valid ||
                         mu > (*seeds)[existing].mu))
        {
            (*seeds)[existing].mu = mu;
            (*seeds)[existing].mu_valid = true;
        }
        return;
    }
    if (*n >= *cap)
    {
        *cap *= 2;
        *seeds = (RelateSeed *) repalloc(*seeds, sizeof(RelateSeed) * *cap);
    }
    (*seeds)[*n].id = *id;
    (*seeds)[*n].mu = mu;
    (*seeds)[*n].mu_valid = mu_valid;
    (*n)++;
}

static int
fetch_relate_seeds(Datum endpoint, RelateSeed **seeds_out)
{
    int         cap = 16;
    int         n = 0;
    RelateSeed *seeds = (RelateSeed *) palloc(sizeof(RelateSeed) * cap);
    Datum       args[1] = { endpoint };
    hash128_t   endpoint_id = datum_to_hash128(endpoint);
    int         rc;

    relate_seed_add(&seeds, &n, &cap, &endpoint_id, false, 0);
    rc = SPI_execute_plan(relate_senses_plan, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "relate_path_raw: lexical.senses failed: %s",
             SPI_result_code_string(rc));

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td = SPI_tuptable->tupdesc;
        bool      id_null;
        bool      mu_null;
        Datum     id_d = SPI_getbinval(tup, td, 1, &id_null);
        Datum     mu_d = SPI_getbinval(tup, td, 2, &mu_null);

        if (!id_null)
        {
            hash128_t id = datum_to_hash128(id_d);
            relate_seed_add(&seeds, &n, &cap, &id, !mu_null,
                            mu_null ? 0 : DatumGetInt64(mu_d));
        }
    }
    *seeds_out = seeds;
    return n;
}

static bool
relate_candidate_better(int len, bool mu_valid, int64 mu,
                        int best_len, bool best_mu_valid, int64 best_mu)
{
    if (len != best_len)
        return len < best_len;
    if (mu_valid != best_mu_valid)
        return mu_valid;
    return mu_valid && mu > best_mu;
}

/* One batched scan for one orientation. reverse=false scans x -> y; true scans
 * y -> x but stores the result in display order x,y with direction -1. */
static void
relate_direct_scan(const RelateSeed *from, int n_from,
                   const RelateSeed *target, int n_target,
                   Datum types_arr_datum, bool reverse,
                   RelateDirect *best)
{
    Datum     *from_ids = (Datum *) palloc(sizeof(Datum) * n_from);
    ArrayType *from_arr;
    Datum      args[2];
    int        rc;

    for (int i = 0; i < n_from; i++)
        from_ids[i] = hash128_to_datum(&from[i].id);
    from_arr = construct_array(from_ids, n_from, BYTEAOID,
                               -1, false, TYPALIGN_INT);
    args[0] = PointerGetDatum(from_arr);
    args[1] = types_arr_datum;
    rc = SPI_execute_plan(tax_edges_plan, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "relate_path_raw: lateral edge scan failed: %s",
             SPI_result_code_string(rc));

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td = SPI_tuptable->tupdesc;
        bool      isnull;
        int64     ord = DatumGetInt64(SPI_getbinval(tup, td, 1, &isnull));
        Datum     object_d;
        hash128_t object;
        hash128_t type;
        int64     rating;
        int64     rd;
        int64     mu;

        if (isnull || ord < 1 || ord > n_from)
            continue;
        object_d = SPI_getbinval(tup, td, 2, &isnull);
        if (isnull)
            continue;
        object = datum_to_hash128(object_d);
        if (relate_seed_find(target, n_target, &object) < 0)
            continue;
        type = datum_to_hash128(SPI_getbinval(tup, td, 3, &isnull));
        rating = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));
        rd = DatumGetInt64(SPI_getbinval(tup, td, 5, &isnull));
        mu = eff_mu_display_fp(rating, rd);
        if (!best->found || mu > best->mu)
        {
            best->found = true;
            best->x = reverse ? object : from[ord - 1].id;
            best->y = reverse ? from[ord - 1].id : object;
            best->type = type;
            best->dir = reverse ? -1 : 1;
            best->mu = mu;
        }
    }
    pfree(from_arr);
    pfree(from_ids);
}

Datum
pg_laplace_relate_path_raw(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum          x;
    Datum          y;
    int32          max_depth;
    RelateSeed    *sx;
    RelateSeed    *sy;
    int            n_sx;
    int            n_sy;
    hash128_t     *up_types;
    hash128_t     *lateral_types;
    int            n_up;
    int            n_lateral;
    TaxNode       *nx;
    TaxNode       *ny;
    int            n_nx;
    int            n_ny;
    RelateDirect   direct = { 0 };
    int            best_x = -1;
    int            best_y = -1;
    int            best_len = INT_MAX;
    int64_t        best_mu = 0;
    bool           best_mu_valid = false;
    HTAB          *xmap;
    HASHCTL        ctl;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("relate_path_raw: endpoints must not be NULL")));
    x = PG_GETARG_DATUM(0);
    y = PG_GETARG_DATUM(1);
    max_depth = PG_ARGISNULL(2) ? 7 : PG_GETARG_INT32(2);
    if (max_depth < 0)
        ereport(ERROR, (errmsg("relate_path_raw: p_depth must be >= 0")));

    InitMaterializedSRF(fcinfo, 0);
    {
        bool spi_top = false;
        if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
            elog(ERROR, "relate_path_raw: SPI_connect failed");

        ensure_tax_edges_plan();
        ensure_relate_plans();
        n_sx = fetch_relate_seeds(x, &sx);
        n_sy = fetch_relate_seeds(y, &sy);
        tax_fetch_relation_set("PATH_UPWARD", &up_types, &n_up);
        tax_fetch_relation_set("PATH_LATERAL", &lateral_types, &n_lateral);

        {
            Datum *type_datums = (Datum *) palloc(sizeof(Datum) * n_lateral);
            Datum  types_arr;

            for (int i = 0; i < n_lateral; i++)
                type_datums[i] = hash128_to_datum(&lateral_types[i]);
            types_arr = PointerGetDatum(construct_array(
                type_datums, n_lateral, BYTEAOID, -1, false, TYPALIGN_INT));
            relate_direct_scan(sx, n_sx, sy, n_sy, types_arr, false, &direct);
            relate_direct_scan(sy, n_sy, sx, n_sx, types_arr, true, &direct);
            pfree(DatumGetPointer(types_arr));
            pfree(type_datums);
        }

        {
            hash128_t *ids_x = (hash128_t *) palloc(sizeof(hash128_t) * n_sx);
            hash128_t *ids_y = (hash128_t *) palloc(sizeof(hash128_t) * n_sy);
            int64_t   *mus_x = (int64_t *) palloc(sizeof(int64_t) * n_sx);
            int64_t   *mus_y = (int64_t *) palloc(sizeof(int64_t) * n_sy);
            bool      *valid_x = (bool *) palloc(sizeof(bool) * n_sx);
            bool      *valid_y = (bool *) palloc(sizeof(bool) * n_sy);

            for (int i = 0; i < n_sx; i++)
            {
                ids_x[i] = sx[i].id; mus_x[i] = sx[i].mu; valid_x[i] = sx[i].mu_valid;
            }
            for (int i = 0; i < n_sy; i++)
            {
                ids_y[i] = sy[i].id; mus_y[i] = sy[i].mu; valid_y[i] = sy[i].mu_valid;
            }
            n_nx = tax_bfs_up_weighted(ids_x, mus_x, valid_x, n_sx, max_depth,
                                       up_types, n_up, &nx);
            n_ny = tax_bfs_up_weighted(ids_y, mus_y, valid_y, n_sy, max_depth,
                                       up_types, n_up, &ny);
        }

        memset(&ctl, 0, sizeof(ctl));
        ctl.keysize = 16;
        ctl.entrysize = sizeof(TaxIdxEntry);
        xmap = hash_create("relate_path_raw xmap", TAX_WALK_INITIAL, &ctl,
                           HASH_ELEM | HASH_BLOBS);
        for (int i = 0; i < n_nx; i++)
            tax_idx_add(xmap, &nx[i].id, i);

        for (int iy = 0; iy < n_ny; iy++)
        {
            int ix = tax_idx_find(xmap, &ny[iy].id);
            int len;
            int64_t mu;
            bool mu_valid;

            if (ix < 0)
                continue;
            len = nx[ix].depth + ny[iy].depth;
            if (len == 0)
                continue;
            if (nx[ix].path_mu_valid && ny[iy].path_mu_valid)
            {
                mu = Min(nx[ix].path_mu, ny[iy].path_mu);
                mu_valid = true;
            }
            else if (nx[ix].path_mu_valid)
            {
                mu = nx[ix].path_mu;
                mu_valid = true;
            }
            else
            {
                mu = ny[iy].path_mu;
                mu_valid = ny[iy].path_mu_valid;
            }
            if (relate_candidate_better(len, mu_valid, mu,
                                        best_len, best_mu_valid, best_mu))
            {
                best_x = ix;
                best_y = iy;
                best_len = len;
                best_mu = mu;
                best_mu_valid = mu_valid;
            }
        }

        if (direct.found &&
            relate_candidate_better(1, true, direct.mu,
                                    best_len, best_mu_valid, best_mu))
        {
            Datum values[5];
            bool  nulls[5] = { false, false, false, false, false };
            Datum nodes[2] = { hash128_to_datum(&direct.x), hash128_to_datum(&direct.y) };
            Datum types[1] = { hash128_to_datum(&direct.type) };
            Datum dirs[1] = { Int32GetDatum(direct.dir) };

            values[0] = PointerGetDatum(construct_array(nodes, 2, BYTEAOID,
                                                        -1, false, TYPALIGN_INT));
            values[1] = PointerGetDatum(construct_array(types, 1, BYTEAOID,
                                                        -1, false, TYPALIGN_INT));
            values[2] = PointerGetDatum(construct_array(dirs, 1, INT4OID,
                                                        4, true, TYPALIGN_INT));
            values[3] = fp_display_numeric(direct.mu);
            values[4] = hash128_to_datum(&direct.type);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        }
        else if (best_x >= 0)
        {
            Datum values[5];
            bool  nulls[5] = { false, false, false, false, true };
            Datum *xp;
            Datum *xt;
            Datum *yp;
            Datum *yt;
            Datum xmu;
            Datum ymu;
            Datum *nodes = (Datum *) palloc(sizeof(Datum) * (best_len + 1));
            Datum *types = (Datum *) palloc(sizeof(Datum) * best_len);
            Datum *dirs = (Datum *) palloc(sizeof(Datum) * best_len);
            int dx = nx[best_x].depth;
            int dy = ny[best_y].depth;
            int pos = 0;

            reconstruct_path(nx, best_x, &xp, &xt, &xmu);
            reconstruct_path(ny, best_y, &yp, &yt, &ymu);
            for (int i = 0; i <= dx; i++)
                nodes[pos++] = xp[i];
            for (int i = dy - 1; i >= 0; i--)
                nodes[pos++] = yp[i];
            pos = 0;
            for (int i = 0; i < dx; i++)
            {
                types[pos] = xt[i];
                dirs[pos++] = Int32GetDatum(1);
            }
            for (int i = dy - 1; i >= 0; i--)
            {
                types[pos] = yt[i];
                dirs[pos++] = Int32GetDatum(-1);
            }
            values[0] = PointerGetDatum(construct_array(nodes, best_len + 1,
                                                        BYTEAOID, -1, false, TYPALIGN_INT));
            values[1] = PointerGetDatum(construct_array(types, best_len,
                                                        BYTEAOID, -1, false, TYPALIGN_INT));
            values[2] = PointerGetDatum(construct_array(dirs, best_len,
                                                        INT4OID, 4, true, TYPALIGN_INT));
            if (best_mu_valid)
                values[3] = fp_display_numeric(best_mu);
            else
                nulls[3] = true;
            values[4] = (Datum) 0;
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        }

        hash_destroy(xmap);
        laplace_spi_finish(spi_top);
    }
    return (Datum) 0;
}
