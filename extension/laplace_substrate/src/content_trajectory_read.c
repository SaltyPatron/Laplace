/* Canonical content physicality IDs are computable from entity identity. Route
 * each ID to its actual PostgreSQL hash partition before the batched PK probe. */
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
#include "partitioning/partbounds.h"
#include "partitioning/partdesc.h"
#include "utils/acl.h"
#include "utils/builtins.h"
#include "utils/fmgroids.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/partcache.h"
#include "utils/rel.h"
#include "utils/rls.h"
#include "utils/snapmgr.h"
#include "laplace/core/content_witness_batch.h"
#include "content_trajectory_read.h"
#include "spi_common.h"

static void
read_leaf(Oid oid, ArrayType *ids, LaplaceContentTrajectoryConsumer consume, void *context)
{
    Relation relation = table_open(oid, AccessShareLock);
    Oid index_oid = RelationGetPrimaryKeyIndex(relation, false);
    AttrNumber id = get_attnum(oid, "id");
    AttrNumber type = get_attnum(oid, "type");
    AttrNumber trajectory = get_attnum(oid, "trajectory");
    if (!OidIsValid(index_oid) || id <= 0 || type <= 0 || trajectory <= 0)
        elog(ERROR, "content trajectory read requires canonical physicality storage");
    Relation index = index_open(index_oid, AccessShareLock);
    if (index->rd_rel->relam != BTREE_AM_OID || !index->rd_index->indisvalid ||
        !index->rd_index->indisready || index->rd_index->indnkeyatts != 1 ||
        index->rd_index->indkey.values[0] != id ||
        index->rd_opfamily[0] != BYTEA_BTREE_FAM_OID || RelationGetIndexPredicate(index) != NIL)
        elog(ERROR, "content trajectory read requires a valid identity primary key");
    TupleTableSlot *slot = table_slot_create(relation, NULL);
    ScanKeyData key;
    ScanKeyEntryInitialize(&key, SK_SEARCHARRAY, 1, BTEqualStrategyNumber,
        BYTEAOID, InvalidOid, F_BYTEAEQ, PointerGetDatum(ids));
    IndexScanDesc scan = index_beginscan(relation, index, GetActiveSnapshot(), NULL, 1, 0);
    index_rescan(scan, &key, 1, NULL, 0);
    while (index_getnext_slot(scan, ForwardScanDirection, slot))
    {
        bool isnull;
        Datum kind = slot_getattr(slot, type, &isnull);
        if (!isnull && DatumGetInt16(kind) == 1)
        {
            Datum geometry = slot_getattr(slot, trajectory, &isnull);
            if (!isnull) consume(geometry, context);
        }
        ExecClearTuple(slot);
        CHECK_FOR_INTERRUPTS();
    }
    index_endscan(scan);
    ExecDropSingleTupleTableSlot(slot);
    index_close(index, AccessShareLock);
    table_close(relation, NoLock);
}

void
laplace_content_trajectory_read(ArrayType *entities,
    LaplaceContentTrajectoryConsumer consume, void *context)
{
    Oid root = get_relname_relid("physicalities", get_namespace_oid("laplace", false));
    if (!OidIsValid(root)) elog(ERROR, "laplace.physicalities does not exist");
    AclResult acl = pg_class_aclcheck(root, GetUserId(), ACL_SELECT);
    if (acl != ACLCHECK_OK) aclcheck_error(acl, OBJECT_TABLE, "laplace.physicalities");
    if (check_enable_rls(root, InvalidOid, true) == RLS_ENABLED)
        elog(ERROR, "native content trajectory read cannot bypass row security");
    if (ARR_NDIM(entities) > 1 || ARR_ELEMTYPE(entities) != BYTEAOID)
        elog(ERROR, "content trajectory read requires a 1-D bytea array");
    Relation relation = table_open(root, AccessShareLock);
    if (relation->rd_rel->relkind != RELKIND_PARTITIONED_TABLE)
        elog(ERROR, "content trajectory read requires partitioned physicalities");
    PartitionKey key = RelationGetPartitionKey(relation);
    if (key == NULL || key->strategy != PARTITION_STRATEGY_HASH || key->partnatts != 1 ||
        key->partattrs[0] != get_attnum(root, "id") || key->parttypid[0] != BYTEAOID)
        elog(ERROR, "content trajectory read requires physicalities partitioned by identity hash");
    PartitionDesc partitions = RelationGetPartitionDesc(relation, true);
    Datum *values;
    bool *nulls;
    int count;
    deconstruct_array(entities, BYTEAOID, -1, false, TYPALIGN_INT, &values, &nulls, &count);
    ArrayBuildState **batches = palloc0(sizeof(*batches) * Max(partitions->nparts, 1));
    for (int i = 0; i < count; ++i)
    {
        if (nulls[i]) continue;
        bytea *value = DatumGetByteaPP(values[i]);
        hash128_t entity, physicality;
        if (VARSIZE_ANY_EXHDR(value) != sizeof(entity))
            elog(ERROR, "content trajectory read requires 16-byte identities");
        memcpy(&entity, VARDATA_ANY(value), sizeof(entity));
        laplace_physicality_id_compute(entity, 1, &physicality);
        Datum id = hash128_to_datum(&physicality);
        bool isnull = false;
        uint64 hash = compute_partition_hash_value(1, key->partsupfunc,
            key->partcollation, &id, &isnull);
        int partition = partitions->boundinfo->indexes[hash % partitions->boundinfo->nindexes];
        if (partition < 0 || partition >= partitions->nparts)
            elog(ERROR, "content trajectory identity has no physicality partition");
        batches[partition] = accumArrayResult(batches[partition], id, false, BYTEAOID,
                                              CurrentMemoryContext);
        pfree(DatumGetPointer(id));
        CHECK_FOR_INTERRUPTS();
    }
    for (int i = 0; i < partitions->nparts; ++i)
        if (batches[i])
            read_leaf(partitions->oids[i], DatumGetArrayTypeP(makeArrayResult(batches[i],
                CurrentMemoryContext)), consume, context);
    pfree(batches);
    pfree(values);
    pfree(nulls);
    /* Partition locks, like normal SELECT locks, survive to transaction end. */
    table_close(relation, NoLock);
}
