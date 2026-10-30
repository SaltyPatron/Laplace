#include "postgres.h"
#include "miscadmin.h"

#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "nodes/makefuncs.h"
#include "parser/parse_func.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"

#include "laplace/core/mantissa.h"
#include "content_trajectory_read.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "trajectory_wkb.h"
#include "perfcache_native.h"
#include "laplace/core/sql_catalog.h"
#include "spi_stream.h"

/*
 * realize.constituents_closure(roots, max_depth)
 *
 * Descends the composition face level by level: each level first probes the
 * canonical (entity_id, type=1) primary-key address on the exact owning HASH(id)
 * leaf partition via laplace_content_trajectory_read, and falls back to the
 * entity_id-indexed query only for any frontier entity not found at its
 * canonical physicality id.
 */
static const char *CLOSURE_LEVEL_QUERY =
    "SELECT DISTINCT ON (p.entity_id) p.entity_id, public.ST_AsBinary(p.trajectory) "
    "FROM laplace.physicalities p "
    "WHERE p.entity_id = ANY($1::bytea[]) AND p.type = 1 AND p.trajectory IS NOT NULL "
    "ORDER BY p.entity_id, p.id, public.ST_NPoints(p.trajectory) DESC";

/* Prepared once per backend; PostgreSQL chooses custom/generic plans for the
 * actual frontier and honors the operator's plan_cache_mode. */
static SPIPlanPtr closure_level_plan = NULL;
static SPIPlanPtr native_closure_plan = NULL;
static Oid closure_st_asbinary_oid = InvalidOid;

static Oid
ensure_closure_asbinary_oid(void)
{
    if (!OidIsValid(closure_st_asbinary_oid))
    {
        Oid phys_rel = get_relname_relid("physicalities", get_namespace_oid("laplace", false));
        Oid geom_type = get_atttype(phys_rel, get_attnum(phys_rel, "trajectory"));
        closure_st_asbinary_oid = LookupFuncName(
            list_make2(makeString("public"), makeString("st_asbinary")),
            1, &geom_type, false);
    }
    return closure_st_asbinary_oid;
}

static void
ensure_closure_level_plan(void)
{
    if (closure_level_plan != NULL)
        return;
    {
        Oid argtypes[1] = {BYTEAARRAYOID};
        SPIPlanPtr plan = SPI_prepare_cursor(CLOSURE_LEVEL_QUERY, 1, argtypes,
                                              CURSOR_OPT_PARALLEL_OK);
        if (plan == NULL)
            elog(ERROR, "constituents_closure: SPI_prepare(level) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "constituents_closure: SPI_keepplan(level) failed");
        closure_level_plan = plan;
    }
}

PG_FUNCTION_INFO_V1(pg_laplace_constituents_closure);

typedef struct {
    hash128_t *ids;
    size_t count;
    size_t cap;
} IdVec;

typedef struct {
    hash128_t parent;
    hash128_t child;
    int32 ordinal;
    int32 run_length;
    int64 flags;
} ClosureEdge;

typedef struct {
    ClosureEdge *items;
    size_t count;
    size_t cap;
} EdgeVec;

static void
idvec_reserve(IdVec *v, size_t required, MemoryContext owner, const char *what)
{
    if (required <= v->cap)
        return;
    if (required > MaxAllocSize / sizeof(hash128_t))
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                        errmsg("constituents_closure: %s exceeds allocation capacity", what)));
    size_t cap = v->cap ? v->cap : 16;
    while (cap < required) {
        if (cap > MaxAllocSize / sizeof(hash128_t) / 2) { cap = required; break; }
        cap *= 2;
    }
    hash128_t *grown = (hash128_t *) MemoryContextAlloc(owner, cap * sizeof(hash128_t));
    if (v->ids) {
        memcpy(grown, v->ids, v->count * sizeof(hash128_t));
        pfree(v->ids);
    }
    v->ids = grown;
    v->cap = cap;
}

