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

PG_FUNCTION_INFO_V1(pg_laplace_hypernyms);
PG_FUNCTION_INFO_V1(pg_laplace_isa_path);

int
tax_find(const hash128_t *ids, int n, const hash128_t *key)
{
    for (int i = 0; i < n; i++)
        if (hash128_eq(&ids[i], key))
            return i;
    return -1;
}

/*
 * One set read per BFS level: the whole frontier is passed as an array and
 * each frontier id's consensus edges of the requested types come back tagged
 * with its ordinal. ORDER BY u.ord keeps rows in frontier order, which fixes
 * parent assignment and output order. consensus_taxonomy_edges is the one
 * owner of which edges stand.
 */
SPIPlanPtr tax_edges_plan = NULL;

void
ensure_tax_edges_plan(void)
{
    if (tax_edges_plan == NULL)
    {
        Oid        argtypes[2] = { BYTEAARRAYOID, BYTEAARRAYOID };
        SPIPlanPtr plan = SPI_prepare_cursor(
            "SELECT u.ord, e.object_id, e.type_id, e.rating, e.rd "
            "FROM unnest($1) WITH ORDINALITY AS u(id, ord) "
            "CROSS JOIN LATERAL taxonomy.consensus_taxonomy_edges(u.id, $2) e "
            "ORDER BY u.ord",
            2, argtypes, CURSOR_OPT_PARALLEL_OK);
        if (plan == NULL)
            elog(ERROR, "tax_bfs_up: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "tax_bfs_up: SPI_keepplan failed");
        tax_edges_plan = plan;
    }
}

int
tax_idx_find(HTAB *map, const hash128_t *h)
{
    bool         found;
    TaxIdxEntry *e = (TaxIdxEntry *) hash_search(map, h, HASH_FIND, &found);

    return found ? e->idx : -1;
}

void
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
    {
        Datum d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
        if (isnull)
            return (Datum) 0;
        return copy_bytea_datum(d);
    }
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

    /* Level-synchronous BFS: one set read per depth level, rows in frontier
     * order. A node already reached at a shallower depth is kept; at equal
     * depth the wider bottleneck mu takes the parent; a deeper node is
     * re-parented to the shallower path and re-queued. */
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

void
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

void
tax_fetch_relation_set(const char *name, hash128_t **types_out, int *n_out)
{
    const hash128_t *slice = NULL;
    size_t           n = 0;
    hash128_t       *types;

    if (laplace_relation_set_ids(name, &slice, &n) != 0)
        elog(ERROR, "relate_path_raw: relation set %s is unavailable", name);
    types = (hash128_t *) palloc(sizeof(hash128_t) * (n > 0 ? n : 1));
    if (n > 0)
        memcpy(types, slice, sizeof(hash128_t) * n);
    *types_out = types;
    *n_out = (int) n;
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
    hash128_t     *up_types;
    int            n_up;

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
    tax_fetch_relation_set("PATH_UPWARD", &up_types, &n_up);

    {
        hash128_t seed = datum_to_hash128(start);
        n_nodes = tax_bfs_up(&seed, 1, max_depth, up_types, n_up, &nodes);
    }

    /* REALIZE the emitted ancestors as one set: labels through
     * spi_realize_batch, glosses through one ordered array read. */
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
    hash128_t     *up_types;
    int            n_up;
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

    tax_fetch_relation_set("ISA_UPWARD", &up_types, &n_up);

    {
        Datum *synsets;
        int    ns;
        Oid    argtypes[1] = { BYTEAOID };
        Datum  args[1] = { y };
        int    rc;

        spi_fetch_synset_ids(y, &synsets, &ns);
        rc = SPI_execute_with_args(
            "SELECT subject_id FROM taxonomy.translation_sources($1)",
            1, argtypes, args, NULL, true, 0);
        if (rc != SPI_OK_SELECT)
            elog(ERROR, "isa_path: translation targets query failed");
        if (SPI_processed + (uint64) ns + 2 > (uint64) INT_MAX ||
            SPI_processed + (uint64) ns + 2 >
                (uint64) (MaxAllocSize / sizeof(hash128_t)))
            ereport(ERROR,
                    (errmsg("isa_path: target frontier exceeds PostgreSQL allocation capacity")));

        targets = (hash128_t *) palloc(
            sizeof(hash128_t) * ((Size) ns + (Size) SPI_processed + 2));
        targets[n_targets++] = datum_to_hash128(x);
        for (int i = 0; i < ns; i++)
            targets[n_targets++] = datum_to_hash128(synsets[i]);
        targets[n_targets++] = datum_to_hash128(y);
        for (uint64 r = 0; r < SPI_processed; r++)
        {
            bool isnull;
            Datum subject = SPI_getbinval(
                SPI_tuptable->vals[r], SPI_tuptable->tupdesc, 1, &isnull);
            if (!isnull)
                targets[n_targets++] = datum_to_hash128(subject);
        }
    }

    {
        hash128_t *starts;
        int        n_starts = 0;
        Datum     *synsets;
        int        ns;

        spi_fetch_synset_ids(x, &synsets, &ns);
        starts = (hash128_t *) palloc(sizeof(hash128_t) * ((Size) ns + 1));
        starts[n_starts++] = datum_to_hash128(x);
        for (int i = 0; i < ns; i++)
            starts[n_starts++] = datum_to_hash128(synsets[i]);
        n_nodes = tax_bfs_up(starts, n_starts, max_depth, up_types, n_up, &nodes);
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
