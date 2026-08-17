





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
#include "graph_taxonomy.h"

PG_FUNCTION_INFO_V1(pg_laplace_hypernyms);
PG_FUNCTION_INFO_V1(pg_laplace_isa_path);
PG_FUNCTION_INFO_V1(pg_laplace_relate_path_raw);

static int
tax_find(const hash128_t *ids, int n, const hash128_t *key)
{
    for (int i = 0; i < n; i++)
        if (hash128_eq(&ids[i], key))
            return i;
    return -1;
}

/*
 * The frontier is expanded ONE SPI round trip per BFS level, not one per
 * dequeued node: the walk was 19.7k sequential SPI calls for a hub word
 * (emperor's depth-7 closure = 8,225 nodes) and dominated the ~14s matchup
 * tape. unnest WITH ORDINALITY + ORDER BY u.ord makes the batched rows arrive
 * in exactly the per-node order the old loop produced, so parent assignment,
 * dedup, and output order are unchanged. consensus_taxonomy_edges stays the
 * single owner of the edge truth table.
 */
static SPIPlanPtr tax_edges_plan = NULL;

static void
ensure_tax_edges_plan(void)
{
    if (tax_edges_plan == NULL)
    {
        Oid        argtypes[2] = { BYTEAARRAYOID, BYTEAARRAYOID };
        SPIPlanPtr plan = SPI_prepare(
            "SELECT u.ord, e.object_id, e.type_id, e.rating, e.rd "
            "FROM unnest($1) WITH ORDINALITY AS u(id, ord) "
            "CROSS JOIN LATERAL taxonomy.consensus_taxonomy_edges(u.id, $2) e "
            "ORDER BY u.ord",
            2, argtypes);
        if (plan == NULL)
            elog(ERROR, "tax_bfs_up: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "tax_bfs_up: SPI_keepplan failed");
        tax_edges_plan = plan;
    }
}

/* id -> node-index map, replacing the O(n) tax_find scan over the BFS node
 * array. Node ids are unique (dedup on insert), so the map is 1:1 and returns
 * exactly the index the linear scan would have — BFS order and output are
 * unchanged. */
typedef struct TaxIdxEntry
{
    char key[16];
    int  idx;
} TaxIdxEntry;

static int
tax_idx_find(HTAB *map, const hash128_t *h)
{
    bool         found;
    TaxIdxEntry *e = (TaxIdxEntry *) hash_search(map, h, HASH_FIND, &found);

    return found ? e->idx : -1;
}

static void
tax_idx_add(HTAB *map, const hash128_t *h, int idx)
{
    bool         found;
    TaxIdxEntry *e = (TaxIdxEntry *) hash_search(map, h, HASH_ENTER, &found);

    e->idx = idx;
}

static Datum
spi_top_synset(Datum word)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { word };
    bool  isnull;
    int   rc;

    rc = SPI_execute_with_args(
        "SELECT taxonomy.top_synset($1)",
        1, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    return copy_bytea_datum(
        SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));
}

static bool
in_ancestor_chain(TaxNode *nodes, int cur, const hash128_t *target)
{
    while (cur >= 0)
    {
        if (hash128_eq(&nodes[cur].id, target))
            return true;
        cur = nodes[cur].parent;
    }
    return false;
}

