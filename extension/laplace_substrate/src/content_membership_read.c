/* Reads the occurrence face of trajectories: which entities' typed trajectories
 * contain given constituents. Each leaf partition's GIN index over the
 * trajectory's constituent-id projection is scanned as a bitmap under the active
 * snapshot; no planner, SPI, or cursor. */
#include "postgres.h"
#include "access/genam.h"
#include "access/relscan.h"
#include "access/table.h"
#include "access/tableam.h"
#include "catalog/index.h"
#include "catalog/namespace.h"
#include "catalog/pg_inherits.h"
#include "catalog/pg_am_d.h"
#include "catalog/pg_type.h"
#include "commands/defrem.h"
#include "executor/tuptable.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "nodes/tidbitmap.h"
#include "parser/parse_func.h"
#include "parser/parse_oper.h"
#include "partitioning/partdesc.h"
#include "utils/acl.h"
#include "utils/builtins.h"
#include "utils/fmgroids.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/rel.h"
#include "utils/rls.h"
#include "utils/snapmgr.h"
#include "content_membership_read.h"
#include "public_direct.h"
#include "spi_common.h"
#include "laplace_pg/executor.h"

PG_FUNCTION_INFO_V1(pg_laplace_native_membership_any);
PG_FUNCTION_INFO_V1(pg_laplace_native_membership_all);

typedef struct
{
    hash128_t entity;
    Datum     path;
} NativeMemberItem;

typedef struct
{
    MemoryContext     caller;
    NativeMemberItem *items;
    uint64            count;
    uint64            cap;
} NativeMemberCollect;

static int
native_member_cmp(const void *a, const void *b)
{
    const NativeMemberItem *x = (const NativeMemberItem *) a;
    const NativeMemberItem *y = (const NativeMemberItem *) b;
    return memcmp(&x->entity, &y->entity, sizeof(hash128_t));
}

static bool
visit_native_member(TupleTableSlot *slot, void *opaque, AttrNumber ent_col, AttrNumber path_col)
{
    NativeMemberCollect *col = (NativeMemberCollect *) opaque;
    bool                 isnull = false;
    Datum                d_ent, d_path, path_cp;
    hash128_t            ent;
    MemoryContext        prev;

    d_ent = slot_getattr(slot, ent_col, &isnull);
    if (isnull)
        elog(ERROR, "Native membership identity is null");
    ent = laplace_pub_blake3_to_hash128(d_ent);

    d_path = slot_getattr(slot, path_col, &isnull);
    if (isnull)
        elog(ERROR, "Native membership path is null");

    prev = MemoryContextSwitchTo(col->caller);
    path_cp = PointerGetDatum(PG_DETOAST_DATUM_COPY(d_path));
    if (col->count >= col->cap)
    {
        Size sz;
        col->cap = col->cap ? col->cap * 2 : 64;
        sz = sizeof(NativeMemberItem) * (Size) col->cap;
        col->items = col->items
            ? (NativeMemberItem *) repalloc(col->items, sz)
            : (NativeMemberItem *) palloc(sz);
    }
    col->items[col->count].entity = ent;
    col->items[col->count].path = path_cp;
    col->count++;
    MemoryContextSwitchTo(prev);
    return true;
}

static bool
visit_holder_member(TupleTableSlot *slot, void *opaque)
{
    return visit_native_member(slot, opaque, 2, 3);
}

static bool
visit_container_member(TupleTableSlot *slot, void *opaque)
{
    return visit_native_member(slot, opaque, 1, 2);
}

static int
cm_cmp_hash128(const void *a, const void *b)
{
    return memcmp(a, b, sizeof(hash128_t));
}

static int
cm_dedup_bytea_ids(ArrayType *arr, hash128_t *dst)
{
    Datum *vals;
    bool  *nulls;
    int    n = 0, out = 0;

    if (arr == NULL)
        return 0;
    deconstruct_array(arr, BYTEAOID, -1, false, TYPALIGN_INT, &vals, &nulls, &n);
    if (n <= 0)
        return 0;
    for (int i = 0; i < n; i++)
    {
        if (!nulls[i])
            dst[out++] = datum_to_hash128(vals[i]);
    }
    pfree(vals);
    pfree(nulls);
    if (out > 1)
    {
        int uniq = 1;
        qsort(dst, (Size) out, sizeof(hash128_t), cm_cmp_hash128);
        for (int i = 1; i < out; i++)
        {
            if (memcmp(&dst[uniq - 1], &dst[i], sizeof(hash128_t)) != 0)
                dst[uniq++] = dst[i];
        }
        return uniq;
    }
    return out;
}

