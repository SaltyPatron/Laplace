/* Native batch access to PostgreSQL's canonical consensus storage. Partition
 * routing and btree array scans use PostgreSQL access methods directly. */
#include "postgres.h"

#include "access/genam.h"
#include "access/nbtree.h"
#include "access/table.h"
#include "access/tableam.h"
#include "catalog/namespace.h"
#include "catalog/pg_am_d.h"
#include "catalog/pg_opfamily_d.h"
#include "catalog/pg_type.h"
#include "executor/tuptable.h"
#include "miscadmin.h"
#include "nodes/primnodes.h"
#include "partitioning/partbounds.h"
#include "partitioning/partdesc.h"
#include "utils/acl.h"
#include "utils/builtins.h"
#include "utils/fmgroids.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/partcache.h"
#include "utils/rel.h"
#include "utils/rls.h"
#include "utils/snapmgr.h"

#include "consensus_scan.h"

typedef struct ScanId { char bytes[16]; } ScanId;
typedef struct ScanSet
{
    ArrayType *array;
    Datum *values;
    int count;
    HTAB *ids;
} ScanSet;

static void
scan_set_init(ScanSet *set, ArrayType *array)
{
    HASHCTL ctl = {0};
    Datum *values;
    bool *nulls;
    int count;

    memset(set, 0, sizeof(*set));
    if (array == NULL) return;
    if (ARR_ELEMTYPE(array) != BYTEAOID || ARR_NDIM(array) > 1)
        ereport(ERROR, (errmsg("consensus scan requires one-dimensional identity arrays")));
    ctl.keysize = sizeof(ScanId);
    ctl.entrysize = sizeof(ScanId);
    ctl.hcxt = CurrentMemoryContext;
    set->ids = hash_create("consensus scan identities", 64, &ctl,
                          HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    deconstruct_array(array, BYTEAOID, -1, false, TYPALIGN_INT,
                      &values, &nulls, &count);
    set->values = palloc(sizeof(Datum) * Max(count, 1));
    for (int i = 0; i < count; ++i)
    {
        bytea *id;
        bool found;
        if (nulls[i]) continue;
        id = DatumGetByteaPP(values[i]);
        if (VARSIZE_ANY_EXHDR(id) != 16)
            ereport(ERROR, (errmsg("consensus scan identities must contain exactly 16 bytes")));
        hash_search(set->ids, VARDATA_ANY(id), HASH_ENTER, &found);
        if (!found) set->values[set->count++] = PointerGetDatum(id);
    }
    set->array = set->count == 0 ? construct_empty_array(BYTEAOID) :
        construct_array(set->values, set->count, BYTEAOID, -1, false, TYPALIGN_INT);
    pfree(values);
    pfree(nulls);
}

static bool
scan_set_contains(const ScanSet *set, Datum value, bool isnull)
{
    bytea *id;
    if (set->array == NULL) return true;
    if (isnull) return false;
    id = DatumGetByteaPP(value);
    return VARSIZE_ANY_EXHDR(id) == 16 &&
        hash_search(set->ids, VARDATA_ANY(id), HASH_FIND, NULL) != NULL;
}

static void
scan_set_destroy(ScanSet *set)
{
    if (set->ids != NULL) hash_destroy(set->ids);
    if (set->array != NULL) pfree(set->array);
    if (set->values != NULL) pfree(set->values);
}

/* Partition metadata is read afresh for this statement. No cached leaf OID can
 * survive an attach/detach or an extension/schema replacement. */
static List *
scan_children(Relation relation, const ScanSet *subjects, const ScanSet *types)
{
    PartitionKey key = RelationGetPartitionKey(relation);
    PartitionDesc desc = RelationGetPartitionDesc(relation, true);
    const ScanSet *set = NULL;
    bool *selected = palloc0(sizeof(bool) * Max(desc->nparts, 1));
    List *children = NIL;

    if (key != NULL && key->partnatts == 1 && key->partattrs[0] > 0 &&
        key->parttypid[0] == BYTEAOID)
    {
        const char *name = NameStr(TupleDescAttr(RelationGetDescr(relation),
                                               key->partattrs[0] - 1)->attname);
        if (strcmp(name, "subject_id") == 0) set = subjects;
        else if (strcmp(name, "type_id") == 0) set = types;
    }
    if (set != NULL && set->array != NULL &&
        (key->strategy == PARTITION_STRATEGY_LIST ||
         key->strategy == PARTITION_STRATEGY_HASH))
    {
        for (int i = 0; i < set->count; ++i)
        {
            int part;
            if (key->strategy == PARTITION_STRATEGY_LIST)
            {
                bool equal;
                int at = partition_list_bsearch(key->partsupfunc,
                    key->partcollation, desc->boundinfo, set->values[i], &equal);
                part = equal ? desc->boundinfo->indexes[at] :
                               desc->boundinfo->default_index;
            }
            else
            {
                bool isnull = false;
                uint64 hash = compute_partition_hash_value(1, key->partsupfunc,
                    key->partcollation, &set->values[i], &isnull);
                part = desc->boundinfo->indexes[hash % desc->boundinfo->nindexes];
            }
            if (part >= 0 && part < desc->nparts) selected[part] = true;
        }
    }
    else
        memset(selected, true, sizeof(bool) * desc->nparts);

    for (int i = 0; i < desc->nparts; ++i)
        if (selected[i]) children = lappend_oid(children, desc->oids[i]);
    pfree(selected);
    return children;
}

static AttrNumber
scan_attribute(Relation relation, const char *name, Oid expected)
{
    TupleDesc desc = RelationGetDescr(relation);
    for (int i = 0; i < desc->natts; ++i)
    {
        Form_pg_attribute attr = TupleDescAttr(desc, i);
        if (!attr->attisdropped && strcmp(NameStr(attr->attname), name) == 0)
        {
            if (attr->atttypid != expected)
                ereport(ERROR, (errmsg("consensus scan: column %s has an incompatible type", name)));
            return i + 1;
        }
    }
    ereport(ERROR, (errmsg("consensus scan: column %s is missing", name)));
    return InvalidAttrNumber;
}

static bool
scan_index_predicate(Relation index, AttrNumber object, bool object_required)
{
    List *predicate = RelationGetIndexPredicate(index);
    NullTest *test;
    Var *var;
    if (predicate == NIL) return true;
    /* An equality/array probe of object proves object IS NOT NULL. Accept
     * precisely that installed partial-index predicate, never an arbitrary
     * partial index whose omitted rows might satisfy this operation. */
    if (!object_required || list_length(predicate) != 1 ||
        !IsA(linitial(predicate), NullTest)) return false;
    test = linitial_node(NullTest, predicate);
    if (test->nulltesttype != IS_NOT_NULL || test->argisrow ||
        !IsA(test->arg, Var)) return false;
    var = (Var *) test->arg;
    return var->varattno == object && var->vartype == BYTEAOID &&
           var->varlevelsup == 0;
}

static Relation
scan_index(Relation relation, AttrNumber endpoint,
           AttrNumber object, bool object_required,
           AttrNumber type, bool type_required)
{
    List *indexes = RelationGetIndexList(relation);
    ListCell *cell;
    Relation selected = NULL;
    int selected_prefix = 0;
    foreach(cell, indexes)
    {
        Relation index = index_open(lfirst_oid(cell), AccessShareLock);
        if (index->rd_rel->relam == BTREE_AM_OID &&
            index->rd_index->indisvalid && index->rd_index->indisready &&
            index->rd_index->indnkeyatts > 0 &&
            index->rd_index->indkey.values[0] == endpoint &&
            scan_index_predicate(index, object, object_required))
        {
            int prefix = 1;
            /* Push the complete constrained prefix into the B-tree. Steering
             * binds both endpoints; neighborhood reads may bind a relation
             * family. Neither should fetch the excluded heap rows first. */
            while (prefix < index->rd_index->indnkeyatts)
            {
                AttrNumber attr = index->rd_index->indkey.values[prefix];
                if ((attr == object && object_required) ||
                    (attr == type && type_required))
                    prefix++;
                else
                    break;
            }
            if (prefix > selected_prefix)
            {
                if (selected != NULL) index_close(selected, AccessShareLock);
                selected = index;
                selected_prefix = prefix;
                continue;
            }
        }
        index_close(index, AccessShareLock);
    }
    list_free(indexes);
    if (selected == NULL)
        ereport(ERROR, (errmsg("consensus scan: %s lacks its endpoint btree index",
                                RelationGetRelationName(relation))));
    return selected;
}

/* Match the expression rather than an index name: fresh installs and upgraded
 * partition children can name the same ordered access path differently. */
static bool
scan_eff_mu_expression(Node *node, AttrNumber rating, AttrNumber rd)
{
    OpExpr *minus, *times;
    Var *rating_var, *rd_var;
    Const *two;
    Oid multiply;
    if (!IsA(node, OpExpr)) return false;
    minus = (OpExpr *) node;
    if (list_length(minus->args) != 2 || !IsA(linitial(minus->args), Var) ||
        !IsA(lsecond(minus->args), OpExpr)) return false;
    if (get_opcode(minus->opno) != F_INT8MI) return false;
    rating_var = linitial_node(Var, minus->args);
    times = lsecond_node(OpExpr, minus->args);
    if (list_length(times->args) != 2 || !IsA(linitial(times->args), Const) ||
        !IsA(lsecond(times->args), Var)) return false;
    multiply = get_opcode(times->opno);
    if (multiply != F_INT48MUL && multiply != F_INT8MUL) return false;
    two = linitial_node(Const, times->args);
    rd_var = lsecond_node(Var, times->args);
    return !two->constisnull &&
        ((two->consttype == INT4OID && DatumGetInt32(two->constvalue) == 2) ||
         (two->consttype == INT8OID && DatumGetInt64(two->constvalue) == 2)) &&
        rating_var->varattno == rating && rating_var->vartype == INT8OID &&
        rating_var->varlevelsup == 0 && rd_var->varattno == rd &&
        rd_var->vartype == INT8OID && rd_var->varlevelsup == 0;
}

static Relation
scan_rank_index(Relation relation, AttrNumber endpoint, AttrNumber object,
                AttrNumber rating, AttrNumber rd)
{
    List *indexes = RelationGetIndexList(relation);
    ListCell *cell;
    Relation selected = NULL;
    foreach(cell, indexes)
    {
        Relation index = index_open(lfirst_oid(cell), AccessShareLock);
        List *expressions;
        if (index->rd_rel->relam == BTREE_AM_OID &&
            index->rd_index->indisvalid && index->rd_index->indisready &&
            index->rd_index->indnkeyatts >= 2 &&
            index->rd_index->indkey.values[0] == endpoint &&
            index->rd_index->indkey.values[1] == 0 &&
            index->rd_opfamily[0] == BYTEA_BTREE_FAM_OID &&
            index->rd_opfamily[1] == INTEGER_BTREE_FAM_OID &&
            (index->rd_indoption[1] & INDOPTION_DESC) != 0 &&
            scan_index_predicate(index, object, true))
        {
            expressions = RelationGetIndexExpressions(index);
            if (expressions != NIL && scan_eff_mu_expression(linitial(expressions), rating, rd))
            {
                selected = index;
                break;
            }
        }
        index_close(index, AccessShareLock);
    }
    list_free(indexes);
    return selected;
}

static void
scan_leaf(Relation relation, const ScanSet *subjects, const ScanSet *objects,
          const ScanSet *types, LaplaceConsensusConsumer consume, void *context,
          LaplaceConsensusScanStats *stats, LaplaceConsensusCutoff cutoff)
{
    AttrNumber subject = scan_attribute(relation, "subject_id", BYTEAOID);
    AttrNumber object = scan_attribute(relation, "object_id", BYTEAOID);
    AttrNumber type = scan_attribute(relation, "type_id", BYTEAOID);
    AttrNumber rating = scan_attribute(relation, "rating", INT8OID);
    AttrNumber rd = scan_attribute(relation, "rd", INT8OID);
    AttrNumber witnesses = scan_attribute(relation, "witness_count", INT8OID);
    const ScanSet *probe = subjects->array != NULL ? subjects : objects;
    AttrNumber endpoint = subjects->array != NULL ? subject : object;
    Relation index = cutoff != NULL ? scan_rank_index(relation, endpoint, object, rating, rd) : NULL;
    bool ranked = index != NULL;
    if (index == NULL) index = scan_index(relation, endpoint,
                               object, objects->array != NULL,
                               type, types->array != NULL);
    TupleTableSlot *slot = table_slot_create(relation, NULL);
    IndexScanDesc scan;
    ScanKeyData keys[INDEX_MAX_KEYS];
    int nkeys = 1;

    ScanKeyEntryInitialize(&keys[0], SK_SEARCHARRAY, 1, BTEqualStrategyNumber,
        BYTEAOID, InvalidOid, F_BYTEAEQ, PointerGetDatum(probe->array));
    while (!ranked && nkeys < index->rd_index->indnkeyatts)
    {
        AttrNumber attr = index->rd_index->indkey.values[nkeys];
        const ScanSet *set = attr == object ? objects : attr == type ? types : NULL;
        if (set == NULL || set->array == NULL) break;
        ScanKeyEntryInitialize(&keys[nkeys], SK_SEARCHARRAY, nkeys + 1, BTEqualStrategyNumber,
            BYTEAOID, InvalidOid, F_BYTEAEQ, PointerGetDatum(set->array));
        nkeys++;
    }
    scan = index_beginscan(relation, index, GetActiveSnapshot(), NULL, nkeys, 0);
    /* Reuse the open leaf/index. Each endpoint has an independent ordered
     * range: reaching its cutoff cannot skip any other prompt seed. */
    for (int probe_index = 0; probe_index < (ranked ? probe->count : 1); ++probe_index)
    {
        if (ranked)
            ScanKeyEntryInitialize(&keys[0], 0, 1, BTEqualStrategyNumber,
                BYTEAOID, InvalidOid, F_BYTEAEQ, probe->values[probe_index]);
        index_rescan(scan, keys, nkeys, NULL, 0);
        stats->index_scans++;
        while (index_getnext_slot(scan, ForwardScanDirection, slot))
        {
            bool snull, onull, tnull, rnull, dnull, wnull;
            Datum s = slot_getattr(slot, subject, &snull);
            Datum o = slot_getattr(slot, object, &onull);
            Datum t = slot_getattr(slot, type, &tnull);
            LaplaceConsensusRow row;
            stats->rows_read++;
            if ((stats->rows_read & 4095) == 0) CHECK_FOR_INTERRUPTS();
            if (snull || tnull || !scan_set_contains(subjects, s, snull) ||
                !scan_set_contains(objects, o, onull) ||
                !scan_set_contains(types, t, tnull))
            {
                ExecClearTuple(slot);
                continue;
            }
            if (VARSIZE_ANY_EXHDR(DatumGetByteaPP(s)) != 16 ||
                VARSIZE_ANY_EXHDR(DatumGetByteaPP(t)) != 16 ||
                (!onull && VARSIZE_ANY_EXHDR(DatumGetByteaPP(o)) != 16))
                ereport(ERROR, (errmsg("consensus scan encountered a malformed stored identity")));
            memcpy(&row.subject, VARDATA_ANY(DatumGetByteaPP(s)), 16);
            memcpy(&row.type, VARDATA_ANY(DatumGetByteaPP(t)), 16);
            memset(&row.object, 0, sizeof(row.object));
            row.object_is_null = onull;
            if (!onull) memcpy(&row.object, VARDATA_ANY(DatumGetByteaPP(o)), 16);
            row.rating = DatumGetInt64(slot_getattr(slot, rating, &rnull));
            row.rd = DatumGetInt64(slot_getattr(slot, rd, &dnull));
            row.witnesses = DatumGetInt64(slot_getattr(slot, witnesses, &wnull));
            if (rnull || dnull || wnull)
                ereport(ERROR, (errmsg("consensus scan encountered incomplete standing")));
            if (ranked && cutoff(&row, context))
            {
                ExecClearTuple(slot);
                break;
            }
            stats->rows_matched++;
            consume(&row, context);
            ExecClearTuple(slot);
        }
    }
    index_endscan(scan);
    ExecDropSingleTupleTableSlot(slot);
    index_close(index, AccessShareLock);
}

static void
consensus_scan_impl(ArrayType *subject_ids, ArrayType *object_ids,
                    ArrayType *type_ids, bool default_only,
                    LaplaceConsensusConsumer consume,
                    void *context, LaplaceConsensusScanStats *stats,
                    LaplaceConsensusCutoff cutoff)
{
    ScanSet subjects, objects, types;
    LaplaceConsensusScanStats local_stats = {0};
    Oid root;
    AclResult acl;
    List *pending;

    if (subject_ids == NULL && object_ids == NULL)
        ereport(ERROR, (errmsg("consensus scan requires an endpoint identity set")));
    if (stats == NULL) stats = &local_stats;
    scan_set_init(&subjects, subject_ids);
    scan_set_init(&objects, object_ids);
    scan_set_init(&types, type_ids);
    if ((subjects.array != NULL && subjects.count == 0) ||
        (objects.array != NULL && objects.count == 0) ||
        (types.array != NULL && types.count == 0)) goto done;

    root = get_relname_relid("consensus", get_namespace_oid("laplace", false));
    if (!OidIsValid(root))
        ereport(ERROR, (errmsg("laplace.consensus does not exist")));
    acl = pg_class_aclcheck(root, GetUserId(), ACL_SELECT);
    if (acl != ACLCHECK_OK)
        aclcheck_error(acl, OBJECT_TABLE, "laplace.consensus");
    if (check_enable_rls(root, InvalidOid, true) == RLS_ENABLED)
        ereport(ERROR, (errmsg("native consensus scan cannot bypass row security")));

    if (default_only)
    {
        Relation relation = table_open(root, AccessShareLock);
        PartitionDesc desc;
        if (relation->rd_rel->relkind != RELKIND_PARTITIONED_TABLE)
        {
            table_close(relation, NoLock);
            goto done;
        }
        desc = RelationGetPartitionDesc(relation, true);
        root = desc->boundinfo->default_index >= 0 ?
            desc->oids[desc->boundinfo->default_index] : InvalidOid;
        table_close(relation, NoLock);
        if (!OidIsValid(root)) goto done;
    }

    pending = list_make1_oid(root);
    while (pending != NIL)
    {
        Oid oid = linitial_oid(pending);
        Relation relation;
        pending = list_delete_first(pending);
        CHECK_FOR_INTERRUPTS();
        relation = table_open(oid, AccessShareLock);
        if (relation->rd_rel->relkind == RELKIND_PARTITIONED_TABLE)
            pending = list_concat(pending, scan_children(relation, &subjects, &types));
        else
            scan_leaf(relation, &subjects, &objects, &types, consume, context, stats, cutoff);
        /* Retain relation locks through the transaction, as an ordinary SELECT
         * does, so partition topology cannot change beneath this snapshot. */
        table_close(relation, NoLock);
    }
done:
    scan_set_destroy(&subjects);
    scan_set_destroy(&objects);
    scan_set_destroy(&types);
}

void
laplace_consensus_scan(ArrayType *subjects, ArrayType *objects, ArrayType *types,
                       LaplaceConsensusConsumer consume, void *context,
                       LaplaceConsensusScanStats *stats)
{
    consensus_scan_impl(subjects, objects, types, false, consume, context, stats, NULL);
}

void
laplace_consensus_scan_default(ArrayType *subjects, ArrayType *objects,
                               LaplaceConsensusConsumer consume, void *context,
                               LaplaceConsensusScanStats *stats)
{
    consensus_scan_impl(subjects, objects, NULL, true, consume, context, stats, NULL);
}

void
laplace_consensus_scan_ranked(ArrayType *subjects, ArrayType *objects, ArrayType *types,
    bool default_only, LaplaceConsensusConsumer consume, LaplaceConsensusCutoff cutoff,
    void *context, LaplaceConsensusScanStats *stats)
{
    consensus_scan_impl(subjects, objects, types, default_only, consume, context, stats, cutoff);
}