int
tax_bfs_up_weighted(const hash128_t *seeds,
                    const int64_t *seed_mu,
                    const bool *seed_mu_valid,
                    int seed_n, int max_depth,
                    const hash128_t *up_types, int up_type_n,
                    TaxNode **nodes_out)
{
    int    tail = 0;
    int    n = 0;
    int    node_cap = TAX_WALK_INITIAL;
    int    queue_cap = TAX_WALK_INITIAL;
    TaxNode *nodes = (TaxNode *) palloc(sizeof(TaxNode) * node_cap);
    int   *queue = (int *) palloc(sizeof(int) * queue_cap);
    Datum  args[2];
    int    rc;
    Datum *type_datums;
    Datum  types_arr_datum;
    HTAB  *idmap;
    HASHCTL ctl;

    ensure_tax_edges_plan();

    /* The up-type array is constant for the whole walk: build it once. */
    type_datums = (Datum *) palloc(sizeof(Datum) * up_type_n);
    for (int ti = 0; ti < up_type_n; ti++)
        type_datums[ti] = hash128_to_datum(&up_types[ti]);
    types_arr_datum = PointerGetDatum(construct_array(
        type_datums, up_type_n, BYTEAOID, -1, false, TYPALIGN_INT));

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(TaxIdxEntry);
    idmap = hash_create("tax_bfs_up idmap", TAX_WALK_INITIAL, &ctl,
                        HASH_ELEM | HASH_BLOBS);

    for (int s = 0; s < seed_n; s++)
    {
        if (n >= node_cap)
        {
            node_cap *= 2;
            nodes = (TaxNode *) repalloc(nodes, sizeof(TaxNode) * node_cap);
        }
        if (tail >= queue_cap)
        {
            queue_cap *= 2;
            queue = (int *) repalloc(queue, sizeof(int) * queue_cap);
        }
        {
            int existing = tax_idx_find(idmap, &seeds[s]);
            bool valid = seed_mu_valid != NULL && seed_mu_valid[s];

            if (existing >= 0)
            {
                if (valid && (!nodes[existing].path_mu_valid ||
                              seed_mu[s] > nodes[existing].path_mu))
                {
                    nodes[existing].path_mu = seed_mu[s];
                    nodes[existing].path_mu_valid = true;
                }
                continue;
            }
        }
        nodes[n].id = seeds[s];
        nodes[n].depth = 0;
        nodes[n].parent = -1;
        nodes[n].via_type = (hash128_t) { 0, 0 };
        nodes[n].rating = 0;
        nodes[n].rd = 0;
        nodes[n].path_mu = seed_mu != NULL ? seed_mu[s] : 0;
        nodes[n].path_mu_valid = seed_mu_valid != NULL && seed_mu_valid[s];
        tax_idx_add(idmap, &nodes[n].id, n);
        queue[tail++] = n++;
    }

    /* Level-synchronous BFS: FIFO order already visits nodes in
     * non-decreasing depth, so expanding a whole level in one batched query —
     * rows ordered by frontier position — replays exactly the per-node
     * sequence the old loop produced. One SPI round trip per depth level
     * (≤ max_depth total) instead of one per node. */
    {
        int level_begin = 0;
        int level_end = tail;

        for (int depth = 0; depth < max_depth && level_begin < level_end; depth++)
        {
            int        frontier_n = level_end - level_begin;
            int        walk_depth = depth + 1;
            Datum     *frontier_ids = (Datum *) palloc(sizeof(Datum) * frontier_n);
            ArrayType *frontier_arr;

            for (int i = 0; i < frontier_n; i++)
                frontier_ids[i] = hash128_to_datum(&nodes[queue[level_begin + i]].id);
            frontier_arr = construct_array(frontier_ids, frontier_n,
                                           BYTEAOID, -1, false, TYPALIGN_INT);

            args[0] = PointerGetDatum(frontier_arr);
            args[1] = types_arr_datum;

            rc = SPI_execute_plan(tax_edges_plan, args, NULL, true, 0);

            if (rc != SPI_OK_SELECT)
                elog(ERROR, "graph_geometry_reads: tax walk query failed: %s",
                     SPI_result_code_string(rc));

            for (uint64 r = 0; r < SPI_processed; r++)
            {
                HeapTuple tup = SPI_tuptable->vals[r];
                TupleDesc td  = SPI_tuptable->tupdesc;
                bool      isnull;
                int64     ord;
                int       cur;
                Datum     obj_d;
                hash128_t obj_h;
                int       pi;
                hash128_t via_type;
                int64     rating;
                int64     rd;
                int64     edge_mu;
                int64     candidate_mu;
                bool      candidate_valid = true;

                ord = DatumGetInt64(SPI_getbinval(tup, td, 1, &isnull));
                if (isnull || ord < 1 || ord > frontier_n)
                    continue;
                cur = queue[level_begin + (int) ord - 1];

                obj_d = SPI_getbinval(tup, td, 2, &isnull);
                if (isnull)
                    continue;
                obj_h = datum_to_hash128(obj_d);

                if (in_ancestor_chain(nodes, cur, &obj_h))
                    continue;

                via_type = datum_to_hash128(
                    SPI_getbinval(tup, td, 3, &isnull));
                rating = DatumGetInt64(
                    SPI_getbinval(tup, td, 4, &isnull));
                rd = DatumGetInt64(
                    SPI_getbinval(tup, td, 5, &isnull));
                edge_mu = eff_mu_display_fp(rating, rd);
                candidate_mu = nodes[cur].path_mu_valid
                    ? Min(nodes[cur].path_mu, edge_mu)
                    : edge_mu;

                pi = tax_idx_find(idmap, &obj_h);
                if (pi >= 0)
                {
                    if (nodes[pi].depth < walk_depth)
                        continue;
                    if (nodes[pi].depth == walk_depth)
                    {
                        if (!candidate_valid ||
                            (nodes[pi].path_mu_valid &&
                             candidate_mu <= nodes[pi].path_mu))
                            continue;
                        nodes[pi].parent = cur;
                        nodes[pi].via_type = via_type;
                        nodes[pi].rating = rating;
                        nodes[pi].rd = rd;
                        nodes[pi].path_mu = candidate_mu;
                        nodes[pi].path_mu_valid = true;
                        continue;
                    }
                    nodes[pi].depth = walk_depth;
                    nodes[pi].parent = cur;
                    nodes[pi].via_type = via_type;
                    nodes[pi].rating = rating;
                    nodes[pi].rd = rd;
                    nodes[pi].path_mu = candidate_mu;
                    nodes[pi].path_mu_valid = candidate_valid;
                    if (tail >= queue_cap)
                    {
                        queue_cap *= 2;
                        queue = (int *) repalloc(queue, sizeof(int) * queue_cap);
                    }
                    queue[tail++] = pi;
                    continue;
                }

                if (n >= node_cap)
                {
                    node_cap *= 2;
                    nodes = (TaxNode *) repalloc(nodes, sizeof(TaxNode) * node_cap);
                }
                if (tail >= queue_cap)
                {
                    queue_cap *= 2;
                    queue = (int *) repalloc(queue, sizeof(int) * queue_cap);
                }

                nodes[n].id = obj_h;
                nodes[n].depth = walk_depth;
                nodes[n].parent = cur;
                nodes[n].via_type = via_type;
                nodes[n].rating = rating;
                nodes[n].rd = rd;
                nodes[n].path_mu = candidate_mu;
                nodes[n].path_mu_valid = candidate_valid;
                tax_idx_add(idmap, &nodes[n].id, n);
                queue[tail++] = n++;
            }

            pfree(frontier_arr);
            pfree(frontier_ids);
            level_begin = level_end;
            level_end = tail;
        }
    }

    hash_destroy(idmap);
    pfree(DatumGetPointer(types_arr_datum));
    pfree(type_datums);
    pfree(queue);
    *nodes_out = nodes;
    return n;
}

