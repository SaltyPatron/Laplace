/*
 * generate_walk_render.c — Constituent closure traversal and lossless
 * text/byte realization (pg_laplace_render_text*, pg_laplace_render_bytes*).
 */
#include "generate_walk_internal.h"

static SPIPlanPtr closure_plan = NULL;

typedef struct RenderMemoKey
{
    char id[16];
} RenderMemoKey;

typedef struct RenderMemoEntry
{
    RenderMemoKey key;
    char *text;
    int length;
} RenderMemoEntry;

typedef struct ClosureChild
{
    Datum child;
    int32 run;
} ClosureChild;

typedef struct ClosureParent
{
    char          key[16];
    ClosureChild *kids;
    int           n;
    int           cap;
    bool          rendering;
    bool          tuple;
    bool          scalar;
} ClosureParent;

static void
ensure_render_plans(void)
{
    if (closure_plan == NULL)
    {
        Oid argtypes[2] = { BYTEAARRAYOID, INT4OID };
        SPIPlanPtr plan = SPI_prepare_cursor(laplace_sql_query_text("readback.constituent_edges"), 2, argtypes, CURSOR_OPT_PARALLEL_OK);
        if (plan == NULL)
            elog(ERROR, "render_text: SPI_prepare(constituents_closure) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "render_text: SPI_keepplan(constituents_closure) failed");
        closure_plan = plan;
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

static void
closure_parent_push(ClosureParent *p, Datum child, int32 run)
{
    if (p->n >= p->cap)
    {
        int ncap = (p->cap == 0) ? 4 : p->cap * 2;
        if (p->kids == NULL)
            p->kids = (ClosureChild *) palloc(sizeof(ClosureChild) * ncap);
        else
            p->kids = (ClosureChild *) repalloc(p->kids, sizeof(ClosureChild) * ncap);
        p->cap = ncap;
    }
    p->kids[p->n].child = child;
    p->kids[p->n].run = (run < 1) ? 1 : run;
    p->n++;
}

typedef struct {
    HTAB *parents;
    MemoryContext owner;
} ClosureRead;

static void
receive_closure_edge(TupleTableSlot *row, void *opaque)
{
    ClosureRead *read = opaque;
    bytea *parent = spi_slot_bytea(row, 1), *child = spi_slot_bytea(row, 2);
    bool isnull, flags_null = false, found;
    Datum count = slot_getattr(row, 3, &isnull);
    Datum flags_d = slot_getattr(row, 4, &flags_null);
    int64 flags = flags_null ? 0 : DatumGetInt64(flags_d);
    uint32_t child_cp;
    if (!parent || !child || VARSIZE_ANY_EXHDR(parent) != 16 ||
        VARSIZE_ANY_EXHDR(child) != 16 || isnull || DatumGetInt32(count) <= 0)
        ereport(ERROR, (errcode(ERRCODE_DATA_CORRUPTED),
            errmsg("render_text: malformed constituent edge")));
    ClosureParent *entry = hash_search(read->parents, VARDATA_ANY(parent), HASH_ENTER, &found);
    if (!found) {
        entry->kids = NULL; entry->n = entry->cap = 0;
        entry->rendering = false; entry->tuple = false; entry->scalar = true;
    }
    if (((flags >> 30) & 7) == 3)
        entry->tuple = true;
    if (!laplace_perfcache_codepoint_for_id((const uint8 *) VARDATA_ANY(child), &child_cp))
        entry->scalar = false;
    MemoryContext previous = MemoryContextSwitchTo(read->owner);
    closure_parent_push(entry, copy_bytea_datum(PointerGetDatum(child)), DatumGetInt32(count));
    MemoryContextSwitchTo(previous);
}

static HTAB *
fetch_constituents_closure(Datum *roots, int n_roots, int32 max_depth)
{
    HASHCTL control = {0};
    control.keysize = 16; control.entrysize = sizeof(ClosureParent);
    control.hcxt = CurrentMemoryContext;
    HTAB *parents = hash_create("render closure", 1024, &control, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    if (n_roots <= 0) return parents;
    ArrayType *ids = construct_array(roots, n_roots, BYTEAOID, -1, false, TYPALIGN_INT);
    Oid types[] = {BYTEAARRAYOID, INT4OID};
    Datum args[] = {PointerGetDatum(ids), Int32GetDatum(max_depth)};
    ClosureRead read = {parents, CurrentMemoryContext};
    laplace_spi_stream(closure_plan, 2, types, args, NULL, receive_closure_edge, &read);
    pfree(ids);
    return parents;
}

typedef struct {
    char id[16];
    ClosureParent *parent;
    int child, depth, start;
    int32 run;
    bool entered, silent;
} RenderFrame;

static void
repeat_render_tail(StringInfo output, int start, int32 run)
{
    Size length = output->len - start;
    if (!length || run <= 1) return;
    uint64 extra = (uint64)length * (uint64)(run - 1);
    if (extra >= MaxAllocSize || extra > (uint64)(MaxAllocSize - output->len - 1))
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
            errmsg("render_text: realized content exceeds PostgreSQL allocation capacity")));
    enlargeStringInfo(output, (int)extra);
    Size copied = length, total = length + (Size)extra;
    while (copied < total) {
        CHECK_FOR_INTERRUPTS();
        Size take = Min(copied, total - copied);
        memcpy(output->data + start + copied, output->data + start, take);
        copied += take;
    }
    output->len += (int)extra;
    output->data[output->len] = '\0';
}

static const char *
render_root(HTAB *closure, HTAB *memo, Datum id, int max_depth, bool as_bytes, int *length)
{
    bytea *bytes = DatumGetByteaPP(id);
    if (VARSIZE_ANY_EXHDR(bytes) != 16)
        ereport(ERROR, (errmsg("render_text: entity id must be 16 bytes")));
    RenderMemoKey key = {0}; bool found;
    memcpy(key.id, VARDATA_ANY(bytes), 16);
    RenderMemoEntry *entry = hash_search(memo, &key, HASH_ENTER, &found);
    if (found) { *length = entry->length; return entry->text; }
    entry->text = NULL; entry->length = 0;
    Size capacity = 32, used = 1;
    RenderFrame *stack = palloc0(capacity * sizeof(*stack));
    memcpy(stack[0].id, key.id, 16); stack[0].run = 1;
    StringInfoData output; initStringInfo(&output);
    bool complete = true;
    while (used) {
        CHECK_FOR_INTERRUPTS();
        RenderFrame *frame = &stack[used - 1];
        if (!frame->entered) {
            frame->start = output.len;
            uint32_t codepoint;
            if (laplace_perfcache_codepoint_for_id((const uint8 *)frame->id, &codepoint)) {
                if (!frame->silent) {
                    append_codepoint_utf8(&output, codepoint);
                    repeat_render_tail(&output, frame->start, frame->run);
                }
                used--; continue;
            }
            frame->parent = hash_search(closure, frame->id, HASH_FIND, NULL);
            if (!frame->parent || !frame->parent->n || frame->parent->rendering ||
                (max_depth > 0 && frame->depth >= max_depth)) {
                complete = false; break;
            }
            frame->entered = true;
            frame->parent->rendering = true;
        }
        if (frame->child == frame->parent->n) {
            if (!frame->silent)
                repeat_render_tail(&output, frame->start, frame->run);
            frame->parent->rendering = false; used--; continue;
        }
        ClosureChild child = frame->parent->kids[frame->child++];
        int depth = frame->depth;
        if (depth == PG_INT32_MAX)
            ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED), errmsg("render_text: depth exceeds addressable state")));
        if (used == capacity) {
            Size maximum = MaxAllocSize / sizeof(*stack);
            if (capacity == maximum)
                ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED), errmsg("render_text: occurrence stack exceeds allocation capacity")));
            capacity = Min(capacity * 2, maximum);
            stack = repalloc(stack, capacity * sizeof(*stack));
        }
        RenderFrame *next = &stack[used++];
        memset(next, 0, sizeof(*next));
        memcpy(next->id, VARDATA_ANY(DatumGetByteaPP(child.child)), 16);
        next->depth = depth + 1; next->run = child.run;
        next->silent = frame->silent ||
            (!as_bytes && frame->parent->tuple && !frame->parent->scalar && frame->child > 1);
    }
    for (Size i = 0; i < used; i++)
        if (stack[i].entered) stack[i].parent->rendering = false;
    pfree(stack);
    if (complete) { entry->text = output.data; entry->length = output.len; }
    else pfree(output.data);
    *length = entry->length;
    return entry->text;
}

