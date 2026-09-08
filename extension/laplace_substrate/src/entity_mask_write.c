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
                          laplace_mask256_t *merged)
{
    memset(merged,0,sizeof(*merged));
    if (!isnull)
    {
        bytea *value=DatumGetByteaPP(datum);
        if (VARSIZE_ANY_EXHDR(value) != sizeof(*merged))
            elog(ERROR,"entity mask write: stored mask must be 32 bytes");
        memcpy(merged,VARDATA_ANY(value),sizeof(*merged));
    }
    bool changed=false;
    for (int i=0;i<4;++i)
    {
        changed |= (merged->w[i] & delta->w[i]) != delta->w[i];
        merged->w[i] |= delta->w[i];
    }
    return changed;
}

/* This storage operation must never silently skip policies or rewrite rules.
 * The substrate entities schema has none; reject unsupported schema changes
 * before writing, preserving transaction rollback rather than bypassing them. */
static void check_storage(Relation relation)
{
    if (relation->rd_rules || relation->trigdesc ||
        (relation->rd_att->constr && relation->rd_att->constr->has_generated_stored))
        elog(ERROR,"entity mask write requires entities without rules, triggers or stored generated columns");
    if (check_enable_rls(RelationGetRelid(relation),InvalidOid,true)==RLS_ENABLED)
        elog(ERROR,"entity mask write cannot bypass row security");
}

int64 laplace_entity_masks_apply(const LaplaceEntityMaskDelta *deltas, int count)
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
        Datum mask=SPI_getbinval(tuple,desc,3,&isnull);
        laplace_mask256_t merged;
        if(!mask_missing(mask,isnull,&delta->mask,&merged)) continue;
        MaskTarget *target=&targets[n++];
        target->delta=(int)(delta-deltas);
        target->tier=DatumGetInt16(SPI_getbinval(tuple,desc,2,&isnull));
        target->relation=DatumGetObjectId(SPI_getbinval(tuple,desc,4,&isnull));
        target->tid=*(ItemPointer)DatumGetPointer(SPI_getbinval(tuple,desc,5,&isnull));
    }
    SPI_freetuptable(SPI_tuptable);
    qsort(targets,n,sizeof(*targets),compare_target);
    HASHCTL ctl={0}; ctl.keysize=sizeof(Oid);ctl.entrysize=sizeof(MaskRelation);
    HTAB *relations=hash_create("entity mask write relations",32,&ctl,HASH_ELEM|HASH_BLOBS);
    int64 updated=0;
    for(uint64 i=0;i<n;++i)
    {
        CHECK_FOR_INTERRUPTS();
        MaskTarget *target=&targets[i]; bool found,isnull;
        const LaplaceEntityMaskDelta *delta=&deltas[target->delta];
        MaskRelation *entry=hash_search(relations,&target->relation,HASH_ENTER,&found);
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
        TM_FailureData failure;
        TM_Result locked=table_tuple_lock(entry->relation,&target->tid,GetActiveSnapshot(),
            entry->old_slot,GetCurrentCommandId(false),LockTupleNoKeyExclusive,LockWaitBlock,
            IsolationUsesXactSnapshot()?0:TUPLE_LOCK_FLAG_FIND_LAST_VERSION,&failure);
        if(locked==TM_Deleted && !IsolationUsesXactSnapshot()) continue;
        if(locked==TM_Updated || locked==TM_Deleted)
            ereport(ERROR,(errcode(ERRCODE_T_R_SERIALIZATION_FAILURE),errmsg("entity mask target changed concurrently")));
        if(locked!=TM_Ok) elog(ERROR,"entity mask write: unexpected tuple lock result %d",locked);
        bytea *current=DatumGetByteaPP(slot_getattr(entry->old_slot,entry->id,&isnull));
        if(isnull || VARSIZE_ANY_EXHDR(current)!=16 || memcmp(VARDATA_ANY(current),delta->id,16)!=0 ||
           DatumGetInt16(slot_getattr(entry->old_slot,entry->tier,&isnull))!=target->tier)
        { ExecClearTuple(entry->old_slot); continue; }
        Datum old_mask=slot_getattr(entry->old_slot,entry->mask,&isnull);
        laplace_mask256_t merged;
        if(mask_missing(old_mask,isnull,&delta->mask,&merged))
        {
            ResetPerTupleExprContext(entry->estate);
            MemoryContext previous=MemoryContextSwitchTo(GetPerTupleMemoryContext(entry->estate));
            ExecCopySlot(entry->new_slot,entry->old_slot);
            bytea *value=palloc(VARHDRSZ+sizeof(merged));SET_VARSIZE(value,VARHDRSZ+sizeof(merged));
            memcpy(VARDATA(value),&merged,sizeof(merged));
            entry->new_slot->tts_values[entry->mask-1]=PointerGetDatum(value);
            entry->new_slot->tts_isnull[entry->mask-1]=false;
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
    hash_destroy(relations);table_close(root,NoLock);
    if(updated) CommandCounterIncrement();
    return updated;
}
