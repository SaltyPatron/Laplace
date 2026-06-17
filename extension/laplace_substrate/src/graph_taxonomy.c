





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

static Datum
spi_top_synset(Datum word)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { word };
    bool  isnull;
    int   rc;

    rc = SPI_execute_with_args(
        "SELECT sn.synset_id FROM laplace.senses($1) sn "
        "ORDER BY sn.eff_mu DESC NULLS LAST LIMIT 1",
        1, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    return copy_bytea_datum(
        SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));
}

static Datum
spi_gloss_for(Datum synset)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { synset };
    bool  isnull;
    int   rc;

    rc = SPI_execute_with_args(
        "SELECT laplace.render_text(g.object_id) "
        "FROM laplace.consensus g "
        "WHERE g.subject_id = $1 "
        "  AND g.type_id = laplace.relation_type_id('DEFINES') "
        "ORDER BY laplace.eff_mu(g.rating, g.rd) DESC LIMIT 1",
        1, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    return SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
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
           TaxNode *nodes, int cap)
{
    int    head = 0;
    int    tail = 0;
    int    n = 0;
    int   *queue = (int *) palloc(sizeof(int) * cap);
    bytea *nodebuf = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
    Oid    argtypes[2] = { BYTEAOID, BYTEAARRAYOID };
    Datum  args[2];
    int    rc;

    SET_VARSIZE(nodebuf, VARHDRSZ + sizeof(hash128_t));

    for (int s = 0; s < seed_n && n < cap; s++)
    {
        if (tax_find(&nodes[0].id, n, &seeds[s]) >= 0)
            continue;
        nodes[n].id = seeds[s];
        nodes[n].depth = 0;
        nodes[n].parent = -1;
        nodes[n].via_type = (hash128_t) { 0, 0 };
        nodes[n].rating = 0;
        nodes[n].rd = 0;
        queue[tail++] = n++;
    }

    while (head < tail)
    {
        int cur = queue[head++];
        TaxNode *u = &nodes[cur];

        if (u->depth >= max_depth)
            continue;

        memcpy(VARDATA(nodebuf), &u->id, sizeof(hash128_t));
        Datum *type_datums = (Datum *) palloc(sizeof(Datum) * up_type_n);
        for (int ti = 0; ti < up_type_n; ti++)
            type_datums[ti] = hash128_to_datum(&up_types[ti]);

        args[0] = PointerGetDatum(nodebuf);
        args[1] = PointerGetDatum(construct_array(
            type_datums, up_type_n, BYTEAOID, -1, false, TYPALIGN_INT));

        rc = SPI_execute_with_args(
            "SELECT c.object_id, c.type_id, c.rating, c.rd "
            "FROM laplace.consensus c "
            "WHERE c.subject_id = $1 "
            "  AND c.type_id = ANY ($2) "
            "  AND c.object_id IS NOT NULL "
            "  AND NOT laplace.refuted(c.rating, c.rd)",
            2, argtypes, args, NULL, true, 0);
        pfree(type_datums);
        pfree(DatumGetPointer(args[1]));

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

            pi = tax_find(&nodes[0].id, n, &obj_h);
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
                queue[tail++] = pi;
                continue;
            }

            if (n >= cap)
                ereport(ERROR, (errmsg("graph_geometry_reads: taxonomy walk node cap exceeded")));

            nodes[n].id = obj_h;
            nodes[n].depth = walk_depth;
            nodes[n].parent = cur;
            nodes[n].via_type = datum_to_hash128(
                SPI_getbinval(tup, td, 2, &isnull));
            nodes[n].rating = DatumGetInt64(
                SPI_getbinval(tup, td, 3, &isnull));
            nodes[n].rd = DatumGetInt64(
                SPI_getbinval(tup, td, 4, &isnull));
            queue[tail++] = n++;
        }
    }

    pfree(queue);
    pfree(nodebuf);
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

    nodes = (TaxNode *) palloc0(sizeof(TaxNode) * TAX_WALK_CAP);
    {
        hash128_t seed = datum_to_hash128(start);
        n_nodes = tax_bfs_up(&seed, 1, max_depth, up_types, 2, nodes, TAX_WALK_CAP);
    }

    for (int i = 0; i < n_nodes; i++)
    {
        Datum values[3];
        bool  nulls[3] = { false, false, false };
        Datum hypernym;
        Datum gloss;

        if (nodes[i].depth == 0)
            continue;

        hypernym = spi_realize(hash128_to_datum(&nodes[i].id), lang);
        gloss = spi_gloss_for(hash128_to_datum(&nodes[i].id));

        values[0] = Int32GetDatum(nodes[i].depth);
        if (hypernym == (Datum) 0)
            nulls[1] = true;
        else
            values[1] = hypernym;
        if (gloss == (Datum) 0)
            nulls[2] = true;
        else
            values[2] = gloss;

        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
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

    targets = (hash128_t *) palloc(sizeof(hash128_t) * (TAX_WALK_CAP + 64));
    targets[n_targets++] = datum_to_hash128(x);

    {
        Datum *synsets;
        int    ns;
        spi_fetch_synset_ids(y, &synsets, &ns);
        for (int i = 0; i < ns; i++)
            targets[n_targets++] = datum_to_hash128(synsets[i]);
        targets[n_targets++] = datum_to_hash128(y);

        {
            hash128_t trans_type = rel_type_id("IS_TRANSLATION_OF");
            Oid       argtypes[2] = { BYTEAOID, BYTEAOID };
            Datum     args[2] = { y, hash128_to_datum(&trans_type) };
            int       rc = SPI_execute_with_args(
                "SELECT c.subject_id FROM laplace.consensus c "
                "WHERE c.type_id = $2 AND c.object_id = $1 "
                "  AND NOT laplace.refuted(c.rating, c.rd)",
                2, argtypes, args, NULL, true, 0);
            if (rc != SPI_OK_SELECT)
                elog(ERROR, "isa_path: translation targets query failed");
            for (uint64 r = 0; r < SPI_processed; r++)
            {
                bool isnull;
                targets[n_targets++] = datum_to_hash128(
                    SPI_getbinval(SPI_tuptable->vals[r], SPI_tuptable->tupdesc, 1, &isnull));
            }
        }
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

        nodes = (TaxNode *) palloc0(sizeof(TaxNode) * TAX_WALK_CAP);
        n_nodes = tax_bfs_up(starts, n_starts, max_depth, up_types, 1,
                             nodes, TAX_WALK_CAP);
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