PG_FUNCTION_INFO_V1(pg_laplace_render_text);
PG_FUNCTION_INFO_V1(pg_laplace_render_text_fast);
PG_FUNCTION_INFO_V1(pg_laplace_render_text_batch);
PG_FUNCTION_INFO_V1(pg_laplace_render_bytes);
PG_FUNCTION_INFO_V1(pg_laplace_render_bytes_batch);

Datum pg_laplace_render_text_batch(PG_FUNCTION_ARGS);
Datum pg_laplace_render_bytes_batch(PG_FUNCTION_ARGS);
static Datum render_batch(FunctionCallInfo fcinfo, bool as_bytes);

static Datum
render_single(Datum id, int32 max_depth, bool as_bytes, bool *isnull)
{
    ArrayType *ids = construct_array(&id, 1, BYTEAOID, -1, false, TYPALIGN_INT);
    Datum result = DirectFunctionCall2(as_bytes ? pg_laplace_render_bytes_batch : pg_laplace_render_text_batch,
                                       PointerGetDatum(ids), Int32GetDatum(max_depth));
    Datum *items;
    bool *nulls;
    int count;
    deconstruct_array(DatumGetArrayTypeP(result), as_bytes ? BYTEAOID : TEXTOID, -1, false,
                      TYPALIGN_INT, &items, &nulls, &count);
    Assert(count == 1);
    *isnull = nulls[0];
    return nulls[0] ? (Datum) 0 : items[0];
}

