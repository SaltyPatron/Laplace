#include "postgres.h"
#include "common/int.h"

#include "catalog/pg_collation.h"
#include "catalog/pg_type.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/datum.h"
#include "utils/fmgrprotos.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"

#include "laplace/core/math4d.h"

#include "recall_internal.h"
#include "query_response_field.h"
#include "spi_common.h"
#include "consensus_scan.h"

PG_FUNCTION_INFO_V1(pg_laplace_define_fast);

typedef struct { hash128_t id;Datum label;int64 score,witnesses; } DefineCandidate;
typedef struct { hash128_t id;int ordinal; } DefineIndex;
typedef struct { DefineCandidate *rows;HTAB *index; } DefineContext;

static void define_context_cell(const LaplaceConsensusRow *row,void *context)
{
    DefineContext *read=context;if(row->object_is_null)return;
    DefineIndex *entry=hash_search(read->index,&row->object,HASH_FIND,NULL);
    if(entry && pg_add_s64_overflow(read->rows[entry->ordinal].score,
        laplace_effective_mu_fp(row->rating,row->rd),&read->rows[entry->ordinal].score))
        ereport(ERROR,(errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),errmsg("Definition context exceeds integer capacity")));
}

static int define_order(const void *a,const void *b)
{
    const DefineCandidate *left=a,*right=b;
    if(left->score!=right->score)return left->score>right->score?-1:1;
    return memcmp(&left->id,&right->id,sizeof(left->id));
}

/* The producer-declared program is shared with the UI; no retired relation
 * vocabulary or a parallel legacy entity/consensus stack decides a gloss. */
static void define_fast_impl(Datum word,ArrayType *context,int limit,ReplyBuf *buf)
{
    if(limit<0)ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),errmsg("Definition limit must not be negative")));
    if(!limit)return;
    hash128_t w=datum_to_hash128(word);
    int n_defs=0;
    RdDefOutRow *defs=laplace_recall_declared_definitions_c(&w,&n_defs);
    uint64 n=(uint64)n_defs;
    if(n>MaxAllocSize/sizeof(DefineCandidate))elog(ERROR,"Declared definitions exceed memory capacity");
    DefineCandidate *rows=palloc(Max(n,1)*sizeof(*rows));Datum *ids=palloc(Max(n,1)*sizeof(Datum));
    HASHCTL ctl={0};ctl.keysize=sizeof(hash128_t);ctl.entrysize=sizeof(DefineIndex);
    HTAB *index=hash_create("Declared definition context",Max(n,16),&ctl,HASH_ELEM|HASH_BLOBS);
    for(uint64 i=0;i<n;i++){
        rows[i]=(DefineCandidate){
            defs[i].entity_id,
            CStringGetTextDatum(defs[i].label?defs[i].label:""),
            defs[i].rating_fp,
            defs[i].witnesses
        };
        if(defs[i].label)pfree(defs[i].label);
        ids[i]=hash128_to_datum(&rows[i].id);
        DefineIndex *entry=hash_search(index,&rows[i].id,HASH_ENTER,NULL);entry->ordinal=(int)i;
    }
    if(defs)pfree(defs);
    if(n && context && ArrayGetNItems(ARR_NDIM(context),ARR_DIMS(context))){
        ArrayType *candidates=construct_array(ids,(int)n,BYTEAOID,-1,false,TYPALIGN_INT);
        DefineContext read={rows,index};
        laplace_consensus_scan(context,candidates,NULL,define_context_cell,&read,NULL);
        pfree(candidates);
    }
    qsort(rows,(size_t)n,sizeof(*rows),define_order);
    for(uint64 i=0;i<n && i<(uint64)limit;i++)
        reply_buf_add(buf,rows[i].label,fp_display_numeric(rows[i].score),Int64GetDatum(rows[i].witnesses),false,false,false);
    hash_destroy(index);pfree(ids);pfree(rows);
}

Datum
pg_laplace_define_fast(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum          word;
    ArrayType     *context_arr = NULL;
    int            limit;
    ReplyBuf       buf;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    word = PG_GETARG_DATUM(0);
    if (!PG_ARGISNULL(1))
        context_arr = PG_GETARG_ARRAYTYPE_P(1);
    limit = PG_ARGISNULL(2) ? 5 : PG_GETARG_INT32(2);

    reply_buf_init(&buf);
    define_fast_impl(word, context_arr, limit, &buf);
    InitMaterializedSRF(fcinfo, 0);
    reply_buf_emit(rsinfo, &buf);
    return (Datum) 0;
}
