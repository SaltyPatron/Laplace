#include "postgres.h"
#include "catalog/namespace.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "parser/parse_func.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/tier_tree.h"
#include "laplace/core/trajectory.h"
#include "content_membership_read.h"
#include "trajectory_wkb.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "perfcache_native.h"
#include "laplace/core/sql_catalog.h"

/* Descend only the selected roots' first paths. Root/node pairs preserve
 * aliases and shared prefixes; visited pairs terminate malformed cycles. */
typedef struct FirstPath { hash128_t root,node; } FirstPath;
PG_FUNCTION_INFO_V1(pg_laplace_starts_with);
Datum pg_laplace_starts_with(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo,MAT_SRF_USE_EXPECTED_DESC);
    if(PG_ARGISNULL(0) || PG_ARGISNULL(1)) return (Datum)0;
    ReturnSetInfo *r=(ReturnSetInfo*)fcinfo->resultinfo;
    MemoryContext owner=CurrentMemoryContext;
    HASHCTL ctl={0};ctl.keysize=ctl.entrysize=sizeof(hash128_t);ctl.hcxt=owner;
    HTAB *prefix=hash_create("first path targets",32,&ctl,HASH_ELEM|HASH_BLOBS|HASH_CONTEXT);
    HTAB *matched=hash_create("first path matches",128,&ctl,HASH_ELEM|HASH_BLOBS|HASH_CONTEXT);
    Datum *ids;bool *nulls;int n;
    deconstruct_array(PG_GETARG_ARRAYTYPE_P(1),BYTEAOID,-1,false,TYPALIGN_INT,&ids,&nulls,&n);
    for(int i=0;i<n;i++)if(!nulls[i]){
        hash128_t id=datum_to_hash128(ids[i]);hash_search(prefix,&id,HASH_ENTER,NULL);
    }
    if(!hash_get_num_entries(prefix))return (Datum)0;
    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0),BYTEAOID,-1,false,TYPALIGN_INT,&ids,&nulls,&n);
    if((Size)n>MaxAllocSize/sizeof(FirstPath))elog(ERROR,"first path frontier exceeds capacity");
    FirstPath *frontier=palloc(Max(n,1)*sizeof(*frontier));int count=0;
    ctl.keysize=ctl.entrysize=sizeof(FirstPath);
    HTAB *seen=hash_create("first path visited",128,&ctl,HASH_ELEM|HASH_BLOBS|HASH_CONTEXT);
    for(int i=0;i<n;i++)if(!nulls[i]){
        hash128_t id=datum_to_hash128(ids[i]);bool found;
        if(hash_search(prefix,&id,HASH_FIND,NULL)){hash_search(matched,&id,HASH_ENTER,NULL);continue;}
        FirstPath pair={id,id};hash_search(seen,&pair,HASH_ENTER,&found);
        if(!found)frontier[count++]=pair;
    }
    bool finish=false;
    if(laplace_spi_connect(&finish)!=SPI_OK_CONNECT)elog(ERROR,"first path SPI connect failed");
    MemoryContextSwitchTo(owner);
    static SPIPlanPtr plan=NULL;Oid types[]={BYTEAARRAYOID,BYTEAARRAYOID};
    while(count){
        CHECK_FOR_INTERRUPTS();
        Datum *roots=palloc(count*sizeof(Datum)),*nodes=palloc(count*sizeof(Datum));
        for(int i=0;i<count;i++){
            roots[i]=hash128_to_datum(&frontier[i].root);nodes[i]=hash128_to_datum(&frontier[i].node);
        }
        Datum args[]={PointerGetDatum(construct_array(roots,count,BYTEAOID,-1,false,TYPALIGN_INT)),
                      PointerGetDatum(construct_array(nodes,count,BYTEAOID,-1,false,TYPALIGN_INT))};
        if(SPI_execute_plan(spi_common_ensure_plan(&plan,laplace_sql_query_text("content.first_children"),2,types),args,NULL,true,0)!=SPI_OK_SELECT)
            elog(ERROR,"first path read failed");
        FirstPath *next=NULL;int next_count=0,capacity=0;
        for(uint64 i=0;i<SPI_processed;i++){
            bool missing,found;FirstPath pair;
            pair.root=datum_to_hash128(SPI_getbinval(SPI_tuptable->vals[i],SPI_tuptable->tupdesc,1,&missing));
            pair.node=datum_to_hash128(SPI_getbinval(SPI_tuptable->vals[i],SPI_tuptable->tupdesc,2,&missing));
            if(hash_search(matched,&pair.root,HASH_FIND,NULL))continue;
            if(hash_search(prefix,&pair.node,HASH_FIND,NULL)){hash_search(matched,&pair.root,HASH_ENTER,NULL);continue;}
            hash_search(seen,&pair,HASH_ENTER,&found);if(found)continue;
            if(next_count==capacity){
                int64 grown=capacity?(int64)capacity*2:64;
                if(grown>MaxAllocSize/sizeof(*next))elog(ERROR,"first path frontier exceeds capacity");
                capacity=(int)grown;next=next?repalloc(next,capacity*sizeof(*next)):palloc(capacity*sizeof(*next));
            }
            next[next_count++]=pair;
        }
        SPI_freetuptable(SPI_tuptable);pfree(frontier);pfree(roots);pfree(nodes);
        frontier=next;count=next_count;
    }
    HASH_SEQ_STATUS scan;hash128_t *id;hash_seq_init(&scan,matched);
    while((id=hash_seq_search(&scan))){
        Datum values[]={hash128_to_datum(id)};bool missing[]={false};
        tuplestore_putvalues(r->setResult,r->setDesc,values,missing);
    }
    hash_destroy(prefix);hash_destroy(matched);hash_destroy(seen);laplace_spi_finish(finish);
    return (Datum)0;
}
