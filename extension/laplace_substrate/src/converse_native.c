#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/numeric.h"

#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"

PG_FUNCTION_INFO_V1(pg_laplace_hypernyms);
PG_FUNCTION_INFO_V1(pg_laplace_isa_path);
PG_FUNCTION_INFO_V1(pg_laplace_contrast);
PG_FUNCTION_INFO_V1(pg_laplace_cascade);
PG_FUNCTION_INFO_V1(pg_laplace_nearest_neighbors_4d);

#define NEUTRAL_MU INT64CONST(1500000000000)
#define TAX_WALK_CAP 2048
#define CONTRAST_FEAT_CAP 512

static Datum
copy_bytea_datum(Datum d)
{
    bytea *src = DatumGetByteaPP(d);
    Size   len = VARSIZE_ANY(src);
    bytea *dst = (bytea *) palloc(len);
    memcpy(dst, src, len);
    return PointerGetDatum(dst);
}

static bool
bytea_eq(Datum a, Datum b)
{
    bytea *ba = DatumGetByteaPP(a);
    bytea *bb = DatumGetByteaPP(b);
    Size   la = VARSIZE_ANY_EXHDR(ba);
    Size   lb = VARSIZE_ANY_EXHDR(bb);
    if (la != lb)
        return false;
    return memcmp(VARDATA_ANY(ba), VARDATA_ANY(bb), la) == 0;
}

static hash128_t
datum_to_hash128(Datum d)
{
    bytea *b = DatumGetByteaPP(d);
    hash128_t h;
    if (VARSIZE_ANY_EXHDR(b) < (int) sizeof(hash128_t))
        ereport(ERROR, (errmsg("converse_native: expected 16-byte entity id")));
    memcpy(&h, VARDATA_ANY(b), sizeof(hash128_t));
    return h;
}

static Datum
hash128_to_datum(const hash128_t *h)
{
    bytea *b = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
    SET_VARSIZE(b, VARHDRSZ + sizeof(hash128_t));
    memcpy(VARDATA(b), h, sizeof(hash128_t));
    return PointerGetDatum(b);
}

static bool
hash128_eq(const hash128_t *a, const hash128_t *b)
{
    return a->hi == b->hi && a->lo == b->lo;
}

static Datum
eff_mu_display_numeric(int64 rating, int64 rd)
{
    int64 eff = rating - 2 * rd;
    Datum n = DirectFunctionCall1(int8_numeric, Int64GetDatum(eff));
    Datum b = DirectFunctionCall1(int8_numeric, Int64GetDatum(INT64CONST(1000000000)));
    Datum d = DirectFunctionCall2(numeric_div, n, b);
    return DirectFunctionCall2(numeric_round, d, Int32GetDatum(3));
}

static hash128_t
rel_type_id(const char *name)
{
    hash128_t id;
    if (laplace_relation_type_id(name, &id) < 0)
        ereport(ERROR, (errmsg("converse_native: unknown relation type %s", name)));
    return id;
}

static int
tax_find(const hash128_t *ids, int n, const hash128_t *key)
{
    for (int i = 0; i < n; i++)
        if (hash128_eq(&ids[i], key))
            return i;
    return -1;
}

typedef struct {
    hash128_t  id;
    int        depth;
    int        parent;
    hash128_t  via_type;
    int64_t    rating;
    int64_t    rd;
} TaxNode;