static NativeMemberItem *
collect_native_membership(ArrayType *members, bool require_all, ArrayType *required,
                          uint64 *out_count)
{
    MemoryContext       caller = CurrentMemoryContext;
    MemoryContext       work, prev;
    Oid                 b3_arr_oid = laplace_pub_blake3_array_oid();
    int                 n_mem_raw = members ? ArrayGetNItems(ARR_NDIM(members), ARR_DIMS(members)) : 0;
    int                 n_req_raw = required ? ArrayGetNItems(ARR_NDIM(required), ARR_DIMS(required)) : 0;
    hash128_t          *mem_ids = NULL, *req_ids = NULL;
    int                 n_mem = 0, n_req = 0;
    NativeMemberCollect col;
    NativeMemberItem   *res = NULL;

    *out_count = 0;
    if (members == NULL)
        return NULL;

    work = AllocSetContextCreate(caller, "native_membership", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    if (n_mem_raw > 0)
    {
        mem_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_mem_raw);
        n_mem = cm_dedup_bytea_ids(members, mem_ids);
    }
    if (n_req_raw > 0)
    {
        req_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_req_raw);
        n_req = cm_dedup_bytea_ids(required, req_ids);
    }

    MemSet(&col, 0, sizeof(col));
    col.caller = work;

    if (require_all)
    {
        hash128_t *comb = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (n_mem + n_req + 1));
        ArrayType *comb_arr;
        ArrayType *empty_i2 = construct_empty_array(INT2OID);
        Oid        types[2] = { b3_arr_oid, INT2ARRAYOID };
        Datum      args[2];
        bool       nulls[2] = { false, false };

        if (n_mem > 0)
            memcpy(comb, mem_ids, sizeof(hash128_t) * (Size) n_mem);
        if (n_req > 0)
            memcpy(comb + n_mem, req_ids, sizeof(hash128_t) * (Size) n_req);
        comb_arr = laplace_pub_blake3_array(comb, n_mem + n_req);
        args[0] = PointerGetDatum(comb_arr);
        args[1] = PointerGetDatum(empty_i2);
        (void) laplace_pub_call_srf("public", "laplace_containers", 2,
                                    types, args, nulls,
                                    visit_container_member, &col);
    }
    else
    {
        ArrayType *mem_arr = laplace_pub_blake3_array(mem_ids, n_mem);
        ArrayType *req_arr = laplace_pub_blake3_array(req_ids, n_req);
        Oid        types[6] = { b3_arr_oid, b3_arr_oid, BOOLOID, INT2OID, INT8OID, INT2OID };
        Datum      args[6] = {
            PointerGetDatum(mem_arr),
            PointerGetDatum(req_arr),
            BoolGetDatum(false),
            Int16GetDatum(-1),
            Int64GetDatum(-1),
            Int16GetDatum(3)
        };
        bool       nulls[6] = { false, false, false, false, false, false };

        (void) laplace_pub_call_srf("public", "laplace_holders", 6,
                                    types, args, nulls,
                                    visit_holder_member, &col);
    }

    if (col.count > 1)
        qsort(col.items, (Size) col.count, sizeof(NativeMemberItem), native_member_cmp);

    if (col.count > 0)
    {
        MemoryContextSwitchTo(caller);
        res = (NativeMemberItem *) MemoryContextAlloc(
            caller, sizeof(NativeMemberItem) * (Size) col.count);
        for (uint64 i = 0; i < col.count; i++)
        {
            res[i].entity = col.items[i].entity;
            res[i].path = PointerGetDatum(PG_DETOAST_DATUM_COPY(col.items[i].path));
        }
        *out_count = col.count;
    }

    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return res;
}

static bool
native_membership(ArrayType *members, bool require_all, ArrayType *required,
                  uint64 maximum, LaplaceContentMembershipConsumer consume,
                  void *context, uint64 *matched)
{
    uint64            total = 0;
    NativeMemberItem *items;
    bool              complete = true;
    uint64            emitted = 0;

    if (maximum > PG_INT64_MAX - 1)
        elog(ERROR, "membership boundary exceeds SQL capacity");

    items = collect_native_membership(members, require_all, required, &total);
    for (uint64 i = 0; i < total; i++)
    {
        Datum id_d;
        if (maximum && emitted >= maximum)
        {
            complete = false;
            break;
        }
        id_d = hash128_to_datum(&items[i].entity);
        consume(id_d, id_d, items[i].path, context);
        pfree(DatumGetPointer(id_d));
        emitted++;
    }
    if (items != NULL)
    {
        for (uint64 i = 0; i < total; i++)
            pfree(DatumGetPointer(items[i].path));
        pfree(items);
    }
    *matched = emitted;
    return complete;
}