int
tax_bfs_up(const hash128_t *seeds, int seed_n, int max_depth,
           const hash128_t *up_types, int up_type_n,
           TaxNode **nodes_out)
{
    return tax_bfs_up_weighted(seeds, NULL, NULL, seed_n, max_depth,
                               up_types, up_type_n, nodes_out);
}

static void
reconstruct_path(TaxNode *nodes, int idx, Datum **path, Datum **types, Datum *path_mu)
{
    int depth = nodes[idx].depth;
    int n = depth + 1;
    int i = idx;
    Datum mu = nodes[idx].path_mu_valid
        ? fp_display_numeric(nodes[idx].path_mu)
        : (Datum) 0;

    *path = (Datum *) palloc(sizeof(Datum) * n);
    *types = (Datum *) palloc(sizeof(Datum) * depth);

    for (int slot = depth; slot >= 0; slot--)
    {
        (*path)[slot] = hash128_to_datum(&nodes[i].id);
        if (slot > 0)
        {
            (*types)[slot - 1] = hash128_to_datum(&nodes[i].via_type);
            i = nodes[i].parent;
        }
    }
    *path_mu = mu;
}

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
static SPIPlanPtr relation_set_plan = NULL;

static void
ensure_relate_plans(void)
{
    if (relate_senses_plan == NULL)
    {
        Oid argtypes[1] = { BYTEAOID };

        relate_senses_plan = SPI_prepare(
            "SELECT synset_id, (eff_mu * 1000000000)::bigint "
            "FROM lexical.senses($1) WHERE synset_id IS NOT NULL",
            1, argtypes);
        if (relate_senses_plan == NULL || SPI_keepplan(relate_senses_plan) != 0)
            elog(ERROR, "relate_path_raw: could not prepare lexical sense plan");
    }
    if (relation_set_plan == NULL)
    {
        Oid argtypes[1] = { TEXTOID };

        relation_set_plan = SPI_prepare(
            "SELECT consensus.relation_set_ids($1)", 1, argtypes);
        if (relation_set_plan == NULL || SPI_keepplan(relation_set_plan) != 0)
            elog(ERROR, "relate_path_raw: could not prepare relation-set plan");
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

static void
fetch_relation_set(const char *name, hash128_t **types_out, int *n_out)
{
    Datum      args[1] = { CStringGetTextDatum(name) };
    int        rc = SPI_execute_plan(relation_set_plan, args, NULL, true, 1);
    bool       isnull;
    Datum      arr_d;
    Datum     *values;
    bool      *nulls;
    int        n;
    hash128_t *types;

    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        elog(ERROR, "relate_path_raw: relation set %s is unavailable", name);
    arr_d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
    if (isnull)
        elog(ERROR, "relate_path_raw: relation set %s is NULL", name);
    deconstruct_array(DatumGetArrayTypePCopy(arr_d), BYTEAOID,
                      -1, false, TYPALIGN_INT, &values, &nulls, &n);
    types = (hash128_t *) palloc(sizeof(hash128_t) * (n > 0 ? n : 1));
    for (int i = 0; i < n; i++)
    {
        if (nulls[i])
            elog(ERROR, "relate_path_raw: relation set %s contains NULL", name);
        types[i] = datum_to_hash128(values[i]);
    }
    *types_out = types;
    *n_out = n;
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
        fetch_relation_set("PATH_UPWARD", &up_types, &n_up);
        fetch_relation_set("PATH_LATERAL", &lateral_types, &n_lateral);

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

Datum
pg_laplace_hypernyms(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum          word;
    int32          max_depth;
    Datum          start;
    TaxNode       *nodes;
    int            n_nodes;
    Datum          lang;
    hash128_t      up_types[2];

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("hypernyms: p_word must not be NULL")));
    word = PG_GETARG_DATUM(0);
    max_depth = PG_ARGISNULL(1) ? 8 : PG_GETARG_INT32(1);
    if (max_depth < 0)
        ereport(ERROR, (errmsg("hypernyms: p_depth must be >= 0")));

    InitMaterializedSRF(fcinfo, 0);

    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "hypernyms: SPI_connect failed");

    start = spi_top_synset(word);
    if (start == (Datum) 0)
    {
        laplace_spi_finish(spi_top);
        return (Datum) 0;
    }

    lang = spi_word_language(word);
    up_types[0] = rel_type_id("IS_A");
    up_types[1] = rel_type_id("IS_INSTANCE_OF");

    {
        hash128_t seed = datum_to_hash128(start);
        n_nodes = tax_bfs_up(&seed, 1, max_depth, up_types, 2, &nodes);
    }

    /* Batch the label + gloss resolution: the per-node spi_realize +
     * spi_gloss_for pair was 2 unprepared parse/plan/execute round trips per
     * emitted node. realize_batch resolves every id in 6 fixed round trips;
     * the gloss set-query is one more. */
    {
        int    n_emit = 0;
        int   *emit_idx = (int *) palloc(sizeof(int) * (n_nodes > 0 ? n_nodes : 1));
        Datum *emit_ids = (Datum *) palloc(sizeof(Datum) * (n_nodes > 0 ? n_nodes : 1));
        Datum *labels = NULL;
        bool  *label_nulls = NULL;
        Datum *glosses = NULL;
        bool  *gloss_nulls = NULL;
        int    n_labels = 0, n_glosses = 0;

        for (int i = 0; i < n_nodes; i++)
        {
            if (nodes[i].depth == 0)
                continue;
            emit_idx[n_emit] = i;
            emit_ids[n_emit] = hash128_to_datum(&nodes[i].id);
            n_emit++;
        }

        if (n_emit > 0)
        {
            ArrayType *ids_arr = construct_array(emit_ids, n_emit, BYTEAOID,
                                                 -1, false, TYPALIGN_INT);
            Oid   rtypes[1] = { BYTEAARRAYOID };
            Datum rargs[1] = { PointerGetDatum(ids_arr) };
            int   rc2;
            bool  isnull;
            Datum label_arr;

            label_arr = spi_realize_batch(PointerGetDatum(ids_arr), lang);
            if (label_arr != (Datum) 0)
                deconstruct_array(DatumGetArrayTypePCopy(label_arr), TEXTOID,
                                  -1, false, TYPALIGN_INT,
                                  &labels, &label_nulls, &n_labels);

            rc2 = SPI_execute_with_args(
                "SELECT array_agg(taxonomy.synset_gloss(u.id) ORDER BY u.ord) "
                "FROM unnest($1) WITH ORDINALITY AS u(id, ord)",
                1, rtypes, rargs, NULL, true, 1);
            if (rc2 == SPI_OK_SELECT && SPI_processed > 0)
            {
                Datum arr = SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 1, &isnull);
                if (!isnull)
                    deconstruct_array(DatumGetArrayTypePCopy(arr), TEXTOID,
                                      -1, false, TYPALIGN_INT,
                                      &glosses, &gloss_nulls, &n_glosses);
            }
        }

        for (int e = 0; e < n_emit; e++)
        {
            Datum values[3];
            bool  nulls[3] = { false, false, false };
            int   i = emit_idx[e];

            values[0] = Int32GetDatum(nodes[i].depth);
            if (labels != NULL && e < n_labels && !label_nulls[e])
                values[1] = labels[e];
            else
                nulls[1] = true;
            if (glosses != NULL && e < n_glosses && !gloss_nulls[e])
                values[2] = glosses[e];
            else
                nulls[2] = true;

            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        }
    }

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}

