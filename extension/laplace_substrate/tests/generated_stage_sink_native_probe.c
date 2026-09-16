/* Actual production sink helpers + actual native identity/stage/trajectory owners.
 * Only PostgreSQL memory/array/SPI services below are controlled doubles. This
 * checks transport, acceptance and grouping; it does not claim backend execution. */
#include "../src/generated_stage_sink.c"
#include <setjmp.h>
#include <stdarg.h>
#undef snprintf
#undef vsnprintf
#undef printf
#undef fprintf
#undef qsort
static unsigned checks;
static jmp_buf expected_error;
static bool expecting_error;
static char error_text[512];
static int error_code;
MemoryContext CurrentMemoryContext;
volatile sig_atomic_t InterruptPending;
uint64 SPI_processed;
SPITupleTable *SPI_tuptable;
int XactIsoLevel=XACT_READ_COMMITTED;
sigjmp_buf *PG_exception_stack;
ErrorContextCallback *error_context_stack;
static unsigned deleted_contexts;
typedef struct ProbeContext { MemoryContextCallback *callbacks; } ProbeContext;

#define CHECK(expression) do { ++checks; if (!(expression)) { \
 fprintf(stderr,"check failed at %s:%d: %s\n",__FILE__,__LINE__,#expression); exit(1); } } while(0)
#define REFUSES(expression, fragment) do { \
 expecting_error=true; error_text[0]=0; \
 if(setjmp(expected_error)==0) { expression; CHECK(false); } \
 expecting_error=false; CHECK(strstr(error_text,fragment)!=NULL); \
} while(0)
bool errstart(int level, const char *domain) { (void)level; (void)domain; return true; }
bool errstart_cold(int level, const char *domain) { return errstart(level, domain); }
int errcode(int code) { error_code = code; return 0; }
int errmsg(const char *format, ...) {
    va_list args; va_start(args, format); vsnprintf(error_text, sizeof(error_text), format, args); va_end(args); return 0;
}
int errmsg_internal(const char *format, ...) {
    va_list args; va_start(args,format);vsnprintf(error_text,sizeof(error_text),format,args);va_end(args);return 0;
}
void errfinish(const char *file, int line, const char *function) {
    (void)file; (void)line; (void)function;
    if (PG_exception_stack != NULL) siglongjmp(*PG_exception_stack,1);
    if (expecting_error) longjmp(expected_error, 1);
    fprintf(stderr, "unexpected PG test error %d: %s\n", error_code, error_text); exit(1);
}
int pg_snprintf(char *buffer, size_t size, const char *format, ...) {
    int result; va_list args; va_start(args, format); result = vsnprintf(buffer, size, format, args); va_end(args); return result;
}
void pg_qsort(void *base, size_t count, size_t size, int (*compare)(const void *, const void *)) {
    qsort(base, count, size, compare);
}
void *palloc(Size bytes) { void *p = malloc(bytes ? bytes : 1); CHECK(p != NULL); return p; }
void *palloc0(Size bytes) { void *p = calloc(1, bytes ? bytes : 1); CHECK(p != NULL); return p; }
void *MemoryContextAllocZero(MemoryContext context, Size bytes) { (void)context; return palloc0(bytes); }
void pfree(void *pointer) { free(pointer); }
struct varlena *pg_detoast_datum_packed(struct varlena *datum) { return datum; }
struct varlena *pg_detoast_datum(struct varlena *datum) { return datum; }