static Datum
native_membership_sql(PG_FUNCTION_ARGS, bool require_all)
{
    ReturnSetInfo    *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType        *members;
    ArrayType        *required;
    int64             limit = 0;
    NativeMemberItem *items;
    uint64            total = 0;
    uint64            emit_cap;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(2))
        return (Datum) 0;
    members = PG_GETARG_ARRAYTYPE_P(0);
    required = PG_ARGISNULL(1) ? NULL : PG_GETARG_ARRAYTYPE_P(1);
    limit = PG_GETARG_INT64(2);
    if (limit < 0)
        limit = 0;

    items = collect_native_membership(members, require_all, required, &total);
    emit_cap = (limit == 0) ? total : Min(total, (uint64) limit + 1);
    for (uint64 i = 0; i < emit_cap; i++)
    {
        Datum values[2] = {
            hash128_to_datum(&items[i].entity),
            items[i].path
        };
        bool  nulls[2] = { false, false };

        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        pfree(DatumGetPointer(values[0]));
    }
    if (items != NULL)
    {
        for (uint64 i = 0; i < total; i++)
            pfree(DatumGetPointer(items[i].path));
        pfree(items);
    }
    return (Datum) 0;
}

Datum
pg_laplace_native_membership_any(PG_FUNCTION_ARGS)
{
    return native_membership_sql(fcinfo, false);
}

Datum
pg_laplace_native_membership_all(PG_FUNCTION_ARGS)
{
    return native_membership_sql(fcinfo, true);
}

static bool
is_column(Node *node, AttrNumber column)
{
    return IsA(node, Var) && ((Var *)node)->varattno == column &&
        ((Var *)node)->varno == 1 && ((Var *)node)->varlevelsup == 0;
}

/* A similarly named index with a different predicate can silently omit rows.
 * Validate the canonical expression and requested type predicate, not its name. */
static void
validate_membership_index(Relation index, AttrNumber trajectory, AttrNumber type,
               Oid projection, Oid family, int16 physicality_type)
{
    List *expressions = RelationGetIndexExpressions(index);
    List *predicate = RelationGetIndexPredicate(index);
    bool kind = false, present = false;
    if (index->rd_rel->relam != GIN_AM_OID || !index->rd_index->indisvalid ||
        !index->rd_index->indisready || index->rd_index->indnkeyatts != 1 ||
        index->rd_index->indkey.values[0] != 0 || index->rd_opfamily[0] != family ||
        list_length(expressions) != 1 || !IsA(linitial(expressions), FuncExpr))
        elog(ERROR, "content membership requires the valid canonical GIN index");
    FuncExpr *expression = linitial_node(FuncExpr, expressions);
    if (expression->funcid != projection || list_length(expression->args) != 1 ||
        !is_column(linitial(expression->args), trajectory) || list_length(predicate) != 2)
        elog(ERROR, "content membership requires the canonical typed expression and predicate");
    ListCell *cell;
    foreach(cell, predicate)
    {
        Node *node = lfirst(cell);
        if (IsA(node, NullTest))
        {
            NullTest *test = (NullTest *)node;
            present = test->nulltesttype == IS_NOT_NULL && !test->argisrow &&
                is_column((Node *)test->arg, trajectory);
        }
        else if (IsA(node, OpExpr))
        {
            OpExpr *op = (OpExpr *)node;
            if (list_length(op->args) == 2 && is_column(linitial(op->args), type) &&
                IsA(lsecond(op->args), Const))
            {
                Const *value = lsecond_node(Const, op->args);
                Oid function = get_opcode(op->opno);
                kind = !value->constisnull &&
                    ((function == F_INT24EQ && value->consttype == INT4OID &&
                      DatumGetInt32(value->constvalue) == physicality_type) ||
                     (function == F_INT2EQ && value->consttype == INT2OID &&
                      DatumGetInt16(value->constvalue) == physicality_type));
            }
        }
    }
    if (!kind || !present)
        elog(ERROR, "content membership requires the exact requested physicality index predicate");
}

