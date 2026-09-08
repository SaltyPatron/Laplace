/* Native Content GIN set reader. PostgreSQL owns index consistency, bitmap
 * memory limits and MVCC; C owns consumption. No planner, SPI or SQL cursor. */
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
#include "spi_common.h"

static bool
is_column(Node *node, AttrNumber column)
{
    return IsA(node, Var) && ((Var *)node)->varattno == column &&
        ((Var *)node)->varno == 1 && ((Var *)node)->varlevelsup == 0;
}

/* A similarly named index with a different predicate can silently omit rows.
 * Validate the actual canonical expression and Content predicate, not its name. */
static void
validate_membership_index(Relation index, AttrNumber trajectory, AttrNumber type,
               Oid projection, Oid family)
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
        elog(ERROR, "content membership requires the canonical Content expression and predicate");
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
                      DatumGetInt32(value->constvalue) == 1) ||
                     (function == F_INT2EQ && value->consttype == INT2OID &&
                      DatumGetInt16(value->constvalue) == 1));
            }
        }
    }
    if (!kind || !present)
        elog(ERROR, "content membership requires the exact Content index predicate");
}

static void
read_leaf(Relation relation, Oid index_oid, ArrayType *members, Oid projection,
          Oid family, Oid operation, StrategyNumber strategy,
          LaplaceContentMembershipConsumer consume, void *context)
{
    Oid oid = RelationGetRelid(relation);
    AttrNumber id = get_attnum(oid, "id"), entity = get_attnum(oid, "entity_id"),
        type = get_attnum(oid, "type"), trajectory = get_attnum(oid, "trajectory");
    if (id <= 0 || entity <= 0 || type <= 0 || trajectory <= 0 || !OidIsValid(index_oid))
        elog(ERROR, "content membership requires indexed physicality storage");
    Relation index = index_open(index_oid, AccessShareLock);
    validate_membership_index(index, trajectory, type, projection, family);
    ScanKeyData key;
    ScanKeyEntryInitialize(&key, 0, 1, strategy, ANYARRAYOID, InvalidOid,
                           operation, PointerGetDatum(members));
    IndexScanDesc index_scan = index_beginscan_bitmap(index, GetActiveSnapshot(), NULL, 1);
    index_rescan(index_scan, &key, 1, NULL, 0);
    TIDBitmap *bitmap = tbm_create((Size)work_mem * 1024, NULL);
    index_getbitmap(index_scan, bitmap);
    index_endscan(index_scan);
    TableScanDesc scan = table_beginscan_bm(relation, GetActiveSnapshot(), 0, NULL);
    scan->st.rs_tbmiterator = tbm_begin_iterate(bitmap, NULL, InvalidDsaPointer);
    TupleTableSlot *slot = table_slot_create(relation, NULL);
    MemoryContext owner = CurrentMemoryContext;
    MemoryContext row = AllocSetContextCreate(owner, "content membership row", ALLOCSET_SMALL_SIZES);
    bool recheck;
    uint64 lossy = 0, exact = 0;
    while (table_scan_bitmap_next_tuple(scan, slot, &recheck, &lossy, &exact))
    {
        CHECK_FOR_INTERRUPTS();
        MemoryContextSwitchTo(row);
        bool isnull;
        Datum kind = slot_getattr(slot, type, &isnull);
        if (!isnull && DatumGetInt16(kind) == 1)
        {
            Datum geometry = slot_getattr(slot, trajectory, &isnull);
            if (!isnull && (!recheck || DatumGetBool(OidFunctionCall2(operation,
                    OidFunctionCall1(projection, geometry), PointerGetDatum(members)))))
            {
                Datum physicality = slot_getattr(slot, id, &isnull);
                if (isnull) elog(ERROR, "content membership physicality lacks identity");
                Datum root = slot_getattr(slot, entity, &isnull);
                if (isnull) elog(ERROR, "content membership physicality lacks entity");
                MemoryContextSwitchTo(owner);
                consume(physicality, root, geometry, context);
            }
        }
        MemoryContextSwitchTo(owner);
        ExecClearTuple(slot);
        MemoryContextReset(row);
    }
    MemoryContextDelete(row);
    tbm_end_iterate(&scan->st.rs_tbmiterator);
    table_endscan(scan);
    tbm_free(bitmap);
    ExecDropSingleTupleTableSlot(slot);
    index_close(index, NoLock);
}

void
laplace_content_membership_read(ArrayType *members, bool require_all,
    LaplaceContentMembershipConsumer consume, void *context)
{
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
    if (count == 0 || (require_all && has_null)) return;
    Relation relation = table_open(root, AccessShareLock);
    if (relation->rd_rel->relkind != RELKIND_PARTITIONED_TABLE)
        elog(ERROR, "content membership requires partitioned physicalities");
    Oid parent_oid = get_relname_relid("physicalities_constituents_gin", schema);
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
    validate_membership_index(parent, trajectory, get_attnum(root, "type"), projection, family);
    PartitionDesc partitions = RelationGetPartitionDesc(relation, true);
    HASHCTL ctl = {0};
    ctl.keysize = ctl.entrysize = sizeof(Oid);
    HTAB *leaves = hash_create("content membership partitions", Max(partitions->nparts, 1),
                              &ctl, HASH_ELEM | HASH_BLOBS);
    for (int i = 0; i < partitions->nparts; ++i)
        hash_search(leaves, &partitions->oids[i], HASH_ENTER, NULL);
    /* Read the index's children once. index_get_partition() walks every index
     * on every leaf and repeatedly probes pg_inherits, recreating catalog I/O. */
    List *indexes = find_inheritance_children(parent_oid, AccessShareLock);
    ListCell *cell;
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
        read_leaf(leaf, index_oid, members, projection,
                  family, operation, strategy, consume, context);
        table_close(leaf, NoLock);
    }
    if (hash_get_num_entries(leaves) != 0)
        elog(ERROR, "content membership index does not cover every physicality partition");
    hash_destroy(leaves);
    list_free(indexes);
    index_close(parent, NoLock);
    table_close(relation, NoLock);
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
laplace_content_membership_entities(ArrayType *members, bool require_all, int *count)
{
    HASHCTL ctl = {0};
    ctl.keysize = ctl.entrysize = sizeof(hash128_t);
    HTAB *ids = hash_create("containing identities", 256, &ctl, HASH_ELEM | HASH_BLOBS);
    laplace_content_membership_read(members, require_all, collect_entity, ids);
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

PG_FUNCTION_INFO_V1(pg_laplace_containers_containing_all);
Datum
pg_laplace_containers_containing_all(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo, MAT_SRF_USE_EXPECTED_DESC);
    ReturnSetInfo *result = (ReturnSetInfo *)fcinfo->resultinfo;
    if (PG_ARGISNULL(0)) return (Datum)0;
    int count;
    hash128_t *ids = laplace_content_membership_entities(PG_GETARG_ARRAYTYPE_P(0), true, &count);
    for (int i = 0; i < count; ++i)
    {
        Datum value = hash128_to_datum(&ids[i]);
        bool isnull = false;
        tuplestore_putvalues(result->setResult, result->setDesc, &value, &isnull);
        pfree(DatumGetPointer(value));
    }
    pfree(ids);
    return (Datum)0;
}
