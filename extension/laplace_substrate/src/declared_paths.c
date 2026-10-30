/* Producer-declared paths: one Native frontier and one set execution per depth.
 * The Engine compiles the default program; PostgreSQL owns the caller's snapshot.
 * Managed, recall and direct SQL consumers receive the same complete path rows. */
#include "postgres.h"
#include "funcapi.h"
#include "catalog/pg_type.h"
#include "nodes/params.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/datum.h"
#include "utils/jsonb.h"
#include "laplace_pg/executor.h"
#include "laplace/core/sql_catalog.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "query_program.h"

PG_FUNCTION_INFO_V1(pg_laplace_query_program);
Datum pg_laplace_query_program(PG_FUNCTION_ARGS)
{
    return DirectFunctionCall1(jsonb_in,CStringGetDatum(laplace_compiled_query_program));
}

typedef struct { hash128_t *relations;int count; } DeclaredChain;
typedef struct {
    hash128_t id,claim;
    int root,program,previous,depth;
    Datum relation,label,mu;int64 witnesses;
} DeclaredNode;
typedef struct {
    MemoryContext owner;ReturnSetInfo *result;
    DeclaredChain *chains;DeclaredNode *nodes;int count,capacity;
    int *next,next_count,cap,received;int64 path;
} DeclaredRead;

static int append_node(DeclaredRead *read,const DeclaredNode *node)
{
    if(read->count==read->capacity){
        int64 capacity=read->capacity?(int64)read->capacity*2:64;
        if(capacity>MaxAllocSize/sizeof(*read->nodes))
            ereport(ERROR,(errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),errmsg("Declared query paths exceed memory capacity")));
        read->capacity=(int)capacity;
        read->nodes=read->nodes?repalloc(read->nodes,capacity*sizeof(*read->nodes)):palloc(capacity*sizeof(*read->nodes));
    }
    read->nodes[read->count]=*node;return read->count++;
}
static void emit_path(DeclaredRead *read,int last)
{
    int count=read->nodes[last].depth;
    int *chain=palloc(sizeof(*chain)*count),at=last;
    for(int i=count-1;i>=0;i--){chain[i]=at;at=read->nodes[at].previous;}
    int64 path=read->path++;
    for(int i=0;i<count;i++){
        const DeclaredNode *node=read->nodes+chain[i];
        Datum values[]={Int32GetDatum(node->root),Int32GetDatum(node->program),Int64GetDatum(path),Int32GetDatum(i),
            hash128_to_datum(&node->claim),hash128_to_datum(&node->id),node->relation,node->label,node->mu,Int64GetDatum(node->witnesses)};
        bool nulls[lengthof(values)]={false};
        tuplestore_putvalues(read->result->setResult,read->result->setDesc,values,nulls);
    }
    pfree(chain);
}
static bool receive_step(TupleTableSlot *slot,void *context)
{
    DeclaredRead *read=context;bool missing;Datum values[7];
    if(++read->received>read->cap)
        ereport(ERROR,(errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),errmsg("Declared query paths exceed the admitted frontier; no partial path was returned")));
    for(int i=0;i<7;i++){
        values[i]=slot_getattr(slot,i+1,&missing);
        if(missing)elog(ERROR,"Declared query path has a null field");
    }
    int previous=DatumGetInt32(values[0]);
    if(previous<0 || previous>=read->count)elog(ERROR,"Declared query path has an invalid frontier slot");
    MemoryContext caller=MemoryContextSwitchTo(read->owner);
    DeclaredNode node=read->nodes[previous];node.previous=previous;node.depth++;
    node.claim=datum_to_hash128(values[1]);node.id=datum_to_hash128(values[2]);
    node.relation=datumCopy(values[3],false,-1);node.label=datumCopy(values[4],false,-1);
    node.mu=datumCopy(values[5],false,-1);node.witnesses=DatumGetInt64(values[6]);
    int added=append_node(read,&node);
    if(node.depth==read->chains[node.program].count)emit_path(read,added);
    else read->next[read->next_count++]=added;
    MemoryContextSwitchTo(caller);return true;
}
static DeclaredChain *read_chains(Jsonb *program,int *count)
{
    if(!JB_ROOT_IS_OBJECT(program))
        ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),errmsg("Declared query program must be an object")));
    JsonbValue value,*chains=getKeyJsonValueFromContainer(&program->root,"chains",6,&value);
    if(!chains || chains->type!=jbvBinary || !JsonContainerIsArray(chains->val.binary.data))
        ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),errmsg("Declared query program requires chains")));
    JsonbContainer *paths=chains->val.binary.data;
    *count=JsonContainerSize(paths);
    if(!*count || *count>MaxAllocSize/sizeof(DeclaredChain))
        ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),errmsg("Declared query program requires nonempty bounded chains")));
    DeclaredChain *result=palloc0(sizeof(*result)*(*count));
    for(int i=0;i<*count;i++){
        JsonbValue *path=getIthJsonbValueFromContainer(paths,i);
        if(path->type!=jbvBinary || !JsonContainerIsArray(path->val.binary.data))
            ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),errmsg("Declared query chain must be an array")));
        JsonbContainer *relations=path->val.binary.data;int n=JsonContainerSize(relations);
        if(!n || n>MaxAllocSize/sizeof(hash128_t))
            ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),errmsg("Declared query chain must be nonempty and bounded")));
        result[i].count=n;result[i].relations=palloc(n*sizeof(hash128_t));
        for(int j=0;j<n;j++){
            JsonbValue *binding=getIthJsonbValueFromContainer(relations,j),entity;
            JsonbValue *id=binding->type==jbvBinary && JsonContainerIsObject(binding->val.binary.data)
                ?getKeyJsonValueFromContainer(binding->val.binary.data,"entity",6,&entity):NULL;
            if(!id || id->type!=jbvString || id->val.string.len!=32)
                ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),errmsg("Compiled query relations require 16-byte identities")));
            hex_decode(id->val.string.val,32,(char *)(result[i].relations+j));
        }
    }
    return result;
}

