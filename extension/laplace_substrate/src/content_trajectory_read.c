/* Canonical typed physicality IDs are computable from entity identity. Route
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
#include "identity_scan.h"
#include "spi_common.h"

typedef struct ContentReadContext
{
    LaplaceContentTrajectoryConsumer consume;
    LaplaceContentCarrierConsumer carrier;
    void *context;
    AttrNumber entity, type, trajectory, n_constituents;
    int16 physicality_type;
} ContentReadContext;

static void
consume_content(TupleTableSlot *slot, AttrNumber id, void *opaque)
{
    ContentReadContext *read = opaque;
    bool isnull;
    Datum kind = slot_getattr(slot, read->type, &isnull);
    if (!isnull && DatumGetInt16(kind) == read->physicality_type)
    {
        Datum geometry = slot_getattr(slot, read->trajectory, &isnull);
        if (!isnull)
        {
            bool physicality_null, entity_null;
            Datum physicality_id = slot_getattr(slot, id, &physicality_null);
            Datum entity_id = slot_getattr(slot, read->entity, &entity_null);
            if (physicality_null || entity_null)
                elog(ERROR, "content trajectory read requires physicality and entity identities");
            if (read->carrier)
            {
                Datum count = slot_getattr(slot, read->n_constituents, &isnull);
                if (isnull) elog(ERROR, "content carrier read requires a stored count");
                read->carrier(physicality_id, entity_id, DatumGetInt32(count),
                    geometry, read->context);
            }
            else
                read->consume(physicality_id, entity_id, geometry, read->context);
        }
    }
}

static void
read_leaf(Oid oid, ArrayType *ids, int16 physicality_type,
          LaplaceContentTrajectoryConsumer consume,
          LaplaceContentCarrierConsumer carrier, void *context)
{
    ContentReadContext read = {consume, carrier, context, get_attnum(oid, "entity_id"),
        get_attnum(oid, "type"), get_attnum(oid, "trajectory"),
        carrier ? get_attnum(oid, "n_constituents") : 0, physicality_type};
    if (read.entity <= 0 || read.type <= 0 || read.trajectory <= 0 ||
        (carrier && (read.n_constituents <= 0 ||
                     get_atttype(oid, read.n_constituents) != INT4OID)) ||
        !laplace_identity_scan(oid, ids, consume_content, &read))
        elog(ERROR, "content trajectory read requires canonical identity storage");
}

static void
read_trajectories(ArrayType *entities, int16 physicality_type,
    LaplaceContentTrajectoryConsumer consume,
    LaplaceContentCarrierConsumer carrier, void *context)
{
    if (physicality_type <= 0)
        elog(ERROR, "trajectory read requires a positive physicality type");
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
        laplace_physicality_id_compute(entity, physicality_type, &physicality);
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
                CurrentMemoryContext)), physicality_type, consume, carrier, context);
    pfree(batches);
    pfree(values);
    pfree(nulls);
    /* Partition locks, like normal SELECT locks, survive to transaction end. */
    table_close(relation, NoLock);
}

void
laplace_typed_trajectory_read(ArrayType *entities, int16 physicality_type,
    LaplaceContentTrajectoryConsumer consume, void *context)
{
    read_trajectories(entities, physicality_type, consume, NULL, context);
}

void
laplace_content_carrier_read(ArrayType *entities,
    LaplaceContentCarrierConsumer consume, void *context)
{
    read_trajectories(entities, 1, NULL, consume, context);
}

void
laplace_content_trajectory_read(ArrayType *entities,
    LaplaceContentTrajectoryConsumer consume, void *context)
{
    laplace_typed_trajectory_read(entities, 1, consume, context);
}
