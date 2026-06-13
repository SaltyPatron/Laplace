#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/lsyscache.h"
#include "utils/numeric.h"
#include "utils/hsearch.h"
#include "lib/stringinfo.h"
#include "mb/pg_wchar.h"

#include "spi_common.h"
#include "spi_nested.h"
#include "perfcache_native.h"

PG_FUNCTION_INFO_V1(pg_laplace_generate_tree);
PG_FUNCTION_INFO_V1(pg_laplace_generate_greedy);

#define GENERATE_NODE_BUDGET 1000000

static const char *EDGE_QUERY =
    "SELECT c.object_id, c.type_id, c.rating, c.rd, c.witness_count "
    "FROM laplace.consensus c "
    "WHERE c.subject_id = $1 AND c.object_id IS NOT NULL "
    "  AND ($2::bytea IS NULL OR c.type_id = $2) "
    "  AND NOT laplace.refuted(c.rating, c.rd) "
    "  AND NOT (c.object_id = ANY ($4::bytea[])) "
    "ORDER BY laplace.eff_mu(c.rating, c.rd) DESC "
    "LIMIT $3";

static SPIPlanPtr edge_plan = NULL;

static void
ensure_edge_plan(void)
{
    if (edge_plan == NULL)
    {
        Oid argtypes[4] = { BYTEAOID, BYTEAOID, INT4OID, BYTEAARRAYOID };
        SPIPlanPtr plan = SPI_prepare(EDGE_QUERY, 4, argtypes);
        if (plan == NULL)
            elog(ERROR, "generate walk: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "generate walk: SPI_keepplan failed");
        edge_plan = plan;
    }
}

/* eff_mu_display_numeric / copy_bytea_datum live in spi_common.h */

typedef struct WalkNode
{
    int     parent;
    int     depth;
    Datum   entity;
    Datum   rel_type;
    Datum   eff_mu;
    Datum   path_mu;
    int64   witnesses;
} WalkNode;

static ArrayType *
branch_array(WalkNode *nodes, int idx, bool types)
{
    int     depth = nodes[idx].depth;
    int     n = types ? depth : depth + 1;
    Datum  *elems = (Datum *) palloc(sizeof(Datum) * (n > 0 ? n : 1));
    int     i = idx;

    for (int slot = n - 1; slot >= 0; slot--)
    {
        elems[slot] = types ? nodes[i].rel_type : nodes[i].entity;
        i = nodes[i].parent;
    }
    return construct_array(elems, n, BYTEAOID, -1, false, TYPALIGN_INT);
}

Datum
pg_laplace_generate_tree(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    bytea  *prompt;
    Datum   type_datum = 0;
    bool    type_null;
    int32   max_depth, beam;
    WalkNode *nodes;
    int     n_nodes = 0, cap;
    int    *order;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("generate_tree: prompt entity must not be NULL")));
    prompt    = PG_GETARG_BYTEA_PP(0);
    type_null = PG_ARGISNULL(1);
    if (!type_null)
        type_datum = PG_GETARG_DATUM(1);
    max_depth = PG_ARGISNULL(2) ? 4 : PG_GETARG_INT32(2);
    beam      = PG_ARGISNULL(3) ? 5 : PG_GETARG_INT32(3);
    if (max_depth < 0 || beam < 0)
        ereport(ERROR, (errmsg("generate_tree: depth and beam must be >= 0")));

    InitMaterializedSRF(fcinfo, 0);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "generate_tree: SPI_connect failed");
    ensure_edge_plan();

    cap = 256;
    nodes = (WalkNode *) palloc(sizeof(WalkNode) * cap);

    nodes[0].parent    = -1;
    nodes[0].depth     = 0;
    nodes[0].entity    = copy_bytea_datum(PointerGetDatum(prompt));
    nodes[0].rel_type      = (Datum) 0;
    nodes[0].eff_mu    = (Datum) 0;
    nodes[0].path_mu   = DirectFunctionCall1(int8_numeric, Int64GetDatum(0));
    nodes[0].witnesses = 0;
    n_nodes = 1;

    for (int frontier_start = 0, level = 0; level < max_depth; level++)
    {
        int frontier_end = n_nodes;
        if (frontier_start == frontier_end)
            break;

        for (int f = frontier_start; f < frontier_end; f++)
        {
            ArrayType *path_arr = branch_array(nodes, f, false);
            Datum  args[4];
            char   nulls[5] = "    ";
            int    rc;

            args[0] = nodes[f].entity;
            args[1] = type_null ? (Datum) 0 : type_datum;
            if (type_null) nulls[1] = 'n';
            args[2] = Int32GetDatum(beam);
            args[3] = PointerGetDatum(path_arr);

            rc = SPI_execute_plan(edge_plan, args, nulls, true, beam);
            if (rc != SPI_OK_SELECT)
                elog(ERROR, "generate_tree: edge query failed: %s",
                     SPI_result_code_string(rc));

            for (uint64 r = 0; r < SPI_processed; r++)
            {
                HeapTuple  tup = SPI_tuptable->vals[r];
                TupleDesc  td  = SPI_tuptable->tupdesc;
                bool       isnull;
                Datum      obj   = SPI_getbinval(tup, td, 1, &isnull);
                Datum      etype = SPI_getbinval(tup, td, 2, &isnull);
                int64      rating = DatumGetInt64(SPI_getbinval(tup, td, 3, &isnull));
                int64      rd     = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));
                int64      wc     = DatumGetInt64(SPI_getbinval(tup, td, 5, &isnull));
                Datum      mu;

                if (n_nodes >= GENERATE_NODE_BUDGET)
                    ereport(ERROR, (errmsg(
                        "generate_tree: node budget %d exceeded (beam %d × depth %d) — narrow the walk",
                        GENERATE_NODE_BUDGET, beam, max_depth)));
                if (n_nodes == cap)
                {
                    cap *= 2;
                    nodes = (WalkNode *) repalloc(nodes, sizeof(WalkNode) * cap);
                }

                mu = eff_mu_display_numeric(rating, rd);
                nodes[n_nodes].parent    = f;
                nodes[n_nodes].depth     = level + 1;
                nodes[n_nodes].entity    = copy_bytea_datum(obj);
                nodes[n_nodes].rel_type      = copy_bytea_datum(etype);
                nodes[n_nodes].eff_mu    = mu;
                nodes[n_nodes].path_mu   = DirectFunctionCall2(numeric_add,
                                                               nodes[f].path_mu, mu);
                nodes[n_nodes].witnesses = wc;
                n_nodes++;
            }
            pfree(path_arr);
        }
        frontier_start = frontier_end;
    }

    order = (int *) palloc(sizeof(int) * n_nodes);
    for (int i = 0; i < n_nodes; i++) order[i] = i;
    for (int i = 2; i < n_nodes; i++)
    {
        int j = i, v = order[i];
        while (j > 1)
        {
            int u = order[j - 1];
            if (nodes[u].depth < nodes[v].depth) break;
            if (nodes[u].depth == nodes[v].depth)
            {
                int32 cmp = DatumGetInt32(DirectFunctionCall2(
                    numeric_cmp, nodes[u].path_mu, nodes[v].path_mu));
                if (cmp >= 0) break;
            }
            order[j] = u;
            j--;
        }
        order[j] = v;
    }

    for (int oi = 1; oi < n_nodes; oi++)
    {
        int    i = order[oi];
        Datum  values[7];
        bool   rnulls[7] = { false, false, false, false, false, false, false };

        values[0] = Int32GetDatum(nodes[i].depth);
        values[1] = PointerGetDatum(branch_array(nodes, i, false));
        values[2] = PointerGetDatum(branch_array(nodes, i, true));
        values[3] = nodes[i].entity;
        values[4] = nodes[i].eff_mu;
        values[5] = nodes[i].path_mu;
        values[6] = Int64GetDatum(nodes[i].witnesses);
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, rnulls);
    }

    SPI_finish();
    return (Datum) 0;
}