PG_FUNCTION_INFO_V1(pg_laplace_declared_paths);
Datum pg_laplace_declared_paths(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo,0);
    if(PG_ARGISNULL(0)||PG_ARGISNULL(1)||PG_ARGISNULL(2))return (Datum)0;
    int cap=PG_GETARG_INT32(2);
    if(cap<1 || cap>MaxAllocSize/sizeof(int))
        ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),errmsg("Declared query frontier capacity must be positive and bounded")));
    DeclaredRead read={.owner=CurrentMemoryContext,.result=(ReturnSetInfo *)fcinfo->resultinfo,.cap=cap};
    int programs;read.chains=read_chains(PG_GETARG_JSONB_P(1),&programs);
    Datum *roots;bool *nulls;int count;
    ArrayType *input=PG_GETARG_ARRAYTYPE_P(0);
    if(ARR_NDIM(input)>1 || ARR_ELEMTYPE(input)!=BYTEAOID)
        ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),errmsg("Declared query roots require a one-dimensional identity array")));
    deconstruct_array(input,BYTEAOID,-1,false,TYPALIGN_INT,&roots,&nulls,&count);
    if((int64)count*programs>cap)
        ereport(ERROR,(errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),errmsg("Declared query roots exceed the admitted frontier")));
    int *frontier=palloc(cap*sizeof(int));read.next=palloc(cap*sizeof(int));int n=0;
    for(int i=0;i<count;i++){
        if(nulls[i])ereport(ERROR,(errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),errmsg("Declared query roots cannot contain nulls")));
        if(VARSIZE_ANY_EXHDR(DatumGetByteaPP(roots[i]))!=16)
            ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),errmsg("Declared query roots require 16-byte identities")));
        hash128_t id=datum_to_hash128(roots[i]);
        for(int j=0;j<programs;j++){
            DeclaredNode node={.id=id,.root=i,.program=j,.previous=-1};
            frontier[n++]=append_node(&read,&node);
        }
    }
    static SPIPlanPtr plan;bool finish;
    if(laplace_spi_connect(&finish)!=SPI_OK_CONNECT)elog(ERROR,"Declared query SPI connect failed");
    Oid types[]={INT4ARRAYOID,BYTEAARRAYOID,BYTEAARRAYOID,INT4OID};
    spi_common_ensure_plan(&plan,laplace_sql_query_text("query.declared_paths_step"),4,types);
    MemoryContext transport=AllocSetContextCreate(CurrentMemoryContext,"Declared query frontier transport",ALLOCSET_DEFAULT_SIZES);
    while(n){
        CHECK_FOR_INTERRUPTS();
        MemoryContext previous=MemoryContextSwitchTo(transport);
        Datum *slots=palloc(n*sizeof(Datum)),*nodes=palloc(n*sizeof(Datum)),*relations=palloc(n*sizeof(Datum));
        for(int i=0;i<n;i++){
            DeclaredNode *node=read.nodes+frontier[i];
            slots[i]=Int32GetDatum(frontier[i]);nodes[i]=hash128_to_datum(&node->id);
            relations[i]=hash128_to_datum(read.chains[node->program].relations+node->depth);
        }
        ArrayType *slot_array=construct_array(slots,n,INT4OID,4,true,TYPALIGN_INT);
        ArrayType *node_array=construct_array(nodes,n,BYTEAOID,-1,false,TYPALIGN_INT);
        ArrayType *relation_array=construct_array(relations,n,BYTEAOID,-1,false,TYPALIGN_INT);
        ParamListInfo parameters=makeParamList(4);
        Datum args[]={PointerGetDatum(slot_array),PointerGetDatum(node_array),PointerGetDatum(relation_array),Int32GetDatum(cap==INT_MAX?cap:cap+1)};
        for(int i=0;i<4;i++)parameters->params[i]=(ParamExternData){.value=args[i],.isnull=false,.pflags=PARAM_FLAG_CONST,.ptype=types[i]};
        read.next_count=read.received=0;
        lp_pg_execute_stream_until(plan,parameters,true,receive_step,&read);
        int *swap=frontier;frontier=read.next;read.next=swap;n=read.next_count;
        MemoryContextSwitchTo(previous);MemoryContextReset(transport);
    }
    MemoryContextDelete(transport);
    laplace_spi_finish(finish);return (Datum)0;
}
