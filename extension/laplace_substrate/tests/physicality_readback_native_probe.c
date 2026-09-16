/* Production C helper checks using real PG headers and native descriptor,
 * trajectory and identity owners. PG allocation/errors/PostGIS conversion are
 * controlled services here; no SQL, index scan or backend execution is claimed. */
#ifndef LAPLACE_READBACK_PG_SOURCE
#define LAPLACE_READBACK_PG_SOURCE "../src/physicality_descriptor_readback.c"
#endif
#include LAPLACE_READBACK_PG_SOURCE
#include <setjmp.h>
#include <stdarg.h>
#undef snprintf
#undef vsnprintf
#undef printf
#undef fprintf
#undef qsort
static unsigned checks, conversions;
static jmp_buf expected_error;
static bool expecting_error;
static char error_text[512];
static size_t raw_override;
MemoryContext CurrentMemoryContext;
volatile sig_atomic_t InterruptPending;
#define CHECK(x) do { ++checks; if(!(x)){fprintf(stderr,"check failed line%d: %s\n",__LINE__,#x);exit(1);} } while(0)
#define REFUSES(x,message) do { expecting_error=true;error_text[0]=0;if(setjmp(expected_error)==0){x;CHECK(false);}expecting_error=false;CHECK(strstr(error_text,message)!=NULL);}while(0)
bool errstart(int level,const char *domain){(void)level;(void)domain;return true;}
bool errstart_cold(int level,const char *domain){return errstart(level,domain);}
int errcode(int code){(void)code;return 0;}
int errmsg(const char *format,...){va_list args;va_start(args,format);vsnprintf(error_text,sizeof(error_text),format,args);va_end(args);return 0;}
void errfinish(const char *file,int line,const char *function){(void)file;(void)line;(void)function;if(expecting_error)longjmp(expected_error,1);fprintf(stderr,"unexpected: %s\n",error_text);exit(1);}
int pg_snprintf(char *buffer,size_t size,const char *format,...){int r;va_list args;va_start(args,format);r=vsnprintf(buffer,size,format,args);va_end(args);return r;}
void pg_qsort(void *base,size_t count,size_t size,int(*cmp)(const void*,const void*)){qsort(base,count,size,cmp);}
void *palloc(Size bytes){void *p=malloc(bytes?bytes:1);CHECK(p!=NULL);return p;}
void *palloc0(Size bytes){void *p=calloc(1,bytes?bytes:1);CHECK(p!=NULL);return p;}
void pfree(void *pointer){free(pointer);}
struct varlena *pg_detoast_datum_packed(struct varlena *datum){return datum;}
struct varlena *pg_detoast_datum(struct varlena *datum){return datum;}
Size toast_raw_datum_size(Datum datum){return raw_override?raw_override:VARSIZE_ANY(DatumGetPointer(datum));}
void ProcessInterrupts(void){CHECK(false);}
text *cstring_to_text_with_len(const char *string,int length){text *p=palloc(VARHDRSZ+length);SET_VARSIZE(p,VARHDRSZ+length);memcpy(VARDATA(p),string,length);return p;}
Datum OidFunctionCall1Coll(Oid function,Oid collation,Datum value){(void)function;(void)collation;++conversions;size_t n=VARSIZE_ANY(DatumGetPointer(value));void *copy=palloc(n);memcpy(copy,DatumGetPointer(value),n);return PointerGetDatum(copy);}
static ReadState state(void){ReadState s={0};static bool seen;seen=false;s.frontier_seen=&seen;s.maximum_bytes=64u*1024u*1024u;s.maximum_work=1000000;s.maximum_operations=128;s.hydration_type=1;return s;}
static bytea *wkb(const double *vertices,uint32 count){size_t header=count==1?5:9;bytea *result=palloc(VARHDRSZ+header+count*32);SET_VARSIZE(result,VARHDRSZ+header+count*32);unsigned char *p=(unsigned char*)VARDATA(result);uint32 type=count==1?3001:3002;p[0]=1;memcpy(p+1,&type,4);if(count!=1)memcpy(p+5,&count,4);memcpy(p+header,vertices,count*32);return result;}
typedef struct IndexedManifest {hash128_t entity;int16 type;int32 count;bytea *geometry;} IndexedManifest;
static IndexedManifest indexed_rows[2];static size_t indexed_count,read_calls,requested_counts[4];static int16 requested_types[4];
ArrayType *construct_array(Datum *items,int count,Oid type,int length,bool byval,char align) {
    CHECK(type==BYTEAOID && length==-1 && !byval && align==TYPALIGN_INT);
    size_t bytes=ARR_OVERHEAD_NONULLS(count?1:0);for(int i=0;i<count;++i)bytes+=INTALIGN(VARSIZE_ANY(DatumGetPointer(items[i])));
    ArrayType *a=palloc0(bytes);SET_VARSIZE(a,bytes);ARR_NDIM(a)=count?1:0;ARR_ELEMTYPE(a)=type;
    if(count){ARR_DIMS(a)[0]=count;ARR_LBOUND(a)[0]=1;}
    char *p=ARR_DATA_PTR(a);for(int i=0;i<count;++i){size_t n=VARSIZE_ANY(DatumGetPointer(items[i]));memcpy(p,DatumGetPointer(items[i]),n);p+=INTALIGN(n);}return a;
}
void laplace_typed_carrier_read_bounded(ArrayType *entities,int16 type,
    LaplaceContentCarrierConsumer consumer,void *opaque,LaplaceContentReadBudget *budget) {
    CHECK(read_calls<4 && budget->maximum_scratch_bytes>0);size_t count=ARR_DIMS(entities)[0];
    requested_types[read_calls]=type;requested_counts[read_calls++]=count;
    if(budget->leaf_reads>=budget->maximum_leaf_reads)read_limit("controlled indexed read operation grant");
    ++budget->leaf_reads;
    const char *cursor=ARR_DATA_PTR(entities);
    for(size_t i=0;i<count;++i){hash128_t id;memcpy(&id,VARDATA_ANY(cursor),16);cursor+=INTALIGN(VARSIZE_ANY(cursor));
        for(size_t j=0;j<indexed_count;++j)if(indexed_rows[j].type==type && !memcmp(&indexed_rows[j].entity,&id,16)) {
            hash128_t placement;laplace_physicality_id_compute(id,type,&placement);Datum p=id_datum(&placement),e=id_datum(&id);
            consumer(p,e,indexed_rows[j].count,PointerGetDatum(indexed_rows[j].geometry),opaque);pfree(DatumGetPointer(p));pfree(DatumGetPointer(e));
        }
    }
}
static physicality_descriptor_basis_t basis(void){physicality_descriptor_basis_t b={0};for(size_t i=0;i<PHYSICALITY_DESCRIPTOR_TAG_COUNT;++i)b.tags[i]=(hash128_t){100+i,700};for(size_t i=0;i<256;++i)b.byte_numbers[i]=(hash128_t){500+i,800};return b;}
int main(void){
    ReadState s=state();read_charge(&s,17);CHECK(s.bytes==17&&s.peak==17);reserve(&s,31);CHECK(s.bytes==17&&s.peak==48);
    s.maximum_bytes=48;REFUSES(read_charge(&s,32),"byte grant");CHECK(s.bytes==17);s.maximum_work=2;work(&s,2);REFUSES(work(&s,1),"logical-work");
    s.maximum_operations=1;operation(&s);REFUSES(operation(&s),"database-operation");CHECK(s.operations==1);
    REFUSES((void)plus(SIZE_MAX,1),"overflow");REFUSES((void)times(SIZE_MAX,2),"overflow");
    SnapshotData snap={0};TransactionId xip[]={43,51};snap.xmin=42;snap.xmax=99;snap.curcid=7;snap.xcnt=2;snap.xip=xip;s=state();s.snapshot=&snap;text *receipt=snapshot_receipt(&s);
    const char *expected="active-mvcc-v1;xmin=42;xmax=99;cid=7;recovery=0;suboverflow=0;xip=43,51;subxip=";
    CHECK(VARSIZE_ANY_EXHDR(receipt)==strlen(expected)&&!memcmp(VARDATA_ANY(receipt),expected,strlen(expected)));pfree(receipt);
    physicality_descriptor_input_t input={0};input.entity_id=(hash128_t){123,456};input.type=3;input.coord[0]=-0.0;input.coord[1]=.25;input.coord[2]=.5;input.coord[3]=.125;input.alignment_residual_is_null=0;input.alignment_residual=-0.0;input.source_dim=7;
    const hash128_t refs[]={{10,20},{30,40}};double packed[8];CHECK(trajectory_build(refs,2,packed)==0);input.trajectory_xyzm=packed;input.trajectory_vertices=2;input.n_constituents=2;
    physicality_descriptor_basis_t b=basis();physicality_descriptor_limits_t limits={8u*1024u*1024u};physicality_descriptor_plan_t *plan=NULL;
    CHECK(physicality_descriptor_plan_build(&input,1,&b,&limits,&plan)==PHYSICALITY_DESCRIPTOR_OK);size_t nodes,children;const physicality_descriptor_node_t *catalog=physicality_descriptor_plan_nodes(plan,&nodes);const hash128_t *operands=physicality_descriptor_plan_children(plan,&children);const hash128_t *roots=physicality_descriptor_plan_roots(plan,NULL);
    const physicality_descriptor_node_t *root=NULL;for(size_t i=0;i<nodes;++i)if(!memcmp(&catalog[i].id,roots,16))root=&catalog[i];CHECK(root!=NULL&&root->child_count==9);
    hash128_t decoded;CHECK(physicality_descriptor_readback_root_entity(root,operands,children,&b,&decoded)&&!memcmp(&decoded,&input.entity_id,16));
    double root_vertices[36];CHECK(trajectory_build(operands+root->first_child,9,root_vertices)==0);bytea *geometry=wkb(root_vertices,9);hash128_t placement;laplace_physicality_id_compute(*roots,1,&placement);Datum pid=id_datum(&placement),eid=id_datum(roots);
    s=state();s.frontier=(hash128_t*)roots;s.frontier_count=1;conversions=0;
    receive_node(pid,eid,9,PointerGetDatum(geometry),&s);
    CHECK(conversions==1&&s.node_count==1&&s.child_count==9&&s.hydrated_nodes==1&&s.work==9);
    CHECK(!memcmp(s.children,operands+root->first_child,9*sizeof(hash128_t)));
    CHECK(physicality_descriptor_readback_root_entity(s.nodes,s.children,s.child_count,&b,&decoded));
    ReadState retention=state();retention.hydration_type=PHYSICALITY_DESCRIPTOR_RETENTION_TYPE;
    retention.frontier=(hash128_t*)roots;retention.frontier_count=1;
    hash128_t retention_placement;laplace_physicality_id_compute(*roots,PHYSICALITY_DESCRIPTOR_RETENTION_TYPE,&retention_placement);
    Datum retention_id=id_datum(&retention_placement);
    REFUSES(receive_node(pid,eid,9,PointerGetDatum(geometry),&retention),"outside requested");
    receive_node(retention_id,eid,9,PointerGetDatum(geometry),&retention);
    CHECK(retention.node_count==1 && retention.child_count==9 && !memcmp(retention.children,s.children,9*sizeof(hash128_t)));
    CHECK(physicality_descriptor_readback_root_entity(retention.nodes,retention.children,retention.child_count,&b,&decoded));
    CHECK(!memcmp(&decoded,&input.entity_id,16));pfree(DatumGetPointer(retention_id));

    REFUSES(receive_node(pid,eid,9,PointerGetDatum(geometry),&s),"duplicate");
    s=state();s.frontier=(hash128_t*)roots;s.frontier_count=1;s.maximum_work=8;conversions=0;
    REFUSES(receive_node(pid,eid,9,PointerGetDatum(geometry),&s),"logical-work");CHECK(conversions==0);
    s=state();s.frontier=(hash128_t*)roots;s.frontier_count=1;s.maximum_bytes=4096;raw_override=8192;conversions=0;
    REFUSES(receive_node(pid,eid,9,PointerGetDatum(geometry),&s),"byte grant");CHECK(conversions==0);raw_override=0;
    s=state();s.frontier=(hash128_t*)roots;s.frontier_count=1;
    REFUSES(receive_node(pid,eid,8,PointerGetDatum(geometry),&s),"stored descriptor carrier count");
    s=state();s.frontier=(hash128_t*)roots;s.frontier_count=1;
    hash128_t other=placement;other.lo^=1;Datum wrong=id_datum(&other);
    REFUSES(receive_node(wrong,eid,9,PointerGetDatum(geometry),&s),"outside requested");pfree(DatumGetPointer(wrong));
    const physicality_descriptor_node_t *other_node=&catalog[0];
    if(!memcmp(&other_node->id,roots,16))other_node=&catalog[1];
    double *other_packed=palloc(other_node->child_count*4*sizeof(double));
    CHECK(trajectory_build(operands+other_node->first_child,other_node->child_count,other_packed)==0);
    bytea *other_geometry=wkb(other_packed,(uint32)other_node->child_count);pfree(other_packed);
    indexed_rows[0]=(IndexedManifest){*roots,PHYSICALITY_DESCRIPTOR_RETENTION_TYPE,9,geometry};
    indexed_rows[1]=(IndexedManifest){other_node->id,1,(int32)other_node->child_count,other_geometry};indexed_count=2;
    hash128_t requested[3]={*roots,other_node->id,*roots};read_calls=0;s=state();hydrate(&s,requested,3);
    CHECK(read_calls==2 && requested_types[0]==9 && requested_counts[0]==2 && requested_types[1]==1 && requested_counts[1]==1);
    CHECK(s.node_count==2 && s.operations==2 && s.rounds==1 && s.frontier==NULL && s.frontier_seen==NULL);
    indexed_count=1;read_calls=0;s=state();hydrate(&s,roots,1);
    CHECK(read_calls==1 && requested_types[0]==9 && s.node_count==1 && s.operations==1);
    indexed_rows[0].type=1;read_calls=0;s=state();hydrate(&s,roots,1);
    CHECK(read_calls==2 && s.node_count==1 && s.operations==2);
    read_calls=0;s=state();s.maximum_operations=1;REFUSES(hydrate(&s,roots,1),"operation grant");
    CHECK(s.node_count==0 && s.operations==1);
    indexed_count=0;read_calls=0;s=state();REFUSES(hydrate(&s,roots,1),"node is absent");CHECK(s.operations==2);
    pfree(other_geometry);
    s=state();Datum exact=binary64_bits(&s,input.coord,4);const unsigned char expected_bits[32]={0,0,0,0,0,0,0,128,0,0,0,0,0,0,208,63,0,0,0,0,0,0,224,63,0,0,0,0,0,0,192,63};CHECK(VARSIZE_ANY_EXHDR(DatumGetPointer(exact))==32&&!memcmp(VARDATA_ANY(DatumGetPointer(exact)),expected_bits,32));pfree(DatumGetPointer(exact));
    physicality_descriptor_readback_t *empty=NULL;size_t empty_count=99;
    CHECK(physicality_descriptor_readback_prepare(NULL,0,NULL,0,NULL,0,&b,&limits,
        limits.maximum_plan_bytes,0,&empty)==PHYSICALITY_DESCRIPTOR_OK);
    CHECK(physicality_descriptor_readback_inputs(empty,&empty_count)==NULL&&empty_count==0);
    CHECK(physicality_descriptor_readback_content_hash_operands(empty)==0);
    physicality_descriptor_readback_free(empty);
    physicality_descriptor_plan_free(plan);pfree(geometry);pfree(DatumGetPointer(pid));pfree(DatumGetPointer(eid));
    printf("physicality readback production helper checks: %u; SQL/backend/index executions: 0\n",checks);return 0;
}
