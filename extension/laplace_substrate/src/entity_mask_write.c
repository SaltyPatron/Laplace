#include "postgres.h"
#include "access/table.h"
#include "access/tableam.h"
#include "access/xact.h"
#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "executor/executor.h"
#include "executor/spi.h"
#include "miscadmin.h"
#include "utils/acl.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/rel.h"
#include "utils/rls.h"
#include "utils/snapmgr.h"
#include "laplace/core/sql_catalog.h"
#include "entity_mask_write.h"
#include "spi_common.h"

typedef struct MaskTarget
{
    int delta;
    int16 tier;
    Oid relation;
    ItemPointerData tid;
} MaskTarget;

typedef struct MaskRelation
{
    Oid oid;
    Relation relation;
    EState *estate;
    ResultRelInfo result;
    TupleTableSlot *old_slot, *new_slot;
    AttrNumber id, tier, mask;
} MaskRelation;

static int compare_id(const void *a, const void *b) { return memcmp(a,b,16); }
static int compare_target(const void *a, const void *b)
{
    const MaskTarget *x=a,*y=b;
    if (x->delta != y->delta) return x->delta < y->delta ? -1 : 1;
    return (x->tier > y->tier) - (x->tier < y->tier);
}

static bool mask_missing(Datum datum, bool isnull, const laplace_mask256_t *delta,
                          laplace_mask256_t *merged, bool replace)
{
    memset(merged,0,sizeof(*merged));
    if (!isnull)
    {
        bytea *value=DatumGetByteaPP(datum);
        if (VARSIZE_ANY_EXHDR(value) != sizeof(*merged))
            elog(ERROR,"entity mask write: stored mask must be 32 bytes");
        memcpy(merged,VARDATA_ANY(value),sizeof(*merged));
    }
    if (replace)
    {
        bool empty=(delta->w[0]|delta->w[1]|delta->w[2]|delta->w[3])==0;
        bool changed=empty ? !isnull : isnull || memcmp(merged,delta,sizeof(*merged))!=0;
        *merged=*delta;
        return changed;
    }
    bool changed=false;
    for (int i=0;i<4;++i)
    {
        changed |= (merged->w[i] & delta->w[i]) != delta->w[i];
        merged->w[i] |= delta->w[i];
    }
    return changed;
}

/* This storage operation performs a direct executor UPDATE and therefore may
 * bypass no UPDATE policy. The canonical-identity admission trigger is
 * INSERT-only and does not participate in highway-mask maintenance, so it is
 * explicitly compatible. Reject rules, generated stored columns, row security,
 * or any UPDATE trigger before writing rather than silently bypassing them. */
static void check_storage(Relation relation)
{
    TriggerDesc *triggers = relation->trigdesc;
    bool update_triggers = triggers && (
        triggers->trig_update_before_row || triggers->trig_update_after_row ||
        triggers->trig_update_instead_row || triggers->trig_update_before_statement ||
        triggers->trig_update_after_statement);
    if (relation->rd_rules || update_triggers ||
        (relation->rd_att->constr && relation->rd_att->constr->has_generated_stored))
        elog(ERROR,"entity mask write requires entities without rules, UPDATE triggers or stored generated columns");
    if (check_enable_rls(RelationGetRelid(relation),InvalidOid,true)==RLS_ENABLED)
        elog(ERROR,"entity mask write cannot bypass row security");
}

/* Incremental Highway masks are rebuildable acceleration, never semantic
 * authority. If an ingest-time accretion would wait behind another tuple owner,
 * retain that canonical entity id for the existing authoritative refresh lane
 * instead of letting a performance structure block or deadlock canonical ingest.
 *
 * The caller already owns one SPI connection. Keep recovery set-sized and ordered;
 * entities.mask_dirty performs DISTINCT + ON CONFLICT and the later refresh reads
 * current consensus, so multiple missed deltas collapse safely to one recompute. */