Datum
pg_laplace_generate_greedy(PG_FUNCTION_ARGS)
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
        ereport(ERROR, (errmsg("generate_greedy: prompt entity must not be NULL")));
    prompt    = PG_GETARG_BYTEA_PP(0);
    type_null = PG_ARGISNULL(1);
    if (!type_null)
        type_datum = PG_GETARG_DATUM(1);
    max_depth = PG_ARGISNULL(2) ? 8 : PG_GETARG_INT32(2);

    InitMaterializedSRF(fcinfo, 0);

    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "generate_greedy: SPI_connect failed");
    ensure_edge_plan();

    seen_cap = max_depth + 1;
    if (seen_cap < 8) seen_cap = 8;
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
            elog(ERROR, "generate_greedy: edge query failed: %s",
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


#define VFLAG_HAS_ATOM   ((int64) 1)
#define VFLAG_ATOM_SHIFT 31
#define VFLAG_ATOM_MASK  ((int64) 0x1FFFFF)

static const char *CONSTITUENTS_QUERY =
    "SELECT c.child_id, c.run_length, c.flags FROM laplace.constituents($1) c ORDER BY c.ordinal";

static SPIPlanPtr constituents_plan = NULL;

typedef struct RenderMemoEntry
{
    char  key[16];
    char *text;
} RenderMemoEntry;

static void
ensure_render_plans(void)
{
    if (constituents_plan == NULL)
    {
        Oid argtypes[1] = { BYTEAOID };
        SPIPlanPtr plan = SPI_prepare(CONSTITUENTS_QUERY, 1, argtypes);
        if (plan == NULL)
            elog(ERROR, "render_text: SPI_prepare(constituents) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "render_text: SPI_keepplan failed");
        constituents_plan = plan;
    }
}

static void
append_codepoint_utf8(StringInfo out, uint32 cp)
{
    unsigned char buf[4];
    if (cp < 0x80) { appendStringInfoChar(out, (char) cp); return; }
    if (cp < 0x800)
    {
        buf[0] = 0xC0 | (cp >> 6);
        buf[1] = 0x80 | (cp & 0x3F);
        appendBinaryStringInfo(out, (char *) buf, 2);
        return;
    }
    if (cp < 0x10000)
    {
        buf[0] = 0xE0 | (cp >> 12);
        buf[1] = 0x80 | ((cp >> 6) & 0x3F);
        buf[2] = 0x80 | (cp & 0x3F);
        appendBinaryStringInfo(out, (char *) buf, 3);
        return;
    }
    buf[0] = 0xF0 | (cp >> 18);
    buf[1] = 0x80 | ((cp >> 12) & 0x3F);
    buf[2] = 0x80 | ((cp >> 6) & 0x3F);
    buf[3] = 0x80 | (cp & 0x3F);
    appendBinaryStringInfo(out, (char *) buf, 4);
}

/* T0 leaf render: perfcache reverse lookup, in process. Replaced the
 * per-grapheme SPI probe into the retired codepoint_render table. */
static bool
append_codepoint_render(StringInfo out, Datum id)
{
    bytea   *idb = DatumGetByteaPP(id);
    uint32_t cp;

    if (VARSIZE_ANY_EXHDR(idb) != 16)
        return false;
    if (!laplace_perfcache_codepoint_for_id((const uint8 *) VARDATA_ANY(idb), &cp))
        return false;
    append_codepoint_utf8(out, cp);
    return true;
}

static const char *
render_node(HTAB *memo, Datum id, int depth, int max_depth)
{
    char key[16];
    bool found;
    RenderMemoEntry *e;
    bytea *idb = DatumGetByteaPP(id);

    if (VARSIZE_ANY_EXHDR(idb) != 16)
        ereport(ERROR, (errmsg("render_text: entity id must be 16 bytes")));
    memcpy(key, VARDATA_ANY(idb), 16);

    e = (RenderMemoEntry *) hash_search(memo, key, HASH_ENTER, &found);
    if (found)
        return e->text;
    e->text = NULL;

    if (depth < max_depth)
    {
        Datum args[1] = { id };
        int rc = SPI_execute_plan(constituents_plan, args, NULL, true, 0);
        uint64 nrows;

        if (rc != SPI_OK_SELECT)
            elog(ERROR, "render_text: constituents fetch failed: %s",
                 SPI_result_code_string(rc));
        nrows = SPI_processed;

        if (nrows > 0)
        {
            typedef struct { Datum child; int32 run; int64 flags; } ChildRow;
            ChildRow *rows = (ChildRow *) palloc(sizeof(ChildRow) * nrows);
            StringInfoData out;
            bool ok = true;

            for (uint64 r = 0; r < nrows; r++)
            {
                HeapTuple tup = SPI_tuptable->vals[r];
                TupleDesc td = SPI_tuptable->tupdesc;
                bool isnull;
                rows[r].child = copy_bytea_datum(SPI_getbinval(tup, td, 1, &isnull));
                rows[r].run   = DatumGetInt32(SPI_getbinval(tup, td, 2, &isnull));
                if (rows[r].run < 1) rows[r].run = 1;
                rows[r].flags = DatumGetInt64(SPI_getbinval(tup, td, 3, &isnull));
            }

            initStringInfo(&out);
            for (uint64 r = 0; r < nrows && ok; r++)
            {
                if (rows[r].flags & VFLAG_HAS_ATOM)
                {
                    uint32 cp = (uint32) ((rows[r].flags >> VFLAG_ATOM_SHIFT) & VFLAG_ATOM_MASK);
                    for (int32 k = 0; k < rows[r].run; k++)
                        append_codepoint_utf8(&out, cp);
                }
                else
                {
                    const char *child_text = render_node(memo, rows[r].child,
                                                         depth + 1, max_depth);
                    if (child_text != NULL)
                        for (int32 k = 0; k < rows[r].run; k++)
                            appendStringInfoString(&out, child_text);
                    else if (!append_codepoint_render(&out, rows[r].child))
                        ok = false;
                }
            }
            pfree(rows);

            e = (RenderMemoEntry *) hash_search(memo, key, HASH_FIND, &found);
            Assert(found);
            e->text = ok ? out.data : NULL;
            return e->text;
        }
    }

    {
        StringInfoData out;
        initStringInfo(&out);
        if (append_codepoint_render(&out, id))
        {
            e = (RenderMemoEntry *) hash_search(memo, key, HASH_FIND, &found);
            Assert(found);
            e->text = out.data;
        }
        return e->text;
    }
}

PG_FUNCTION_INFO_V1(pg_laplace_render_text);
PG_FUNCTION_INFO_V1(pg_laplace_render_text_fast);
PG_FUNCTION_INFO_V1(pg_laplace_render_text_batch);

Datum
pg_laplace_render_text(PG_FUNCTION_ARGS)
{
    Datum   id;
    int32   max_depth;
    HASHCTL ctl;
    HTAB   *memo;
    const char *rendered;
    MemoryContext caller_cxt = CurrentMemoryContext;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    id = PG_GETARG_DATUM(0);
    max_depth = PG_ARGISNULL(1) ? 32 : PG_GETARG_INT32(1);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "render_text: SPI_connect failed");
    ensure_render_plans();

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(RenderMemoEntry);
    memo = hash_create("render_text memo", 1024, &ctl, HASH_ELEM | HASH_BLOBS);

    rendered = render_node(memo, id, 0, max_depth);

    if (rendered == NULL || rendered[0] == '\0')
    {
        SPI_finish();
        PG_RETURN_NULL();
    }
    else
    {
        MemoryContext spi_cxt = MemoryContextSwitchTo(caller_cxt);
        text *result = cstring_to_text(rendered);
        MemoryContextSwitchTo(spi_cxt);
        SPI_finish();
        PG_RETURN_TEXT_P(result);
    }
}

Datum
pg_laplace_render_text_fast(PG_FUNCTION_ARGS)
{
    Datum   id;
    int32   max_depth = 8;
    HASHCTL ctl;
    HTAB   *memo;
    const char *rendered;
    MemoryContext caller_cxt = CurrentMemoryContext;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    id = PG_GETARG_DATUM(0);
    if (PG_NARGS() > 1 && !PG_ARGISNULL(1))
        max_depth = PG_GETARG_INT32(1);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "render_text_fast: SPI_connect failed");
    ensure_render_plans();

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(RenderMemoEntry);
    memo = hash_create("render_text_fast memo", 1024, &ctl, HASH_ELEM | HASH_BLOBS);

    rendered = render_node(memo, id, 0, max_depth);

    if (rendered == NULL || rendered[0] == '\0')
    {
        SPI_finish();
        PG_RETURN_NULL();
    }
    MemoryContext spi_cxt = MemoryContextSwitchTo(caller_cxt);
    text *result = cstring_to_text(rendered);
    MemoryContextSwitchTo(spi_cxt);
    SPI_finish();
    PG_RETURN_TEXT_P(result);
}