static void
edgevec_push(EdgeVec *v, const ClosureEdge *edge, MemoryContext owner)
{
    if (v->count == v->cap) {
        if (v->count >= MaxAllocSize / sizeof(ClosureEdge))
            ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                            errmsg("constituents_closure: output exceeds allocation capacity")));
        size_t cap = v->cap ? v->cap * 2 : 128;
        if (cap > MaxAllocSize / sizeof(ClosureEdge))
            cap = MaxAllocSize / sizeof(ClosureEdge);
        ClosureEdge *grown = (ClosureEdge *) MemoryContextAlloc(
            owner, cap * sizeof(ClosureEdge));
        if (v->items) {
            memcpy(grown, v->items, v->count * sizeof(ClosureEdge));
            pfree(v->items);
        }
        v->items = grown;
        v->cap = cap;
    }
    v->items[v->count++] = *edge;
}

static int
edge_compare(const void *a, const void *b)
{
    const ClosureEdge *x = (const ClosureEdge *) a;
    const ClosureEdge *y = (const ClosureEdge *) b;
    int c = memcmp(&x->parent, &y->parent, sizeof(hash128_t));
    if (c != 0) return c;
    if (x->ordinal < y->ordinal) return -1;
    if (x->ordinal > y->ordinal) return 1;
    return memcmp(&x->child, &y->child, sizeof(hash128_t));
}

static void
frontier_add_if_new(HTAB *visited, IdVec *frontier, const hash128_t *id,
                    MemoryContext owner)
{
    uint32_t codepoint;
    /* Tier-0 codepoints resolve in the perfcache and have no constituents: their
     * parent edge is emitted, but they never enter the frontier. */
    if (laplace_perfcache_codepoint_for_id((const uint8_t *) id, &codepoint))
        return;
    bool found;
    (void) hash_search(visited, id, HASH_ENTER, &found);
    if (!found) {
        idvec_reserve(frontier, frontier->count + 1, owner, "frontier");
        frontier->ids[frontier->count++] = *id;
    }
}

static void
append_manifest_edges(const hash128_t *parent, const bytea *wkb,
                      EdgeVec *edges, IdVec *next, HTAB *visited,
                      MemoryContext owner)
{
    const unsigned char *points;
    uint32 npoints;
    int64 ordinal = 1;

    points = laplace_trajectory_wkb_points(wkb, &npoints);

    for (uint32 i = 0; i < npoints; ++i) {
        double vertex[4];
        mantissa_payload_t payload;
        int32 run;
        ClosureEdge edge;

        memcpy(vertex, points + (Size) i * 32, sizeof(vertex));
        mantissa_unpack(vertex, &payload);
        run = payload.run_length ? (int32) payload.run_length : 1;
        if (ordinal > PG_INT32_MAX)
            ereport(ERROR, (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                            errmsg("constituents_closure: ordinal exceeds int4")));
        edge.parent = *parent;
        edge.child = payload.entity_id;
        edge.ordinal = (int32) ordinal;
        edge.run_length = run;
        edge.flags = (int64) payload.flags;
        edgevec_push(edges, &edge, owner);
        frontier_add_if_new(visited, next, &payload.entity_id, owner);
        ordinal += run;
    }
}

typedef struct ClosureReadCtx
{
    Oid as_binary;
    HTAB *resolved;
    EdgeVec *edges;
    IdVec *next;
    HTAB *visited;
    MemoryContext owner;
} ClosureReadCtx;

static void native_closure_edge(TupleTableSlot *slot,void *context)
{
    ClosureReadCtx *read=context;Datum values[5];bool missing;
    for(int i=0;i<5;i++){
        values[i]=slot_getattr(slot,i+1,&missing);
        if(missing)elog(ERROR,"Native constituent frontier contains a null field");
    }
    ClosureEdge edge={datum_to_hash128(values[0]),datum_to_hash128(values[2]),
        DatumGetInt32(values[1]),DatumGetInt32(values[3]),DatumGetInt64(values[4])};
    if(edge.ordinal<1 || edge.run_length<1)elog(ERROR,"Native constituent frontier contains an invalid span");
    (void)hash_search(read->resolved,&edge.parent,HASH_ENTER,NULL);
    edgevec_push(read->edges,&edge,read->owner);
    frontier_add_if_new(read->visited,read->next,&edge.child,read->owner);
}