static bool
read_leaf(Relation relation, Oid index_oid, ArrayType *members, ArrayType *required_members,
          Oid projection, Oid family, Oid operation, StrategyNumber strategy,
          Oid required_operation, StrategyNumber required_strategy,
          int16 physicality_type, uint64 max_rows, uint64 *matched_rows,
          LaplaceContentMembershipConsumer consume, void *context)
{
    Oid oid = RelationGetRelid(relation);
    AttrNumber id = get_attnum(oid, "id"), entity = get_attnum(oid, "entity_id"),
        type = get_attnum(oid, "type"), trajectory = get_attnum(oid, "trajectory");
    if (id <= 0 || entity <= 0 || type <= 0 || trajectory <= 0 || !OidIsValid(index_oid))
        elog(ERROR, "content membership requires indexed physicality storage");
    Relation index = index_open(index_oid, AccessShareLock);
    validate_membership_index(index, trajectory, type, projection, family, physicality_type);
    ScanKeyData keys[2];
    int key_count = required_members ? 2 : 1;
    ScanKeyEntryInitialize(&keys[0], 0, 1, strategy, ANYARRAYOID, InvalidOid,
                           operation, PointerGetDatum(members));
    if (required_members)
        ScanKeyEntryInitialize(&keys[1], 0, 1, required_strategy, ANYARRAYOID, InvalidOid,
                               required_operation, PointerGetDatum(required_members));
    IndexScanDesc index_scan = index_beginscan_bitmap(index, GetActiveSnapshot(), NULL, key_count);
    index_rescan(index_scan, keys, key_count, NULL, 0);
    TIDBitmap *bitmap = tbm_create((Size)work_mem * 1024, NULL);
    index_getbitmap(index_scan, bitmap);
    index_endscan(index_scan);
    TableScanDesc scan = table_beginscan_bm(relation, GetActiveSnapshot(), 0, NULL);
    scan->st.rs_tbmiterator = tbm_begin_iterate(bitmap, NULL, InvalidDsaPointer);
    TupleTableSlot *slot = table_slot_create(relation, NULL);
    MemoryContext owner = CurrentMemoryContext;
    MemoryContext row = AllocSetContextCreate(owner, "content membership row", ALLOCSET_SMALL_SIZES);
    bool recheck;
    bool complete = true;
    uint64 lossy = 0, exact = 0;
    while (table_scan_bitmap_next_tuple(scan, slot, &recheck, &lossy, &exact))
    {
        CHECK_FOR_INTERRUPTS();
        MemoryContextSwitchTo(row);
        bool isnull;
        Datum kind = slot_getattr(slot, type, &isnull);
        if (!isnull && DatumGetInt16(kind) == physicality_type)
        {
            Datum geometry = slot_getattr(slot, trajectory, &isnull);
            bool matches = !isnull;
            if (matches && recheck)
            {
                Datum ids = OidFunctionCall1(projection, geometry);
                matches = DatumGetBool(OidFunctionCall2(operation, ids, PointerGetDatum(members))) &&
                    (!required_members || DatumGetBool(OidFunctionCall2(required_operation,
                        ids, PointerGetDatum(required_members))));
            }
            if (matches)
            {
                if (max_rows > 0 && *matched_rows >= max_rows)
                {
                    complete = false;
                    MemoryContextSwitchTo(owner);
                    break;
                }
                Datum physicality = slot_getattr(slot, id, &isnull);
                if (isnull) elog(ERROR, "content membership physicality lacks identity");
                Datum root = slot_getattr(slot, entity, &isnull);
                if (isnull) elog(ERROR, "content membership physicality lacks entity");
                MemoryContextSwitchTo(owner);
                consume(physicality, root, geometry, context);
                ++*matched_rows;
            }
        }
        MemoryContextSwitchTo(owner);
        ExecClearTuple(slot);
        MemoryContextReset(row);
    }
    ExecClearTuple(slot);
    MemoryContextDelete(row);
    tbm_end_iterate(&scan->st.rs_tbmiterator);
    table_endscan(scan);
    tbm_free(bitmap);
    ExecDropSingleTupleTableSlot(slot);
    index_close(index, NoLock);
    return complete;
}

bool
laplace_typed_membership_read(ArrayType *members, bool require_all,
    int16 physicality_type, uint64 max_rows,
    LaplaceContentMembershipConsumer consume, void *context)
{
    return laplace_typed_membership_read_with_required(members, require_all, NULL,
        physicality_type, max_rows, consume, context);
}