MemoryContext AllocSetContextCreateInternal(MemoryContext parent,const char *name,
    Size minimum,Size initial,Size maximum)
{
    (void)parent;(void)name;(void)minimum;(void)initial;(void)maximum;
    return (MemoryContext)palloc0(sizeof(ProbeContext));
}
void MemoryContextRegisterResetCallback(MemoryContext context,MemoryContextCallback *callback)
{
    ProbeContext *owner=(ProbeContext *)context;
    callback->next=owner->callbacks;owner->callbacks=callback;
}
void MemoryContextDelete(MemoryContext context)
{
    ProbeContext *owner=(ProbeContext *)context;
    for(MemoryContextCallback *callback=owner->callbacks;callback!=NULL;callback=callback->next)
        callback->func(callback->arg);
    ++deleted_contexts;pfree(owner);
}
void pg_re_throw(void)
{
    if(PG_exception_stack != NULL)siglongjmp(*PG_exception_stack,1);
    if(expecting_error)longjmp(expected_error,1);
    abort();
}

static Datum result_values[256];
static HeapTuple result_rows[256];
static SPITupleTable result_table;
static unsigned query_calls[SQ_COUNT],prepares;
static hash128_t novel_entity,missing_entity;
static bool omit_reference,all_present,accept_none;
static size_t accept_limit=SIZE_MAX;
static size_t actual_cells,actual_groups;
static int64 actual_games,actual_score;
static int64 period_games[8],period_scores[8],period_ratings[8],period_phis[8];
static hash128_t first_accepted;

/* Array contents are explicit Datum vectors in this controlled SPI double.
 * Production uses PostgreSQL construct_md_array; no emulated SQL is evaluated. */
