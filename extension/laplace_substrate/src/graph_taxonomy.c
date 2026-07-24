





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

static int
tax_find(const hash128_t *ids, int n, const hash128_t *key)
{
    for (int i = 0; i < n; i++)
        if (hash128_eq(&ids[i], key))
            return i;
    return -1;
}

/*
 * consensus_taxonomy_edges is prepared once (static) and re-executed per
 * dequeued node instead of re-planning on every SPI_execute_with_args call.
 * The query, its two arguments, and the rows read back are unchanged.
 */
static SPIPlanPtr tax_edges_plan = NULL;

static void
ensure_tax_edges_plan(void)
{
    if (tax_edges_plan == NULL)
    {
        Oid        argtypes[2] = { BYTEAOID, BYTEAARRAYOID };
        SPIPlanPtr plan = SPI_prepare(
            "SELECT object_id, type_id, rating, rd "
            "FROM laplace.consensus_taxonomy_edges($1, $2)",
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
        "SELECT laplace.top_synset($1)",
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
tax_bfs_up(const hash128_t *seeds, int seed_n, int max_depth,
           const hash128_t *up_types, int up_type_n,
           TaxNode **nodes_out)
{
    int    head = 0;
    int    tail = 0;
    int    n = 0;
    int    node_cap = TAX_WALK_INITIAL;
    int    queue_cap = TAX_WALK_INITIAL;
    TaxNode *nodes = (TaxNode *) palloc(sizeof(TaxNode) * node_cap);
    int   *queue = (int *) palloc(sizeof(int) * queue_cap);
    bytea *nodebuf = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
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

    SET_VARSIZE(nodebuf, VARHDRSZ + sizeof(hash128_t));

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
        if (tax_idx_find(idmap, &seeds[s]) >= 0)
            continue;
        nodes[n].id = seeds[s];
        nodes[n].depth = 0;
        nodes[n].parent = -1;
        nodes[n].via_type = (hash128_t) { 0, 0 };
        nodes[n].rating = 0;
        nodes[n].rd = 0;
        tax_idx_add(idmap, &nodes[n].id, n);
        queue[tail++] = n++;
    }

    while (head < tail)
    {
        int cur = queue[head++];
        TaxNode *u = &nodes[cur];

        if (u->depth >= max_depth)
            continue;

        memcpy(VARDATA(nodebuf), &u->id, sizeof(hash128_t));

        args[0] = PointerGetDatum(nodebuf);
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
            Datum     obj_d;
            hash128_t obj_h;
            int       walk_depth;
            int       pi;

            obj_d = SPI_getbinval(tup, td, 1, &isnull);
            if (isnull)
                continue;
            obj_h = datum_to_hash128(obj_d);
            walk_depth = u->depth + 1;

            if (in_ancestor_chain(nodes, cur, &obj_h))
                continue;

            pi = tax_idx_find(idmap, &obj_h);
            if (pi >= 0)
            {
                if (nodes[pi].depth <= walk_depth)
                    continue;
                nodes[pi].depth = walk_depth;
                nodes[pi].parent = cur;
                nodes[pi].via_type = datum_to_hash128(
                    SPI_getbinval(tup, td, 2, &isnull));
                nodes[pi].rating = DatumGetInt64(
                    SPI_getbinval(tup, td, 3, &isnull));
                nodes[pi].rd = DatumGetInt64(
                    SPI_getbinval(tup, td, 4, &isnull));
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
                u = &nodes[cur];    /* the repalloc may have moved the array */
            }
            if (tail >= queue_cap)
            {
                queue_cap *= 2;
                queue = (int *) repalloc(queue, sizeof(int) * queue_cap);
            }

            nodes[n].id = obj_h;
            nodes[n].depth = walk_depth;
            nodes[n].parent = cur;
            nodes[n].via_type = datum_to_hash128(
                SPI_getbinval(tup, td, 2, &isnull));
            nodes[n].rating = DatumGetInt64(
                SPI_getbinval(tup, td, 3, &isnull));
            nodes[n].rd = DatumGetInt64(
                SPI_getbinval(tup, td, 4, &isnull));
            tax_idx_add(idmap, &nodes[n].id, n);
            queue[tail++] = n++;
        }
    }

    hash_destroy(idmap);
    pfree(DatumGetPointer(types_arr_datum));
    pfree(type_datums);
    pfree(queue);
    pfree(nodebuf);
    *nodes_out = nodes;
    return n;
}

static void
reconstruct_path(TaxNode *nodes, int idx, Datum **path, Datum **types, Datum *path_mu)
{
    int depth = nodes[idx].depth;
    int n = depth + 1;
    int i = idx;
    Datum mu = (Datum) 0;

    *path = (Datum *) palloc(sizeof(Datum) * n);
    *types = (Datum *) palloc(sizeof(Datum) * depth);

    for (int slot = depth; slot >= 0; slot--)
    {
        (*path)[slot] = hash128_to_datum(&nodes[i].id);
        if (slot > 0)
        {
            Datum edge_mu = eff_mu_display_numeric(nodes[i].rating, nodes[i].rd);
            (*types)[slot - 1] = hash128_to_datum(&nodes[i].via_type);
            if (mu == (Datum) 0)
                mu = edge_mu;
            else
                mu = DirectFunctionCall2(numeric_cmp, mu, edge_mu) <= 0 ? mu : edge_mu;
            i = nodes[i].parent;
        }
    }
    *path_mu = mu;
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
            Oid   rtypes[2] = { BYTEAARRAYOID, BYTEAOID };
            Datum rargs[2] = { PointerGetDatum(ids_arr), lang };
            char  rnulls[3] = "  ";
            int   rc2;
            bool  isnull;

            if (lang == (Datum) 0)
                rnulls[1] = 'n';
            rc2 = SPI_execute_with_args(
                "SELECT laplace.realize_batch($1, $2)",
                2, rtypes, rargs, rnulls, true, 1);
            if (rc2 == SPI_OK_SELECT && SPI_processed > 0)
            {
                Datum arr = SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 1, &isnull);
                if (!isnull)
                    deconstruct_array(DatumGetArrayTypePCopy(arr), TEXTOID,
                                      -1, false, TYPALIGN_INT,
                                      &labels, &label_nulls, &n_labels);
            }

            rc2 = SPI_execute_with_args(
                "SELECT array_agg(laplace.synset_gloss(u.id) ORDER BY u.ord) "
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
                    "SELECT subject_id FROM laplace.translation_sources($1)",
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