bool
laplace_typed_membership_read_with_required(ArrayType *members, bool require_all,
    ArrayType *required_members, int16 physicality_type, uint64 max_rows,
    LaplaceContentMembershipConsumer consume, void *context)
{
    const char *index_name;
    switch (physicality_type)
    {
        case 1: index_name = "physicalities_constituents_gin"; break;
        case 3: index_name = "physicalities_projection_constituents_gin"; break;
        case 5: index_name = "physicalities_set_constituents_gin"; break;
        case 8: index_name = "physicalities_parse_constituents_gin"; break;
        default: elog(ERROR, "membership read has no index for physicality type %d", physicality_type);
                 pg_unreachable();
    }
    Oid schema = get_namespace_oid("laplace", false);
    Oid root = get_relname_relid("physicalities", schema);
    if (!OidIsValid(root)) elog(ERROR, "laplace.physicalities does not exist");
    AclResult acl = pg_class_aclcheck(root, GetUserId(), ACL_SELECT);
    if (acl != ACLCHECK_OK) aclcheck_error(acl, OBJECT_TABLE, "laplace.physicalities");
    if (check_enable_rls(root, InvalidOid, true) == RLS_ENABLED)
        elog(ERROR, "native content membership read cannot bypass row security");
    if (ARR_NDIM(members) > 1 || ARR_ELEMTYPE(members) != BYTEAOID)
        elog(ERROR, "content membership requires a 1-D bytea array");
    Datum *values; bool *nulls; int count; bool has_null = false;
    deconstruct_array(members, BYTEAOID, -1, false, TYPALIGN_INT, &values, &nulls, &count);
    for (int i = 0; i < count; ++i)
    {
        if (nulls[i]) { has_null = true; continue; }
        if (VARSIZE_ANY_EXHDR(DatumGetByteaPP(values[i])) != sizeof(hash128_t))
            elog(ERROR, "content membership requires 16-byte identities");
    }
    pfree(values); pfree(nulls);
    if (count == 0 || (require_all && has_null)) return true;
    if (required_members)
    {
        if (ARR_NDIM(required_members) > 1 || ARR_ELEMTYPE(required_members) != BYTEAOID)
            elog(ERROR, "required content membership requires a 1-D bytea array");
        has_null = false;
        deconstruct_array(required_members, BYTEAOID, -1, false, TYPALIGN_INT,
                          &values, &nulls, &count);
        for (int i = 0; i < count; ++i)
        {
            if (nulls[i]) { has_null = true; continue; }
            if (VARSIZE_ANY_EXHDR(DatumGetByteaPP(values[i])) != sizeof(hash128_t))
                elog(ERROR, "required content membership requires 16-byte identities");
        }
        pfree(values); pfree(nulls);
        if (has_null) return true;
        if (count == 0) required_members = NULL;
    }
    uint64 native_rows = 0;
    if (physicality_type == 1 && !native_membership(members, require_all, required_members,
            max_rows, consume, context, &native_rows)) return false;
    Relation relation = table_open(root, AccessShareLock);
    if (relation->rd_rel->relkind != RELKIND_PARTITIONED_TABLE)
        elog(ERROR, "content membership requires partitioned physicalities");
    Oid parent_oid = get_relname_relid(index_name, schema);
    if (!OidIsValid(parent_oid)) elog(ERROR, "content membership GIN index is absent");
    Relation parent = index_open(parent_oid, AccessShareLock);
    if (parent->rd_index->indrelid != root)
        elog(ERROR, "content membership index belongs to a different relation");
    AttrNumber trajectory = get_attnum(root, "trajectory");
    Oid geometry = get_atttype(root, trajectory);
    Oid projection = LookupFuncName(list_make2(makeString("public"),
        makeString("laplace_trajectory_constituent_ids")), 1, &geometry, false);
    Oid family = get_opfamily_oid(GIN_AM_OID,
        list_make2(makeString("pg_catalog"), makeString("array_ops")), false);
    Oid operator = LookupOperName(NULL, list_make2(makeString("pg_catalog"),
        makeString(require_all ? "@>" : "&&")), ANYARRAYOID, ANYARRAYOID, false, -1);
    Oid operation = get_opcode(operator);
    StrategyNumber strategy = get_op_opfamily_strategy(operator, family);
    if (!strategy || operation != (require_all ? F_ARRAYCONTAINS : F_ARRAYOVERLAP))
        elog(ERROR, "content membership requires canonical array operators");
    Oid required_operation = InvalidOid;
    StrategyNumber required_strategy = 0;
    if (required_members)
    {
        Oid required_operator = LookupOperName(NULL, list_make2(makeString("pg_catalog"),
            makeString("@>")), ANYARRAYOID, ANYARRAYOID, false, -1);
        required_operation = get_opcode(required_operator);
        required_strategy = get_op_opfamily_strategy(required_operator, family);
        if (!required_strategy || required_operation != F_ARRAYCONTAINS)
            elog(ERROR, "required content membership requires canonical array containment");
    }
    validate_membership_index(parent, trajectory, get_attnum(root, "type"), projection, family,
                              physicality_type);
    PartitionDesc partitions = RelationGetPartitionDesc(relation, true);
    HASHCTL ctl = {0};
    ctl.keysize = ctl.entrysize = sizeof(Oid);
    HTAB *leaves = hash_create("content membership partitions", Max(partitions->nparts, 1),
                              &ctl, HASH_ELEM | HASH_BLOBS);
    for (int i = 0; i < partitions->nparts; ++i)
        hash_search(leaves, &partitions->oids[i], HASH_ENTER, NULL);
    /* Leaf indexes come from the parent index's inheritance children in one
     * read; index_get_partition() would scan every index of every leaf. */
    List *indexes = find_inheritance_children(parent_oid, AccessShareLock);
    ListCell *cell;
    uint64 matched_rows = native_rows;
    bool complete = true;
    foreach(cell, indexes)
    {
        CHECK_FOR_INTERRUPTS();
        Oid index_oid = lfirst_oid(cell);
        Oid leaf_oid = IndexGetRelation(index_oid, false);
        bool found;
        hash_search(leaves, &leaf_oid, HASH_REMOVE, &found);
        if (!found) elog(ERROR, "content membership index partition does not match storage");
        Relation leaf = table_open(leaf_oid, AccessShareLock);
        if (leaf->rd_rel->relkind != RELKIND_RELATION)
            elog(ERROR, "content membership requires physicality leaf partitions");
        if (complete)
            complete = read_leaf(leaf, index_oid, members, required_members, projection,
                family, operation, strategy, required_operation, required_strategy,
                physicality_type, max_rows, &matched_rows,
                consume, context);
        table_close(leaf, NoLock);
    }
    if (hash_get_num_entries(leaves) != 0)
        elog(ERROR, "content membership index does not cover every physicality partition");
    hash_destroy(leaves);
    list_free(indexes);
    index_close(parent, NoLock);
    table_close(relation, NoLock);
    return complete;
}

