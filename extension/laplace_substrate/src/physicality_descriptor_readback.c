#include "postgres.h"
#include "access/detoast.h"
#include "catalog/namespace.h"
#include "catalog/pg_type_d.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "parser/parse_func.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include "utils/snapmgr.h"
#include "utils/syscache.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/physicality_descriptor_admission.h"
#include "laplace/core/sql_catalog.h"
#include "laplace/core/trajectory.h"
#include "content_trajectory_read.h"
#include "perfcache_native.h"
#include "trajectory_wkb.h"

PG_FUNCTION_INFO_V1(pg_laplace_physicality_descriptor_read);
PG_FUNCTION_INFO_V1(pg_laplace_physicality_forms);

/* One statement snapshot, one indexed batch per required typed frontier.
 * Descriptor bodies are decoded by the existing native owner. This reader
 * does not select historical child geometry or realize a V curve. */
typedef struct ReadState {
    MemoryContextCallback cleanup;
    size_t maximum_bytes, bytes, peak, maximum_work, work;
    physicality_descriptor_basis_t basis;
    hash128_t floor;
    physicality_descriptor_vocabulary_t *vocabulary;
    intent_stage_t *source;
    physicality_descriptor_readback_t *decoded;
    physicality_descriptor_node_t *nodes;
    hash128_t *children, *roots, *wanted, *frontier;
    bool *frontier_seen;
    size_t node_count, node_capacity, child_count, child_capacity;
    size_t root_count, wanted_count, frontier_count;
    size_t callback_first, callback_count, native_bytes;
    int rounds, operations, maximum_operations;
    LaplaceContentReadBudget reads;
    Snapshot snapshot;
    Oid as_binary;
    hash128_t cursor;
    bool has_cursor, exhausted;
    size_t candidates, rejected, hydrated_nodes;
    bool candidate_hydration;
} ReadState;