static void
closure_consume_trajectory(Datum physicality, Datum entity, Datum geometry, void *opaque)
{
    ClosureReadCtx *ctx = (ClosureReadCtx *) opaque;
    hash128_t parent = datum_to_hash128(entity);
    bool found;

    (void) physicality;
    (void) hash_search(ctx->resolved, &parent, HASH_ENTER, &found);
    if (found)
        return;
    {
        MemoryContext prev = MemoryContextSwitchTo(ctx->owner);
        bytea *wkb = DatumGetByteaP(OidFunctionCall1(ctx->as_binary, geometry));
        append_manifest_edges(&parent, wkb, ctx->edges, ctx->next, ctx->visited, ctx->owner);
        pfree(wkb);
        MemoryContextSwitchTo(prev);
    }
}

Datum
pg_laplace_constituents_closure(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType *roots_array;
    Datum *roots;
    bool *root_nulls;
    int root_count;
    int32 max_depth;
    IdVec frontier = {0}, next = {0};
    EdgeVec edges = {0};
    MemoryContext owner = CurrentMemoryContext;
    HASHCTL ctl;
    HTAB *visited;
    Oid as_binary;
    bool spi_connected = false;
    bool spi_top = false;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    roots_array = PG_GETARG_ARRAYTYPE_P(0);
    max_depth = PG_ARGISNULL(1) ? 0 : PG_GETARG_INT32(1);
    if (max_depth < 0)
        return (Datum) 0;

    deconstruct_array(roots_array, BYTEAOID, -1, false, TYPALIGN_INT,
                      &roots, &root_nulls, &root_count);
    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(hash128_t);
    ctl.hcxt = CurrentMemoryContext;
    visited = hash_create("constituents closure visited", 1024, &ctl,
                          HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    for (int i = 0; i < root_count; ++i) {
        if (root_nulls[i]) continue;
        bytea *root = DatumGetByteaPP(roots[i]);
        if (VARSIZE_ANY_EXHDR(root) != sizeof(hash128_t)) continue;
        hash128_t id;
        memcpy(&id, VARDATA_ANY(root), sizeof(id));
        frontier_add_if_new(visited, &frontier, &id, owner);
    }
    pfree(roots);
    pfree(root_nulls);
    if (frontier.count == 0)
        return (Datum) 0;

    as_binary = ensure_closure_asbinary_oid();

    for (int depth = 0; frontier.count > 0 && (max_depth == 0 || depth < max_depth); ++depth) {
        if (frontier.count > INT_MAX)
            ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                            errmsg("constituents_closure: frontier exceeds PostgreSQL array capacity")));
        Datum *ids = (Datum *) MemoryContextAlloc(owner, frontier.count * sizeof(Datum));
        for (size_t i = 0; i < frontier.count; ++i)
            ids[i] = hash128_to_datum(&frontier.ids[i]);
        ArrayType *arg_array = construct_array(ids, (int) frontier.count,
                                               BYTEAOID, -1, false, TYPALIGN_INT);

        next.count = 0;
        HASHCTL rctl;
        memset(&rctl, 0, sizeof(rctl));
        rctl.keysize = sizeof(hash128_t);
        rctl.entrysize = sizeof(hash128_t);
        rctl.hcxt = owner;
        HTAB *resolved = hash_create("constituents closure resolved",
                                     (int) Min(frontier.count, 1024), &rctl,
                                     HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        ClosureReadCtx read_ctx = {as_binary, resolved, &edges, &next, visited, owner};
        if(!spi_connected){
            if(laplace_spi_connect(&spi_top)!=SPI_OK_CONNECT)elog(ERROR,"constituents_closure: SPI_connect failed");
            Oid types[]={BYTEAARRAYOID};
            spi_common_ensure_plan(&native_closure_plan,laplace_sql_query_text("native.constituent_frontier"),1,types);
            spi_connected=true;
        }
        {
            Oid types[]={BYTEAARRAYOID};Datum args[]={PointerGetDatum(arg_array)};
            laplace_spi_stream(native_closure_plan,1,types,args,NULL,native_closure_edge,&read_ctx);
        }
        /* Older typed physicalities remain a predecessor input. A canonical
         * carrier always wins; only unresolved IDs probe that representation. */
        if((size_t)hash_get_num_entries(resolved)<frontier.count){
            int remaining=0;Datum *pending=palloc(frontier.count*sizeof(Datum));
            for(size_t i=0;i<frontier.count;i++)
                if(!hash_search(resolved,&frontier.ids[i],HASH_FIND,NULL))pending[remaining++]=ids[i];
            ArrayType *legacy=construct_array(pending,remaining,BYTEAOID,-1,false,TYPALIGN_INT);
            laplace_content_trajectory_read(legacy,closure_consume_trajectory,&read_ctx);
            pfree(legacy);pfree(pending);
        }

        if ((size_t) hash_get_num_entries(resolved) < frontier.count) {
            int rem_n = 0;
            Datum *rem_ids = (Datum *) MemoryContextAlloc(owner, frontier.count * sizeof(Datum));
            for (size_t i = 0; i < frontier.count; ++i) {
                if (!hash_search(resolved, &frontier.ids[i], HASH_FIND, NULL))
                    rem_ids[rem_n++] = ids[i];
            }
            if (rem_n > 0) {
                if (!spi_connected) {
                    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
                        elog(ERROR, "constituents_closure: SPI_connect failed");
                    spi_connected = true;
                }
                ensure_closure_level_plan();
                ArrayType *rem_array = construct_array(rem_ids, rem_n,
                                                       BYTEAOID, -1, false, TYPALIGN_INT);
                Datum args[1] = {PointerGetDatum(rem_array)};
                int rc = SPI_execute_plan(closure_level_plan, args, NULL, true, 0);
                if (rc != SPI_OK_SELECT)
                    elog(ERROR, "constituents_closure: level query failed: %s", SPI_result_code_string(rc));

                for (uint64 row = 0; row < SPI_processed; ++row) {
                    HeapTuple tuple = SPI_tuptable->vals[row];
                    TupleDesc desc = SPI_tuptable->tupdesc;
                    bool isnull;
                    Datum parent_d = SPI_getbinval(tuple, desc, 1, &isnull);
                    if (isnull) continue;
                    bytea *parent_b = DatumGetByteaPP(parent_d);
                    if (VARSIZE_ANY_EXHDR(parent_b) != sizeof(hash128_t)) continue;
                    Datum trajectory_d = SPI_getbinval(tuple, desc, 2, &isnull);
                    if (isnull) continue;
                    hash128_t parent;
                    memcpy(&parent, VARDATA_ANY(parent_b), sizeof(parent));
                    append_manifest_edges(&parent, DatumGetByteaPP(trajectory_d),
                                          &edges, &next, visited, owner);
                }
                SPI_freetuptable(SPI_tuptable);
            }
            pfree(rem_ids);
        }
        hash_destroy(resolved);
        pfree(arg_array);
        pfree(ids);
        IdVec swap = frontier;
        frontier = next;
        next = swap;
    }
    if (spi_connected)
        laplace_spi_finish(spi_top);

    if (edges.count > 1)
        qsort(edges.items, edges.count, sizeof(ClosureEdge), edge_compare);
    for (size_t i = 0; i < edges.count; ++i) {
        Datum values[5];
        bool nulls[5] = {false, false, false, false, false};
        values[0] = hash128_to_datum(&edges.items[i].parent);
        values[1] = Int32GetDatum(edges.items[i].ordinal);
        values[2] = hash128_to_datum(&edges.items[i].child);
        values[3] = Int32GetDatum(edges.items[i].run_length);
        values[4] = Int64GetDatum(edges.items[i].flags);
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
    }
    return (Datum) 0;
}