static void retain_dirty_ids(Datum *ids, int count)
{
    static SPIPlanPtr dirty_plan;
    Oid types[]={BYTEAARRAYOID};
    Datum args[1];

    if (count<=0) return;
    args[0]=PointerGetDatum(construct_array(ids,count,BYTEAOID,-1,false,TYPALIGN_INT));
    if (!dirty_plan)
    {
        dirty_plan=SPI_prepare_cursor(
            laplace_sql_query_text("entities.mask_dirty"),1,types,0);
        if (!dirty_plan || SPI_keepplan(dirty_plan)!=0)
            elog(ERROR,"entity mask write: dirty queue prepare failed");
    }
    if (SPI_execute_plan(dirty_plan,args,NULL,false,0)!=SPI_OK_INSERT)
        elog(ERROR,"entity mask write: dirty queue persistence failed");
}

static MaskRelation *mask_relation(HTAB *relations, Oid oid)
{
    bool found;
    MaskRelation *entry=hash_search(relations,&oid,HASH_ENTER,&found);
    if(!found)
    {
        entry->relation=table_open(entry->oid,RowExclusiveLock);
        check_storage(entry->relation);
        entry->id=get_attnum(entry->oid,"id");entry->tier=get_attnum(entry->oid,"tier");
        entry->mask=get_attnum(entry->oid,"highway_mask");
        if(entry->id<=0 || entry->tier<=0 || entry->mask<=0)
            elog(ERROR,"entity mask write: incomplete entity storage");
        entry->estate=CreateExecutorState();
        entry->estate->es_snapshot=GetActiveSnapshot();
        entry->estate->es_output_cid=GetCurrentCommandId(true);
        InitResultRelInfo(&entry->result,entry->relation,0,NULL,0);
        ExecOpenIndices(&entry->result,false);
        entry->old_slot=table_slot_create(entry->relation,NULL);
        entry->new_slot=MakeSingleTupleTableSlot(RelationGetDescr(entry->relation),&TTSOpsVirtual);
    }
    return entry;
}

/* Lock the captured physical row, following a committed replacement at READ
 * COMMITTED. The exact identity/tier check still precedes every storage write. */
static TM_Result mask_target_lock(MaskRelation *entry, MaskTarget *target,
                                 const LaplaceEntityMaskDelta *delta, bool replace)
{
    TM_FailureData failure;
    bool isnull;
    TM_Result locked=table_tuple_lock(entry->relation,&target->tid,GetActiveSnapshot(),
        entry->old_slot,GetCurrentCommandId(false),LockTupleNoKeyExclusive,
        replace ? LockWaitBlock : LockWaitSkip,
        IsolationUsesXactSnapshot()?0:TUPLE_LOCK_FLAG_FIND_LAST_VERSION,&failure);
    if(!replace && locked==TM_WouldBlock) return locked;
    if(locked==TM_Deleted && !IsolationUsesXactSnapshot()) return locked;
    if(locked==TM_Updated || locked==TM_Deleted)
        ereport(ERROR,(errcode(ERRCODE_T_R_SERIALIZATION_FAILURE),errmsg("entity mask target changed concurrently")));
    if(locked!=TM_Ok) elog(ERROR,"entity mask write: unexpected tuple lock result %d",locked);
    bytea *current=DatumGetByteaPP(slot_getattr(entry->old_slot,entry->id,&isnull));
    if(isnull || VARSIZE_ANY_EXHDR(current)!=16 || memcmp(VARDATA_ANY(current),delta->id,16)!=0 ||
       DatumGetInt16(slot_getattr(entry->old_slot,entry->tier,&isnull))!=target->tier)
        return TM_Deleted;
    return TM_Ok;
}