static Datum *array_values(Datum value)
{
    return (Datum *)ARR_DATA_PTR(DatumGetArrayTypeP(value));
}
static size_t array_count(Datum value)
{
    ArrayType *a=DatumGetArrayTypeP(value);
    return ARR_NDIM(a)==0?0:(size_t)ARR_DIMS(a)[0];
}
ArrayType *construct_md_array(Datum *values,bool *nulls,int ndims,int *dims,int *lbs,
                             Oid type,int width,bool byval,char align)
{
    (void)width;(void)byval;(void)align;
    CHECK(ndims==1 && dims[0]>=0);
    size_t count=(size_t)dims[0],bytes=ARR_OVERHEAD_NONULLS(1)+count*sizeof(Datum);
    ArrayType *out=palloc0(bytes);
    SET_VARSIZE(out,bytes); out->ndim=1;out->elemtype=type;
    ARR_DIMS(out)[0]=dims[0];ARR_LBOUND(out)[0]=lbs[0];
    Datum *data=(Datum *)ARR_DATA_PTR(out);
    for(size_t i=0;i<count;++i)data[i]=nulls!=NULL&&nulls[i]?0:values[i];
    return out;
}
static void result_begin(void)
{
    SPI_processed=0;SPI_tuptable=&result_table;result_table.vals=result_rows;
}
static void result_add(Datum value)
{
    CHECK(SPI_processed<256);result_values[SPI_processed]=value;
    result_rows[SPI_processed]=(HeapTuple)&result_values[SPI_processed];++SPI_processed;
    result_table.numvals=SPI_processed;
}
Datum SPI_getbinval(HeapTuple tuple,TupleDesc desc,int column,bool *isnull)
{
    (void)desc;CHECK(column==1);*isnull=false;return *(Datum *)tuple;
}
void SPI_freetuptable(SPITupleTable *table)
{
    CHECK(table==&result_table);SPI_tuptable=NULL;
}
SPIPlanPtr SPI_prepare(const char *sql,int nargs,Oid *types)
{
    (void)nargs;(void)types;++prepares;
    for(unsigned i=0;i<SQ_COUNT;++i)
        if(strcmp(sql,laplace_sql_query_text(sink_keys[i]))==0)return (SPIPlanPtr)(uintptr_t)(i+1);
    CHECK(false);return NULL;
}
int SPI_keepplan(SPIPlanPtr plan) { CHECK(plan!=NULL);return 0; }
const char *SPI_result_code_string(int code) { (void)code;return "controlled SPI result"; }
int SPI_connect(void) { return SPI_OK_CONNECT; }
int SPI_finish(void) { return SPI_OK_FINISH; }
int SPI_execute_plan(SPIPlanPtr plan,Datum *values,const char *nulls,bool read_only,long count)
{
    CHECK(plan!=NULL && nulls==NULL && !read_only && count==0);
    enum SinkQuery query=(enum SinkQuery)((uintptr_t)plan-1);
    CHECK(query<SQ_COUNT);++query_calls[query];result_begin();
    if(query==SQ_PRESENCE) {
        Datum *ids=array_values(values[0]);size_t n=array_count(values[0]);
        for(size_t i=0;i<n;++i) {
            bytea *value=DatumGetByteaPP(ids[i]);hash128_t id;
            CHECK(VARSIZE_ANY_EXHDR(value)==16);memcpy(&id,VARDATA_ANY(value),16);
            if(!all_present && hash128_equals(&id,&novel_entity))continue;
            if(omit_reference && hash128_equals(&id,&missing_entity))continue;
            result_add(ids[i]);
        }
        return SPI_OK_SELECT;
    }
    if(query>=SQ_ENTITIES && query<=SQ_ATTESTATIONS) {
        size_t n=array_count(values[0]);Datum *ids=array_values(values[0]);
        for(unsigned c=0;c<sink_column_counts[query-SQ_ENTITIES];++c)
            CHECK(array_count(values[c])==n);
        if(query==SQ_ENTITIES)CHECK(n==1 && DatumGetInt16(array_values(values[1])[0])==1);
        if(query==SQ_PHYSICALITIES)CHECK(n==1);
        if(!(query==SQ_ATTESTATIONS && accept_none))
            for(size_t i=0;i<n && (query!=SQ_ATTESTATIONS || i<accept_limit);++i) {
                result_add(ids[i]);
                if(query==SQ_ATTESTATIONS && i==0)
                    memcpy(&first_accepted,VARDATA_ANY(DatumGetByteaPP(ids[i])),16);
            }
        return SPI_OK_INSERT_RETURNING;
    }
    if(query==SQ_FOLD) {
        actual_cells=array_count(values[0]);CHECK(actual_cells==1);
        actual_games=DatumGetInt64(array_values(values[4])[0]);
        actual_score=DatumGetInt64(array_values(values[5])[0]);
        CHECK(array_count(values[8])==actual_cells+1);
        CHECK(DatumGetInt64(array_values(values[8])[0])==0);
        actual_groups=(size_t)DatumGetInt64(array_values(values[8])[actual_cells]);
        CHECK(actual_groups>0 && actual_groups<=8);
        for(unsigned c=9;c<13;++c)CHECK(array_count(values[c])==actual_groups);
        for(size_t i=0;i<actual_groups;++i) {
            period_ratings[i]=DatumGetInt64(array_values(values[9])[i]);
            period_phis[i]=DatumGetInt64(array_values(values[10])[i]);
            period_games[i]=DatumGetInt64(array_values(values[11])[i]);
            period_scores[i]=DatumGetInt64(array_values(values[12])[i]);
        }
        result_add(Int64GetDatum((int64)actual_cells));return SPI_OK_SELECT;
    }
    if(query==SQ_MASKS) {
        CHECK(array_count(values[0])==2*actual_cells && array_count(values[1])==2*actual_cells);
        result_add(Int64GetDatum((int64)(2*actual_cells)));return SPI_OK_SELECT;
    }
    CHECK(query==SQ_EPOCH || query==SQ_LOCK);result_add(Int64GetDatum(1));return SPI_OK_SELECT;
}