Datum
pg_laplace_render_text_batch(PG_FUNCTION_ARGS)
{
    ArrayType  *arr;
    int32       max_depth;
    Datum      *elems;
    bool       *nulls;
    int         n;
    Datum      *out;
    bool       *out_nulls;
    ArrayType  *result;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    arr = PG_GETARG_ARRAYTYPE_P(0);
    max_depth = PG_ARGISNULL(1) ? 8 : PG_GETARG_INT32(1);

    if (ARR_NDIM(arr) != 1)
        ereport(ERROR, (errmsg("render_text_batch: ids must be 1-dimensional")));
    if (ARR_ELEMTYPE(arr) != BYTEAOID)
        ereport(ERROR, (errmsg("render_text_batch: element type must be bytea")));

    deconstruct_array(arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &elems, &nulls, &n);
    out = (Datum *) palloc0(sizeof(Datum) * n);
    out_nulls = (bool *) palloc0(sizeof(bool) * n);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "render_text_batch: SPI_connect failed");
    ensure_render_plans();

    for (int i = 0; i < n; i++)
    {
        HASHCTL ctl;
        HTAB   *memo;
        const char *rendered;

        if (nulls[i])
        {
            out_nulls[i] = true;
            continue;
        }
        memset(&ctl, 0, sizeof(ctl));
        ctl.keysize = 16;
        ctl.entrysize = sizeof(RenderMemoEntry);
        memo = hash_create("render_text_batch memo", 256, &ctl, HASH_ELEM | HASH_BLOBS);
        rendered = render_node(memo, elems[i], 0, max_depth);
        if (rendered == NULL || rendered[0] == '\0')
            out_nulls[i] = true;
        else
            out[i] = CStringGetTextDatum(rendered);
        hash_destroy(memo);
    }
    SPI_finish();

    {
        int dims[1] = { n };
        int lbs[1] = { 1 };
        result = construct_md_array(out, out_nulls, 1, dims, lbs,
                                  TEXTOID, -1, false, TYPALIGN_INT);
    }
    PG_RETURN_ARRAYTYPE_P(result);
}