static int64 entity_masks_write(const LaplaceEntityMaskDelta *deltas, int count, bool replace,
                               LaplaceEntityMaskRefresh recompute, void *context)
{
    if (!count) return 0;
    if (XactReadOnly)
        ereport(ERROR,(errcode(ERRCODE_READ_ONLY_SQL_TRANSACTION),
                       errmsg("cannot deposit entity masks in a read-only transaction")));
    if (IsInParallelMode()) elog(ERROR,"cannot deposit entity masks in parallel mode");
    Oid root_oid=get_relname_relid("entities",get_namespace_oid("laplace",false));
    Relation root=table_open(root_oid,RowExclusiveLock);
    AclResult acl=pg_class_aclcheck(root_oid,GetUserId(),ACL_UPDATE);
    if (acl!=ACLCHECK_OK) aclcheck_error(acl,OBJECT_TABLE,"laplace.entities");
    check_storage(root);
    Datum *ids=palloc(sizeof(Datum)*count);
    for(int i=0;i<count;++i)
    {
        hash128_t id;
        memcpy(&id,deltas[i].id,sizeof(id));
        ids[i]=hash128_to_datum(&id);
    }
    Oid types[]={BYTEAARRAYOID};
    Datum args[]={PointerGetDatum(construct_array(ids,count,BYTEAOID,-1,false,TYPALIGN_INT))};
    static SPIPlanPtr read_plan;
    if (!read_plan)
    {
        read_plan=SPI_prepare_cursor(laplace_sql_query_text("entities.mask_targets"),1,types,CURSOR_OPT_PARALLEL_OK);
        if (!read_plan || SPI_keepplan(read_plan)!=0) elog(ERROR,"entity mask write: prepare failed");
    }
    if(SPI_execute_plan(read_plan,args,NULL,true,0)!=SPI_OK_SELECT)
        elog(ERROR,"entity mask write: target read failed");
    if(SPI_processed>MaxAllocSize/sizeof(MaskTarget))
        elog(ERROR,"entity mask write: target set exceeds allocation capacity");
    MaskTarget *targets=palloc(Max(SPI_processed,1)*sizeof(MaskTarget));
    uint64 n=0;
    for(uint64 i=0;i<SPI_processed;++i)
    {
        HeapTuple tuple=SPI_tuptable->vals[i]; TupleDesc desc=SPI_tuptable->tupdesc;
        bool isnull;
        bytea *id=DatumGetByteaPP(SPI_getbinval(tuple,desc,1,&isnull));
        if(isnull || VARSIZE_ANY_EXHDR(id)!=16) elog(ERROR,"entity mask write: invalid stored identity");
        const LaplaceEntityMaskDelta *delta=bsearch(VARDATA_ANY(id),deltas,count,sizeof(*deltas),compare_id);
        if(!delta) elog(ERROR,"entity mask write: unexpected identity");
        /* Even an apparent OR no-op must lock or retain dirty work: a
         * concurrent authoritative refresh may be about to clear that bit. */
        MaskTarget *target=&targets[n++];
        target->delta=(int)(delta-deltas);
        target->tier=DatumGetInt16(SPI_getbinval(tuple,desc,2,&isnull));
        target->relation=DatumGetObjectId(SPI_getbinval(tuple,desc,4,&isnull));
        target->tid=*(ItemPointer)DatumGetPointer(SPI_getbinval(tuple,desc,5,&isnull));
    }
    SPI_freetuptable(SPI_tuptable);
    qsort(targets,n,sizeof(*targets),compare_target);
    Datum *dirty=palloc(Max(n,(uint64)1)*sizeof(Datum));
    int dirty_count=0;
    HASHCTL ctl={0}; ctl.keysize=sizeof(Oid);ctl.entrysize=sizeof(MaskRelation);
    HTAB *relations=hash_create("entity mask write relations",32,&ctl,HASH_ELEM|HASH_BLOBS);
    int64 updated=0;
    if(recompute)
    {
        /* Freeze exactly the existing physical target set before opening the
         * incident snapshot. Later-created entities/tiers are not replacement
         * targets; their ordinary deposits remain intact. */
        for(uint64 i=0;i<n;++i)
        {
            CHECK_FOR_INTERRUPTS();
            MaskTarget *target=&targets[i];
            MaskRelation *entry=mask_relation(relations,target->relation);
            if(mask_target_lock(entry,target,&deltas[target->delta],true)!=TM_Ok)
                target->relation=InvalidOid;
            ExecClearTuple(entry->old_slot);
        }
        recompute(context);
    }
    for(uint64 i=0;i<n;++i)
    {
        CHECK_FOR_INTERRUPTS();
        MaskTarget *target=&targets[i]; bool isnull;
        if(!OidIsValid(target->relation)) continue;
        const LaplaceEntityMaskDelta *delta=&deltas[target->delta];
        MaskRelation *entry=mask_relation(relations,target->relation);
        TM_Result locked=mask_target_lock(entry,target,delta,replace);
        if(!replace && locked==TM_WouldBlock)
        {
            hash128_t id;
            memcpy(&id,delta->id,sizeof(id));
            dirty[dirty_count++]=hash128_to_datum(&id);
            ExecClearTuple(entry->old_slot);
            continue;
        }
        if(locked!=TM_Ok) { ExecClearTuple(entry->old_slot); continue; }
        Datum old_mask=slot_getattr(entry->old_slot,entry->mask,&isnull);
        laplace_mask256_t merged;
        if(mask_missing(old_mask,isnull,&delta->mask,&merged,replace))
        {
            ResetPerTupleExprContext(entry->estate);
            MemoryContext previous=MemoryContextSwitchTo(GetPerTupleMemoryContext(entry->estate));
            ExecCopySlot(entry->new_slot,entry->old_slot);
            bytea *value=palloc(VARHDRSZ+sizeof(merged));SET_VARSIZE(value,VARHDRSZ+sizeof(merged));
            memcpy(VARDATA(value),&merged,sizeof(merged));
            entry->new_slot->tts_values[entry->mask-1]=PointerGetDatum(value);
            entry->new_slot->tts_isnull[entry->mask-1]=replace &&
                (merged.w[0]|merged.w[1]|merged.w[2]|merged.w[3])==0;
            ExecSimpleRelationUpdate(&entry->result,entry->estate,NULL,entry->old_slot,entry->new_slot);
            ExecClearTuple(entry->new_slot);
            MemoryContextSwitchTo(previous);
            ++updated;
        }
        ExecClearTuple(entry->old_slot);
    }
    HASH_SEQ_STATUS scan;MaskRelation *entry;
    hash_seq_init(&scan,relations);
    while((entry=hash_seq_search(&scan)))
    {
        ExecDropSingleTupleTableSlot(entry->old_slot);ExecDropSingleTupleTableSlot(entry->new_slot);
        ExecCloseIndices(&entry->result);FreeExecutorState(entry->estate);
        table_close(entry->relation,NoLock);
    }
    hash_destroy(relations);
    if(dirty_count) retain_dirty_ids(dirty,dirty_count);
    table_close(root,NoLock);
    if(updated) CommandCounterIncrement();
    return updated;
}

/* Accumulation and authoritative refresh share tuple routing, permissions and
 * index maintenance. Incremental accretion never waits on a contended tuple:
 * it defers that entity to the authoritative dirty-refresh queue because the
 * Highway mask is an accelerator. Replacement/bit-clearing remains blocking
 * and authoritative. Empty replacement means NULL, not an absent request. */
int64 laplace_entity_masks_apply(const LaplaceEntityMaskDelta *deltas, int count)
{
    return entity_masks_write(deltas,count,false,NULL,NULL);
}

int64 laplace_entity_masks_replace(const LaplaceEntityMaskDelta *masks, int count)
{
    return entity_masks_write(masks,count,true,NULL,NULL);
}

/* Recompute only after all captured entity tuples are locked. The callback
 * owns its fresh incident snapshot and restores it before returning. */
int64 laplace_entity_masks_refresh(const LaplaceEntityMaskDelta *masks, int count,
                                  LaplaceEntityMaskRefresh recompute, void *context)
{
    if(!recompute) elog(ERROR,"entity mask refresh requires a recomputation callback");
    return entity_masks_write(masks,count,true,recompute,context);
}