static SinkState *probe_state(const intent_stage_t *const *stages,size_t count)
{
    SinkState *s=palloc0(sizeof(*s));
    s->limits=(LaplaceGeneratedStageSinkLimits){10000,64*1024*1024,100000,100};
    sink_charge(s,sizeof(*s));
    for(size_t i=0;i<count;++i) {
        s->receipt.input_rows[0]+=intent_stage_entity_count(stages[i]);
        s->receipt.input_rows[1]+=intent_stage_physicality_count(stages[i]);
        s->receipt.input_rows[2]+=intent_stage_attestation_count(stages[i]);
    }
    return s;
}
static void reset_spi(void)
{
    memset(query_calls,0,sizeof(query_calls));actual_cells=actual_groups=0;actual_games=actual_score=0;
    accept_none=all_present=omit_reference=false;accept_limit=SIZE_MAX;
}
static void add_witness(intent_stage_t *stage,const hash128_t *subject,const hash128_t *object,
                        hash128_t source,hash128_t context,int64 games,int64 score,int64 phi,int64 rating)
{
    hash128_t relation,id;int16 outcome;
    CHECK(laplace_relation_resolve("HAS_PHYSICALITY",&relation)==0);
    laplace_attestation_id_compute(subject,&relation,object,0,&source,&context,0,&id);
    CHECK(laplace_attestation_outcome_from_totals_fp(games,score,&outcome)==0);
    CHECK(intent_stage_add_attestation(stage,&id,subject,&relation,object,&source,&context,outcome,
        INTENT_STAGE_PG_EPOCH_UNIX_US+555,games,score,phi,rating,NULL)==0);
}