static int
spi_fetch_synsets(Datum word, Datum **out_ids, int *out_n)
{
    Oid     argtypes[1] = { BYTEAOID };
    Datum   args[1] = { word };
    int     rc;

    *out_ids = NULL;
    *out_n = 0;

    rc = SPI_execute_with_args(
        "SELECT sn.synset_id FROM laplace.senses($1) sn",
        1, argtypes, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "converse_native: senses query failed: %s",
             SPI_result_code_string(rc));

    if (SPI_processed == 0)
        return 0;

    *out_ids = (Datum *) palloc(sizeof(Datum) * SPI_processed);
    for (uint64 r = 0; r < SPI_processed; r++)
    {
        bool isnull;
        (*out_ids)[r] = copy_bytea_datum(
            SPI_getbinval(SPI_tuptable->vals[r], SPI_tuptable->tupdesc, 1, &isnull));
    }
    *out_n = (int) SPI_processed;
    return *out_n;
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
spi_realize(Datum id, Datum lang)
{
    Oid     argtypes[2] = { BYTEAOID, BYTEAOID };
    Datum   args[2] = { id, lang };
    char    nulls[3] = " n";
    bool    isnull;
    int     rc;

    if (lang == (Datum) 0)
        nulls[1] = 'n';

    rc = SPI_execute_with_args(
        "SELECT laplace.realize($1, $2)", 2, argtypes, args, nulls, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    return SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
}

static Datum
spi_type_label(Datum type_id)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { type_id };
    bool  isnull;
    int   rc;

    rc = SPI_execute_with_args(
        "SELECT laplace.type_label($1)", 1, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    return SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
}

static Datum
spi_word_language(Datum word)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { word };
    bool  isnull;
    int   rc;

    rc = SPI_execute_with_args(
        "SELECT laplace.word_language($1)", 1, argtypes, args, NULL, true, 1);
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

static int
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
            elog(ERROR, "converse_native: tax walk query failed: %s",
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
                ereport(ERROR, (errmsg("converse_native: taxonomy walk node cap exceeded")));

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

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "hypernyms: SPI_connect failed");

    start = spi_top_synset(word);
    if (start == (Datum) 0)
    {
        SPI_finish();
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

    SPI_finish();
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

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "isa_path: SPI_connect failed");

    up_types[0] = rel_type_id("IS_A");

    targets = (hash128_t *) palloc(sizeof(hash128_t) * (TAX_WALK_CAP + 64));
    targets[n_targets++] = datum_to_hash128(x);

    {
        Datum *synsets;
        int    ns;
        spi_fetch_synsets(y, &synsets, &ns);
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
        spi_fetch_synsets(x, &synsets, &ns);
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

    SPI_finish();
    return (Datum) 0;
}

typedef struct {
    hash128_t type_id;
    hash128_t object_id;
    Datum     mu;
    bool      from_x;
    bool      from_y;
} ContrastRow;

static int
contrast_row_find(ContrastRow *rows, int n, const hash128_t *tid, const hash128_t *oid)
{
    for (int i = 0; i < n; i++)
        if (hash128_eq(&rows[i].type_id, tid) && hash128_eq(&rows[i].object_id, oid))
            return i;
    return -1;
}

static void
contrast_add_fact(ContrastRow *rows, int *n, int cap,
                  const hash128_t *tid, const hash128_t *oid, Datum mu, bool from_x)
{
    int idx = contrast_row_find(rows, *n, tid, oid);
    if (idx < 0)
    {
        if (*n >= cap)
            ereport(ERROR, (errmsg("converse_native: contrast row cap exceeded")));
        rows[*n].type_id = *tid;
        rows[*n].object_id = *oid;
        rows[*n].mu = mu;
        rows[*n].from_x = from_x;
        rows[*n].from_y = !from_x;
        (*n)++;
        return;
    }
    if (from_x)
        rows[idx].from_x = true;
    else
        rows[idx].from_y = true;
    if (mu != (Datum) 0 && (rows[idx].mu == (Datum) 0 ||
        DatumGetInt32(DirectFunctionCall2(numeric_cmp, mu, rows[idx].mu)) > 0))
        rows[idx].mu = mu;
}

static bool
contrast_type_allowed(const hash128_t *type_id, const hash128_t *feat_types, int feat_n)
{
    int in_family = 0;
    for (int i = 0; i < feat_n; i++)
        if (hash128_eq(type_id, &feat_types[i]))
            return true;
    if (laplace_relation_in_family(type_id, "HAS_POS", &in_family) == 0 && in_family)
        return true;
    return false;
}

Datum
pg_laplace_contrast(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum          x, y, lang;
    int32          lim;
    hash128_t      up_types[2];
    hash128_t      feat_types[9];
    int            feat_n = 9;
    TaxNode       *ax, *ay;
    int            n_ax, n_ay;
    ContrastRow   *rows;
    int            n_rows = 0;
    int            emitted = 0;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("contrast: endpoints must not be NULL")));
    x = PG_GETARG_DATUM(0);
    y = PG_GETARG_DATUM(1);
    lang = PG_ARGISNULL(2) ? (Datum) 0 : PG_GETARG_DATUM(2);
    lim = PG_ARGISNULL(3) ? 80 : PG_GETARG_INT32(3);

    InitMaterializedSRF(fcinfo, 0);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "contrast: SPI_connect failed");

    up_types[0] = rel_type_id("IS_A");
    up_types[1] = rel_type_id("IS_INSTANCE_OF");
    feat_types[0] = rel_type_id("HAS_PART");
    feat_types[1] = rel_type_id("HAS_MEMBER");
    feat_types[2] = rel_type_id("HAS_SUBSTANCE");
    feat_types[3] = rel_type_id("HAS_ATTRIBUTE");
    feat_types[4] = rel_type_id("CAUSES");
    feat_types[5] = rel_type_id("USED_FOR");
    feat_types[6] = rel_type_id("IS_ANTONYM_OF");
    feat_types[7] = rel_type_id("IS_SIMILAR_TO");
    feat_types[8] = rel_type_id("PERTAINS_TO");

    ax = (TaxNode *) palloc0(sizeof(TaxNode) * TAX_WALK_CAP);
    ay = (TaxNode *) palloc0(sizeof(TaxNode) * TAX_WALK_CAP);

    {
        hash128_t seeds_x[32], seeds_y[32];
        int nx = 0, ny = 0;
        Datum *synsets_x = NULL;
        Datum *synsets_y = NULL;
        int    ns_x = 0, ns_y = 0;

        seeds_x[nx++] = datum_to_hash128(x);
        spi_fetch_synsets(x, &synsets_x, &ns_x);
        for (int i = 0; i < ns_x && nx < 32; i++)
            seeds_x[nx++] = datum_to_hash128(synsets_x[i]);

        seeds_y[ny++] = datum_to_hash128(y);
        spi_fetch_synsets(y, &synsets_y, &ns_y);
        for (int i = 0; i < ns_y && ny < 32; i++)
            seeds_y[ny++] = datum_to_hash128(synsets_y[i]);

        {
            Datum up_d[2] = { hash128_to_datum(&up_types[0]), hash128_to_datum(&up_types[1]) };
            n_ax = tax_bfs_up(seeds_x, nx, 7, up_types, 2, ax, TAX_WALK_CAP);
            n_ay = tax_bfs_up(seeds_y, ny, 7, up_types, 2, ay, TAX_WALK_CAP);
            pfree(DatumGetPointer(up_d[0]));
            pfree(DatumGetPointer(up_d[1]));
        }
    }

    rows = (ContrastRow *) palloc0(sizeof(ContrastRow) * CONTRAST_FEAT_CAP);
  {
    hash128_t isa_tid = up_types[0];
    for (int i = 0; i < n_ax; i++)
        if (ax[i].depth > 0)
            contrast_add_fact(rows, &n_rows, CONTRAST_FEAT_CAP,
                              &isa_tid, &ax[i].id, (Datum) 0, true);
    for (int i = 0; i < n_ay; i++)
        if (ay[i].depth > 0)
            contrast_add_fact(rows, &n_rows, CONTRAST_FEAT_CAP,
                              &isa_tid, &ay[i].id, (Datum) 0, false);
  }

    {
        bytea *subjbuf = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
        Oid    argtypes[1] = { BYTEAOID };
        Datum  args[1];
        SET_VARSIZE(subjbuf, VARHDRSZ + sizeof(hash128_t));

        for (int side = 0; side < 2; side++)
        {
            Datum  anchor = side == 0 ? x : y;
            Datum *synsets;
            int    ns;
            bool   from_x = side == 0;

            spi_fetch_synsets(anchor, &synsets, &ns);
            for (int si = -1; si < ns; si++)
            {
                Datum subj = si < 0 ? anchor : synsets[si];
                int   rc;

                args[0] = subj;
                rc = SPI_execute_with_args(
                    "SELECT c.type_id, c.object_id, c.rating, c.rd "
                    "FROM laplace.consensus c "
                    "WHERE c.subject_id = $1 "
                    "  AND c.object_id IS NOT NULL "
                    "  AND NOT laplace.refuted(c.rating, c.rd)",
                    1, argtypes, args, NULL, true, 0);
                if (rc != SPI_OK_SELECT)
                    elog(ERROR, "contrast: consensus query failed");

                for (uint64 r = 0; r < SPI_processed; r++)
                {
                    HeapTuple tup = SPI_tuptable->vals[r];
                    TupleDesc td  = SPI_tuptable->tupdesc;
                    bool      isnull;
                    hash128_t tid, oid;
                    int64     rating, rd;
                    Datum     mu;

                    tid = datum_to_hash128(SPI_getbinval(tup, td, 1, &isnull));
                    oid = datum_to_hash128(SPI_getbinval(tup, td, 2, &isnull));
                    rating = DatumGetInt64(SPI_getbinval(tup, td, 3, &isnull));
                    rd = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));
                    if (!contrast_type_allowed(&tid, feat_types, feat_n))
                        continue;
                    mu = eff_mu_display_numeric(rating, rd);
                    contrast_add_fact(rows, &n_rows, CONTRAST_FEAT_CAP, &tid, &oid, mu, from_x);
                }
            }
        }
        pfree(subjbuf);
    }

    for (int i = 0; i < n_rows && emitted < lim; i++)
    {
        Datum values[4];
        bool  nulls[4] = { false, false, false, false };
        Datum fact, type_lbl;
        const char *holder;

        if (rows[i].from_x && rows[i].from_y)
            holder = "both";
        else if (rows[i].from_x)
            holder = "x-only";
        else
            holder = "y-only";

        fact = spi_realize(hash128_to_datum(&rows[i].object_id), lang);
        type_lbl = spi_type_label(hash128_to_datum(&rows[i].type_id));

        values[0] = CStringGetTextDatum(holder);
        if (type_lbl == (Datum) 0)
            nulls[1] = true;
        else
            values[1] = type_lbl;
        if (fact == (Datum) 0)
            nulls[2] = true;
        else
            values[2] = fact;
        if (rows[i].mu == (Datum) 0)
            nulls[3] = true;
        else
            values[3] = rows[i].mu;

        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        emitted++;
    }

    SPI_finish();
    return (Datum) 0;
}

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
    max_depth = PG_ARGISNULL(2) ? 7 : PG_GETARG_INT32(2);

    InitMaterializedSRF(fcinfo, 0);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "cascade: SPI_connect failed");

    {
        Oid   argtypes[1] = { TEXTOID };
        Datum args[1];
        bool  isnull;
        int   rc;

        args[0] = PointerGetDatum(x_txt);
        rc = SPI_execute_with_args(
            "SELECT laplace.resolve_last_word($1)", 1, argtypes, args, NULL, true, 1);
        if (rc != SPI_OK_SELECT || SPI_processed == 0)
        {
            SPI_finish();
            return (Datum) 0;
        }
        x_id = copy_bytea_datum(
            SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));
        if (isnull)
        {
            SPI_finish();
            return (Datum) 0;
        }

        args[0] = PointerGetDatum(y_txt);
        rc = SPI_execute_with_args(
            "SELECT laplace.resolve_last_word($1)", 1, argtypes, args, NULL, true, 1);
        if (rc != SPI_OK_SELECT || SPI_processed == 0)
        {
            SPI_finish();
            return (Datum) 0;
        }
        y_id = copy_bytea_datum(
            SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));
        if (isnull)
        {
            SPI_finish();
            return (Datum) 0;
        }
    }

    goals = (Datum *) palloc(sizeof(Datum) * 64);
    goals[n_goals++] = y_id;

    {
        Datum *synsets;
        int    ns;
        spi_fetch_synsets(y_id, &synsets, &ns);
        for (int i = 0; i < ns && n_goals < 64; i++)
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
            "FROM laplace.astar_path($1, $2, $3) ap "
            "ORDER BY ap.step",
            3, argtypes, args, NULL, true, 0);
        pfree(DatumGetPointer(args[1]));

        if (rc != SPI_OK_SELECT || SPI_processed <= 1)
        {
            SPI_finish();
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
        int       *via_dirs;
        Datum      lang;
        Oid        argtypes[4];
        Datum      args[4];
        char       nulls[5] = "    ";
        int        rc;
        double     max_cost = costs[0];

        via_types = (Datum *) palloc0(sizeof(Datum) * n_steps);
        via_dirs = (int *) palloc0(sizeof(int) * n_steps);

        for (int s = 1; s < n_steps; s++)
        {
            Oid   eargs[2] = { BYTEAOID, BYTEAOID };
            Datum eargv[2] = { steps[s - 1], steps[s] };
            int   erc = SPI_execute_with_args(
                "SELECT c.type_id, c.subject_id "
                "FROM laplace.consensus c "
                "WHERE ((c.subject_id = $1 AND c.object_id = $2) "
                "    OR (c.subject_id = $2 AND c.object_id = $1)) "
                "  AND NOT laplace.refuted(c.rating, c.rd) "
                "ORDER BY laplace.eff_mu(c.rating, c.rd) DESC "
                "LIMIT 1",
                2, eargs, eargv, NULL, true, 1);
            if (erc == SPI_OK_SELECT && SPI_processed > 0)
            {
                HeapTuple tup = SPI_tuptable->vals[0];
                TupleDesc td  = SPI_tuptable->tupdesc;
                bool      isnull;
                Datum     subj;
                via_types[s] = copy_bytea_datum(
                    SPI_getbinval(tup, td, 1, &isnull));
                subj = SPI_getbinval(tup, td, 2, &isnull);
                via_dirs[s] = bytea_eq(subj, steps[s - 1]) ? 1 : -1;
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
        args[1] = PointerGetDatum(construct_array(via_types + 1, n_steps - 1,
                                                  BYTEAOID, -1, false, TYPALIGN_INT));
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
            "SELECT laplace.realize_path($1::bytea[], $2::bytea[], $3::int[], $4)",
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
        pfree(via_dirs);
    }

    SPI_finish();
    return (Datum) 0;
}

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

    if (SPI_connect() != SPI_OK_CONNECT)
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
            SPI_finish();
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
        Oid   argtypes[2] = { BYTEAOID, INT4OID };
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
            Oid       gargs[2] = { BYTEAOID, BYTEAOID };
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
                Oid   fargs[2] = { BYTEAOID, BYTEAOID };
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

    SPI_finish();
    return (Datum) 0;
}
