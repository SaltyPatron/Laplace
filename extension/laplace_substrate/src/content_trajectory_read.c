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
    bool strict;
} ContentReadContext;

static void
consume_content(TupleTableSlot *slot, AttrNumber id, void *opaque)
{
    ContentReadContext *read = opaque;
    bool isnull;
    Datum kind = slot_getattr(slot, read->type, &isnull);
    if (read->strict && (isnull || DatumGetInt16(kind) != read->physicality_type))
        ereport(ERROR, (errcode(ERRCODE_DATA_CORRUPTED),
            errmsg("typed trajectory read found an unexpected physicality kind")));
    if (!isnull && DatumGetInt16(kind) == read->physicality_type)
    {
        Datum geometry = slot_getattr(slot, read->trajectory, &isnull);
        if (read->strict && isnull)
            ereport(ERROR, (errcode(ERRCODE_DATA_CORRUPTED),
                errmsg("typed trajectory read found a NULL required manifest")));
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
          LaplaceContentCarrierConsumer carrier, void *context, bool strict)
{
    ContentReadContext read = {consume, carrier, context, get_attnum(oid, "entity_id"),
        get_attnum(oid, "type"), get_attnum(oid, "trajectory"),
        carrier ? get_attnum(oid, "n_constituents") : 0, physicality_type, strict};
    if (read.entity <= 0 || read.type <= 0 || read.trajectory <= 0 ||
        (carrier && (read.n_constituents <= 0 ||
                     get_atttype(oid, read.n_constituents) != INT4OID)) ||
        !laplace_identity_scan(oid, ids, consume_content, &read))
        elog(ERROR, "content trajectory read requires canonical identity storage");
}

static void
read_trajectories(ArrayType *entities, int16 physicality_type,
    LaplaceContentTrajectoryConsumer consume,
    LaplaceContentCarrierConsumer carrier, void *context,
    LaplaceContentReadBudget *budget)
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
    if (budget != NULL)
    {
        size_t requested = (size_t) ArrayGetNItems(ARR_NDIM(entities), ARR_DIMS(entities));
        size_t partitions_bytes = (size_t) Max(partitions->nparts, 1) * sizeof(ArrayBuildState *);
        /* Array-build growth, routed bytea keys, projected physical tuples and
         * the dense partition-pointer array coexist until this frontier ends. */
        size_t per_key = 2 * BLCKSZ + 1024;
        if (partitions_bytes > SIZE_MAX - 4096 ||
            requested > (SIZE_MAX - 4096 - partitions_bytes) / per_key ||
            4096 + partitions_bytes + requested * per_key > budget->maximum_scratch_bytes)
            ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                errmsg("content trajectory scratch grant exhausted")));
    }
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
        {
            if (budget != NULL)
            {
                if (budget->leaf_reads >= budget->maximum_leaf_reads)
                    ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                        errmsg("content trajectory partition-read grant exhausted")));
                ++budget->leaf_reads;
            }
            read_leaf(partitions->oids[i], DatumGetArrayTypeP(makeArrayResult(batches[i],
                CurrentMemoryContext)), physicality_type, consume, carrier, context, budget != NULL);
        }
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
    read_trajectories(entities, physicality_type, consume, NULL, context, NULL);
}

void
laplace_content_carrier_read(ArrayType *entities,
    LaplaceContentCarrierConsumer consume, void *context)
{
    read_trajectories(entities, 1, NULL, consume, context, NULL);
}

typedef struct BoundedContentCallback {
    LaplaceContentCarrierConsumer consume;
    void *context;
    MemoryContext caller;
} BoundedContentCallback;

static void
bounded_content_callback(Datum physicality, Datum entity, int32 count,
                         Datum geometry, void *opaque)
{
    BoundedContentCallback *callback = opaque;
    MemoryContext previous = MemoryContextSwitchTo(callback->caller);
    callback->consume(physicality, entity, count, geometry, callback->context);
    MemoryContextSwitchTo(previous);
}

void
laplace_typed_carrier_read_bounded(ArrayType *entities, int16 physicality_type,
    LaplaceContentCarrierConsumer consume, void *context,
    LaplaceContentReadBudget *budget)
{
    if (budget == NULL || budget->maximum_leaf_reads < 0 || budget->leaf_reads < 0 ||
        budget->maximum_scratch_bytes == 0)
        elog(ERROR, "content trajectory reader requires a finite read budget");
    MemoryContext caller = CurrentMemoryContext;
    MemoryContext scratch = AllocSetContextCreate(caller,
        "bounded typed manifest frontier", ALLOCSET_DEFAULT_SIZES);
    BoundedContentCallback callback = {consume, context, caller};
    PG_TRY();
    {
        MemoryContextSwitchTo(scratch);
        read_trajectories(entities, physicality_type, NULL, bounded_content_callback, &callback, budget);
    }
    PG_FINALLY();
    {
        MemoryContextSwitchTo(caller);
        MemoryContextDelete(scratch);
    }
    PG_END_TRY();
}

void
laplace_content_carrier_read_bounded(ArrayType *entities,
    LaplaceContentCarrierConsumer consume, void *context, LaplaceContentReadBudget *budget)
{
    laplace_typed_carrier_read_bounded(entities, 1, consume, context, budget);
}

void
laplace_content_trajectory_read(ArrayType *entities,
    LaplaceContentTrajectoryConsumer consume, void *context)
{
    laplace_typed_trajectory_read(entities, 1, consume, context);
}