int main(void)
{
    hash128_t children[2]={{11,12},{21,22}},source={31,32},context={41,42},placement;
    double trajectory[8],coord[4]={.1,.2,.3,.4};hilbert128_t hilbert;
    CHECK(trajectory_build(children,2,trajectory)==0);
    size_t logical;CHECK(trajectory_content_identity(trajectory,2,&novel_entity,&logical)==0 && logical==2);
    laplace_physicality_id_compute(novel_entity,1,&placement);hilbert4d_encode(coord,&hilbert);
    intent_stage_t *stage=intent_stage_new(8);
    hash128_t tier1=laplace_content_tier_type_id(1),tier2=laplace_content_tier_type_id(2);
    CHECK(intent_stage_add_entity(stage,&novel_entity,2,&tier2,&source)==0);
    CHECK(intent_stage_add_entity(stage,&novel_entity,1,&tier1,&source)==0);
    for(unsigned i=0;i<2;++i)CHECK(intent_stage_add_physicality(stage,&placement,&novel_entity,1,
        coord,&hilbert,trajectory,2,2,1,0,1,0,INTENT_STAGE_PG_EPOCH_UNIX_US+i)==0);
    add_witness(stage,&children[0],&novel_entity,source,context,1,1000000000,100,1500000000000);
    add_witness(stage,&children[0],&novel_entity,source,context,1,1000000000,100,1500000000000);
    add_witness(stage,&children[0],&novel_entity,(hash128_t){51,52},(hash128_t){61,62},2,1000000000,200,1600000000000);
    add_witness(stage,&children[0],&novel_entity,(hash128_t){71,72},(hash128_t){81,82},3,1500000000,100,1500000000000);
    const intent_stage_t *stages[1]={stage};
    SinkState *s=probe_state(stages,1);
    sink_parse(s,stages,1);
    CHECK(s->receipt.distinct_rows[0]==1 && s->receipt.distinct_rows[1]==1 && s->receipt.distinct_rows[2]==3);
    reset_spi();sink_validate_bodies(s,stages,1);
    CHECK(s->receipt.logical_work==4 && s->receipt.stored_vertices==4);
    for(unsigned i=0;i<3;++i)sink_insert(s,i);
    sink_fold(s);
    CHECK(s->receipt.inserted_rows[0]==1 && s->receipt.inserted_rows[1]==1 && s->receipt.inserted_rows[2]==3);
    CHECK(actual_games==6 && actual_score==3500000000 && actual_groups==2);
    CHECK(period_games[0]+period_games[1]==6 && period_scores[0]+period_scores[1]==3500000000);
    CHECK((period_games[0]==4 && period_phis[0]==100 && period_ratings[0]==1500000000000) ||
          (period_games[1]==4 && period_phis[1]==100 && period_ratings[1]==1500000000000));
    CHECK(s->receipt.folded_cells==1 && s->receipt.folded_observations==6 && s->receipt.mask_pairs==2);
    CHECK(query_calls[SQ_PRESENCE]==1 && query_calls[SQ_ENTITIES]==1 && query_calls[SQ_PHYSICALITIES]==1 &&
          query_calls[SQ_ATTESTATIONS]==1 && query_calls[SQ_FOLD]==1 && query_calls[SQ_MASKS]==1);
    CHECK(s->receipt.operations==12 && prepares==6);
    sink_cleanup(s);

    /* Conflicts returned by PostgreSQL are replays. No accepted A means no
     * consensus or highway call, even when other stage rows are present. */
    reset_spi();all_present=accept_none=true;
    s=probe_state(stages,1);sink_parse(s,stages,1);sink_validate_bodies(s,stages,1);
    for(unsigned i=0;i<3;++i) { sink_insert(s,i); }
    sink_fold(s);
    CHECK(s->receipt.inserted_rows[0]==0 && s->receipt.inserted_rows[2]==0);
    CHECK(query_calls[SQ_ENTITIES]==0 && query_calls[SQ_FOLD]==0 && query_calls[SQ_MASKS]==0);
    sink_cleanup(s);

    /* Only the exact subset returned by INSERT participates in the fold. */
    reset_spi();accept_limit=1;s=probe_state(stages,1);sink_parse(s,stages,1);
    sink_validate_bodies(s,stages,1);sink_insert(s,2);sink_fold(s);
    SinkRow *accepted=sink_find(&s->tables[2],&first_accepted);CHECK(accepted!=NULL);
    CHECK(actual_games==sink_integer(&accepted->fields[8]) && actual_score==sink_integer(&accepted->fields[9]));
    CHECK(actual_groups==1 && s->receipt.inserted_rows[2]==1);sink_cleanup(s);

    reset_spi();s=probe_state(stages,1);sink_parse(s,stages,1);s->limits.maximum_logical_occurrences=3;
    REFUSES(sink_validate_bodies(s,stages,1),"logical work grant");CHECK(query_calls[SQ_PRESENCE]==0);
    s->limits.maximum_logical_occurrences=100;s->limits.maximum_bytes=s->bytes+1;
    REFUSES(sink_validate_bodies(s,stages,1),"native export byte grant");
    s=probe_state(stages,1);sink_parse(s,stages,1);s->limits.maximum_operations=0;
    REFUSES(sink_validate_bodies(s,stages,1),"operation grant");sink_cleanup(s);
    reset_spi();omit_reference=true;missing_entity=children[1];s=probe_state(stages,1);sink_parse(s,stages,1);
    REFUSES(sink_validate_bodies(s,stages,1),"referenced entity");sink_cleanup(s);

    /* Exercise the exported entry, including context cleanup and the actual
     * reentrant lock, prepare and execution receipt. PostgreSQL services remain
     * explicit doubles; transaction rollback belongs to the backend fixture. */
    reset_spi();LaplaceGeneratedStageSinkReceipt receipt;
    LaplaceGeneratedStageSinkLimits limits={1000,64*1024*1024,1000,100};
    unsigned prepares_before=prepares,contexts_before=deleted_contexts;
    laplace_generated_stage_sink(stages,1,&limits,&receipt);
    unsigned executions=0;for(unsigned i=0;i<SQ_COUNT;++i)executions+=query_calls[i];
    CHECK(receipt.operations==executions+prepares-prepares_before);
    CHECK(query_calls[SQ_LOCK]==1 && query_calls[SQ_EPOCH]==1);
    CHECK(receipt.inserted_rows[2]==3 && deleted_contexts==contexts_before+1);
    reset_spi();memset(&receipt,0x55,sizeof(receipt));
    LaplaceGeneratedStageSinkReceipt unchanged=receipt;
    limits.maximum_operations=0;
    REFUSES(laplace_generated_stage_sink(stages,1,&limits,&receipt),"operation grant");
    CHECK(memcmp(&receipt,&unchanged,sizeof(receipt))==0 && query_calls[SQ_LOCK]==0);
    limits.maximum_operations=100;limits.maximum_rows=1;
    REFUSES(laplace_generated_stage_sink(stages,1,&limits,&receipt),"row grant");
    CHECK(memcmp(&receipt,&unchanged,sizeof(receipt))==0);

    /* Framing uses native table enum1/2/3; malformed same-placement body is
     * refused instead of silently choosing an arbitrary different form. */
    coord[0]=.3;
    CHECK(intent_stage_add_physicality(stage,&placement,&novel_entity,1,coord,&hilbert,trajectory,2,2,1,0,1,0,
        INTENT_STAGE_PG_EPOCH_UNIX_US)==0);
    s=probe_state(stages,1);REFUSES(sink_parse(s,stages,1),"conflicting fields");
    /* A valid placement key does not authenticate arbitrary Content bytes. */
    intent_stage_t *forged=intent_stage_new(1);
    hash128_t false_entity={991,992},false_placement;
    laplace_physicality_id_compute(false_entity,1,&false_placement);
    CHECK(intent_stage_add_physicality(forged,&placement,&novel_entity,1,coord,&hilbert,
        trajectory,2,2,1,0,1,0,INTENT_STAGE_PG_EPOCH_UNIX_US)==0);
    size_t tuple_bytes,offset=0;
    const uint8 *tuple=intent_stage_tuple_ptr(forged,INTENT_STAGE_TABLE_PHYSICALITIES,&tuple_bytes);
    uint8 *corrupt=palloc(tuple_bytes);memcpy(corrupt,tuple,tuple_bytes);
    SinkRow parsed={0};sink_read_row(corrupt,tuple_bytes,&offset,10,&parsed);
    memcpy((uint8 *)parsed.fields[0].data,&false_placement,16);
    memcpy((uint8 *)parsed.fields[1].data,&false_entity,16);
    intent_stage_free(forged);forged=NULL;
    CHECK(intent_stage_from_tuple_bytes(NULL,0,corrupt,tuple_bytes,NULL,0,1024*1024,&forged)==0);
    pfree(corrupt);
    const intent_stage_t *forged_stages[1]={forged};
    reset_spi();s=probe_state(forged_stages,1);sink_parse(s,forged_stages,1);
    REFUSES(sink_validate_bodies(s,forged_stages,1),"Content identity");
    CHECK(query_calls[SQ_PRESENCE]==0);sink_cleanup(s);intent_stage_free(forged);
    forged=intent_stage_new(1);
    hash128_t relation,wrong_id={111,222};CHECK(laplace_relation_resolve("HAS_PHYSICALITY",&relation)==0);
    CHECK(intent_stage_add_attestation(forged,&wrong_id,&children[0],&relation,&novel_entity,&source,
        &context,2,INTENT_STAGE_PG_EPOCH_UNIX_US,1,1000000000,100,1500000000000,NULL)==0);
    forged_stages[0]=forged;s=probe_state(forged_stages,1);
    REFUSES(sink_parse(s,forged_stages,1),"five-tuple");intent_stage_free(forged);
    XactIsoLevel=XACT_REPEATABLE_READ;REFUSES(laplace_generated_stage_sink_lock(),"READ COMMITTED");
    XactIsoLevel=XACT_READ_COMMITTED;laplace_generated_stage_sink_lock();laplace_generated_stage_sink_lock();
    CHECK(query_calls[SQ_LOCK]==2);
    REFUSES((void)sink_sum(INT64_MAX,1),"aggregate overflow");
    intent_stage_free(stage);
    printf("%u checks passed: production sink parsing, budgets, references, actual accepted-subset grouping, replay and lock contract; controlled PostgreSQL services only\n",checks);
    return 0;
}