static pg_noreturn void read_invalid(const char *message) {
    ereport(ERROR, (errcode(ERRCODE_DATA_CORRUPTED),
        errmsg("physicality descriptor read: %s", message)));
}
static pg_noreturn void read_limit(const char *message) {
    ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
        errmsg("physicality descriptor read: %s", message)));
}
static size_t plus(size_t a, size_t b) { if (b > SIZE_MAX-a) read_limit("size overflow"); return a+b; }
static size_t times(size_t a, size_t b) { if (a && b > SIZE_MAX/a) read_limit("size overflow"); return a*b; }
static void read_charge(ReadState *s, size_t n) {
    if (n > s->maximum_bytes-s->bytes) read_limit("byte grant exhausted");
    s->bytes += n; if (s->bytes > s->peak) s->peak = s->bytes;
}
static void reserve(ReadState *s, size_t n) { read_charge(s,n); s->bytes-=n; }
static void work(ReadState *s, size_t n) {
    if (n > s->maximum_work-s->work) read_limit("logical-work grant exhausted");
    s->work += n;
}
static void *allocate(ReadState *s, size_t n) { read_charge(s,n); if(n>MaxAllocSize) read_limit("allocation exceeds PostgreSQL capacity"); return palloc0(Max(n,1)); }
static void release(ReadState *s, void *p, size_t n) { if(p) pfree(p); s->bytes-=n; }
static void grow(ReadState *s, void **p, size_t *capacity, size_t need, size_t width) {
    if(need<=*capacity) return;
    size_t next=Max(*capacity,16);
    while(next<need) next=plus(next,next);
    void *new_items=allocate(s,times(next,width));
    if(*p) { memcpy(new_items,*p,times(*capacity,width)); release(s,*p,times(*capacity,width)); }
    *p=new_items; *capacity=next;
}
static int compare_id(const void *a,const void *b) { return memcmp(a,b,sizeof(hash128_t)); }
static int compare_node(const void *a,const void *b) {
    const physicality_descriptor_node_t *left=a, *right=b;
    return compare_id(&left->id,&right->id);
}
static int compare_node_id(const void *a,const void *b) {
    return compare_id(a,&((const physicality_descriptor_node_t*)b)->id);
}
static void operation(ReadState *s) {
    if(s->operations>=s->maximum_operations)read_limit("database-operation grant exhausted");
    ++s->operations;
}
static Datum id_datum(const hash128_t *id) {
    bytea *value = palloc(VARHDRSZ + sizeof(*id));
    SET_VARSIZE(value, VARHDRSZ + sizeof(*id));
    memcpy(VARDATA(value), id, sizeof(*id));
    return PointerGetDatum(value);
}
static hash128_t read_id(Datum value) {
    bytea *b=DatumGetByteaPP(value); hash128_t id;
    if(VARSIZE_ANY_EXHDR(b)!=sizeof(id)) read_invalid("identity width differs from 16 bytes");
    memcpy(&id,VARDATA_ANY(b),sizeof(id)); return id;
}
static hash128_t *read_ids(ReadState *s, ArrayType *a,size_t *count) {
    Datum *values; bool *nulls; int n;
    if(ARR_NDIM(a)>1 || ARR_ELEMTYPE(a)!=BYTEAOID) read_invalid("expected one-dimensional identity array");
    n=ArrayGetNItems(ARR_NDIM(a),ARR_DIMS(a));
    size_t scratch=times((size_t)n,sizeof(Datum)+sizeof(bool)); read_charge(s,scratch);
    deconstruct_array(a,BYTEAOID,-1,false,TYPALIGN_INT,&values,&nulls,&n);
    hash128_t *ids=allocate(s,times((size_t)n,sizeof(hash128_t)));
    for(int i=0;i<n;++i) { if(nulls[i]) read_invalid("identity array contains NULL"); ids[i]=read_id(values[i]); }
    pfree(values); pfree(nulls); s->bytes-=scratch; *count=(size_t)n; return ids;
}
static ArrayType *id_array(ReadState *s,const hash128_t *ids,size_t count) {
    if(count>PG_INT32_MAX) read_limit("identity array count exceeds int4");
    /* Datum + bytea elements + constructed array coexist during construction. */
    size_t scratch=plus(128,times(count,64)); read_charge(s,scratch);
    Datum *values=palloc(Max(count,1)*sizeof(Datum));
    for(size_t i=0;i<count;++i) values[i]=id_datum(&ids[i]);
    ArrayType *result=construct_array(values,(int)count,BYTEAOID,-1,false,TYPALIGN_INT);
    for(size_t i=0;i<count;++i) pfree(DatumGetPointer(values[i]));
    pfree(values);
    s->bytes-=scratch; read_charge(s,VARSIZE(result)); return result;
}
static void native_status(physicality_descriptor_status_t status) {
    if(status==PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED) read_limit("native decoder grant exhausted");
    if(status!=PHYSICALITY_DESCRIPTOR_OK) read_invalid("native typed body validation failed");
}
static void cleanup(void *arg) {
    ReadState *s=arg;
    physicality_descriptor_readback_free(s->decoded); s->decoded=NULL;
    physicality_descriptor_vocabulary_free(s->vocabulary); s->vocabulary=NULL;
    intent_stage_free(s->source); s->source=NULL;
}
static void vocabulary(ReadState *s) {
    hash128_t source; size_t peak=0;
    if(!laplace_perfcache_ready())
        ereport(ERROR,(errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),errmsg("physicality descriptor read requires the configured Unicode floor")));
    native_status(physicality_descriptor_generated_source_create(s->maximum_bytes-s->bytes,&source,&s->source,&peak));
    reserve(s,peak); read_charge(s,intent_stage_memory_bytes(s->source));
    native_status(physicality_descriptor_vocabulary_create(&source,s->maximum_bytes-s->bytes,&s->vocabulary));
    reserve(s,physicality_descriptor_vocabulary_peak_bytes(s->vocabulary));
    read_charge(s,physicality_descriptor_vocabulary_bytes(s->vocabulary));
    read_charge(s,physicality_descriptor_vocabulary_floor_index_added_bytes(s->vocabulary));
    s->basis=*physicality_descriptor_vocabulary_basis(s->vocabulary);
    s->floor=*physicality_descriptor_vocabulary_floor_receipt(s->vocabulary);
    s->bytes-=physicality_descriptor_vocabulary_bytes(s->vocabulary);
    physicality_descriptor_vocabulary_free(s->vocabulary);s->vocabulary=NULL;
    s->bytes-=intent_stage_memory_bytes(s->source);intent_stage_free(s->source);s->source=NULL;
}
static text *snapshot_receipt(ReadState *s) {
    Snapshot snap=s->snapshot;
    size_t capacity=plus(256,times(plus(snap->xcnt,snap->subxcnt),12));
    char *buffer=allocate(s,capacity);
    size_t used=(size_t)snprintf(buffer,capacity,"active-mvcc-v1;xmin=%u;xmax=%u;cid=%u;recovery=%d;suboverflow=%d;xip=",
        snap->xmin,snap->xmax,snap->curcid,snap->takenDuringRecovery,snap->suboverflowed);
    for(uint32 i=0;i<snap->xcnt;++i) used+=(size_t)snprintf(buffer+used,capacity-used,"%s%u",i?",":"",snap->xip[i]);
    used+=(size_t)snprintf(buffer+used,capacity-used,";subxip=");
    for(int32 i=0;i<snap->subxcnt;++i) used+=(size_t)snprintf(buffer+used,capacity-used,"%s%u",i?",":"",snap->subxip[i]);
    if(used>=capacity) read_invalid("snapshot receipt framing overflow");
    read_charge(s,plus(used,VARHDRSZ));text *result=cstring_to_text_with_len(buffer,(int)used);release(s,buffer,capacity);return result;
}
static int append_child(void *opaque,size_t ordinal,const hash128_t *id,uint64 flags) {
    ReadState *s=opaque;(void)flags;
    if(ordinal==0 || ordinal>s->callback_count) read_invalid("logical carrier ordinal differs from stored count");
    s->children[s->callback_first+ordinal-1]=*id;
    if((ordinal&1023u)==0) CHECK_FOR_INTERRUPTS();
    return 0;
}
static void receive_node(Datum placement_value,Datum entity_value,int32 n_constituents,Datum geometry,void *opaque) {
    ReadState *s=opaque;hash128_t entity=read_id(entity_value),placement=read_id(placement_value),expected;
    laplace_physicality_id_compute(entity,1,&expected);
    hash128_t *selected=bsearch(&entity,s->frontier,s->frontier_count,sizeof(entity),compare_id);
    if(memcmp(&placement,&expected,sizeof(expected)) || !selected)
        read_invalid("indexed row is outside requested Content identity scope");
    size_t selected_index=(size_t)(selected-s->frontier);
    if(s->frontier_seen[selected_index])read_invalid("duplicate indexed Content row");
    s->frontier_seen[selected_index]=true;
    if(n_constituents<(s->candidate_hydration?1:2)) read_invalid("typed descriptor node must have at least two ordinary children");
    work(s,(size_t)n_constituents);
    size_t raw=toast_raw_datum_size(geometry),temporary=plus(times(raw,4),128);
    read_charge(s,temporary);
    bytea *wkb=DatumGetByteaP(OidFunctionCall1(s->as_binary,geometry));
    uint32 vertices;const unsigned char *points=laplace_trajectory_wkb_points(wkb,&vertices);
    size_t point_bytes=times(vertices,4*sizeof(double));
    double *aligned=palloc(Max(point_bytes,1));memcpy(aligned,points,point_bytes);
    size_t logical=0;
    if(trajectory_constituent_count(aligned,vertices,&logical)!=0 || logical!=(size_t)n_constituents)
        read_invalid("stored descriptor carrier count differs from its manifest");
    grow(s,(void**)&s->children,&s->child_capacity,plus(s->child_count,logical),sizeof(hash128_t));
    grow(s,(void**)&s->nodes,&s->node_capacity,plus(s->node_count,1),sizeof(*s->nodes));
    physicality_descriptor_node_t *node=&s->nodes[s->node_count++];
    node->id=entity;node->first_child=s->child_count;node->child_count=logical;++s->hydrated_nodes;
    size_t saved=s->callback_first;s->callback_first=s->child_count;s->callback_count=logical;
    if(trajectory_visit_constituents(aligned,vertices,append_child,s)!=0) read_invalid("invalid ordinary descriptor trajectory");
    s->callback_first=saved;s->child_count+=logical;
    pfree(aligned);pfree(wkb);s->bytes-=temporary;
}
static void hydrate(ReadState *s,const hash128_t *ids,size_t count) {
    if(!count)return;
    size_t bytes=times(count,sizeof(hash128_t));s->frontier=allocate(s,bytes);
    memcpy(s->frontier,ids,bytes);qsort(s->frontier,count,sizeof(hash128_t),compare_id);
    size_t unique=0;for(size_t i=0;i<count;++i)if(!i||compare_id(&s->frontier[i],&s->frontier[i-1]))s->frontier[unique++]=s->frontier[i];
    s->frontier_count=unique;
    s->frontier_seen=allocate(s,times(unique,sizeof(bool)));
    ArrayType *array=id_array(s,s->frontier,unique);
    /* Partition keys, per-leaf arrays and one physical/projected tuple can
     * coexist. TOAST expansion is separately admitted in receive_node. */
    size_t reservation=plus(8192,times(unique,2*BLCKSZ+2048));read_charge(s,reservation);
    s->reads.maximum_scratch_bytes=reservation;
    s->callback_first=s->node_count;size_t start=s->node_count;
    s->reads.maximum_leaf_reads=s->maximum_operations-s->operations;
    s->reads.leaf_reads=0;
    laplace_content_carrier_read_bounded(array,receive_node,s,&s->reads);
    s->operations+=s->reads.leaf_reads;++s->rounds;
    if(s->node_count-start!=unique) read_invalid("required typed descriptor node is absent");
    s->bytes-=reservation;
    release(s,array,VARSIZE(array));release(s,s->frontier,bytes);
    release(s,s->frontier_seen,times(unique,sizeof(bool)));
    s->frontier=NULL;s->frontier_seen=NULL;s->frontier_count=0;
}
static void decode(ReadState *s) {
    for(;;) {
        physicality_descriptor_limits_t limits={s->maximum_bytes-s->bytes};
        /* Exact catalog identities are authenticated once in discovery and
         * again in complete replan verification. Charge the second pass only
         * when the decoder reports a complete body. */
        work(s,s->child_count);
        if(s->child_count>s->maximum_work-s->work)read_limit("complete catalog verification exceeds logical-work grant");
        physicality_descriptor_status_t status=physicality_descriptor_readback_prepare(
            s->nodes,s->node_count,s->children,s->child_count,s->roots,s->root_count,&s->basis,
            &limits,s->maximum_bytes-s->bytes,s->maximum_work-s->work-s->child_count,&s->decoded);
        if(status!=PHYSICALITY_DESCRIPTOR_OK && status!=PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER)native_status(status);
        reserve(s,physicality_descriptor_readback_peak_bytes(s->decoded));
        s->native_bytes=physicality_descriptor_readback_bytes(s->decoded);read_charge(s,s->native_bytes);
        if(status==PHYSICALITY_DESCRIPTOR_OK) {
            work(s,plus(s->child_count,physicality_descriptor_readback_content_hash_operands(s->decoded)));return;
        }
        size_t count;const hash128_t *missing=physicality_descriptor_readback_missing(s->decoded,&count);
        if(!count)read_invalid("incomplete decoder returned no required frontier");
        hydrate(s,missing,count);
        physicality_descriptor_readback_free(s->decoded);s->decoded=NULL;s->bytes-=s->native_bytes;s->native_bytes=0;
    }
}
static void discover(ReadState *s,ArrayType *wanted,bytea *cursor,int32 page) {
    const char *query=laplace_sql_query_text("readback.physicality_form_candidates");
    Oid types[4]={BYTEAOID,BYTEAARRAYOID,BYTEAOID,INT4OID};
    size_t bytes=plus(4096,times((size_t)page+1,2*BLCKSZ+256));read_charge(s,bytes);
    MemoryContext caller=CurrentMemoryContext;
    Datum values[4]={id_datum(&s->basis.tags[PHYSICALITY_DESCRIPTOR_SCHEMA]),PointerGetDatum(wanted),PointerGetDatum(cursor),Int32GetDatum(page+1)};
    operation(s);
    if(SPI_connect()!=SPI_OK_CONNECT)read_invalid("SPI connection failed");
    SPIPlanPtr plan=SPI_prepare(query,4,types);if(!plan)read_invalid("candidate plan preparation failed");
    operation(s);
    if(SPI_execute_snapshot(plan,values,NULL,s->snapshot,InvalidSnapshot,true,false,0)!=SPI_OK_SELECT)read_invalid("indexed candidate read failed");
    s->exhausted=SPI_processed<=(uint64)page;s->candidates=Min(SPI_processed,(uint64)page);
    MemoryContextSwitchTo(caller);
    s->root_count=s->candidates;s->roots=allocate(s,times(s->root_count,sizeof(hash128_t)));
    for(size_t i=0;i<s->root_count;++i) {
        bool isnull;Datum id=SPI_getbinval(SPI_tuptable->vals[i],SPI_tuptable->tupdesc,1,&isnull);
        if(isnull)read_invalid("NULL candidate identity");
        s->roots[i]=read_id(id);
        if(i&&compare_id(&s->roots[i-1],&s->roots[i])>=0)read_invalid("candidate cursor order is not strict");
    }
    if(s->root_count){s->cursor=s->roots[s->root_count-1];s->has_cursor=true;}
    SPI_freetuptable(SPI_tuptable);SPI_freeplan(plan);SPI_finish();pfree(DatumGetPointer(values[0]));s->bytes-=bytes;
    s->candidate_hydration=true;hydrate(s,s->roots,s->root_count);s->candidate_hydration=false;
    qsort(s->nodes,s->node_count,sizeof(*s->nodes),compare_node);
    size_t kept=0;
    for(size_t i=0;i<s->root_count;++i) {
        const physicality_descriptor_node_t *node=bsearch(&s->roots[i],s->nodes,s->node_count,sizeof(*s->nodes),compare_node_id);
        hash128_t entity;
        if(node && node->child_count==9 && !compare_id(&s->children[node->first_child],&s->basis.tags[PHYSICALITY_DESCRIPTOR_SCHEMA]))work(s,9);
        if(node&&physicality_descriptor_readback_root_entity(node,s->children,s->child_count,&s->basis,&entity)&&
            bsearch(&entity,s->wanted,s->wanted_count,sizeof(entity),compare_id))s->roots[kept++]=s->roots[i];
        else ++s->rejected;
    }
    s->root_count=kept;
    /* Incidental schema containment is not a typed node catalog. Exclude
     * rejected roots, including legitimate singleton forms, before decoding. */
    size_t retained=0;
    for(size_t i=0;i<s->node_count;++i)
        if(bsearch(&s->nodes[i].id,s->roots,s->root_count,sizeof(hash128_t),compare_id))s->nodes[retained++]=s->nodes[i];
    s->node_count=retained;
}
static Datum bits(ReadState *s,const void *data,size_t bytes) {
    bytea *result=allocate(s,plus(VARHDRSZ,bytes));SET_VARSIZE(result,VARHDRSZ+bytes);
    if(bytes)memcpy(VARDATA(result),data,bytes);
    return PointerGetDatum(result);
}
static Datum binary64_bits(ReadState *s, const double *numbers, size_t count) {
    bytea *result = allocate(s, plus(VARHDRSZ, times(count, sizeof(double))));
    SET_VARSIZE(result, VARHDRSZ + count * sizeof(double));
    unsigned char *bytes = (unsigned char *) VARDATA(result);
    for (size_t i = 0; i < count; ++i) {
        uint64_t word;
        memcpy(&word, &numbers[i], sizeof(word));
        for (size_t byte = 0; byte < sizeof(word); ++byte)
            bytes[i * sizeof(word) + byte] = (unsigned char) (word >> (8u * byte));
    }
    return PointerGetDatum(result);
}
static ArrayType *column_array(ReadState *s,Datum *values,bool *nulls,int count,Oid type,int16 length,bool byval,char align) {
    int dimensions[1]={count},lower[1]={1};
    size_t reserve_bytes=plus(256,times((size_t)count,8));
    for(int i=0;i<count;++i)if(!nulls[i])reserve_bytes=plus(reserve_bytes,length>0?(size_t)length:VARSIZE_ANY(DatumGetPointer(values[i])));
    read_charge(s,reserve_bytes);
    ArrayType *result=count?construct_md_array(values,nulls,1,dimensions,lower,type,length,byval,align):construct_empty_array(type);
    s->bytes-=reserve_bytes;read_charge(s,VARSIZE(result));return result;
}
static Datum output(FunctionCallInfo fcinfo,ReadState *s) {
    size_t count;const physicality_descriptor_input_t *bodies=physicality_descriptor_readback_inputs(s->decoded,&count);
    if(count!=s->root_count || count>PG_INT32_MAX)read_invalid("decoded body count differs from selected roots");
    size_t tuples=times(count,9*(sizeof(Datum)+sizeof(bool)));
    Datum *columns=allocate(s,times(count,9*sizeof(Datum)));bool *missing=allocate(s,times(count,9*sizeof(bool)));
    /* Reserve body values, output arrays and tuplestore's copy before building.
     * Coordinates/trajectory are exact native little-endian binary64 words. */
    size_t payload=0;for(size_t i=0;i<count;++i)payload=plus(payload,plus(256,times(bodies[i].trajectory_vertices,32)));
    reserve(s,plus(times(payload,3),times(tuples,2)));
    for(size_t i=0;i<count;++i) {
        const physicality_descriptor_input_t *b=&bodies[i];
        columns[i]=bits(s,&s->roots[i],16);columns[count+i]=bits(s,&b->entity_id,16);
        columns[2*count+i]=Int16GetDatum(b->type);columns[3*count+i]=binary64_bits(s,b->coord,4);
        columns[4*count+i]=bits(s,b->hilbert_index.bytes,16);
        if(b->trajectory_vertices)columns[5*count+i]=binary64_bits(s,b->trajectory_xyzm,times(b->trajectory_vertices,4));else missing[5*count+i]=true;
        columns[6*count+i]=Int32GetDatum(b->n_constituents);
        if(!b->alignment_residual_is_null)columns[7*count+i]=binary64_bits(s,&b->alignment_residual,1);else missing[7*count+i]=true;
        if(!b->source_dim_is_null)columns[8*count+i]=Int32GetDatum(b->source_dim);else missing[8*count+i]=true;
    }
    Datum values[20]={0};bool nulls[20]={false};
    for(int i=0;i<9;++i) {
        Oid type=i==2?INT2OID:(i==6||i==8?INT4OID:BYTEAOID);
        int16 width=type==INT2OID?2:(type==INT4OID?4:-1);
        values[i]=PointerGetDatum(column_array(s,columns+i*count,missing+i*count,(int)count,type,width,width!=-1,width==2?TYPALIGN_SHORT:TYPALIGN_INT));
    }
    values[9]=bits(s,&s->cursor,s->has_cursor?16:0);values[10]=BoolGetDatum(s->exhausted);
    values[11]=bits(s,&s->floor,16);values[12]=PointerGetDatum(snapshot_receipt(s));
    values[13]=Int64GetDatum((int64)s->candidates);values[14]=Int64GetDatum((int64)s->rejected);
    values[15]=Int64GetDatum((int64)s->hydrated_nodes);values[16]=Int64GetDatum((int64)s->work);
    values[17]=Int32GetDatum(s->rounds);values[18]=Int32GetDatum(s->operations);
    size_t output_bytes=1024;
    for(int i=0;i<=12;++i)if(i!=10)output_bytes=plus(output_bytes,VARSIZE_ANY(DatumGetPointer(values[i])));
    reserve(s,output_bytes);values[19]=Int64GetDatum((int64)s->peak);
    InitMaterializedSRF(fcinfo,0);ReturnSetInfo *result=(ReturnSetInfo*)fcinfo->resultinfo;
    tuplestore_putvalues(result->setResult,result->setDesc,values,nulls);return (Datum)0;
}
static Datum execute_read(FunctionCallInfo fcinfo,bool forms) {
    int grant_index=forms?3:1;
    int64 byte_grant=PG_GETARG_INT64(grant_index),work_grant=PG_GETARG_INT64(grant_index+2);
    int32 operations=PG_GETARG_INT32(grant_index+1);
    if(byte_grant<=0||work_grant<=0||operations<=0||(uint64)byte_grant>SIZE_MAX||(uint64)work_grant>SIZE_MAX)
        ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),errmsg("physicality descriptor read requires positive finite grants")));
    if(!ActiveSnapshotSet())read_invalid("active statement snapshot required");
    ReadState *s=palloc0(sizeof(*s));s->maximum_bytes=(size_t)byte_grant;s->maximum_work=(size_t)work_grant;s->maximum_operations=operations;
    read_charge(s,sizeof(*s));s->cleanup.func=cleanup;s->cleanup.arg=s;MemoryContextRegisterResetCallback(CurrentMemoryContext,&s->cleanup);
    read_charge(s,toast_raw_datum_size(PG_GETARG_DATUM(0)));
    ArrayType *ids=PG_GETARG_ARRAYTYPE_P(0);
    s->wanted=read_ids(s,ids,&s->wanted_count);qsort(s->wanted,s->wanted_count,sizeof(hash128_t),compare_id);
    vocabulary(s);
    Oid geometry=GetSysCacheOid2(TYPENAMENSP,Anum_pg_type_oid,CStringGetDatum("geometry"),ObjectIdGetDatum(get_namespace_oid("public",false)));
    s->as_binary=LookupFuncName(list_make2(makeString("public"),makeString("st_asbinary")),1,&geometry,false);
    s->snapshot=RegisterSnapshot(GetActiveSnapshot());PushActiveSnapshot(s->snapshot);
    PG_TRY(); {
        if(forms) {
            size_t cursor_bytes=toast_raw_datum_size(PG_GETARG_DATUM(1));
            if(cursor_bytes!=VARHDRSZ && cursor_bytes!=VARHDRSZ+sizeof(hash128_t))
                ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),errmsg("physicality forms requires empty/16-byte cursor")));
            read_charge(s,cursor_bytes);
            bytea *cursor=PG_GETARG_BYTEA_PP(1);int32 page=PG_GETARG_INT32(2);
            if((VARSIZE_ANY_EXHDR(cursor)!=0&&VARSIZE_ANY_EXHDR(cursor)!=16)||page<=0||page==PG_INT32_MAX)
                ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),errmsg("physicality forms requires empty/16-byte cursor and positive finite page size")));
            if(VARSIZE_ANY_EXHDR(cursor)){memcpy(&s->cursor,VARDATA_ANY(cursor),16);s->has_cursor=true;}
            discover(s,ids,cursor,page);
        } else {
            s->roots=read_ids(s,ids,&s->root_count);s->candidates=s->root_count;s->exhausted=true;
            hydrate(s,s->roots,s->root_count);
        }
        decode(s);(void)output(fcinfo,s);
    } PG_FINALLY(); {PopActiveSnapshot();UnregisterSnapshot(s->snapshot);s->snapshot=InvalidSnapshot;cleanup(s);} PG_END_TRY();
    return (Datum)0;
}
Datum pg_laplace_physicality_descriptor_read(PG_FUNCTION_ARGS) {return execute_read(fcinfo,false);}
Datum pg_laplace_physicality_forms(PG_FUNCTION_ARGS) {return execute_read(fcinfo,true);}