Datum
pg_laplace_render_text(PG_FUNCTION_ARGS)
{
    bool isnull;
    Datum result;
    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    result = render_single(PG_GETARG_DATUM(0),
                           PG_ARGISNULL(1) ? 0 : PG_GETARG_INT32(1), false, &isnull);
    if (isnull)
        PG_RETURN_NULL();
    return result;
}

Datum
pg_laplace_render_text_fast(PG_FUNCTION_ARGS)
{
    bool isnull;
    Datum result;
    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    result = render_single(PG_GETARG_DATUM(0),
                           PG_NARGS() > 1 && !PG_ARGISNULL(1)
                               ? PG_GETARG_INT32(1) : 8, false, &isnull);
    if (isnull)
        PG_RETURN_NULL();
    return result;
}

Datum
pg_laplace_render_bytes(PG_FUNCTION_ARGS)
{
    bool isnull;
    if (PG_ARGISNULL(0)) PG_RETURN_NULL();
    Datum result = render_single(PG_GETARG_DATUM(0),
        PG_ARGISNULL(1) ? 0 : PG_GETARG_INT32(1), true, &isnull);
    if (isnull) PG_RETURN_NULL();
    return result;
}

Datum
pg_laplace_render_text_batch(PG_FUNCTION_ARGS)
{
    return render_batch(fcinfo, false);
}

Datum
pg_laplace_render_bytes_batch(PG_FUNCTION_ARGS)
{
    return render_batch(fcinfo, true);
}

static Datum
render_batch(FunctionCallInfo fcinfo, bool as_bytes)
{
    Oid output_type = as_bytes ? BYTEAOID : TEXTOID;
    ArrayType  *arr;
    int32       max_depth;
    Datum      *elems;
    bool       *nulls;
    int         n;
    Datum      *out;
    bool       *out_nulls;
    ArrayType  *result;
    Datum      *roots;
    int         n_roots = 0;
    HTAB       *closure;
    HASHCTL     mctl;
    HTAB       *memo;
    MemoryContext caller_cxt = CurrentMemoryContext;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    arr = PG_GETARG_ARRAYTYPE_P(0);
    max_depth = PG_ARGISNULL(1) ? 0 : PG_GETARG_INT32(1);
    if (max_depth < 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("render_text_batch: max_depth must be zero or positive")));

    if (ARR_NDIM(arr) == 0)
        PG_RETURN_ARRAYTYPE_P(construct_empty_array(output_type));
    if (ARR_NDIM(arr) != 1)
        ereport(ERROR, (errmsg("render_text_batch: ids must be 1-dimensional")));
    if (ARR_ELEMTYPE(arr) != BYTEAOID)
        ereport(ERROR, (errmsg("render_text_batch: element type must be bytea")));

    deconstruct_array(arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &elems, &nulls, &n);
    out = (Datum *) palloc0(sizeof(Datum) * n);
    out_nulls = (bool *) palloc0(sizeof(bool) * n);
    roots = (Datum *) palloc(sizeof(Datum) * n);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "render_text_batch: SPI_connect failed");

    for (int i = 0; i < n; i++)
    {
        if (!nulls[i]) {
            bytea *id = DatumGetByteaPP(elems[i]);
            uint32_t codepoint;
            if (VARSIZE_ANY_EXHDR(id) == 16 &&
                laplace_perfcache_codepoint_for_id(
                    (const uint8_t *) VARDATA_ANY(id), &codepoint))
                continue;
            roots[n_roots++] = elems[i];
        }
    }

    if (n_roots > 0) ensure_render_plans();
    closure = fetch_constituents_closure(roots, n_roots, max_depth);

    memset(&mctl, 0, sizeof(mctl));
    mctl.keysize = sizeof(RenderMemoKey);
    mctl.entrysize = sizeof(RenderMemoEntry);
    memo = hash_create("render_text_batch memo", 1024, &mctl, HASH_ELEM | HASH_BLOBS);

    for (int i = 0; i < n; i++)
    {
        const char *rendered;

        if (nulls[i])
        {
            out_nulls[i] = true;
            continue;
        }
        int rendered_length;
        rendered = render_root(closure, memo, elems[i], max_depth, as_bytes, &rendered_length);
        if (rendered == NULL || rendered_length == 0
            || (!as_bytes && memchr(rendered, 0, rendered_length) != NULL))
            out_nulls[i] = true;
        else
        {
            MemoryContext old = MemoryContextSwitchTo(caller_cxt);
            bytea *value = (bytea *) palloc(VARHDRSZ + rendered_length);
            SET_VARSIZE(value, VARHDRSZ + rendered_length);
            memcpy(VARDATA(value), rendered, rendered_length);
            out[i] = PointerGetDatum(value);
            MemoryContextSwitchTo(old);
        }
    }
    SPI_finish();

    {
        int dims[1] = { n };
        int lbs[1] = { 1 };
        result = construct_md_array(out, out_nulls, 1, dims, lbs,
                                    output_type, -1, false, TYPALIGN_INT);
    }
    PG_RETURN_ARRAYTYPE_P(result);
}