Datum
pg_laplace_isa_path(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum          x, y;
    int32          max_depth;
    TaxNode       *nodes = NULL;
    int            n_nodes = 0;
    hash128_t      up_types[1];
    hash128_t     *targets;
    int            n_targets = 0;
    int            best = -1;
    int            best_len = INT_MAX;
    Datum          best_mu = (Datum) 0;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("isa_path: endpoints must not be NULL")));
    x = PG_GETARG_DATUM(0);
    y = PG_GETARG_DATUM(1);
    max_depth = PG_ARGISNULL(2) ? 8 : PG_GETARG_INT32(2);

    InitMaterializedSRF(fcinfo, 0);

    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "isa_path: SPI_connect failed");

    up_types[0] = rel_type_id("IS_A");

    /* Grown as appended: translation_sources is unbounded, and the old fixed
     * TAX_WALK_CAP+64 sizing was a latent overflow waiting on a well-attested
     * word. */
    {
        int targets_cap = 64;
        targets = (hash128_t *) palloc(sizeof(hash128_t) * targets_cap);

#define ISA_PATH_TARGET_PUSH(h) \
        do { \
            if (n_targets >= targets_cap) \
            { \
                targets_cap *= 2; \
                targets = (hash128_t *) repalloc(targets, sizeof(hash128_t) * targets_cap); \
            } \
            targets[n_targets++] = (h); \
        } while (0)

        ISA_PATH_TARGET_PUSH(datum_to_hash128(x));

        {
            Datum *synsets;
            int    ns;
            spi_fetch_synset_ids(y, &synsets, &ns);
            for (int i = 0; i < ns; i++)
                ISA_PATH_TARGET_PUSH(datum_to_hash128(synsets[i]));
            ISA_PATH_TARGET_PUSH(datum_to_hash128(y));

            {
                Oid       argtypes[1] = { BYTEAOID };
                Datum     args[1] = { y };
                int       rc = SPI_execute_with_args(
                    "SELECT subject_id FROM taxonomy.translation_sources($1)",
                    1, argtypes, args, NULL, true, 0);
                if (rc != SPI_OK_SELECT)
                    elog(ERROR, "isa_path: translation targets query failed");
                for (uint64 r = 0; r < SPI_processed; r++)
                {
                    bool isnull;
                    ISA_PATH_TARGET_PUSH(datum_to_hash128(
                        SPI_getbinval(SPI_tuptable->vals[r], SPI_tuptable->tupdesc, 1, &isnull)));
                }
            }
        }
#undef ISA_PATH_TARGET_PUSH
    }

    {
        hash128_t *starts;
        int        n_starts = 0;
        Datum     *synsets;
        int        ns;

        starts = (hash128_t *) palloc(sizeof(hash128_t) * 64);
        starts[n_starts++] = datum_to_hash128(x);
        spi_fetch_synset_ids(x, &synsets, &ns);
        for (int i = 0; i < ns; i++)
            starts[n_starts++] = datum_to_hash128(synsets[i]);

        n_nodes = tax_bfs_up(starts, n_starts, max_depth, up_types, 1, &nodes);
        pfree(starts);
    }

    for (int i = 0; i < n_nodes; i++)
    {
        int path_len;
        Datum path_mu;

        if (nodes[i].depth <= 0)
            continue;
        if (tax_find(targets, n_targets, &nodes[i].id) < 0)
            continue;

        path_len = nodes[i].depth + 1;
        {
            Datum *path, *types;
            reconstruct_path(nodes, i, &path, &types, &path_mu);
            if (path_len < best_len ||
                (path_len == best_len && best_mu != (Datum) 0 && path_mu != (Datum) 0 &&
                 DatumGetInt32(DirectFunctionCall2(numeric_cmp, path_mu, best_mu)) > 0))
            {
                best = i;
                best_len = path_len;
                best_mu = path_mu;
            }
            pfree(path);
            pfree(types);
        }
    }

    if (best >= 0)
    {
        Datum  values[3];
        bool   nulls[3] = { false, false, false };
        Datum *path, *types;

        reconstruct_path(nodes, best, &path, &types, &best_mu);
        values[0] = PointerGetDatum(construct_array(path, best_len, BYTEAOID,
                                                  -1, false, TYPALIGN_INT));
        values[1] = PointerGetDatum(construct_array(types, best_len - 1, BYTEAOID,
                                                    -1, false, TYPALIGN_INT));
        values[2] = best_mu;
        if (best_mu == (Datum) 0)
            nulls[2] = true;
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        pfree(path);
        pfree(types);
    }

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