void
laplace_content_membership_read(ArrayType *members, bool require_all,
    LaplaceContentMembershipConsumer consume, void *context)
{
    (void) laplace_typed_membership_read(members, require_all, 1, 0, consume, context);
}

static void
collect_entity(Datum physicality, Datum entity, Datum geometry, void *context)
{
    hash128_t id = datum_to_hash128(entity);
    (void)physicality; (void)geometry;
    hash_search(context, &id, HASH_ENTER, NULL);
}

static int
compare_id(const void *left, const void *right)
{
    return memcmp(left, right, sizeof(hash128_t));
}

hash128_t *
laplace_content_membership_entities_bounded(ArrayType *members, bool require_all,
                                           uint64 max_rows, int *count, bool *truncated)
{
    HASHCTL ctl = {0};
    ctl.keysize = ctl.entrysize = sizeof(hash128_t);
    HTAB *ids = hash_create("containing identities", 256, &ctl, HASH_ELEM | HASH_BLOBS);
    bool complete = laplace_typed_membership_read(members, require_all, 1, max_rows, collect_entity, ids);
    if (truncated != NULL) *truncated = !complete;
    long size = hash_get_num_entries(ids);
    if (size > INT_MAX || (Size)size > MaxAllocSize / sizeof(hash128_t))
        elog(ERROR, "content membership identities exceed allocation capacity");
    hash128_t *result = palloc(Max(size, 1) * sizeof(hash128_t));
    HASH_SEQ_STATUS scan; hash128_t *id;
    *count = 0;
    hash_seq_init(&scan, ids);
    while ((id = hash_seq_search(&scan))) result[(*count)++] = *id;
    hash_destroy(ids);
    qsort(result, *count, sizeof(hash128_t), compare_id);
    return result;
}

hash128_t *
laplace_content_membership_entities(ArrayType *members, bool require_all, int *count)
{
    return laplace_content_membership_entities_bounded(members, require_all, 0, count, NULL);
}

