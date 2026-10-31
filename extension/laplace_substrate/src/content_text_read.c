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
#include "content_text_read.h"
#include "trajectory_wkb.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "perfcache_native.h"
#include "laplace/core/sql_catalog.h"

typedef struct TextMatch
{
    HTAB *ids;
    trajectory_suffix_matcher_t *matcher;
    MemoryContext row;
    Oid as_binary;
    bool matched;
} TextMatch;

static int record_match(void *context,size_t ordinal,size_t stride,const hash128_t *successor)
{
    TextMatch *match=context;
    (void)ordinal;(void)successor;
    CHECK_FOR_INTERRUPTS();
    if(stride) match->matched=true;
    return 0;
}

static void match_trajectory(Datum physicality,Datum entity,Datum geometry,void *context)
{
    TextMatch *match=context;
    (void)physicality;
    hash128_t id=datum_to_hash128(entity);
    if(hash_search(match->ids,&id,HASH_FIND,NULL)) return;
    MemoryContext previous=MemoryContextSwitchTo(match->row);
    bytea *wkb=DatumGetByteaP(OidFunctionCall1(match->as_binary,geometry));
    uint32 count;
    const unsigned char *points=laplace_trajectory_wkb_points(wkb,&count);
    match->matched=false;
    if(trajectory_match_occurrences(match->matcher,points,count,record_match,match)!=0)
        elog(ERROR,"text containment: invalid packed trajectory");
    MemoryContextSwitchTo(previous);
    if(match->matched) hash_search(match->ids,&id,HASH_ENTER,NULL);
    MemoryContextReset(match->row);
}

static ArrayType *set_array(HTAB *ids)
{
    long count=hash_get_num_entries(ids);
    if(count>MaxAllocSize/sizeof(Datum)) elog(ERROR,"text containment: identity set exceeds allocation capacity");
    Datum *values=palloc(Max(count,1)*sizeof(Datum));
    HASH_SEQ_STATUS scan;hash128_t *id;int n=0;
    hash_seq_init(&scan,ids);
    while((id=hash_seq_search(&scan))) values[n++]=hash128_to_datum(id);
    return construct_array(values,n,BYTEAOID,-1,false,TYPALIGN_INT);
}

static int compare_id(const void *a,const void *b) { return memcmp(a,b,sizeof(hash128_t)); }
static int compare_span(const void *a,const void *b)
{
    const tier_node_view_t *x=a,*y=b;
    return (x->text_range_off>y->text_range_off)-(x->text_range_off<y->text_range_off);
}

hash128_t *laplace_content_text_containers(ArrayType *forms,int *count)
{
    if(ARR_NDIM(forms)>1 || ARR_ELEMTYPE(forms)!=TEXTOID)
        elog(ERROR,"text containment requires a one-dimensional text array");
    HASHCTL ctl={0};ctl.keysize=ctl.entrysize=sizeof(hash128_t);ctl.hcxt=CurrentMemoryContext;
    TextMatch match={0};
    match.ids=hash_create("text containment identities",128,&ctl,HASH_ELEM|HASH_BLOBS|HASH_CONTEXT);
    match.row=AllocSetContextCreate(CurrentMemoryContext,"text containment trajectory",ALLOCSET_DEFAULT_SIZES);
    Oid physicalities=get_relname_relid("physicalities",get_namespace_oid("laplace",false));
    Oid geometry=get_atttype(physicalities,get_attnum(physicalities,"trajectory"));
    match.as_binary=LookupFuncName(list_make2(makeString("public"),makeString("st_asbinary")),1,&geometry,false);
    Datum *values;bool *nulls;int n;
    deconstruct_array(forms,TEXTOID,-1,false,TYPALIGN_INT,&values,&nulls,&n);
    for(int f=0;f<n;++f)
    {
        CHECK_FOR_INTERRUPTS();
        if(nulls[f]) continue;
        text *input=DatumGetTextPP(values[f]);
        if(!VARSIZE_ANY_EXHDR(input)) continue;
        if(!laplace_perfcache_ready())
            ereport(ERROR,(errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                errmsg("text containment requires the T0 perfcache"),
                errhint("Configure laplace_substrate.perfcache_path with the installed Unicode cache.")));
        tier_tree_t *tree=NULL;
        if(laplace_content_tree_build_public((const uint8_t*)VARDATA_ANY(input),VARSIZE_ANY_EXHDR(input),&tree)!=0)
            elog(ERROR,"text containment: canonical composition failed");
        PG_TRY();
        {
            hash128_t root;
            if(content_witness_tree_root_id(tree,&root)!=0) elog(ERROR,"text containment: missing root");
            hash_search(match.ids,&root,HASH_ENTER,NULL);
            size_t nodes=tier_tree_node_count(tree);
            uint32 root_index=TIER_TREE_INVALID;
            for(uint32 i=0;i<nodes;++i)
            {
                tier_node_view_t node;
                if(tier_tree_get_node(tree,i,&node)!=0) elog(ERROR,"text containment: invalid node");
                if(node.parent_idx==TIER_TREE_INVALID && hash128_equals(&node.id,&root)) root_index=i;
            }
            if(root_index==TIER_TREE_INVALID) elog(ERROR,"text containment: missing root node");
            /* A one-child composition is its child: collapse to the node whose
             * children are the real operands. */
            root_index=laplace_tier_tree_collapse_index(tree,root_index);
            if(nodes>MaxAllocSize/sizeof(tier_node_view_t)) elog(ERROR,"text containment: input exceeds allocation capacity");
            hash128_t *pattern=palloc(Max(nodes,1)*sizeof(hash128_t));
            Datum *members=palloc(Max(nodes,1)*sizeof(Datum));
            tier_node_view_t *children=palloc(Max(nodes,1)*sizeof(tier_node_view_t));
            int width=0;
            /* The pattern is the collapsed root's immediate children in text
             * order, repeats included, as the recipe composed them. */
            for(uint32 i=0;i<nodes;++i)
            {
                tier_node_view_t node;
                tier_tree_get_node(tree,i,&node);
                if(node.parent_idx==root_index && i!=root_index)
                    children[width++]=node;
            }
            qsort(children,width,sizeof(*children),compare_span);
            for(int i=0;i<width;++i)
            { pattern[i]=children[i].id;members[i]=hash128_to_datum(&children[i].id); }
            if(width)
            {
                match.matcher=trajectory_suffix_matcher_create(pattern,width,width);
                if(!match.matcher) elog(ERROR,"text containment: matcher allocation failed");
                laplace_content_membership_read(construct_array(members,width,BYTEAOID,-1,false,TYPALIGN_INT),true,
                                                match_trajectory,&match);
                trajectory_suffix_matcher_free(match.matcher);match.matcher=NULL;
            }
            pfree(pattern);pfree(members);pfree(children);
        }
        PG_CATCH();
        {
            trajectory_suffix_matcher_free(match.matcher);
            tier_tree_free(tree);
            PG_RE_THROW();
        }
        PG_END_TRY();
        tier_tree_free(tree);
    }
    /* Containers are themselves contained: ascend by whole frontiers through
     * the membership read until no new entity appears. */
    ArrayType *frontier=set_array(match.ids);
    for(;;)
    {
        CHECK_FOR_INTERRUPTS();
        int parent_count;
        hash128_t *parents=laplace_content_membership_entities(frontier,false,&parent_count);
        ArrayBuildState *next=NULL;
        for(int i=0;i<parent_count;++i)
        {
            bool found;
            hash_search(match.ids,&parents[i],HASH_ENTER,&found);
            if(!found) next=accumArrayResult(next,hash128_to_datum(&parents[i]),false,BYTEAOID,CurrentMemoryContext);
        }
        pfree(parents);pfree(frontier);
        if(!next) break;
        frontier=DatumGetArrayTypeP(makeArrayResult(next,CurrentMemoryContext));
    }
    long total=hash_get_num_entries(match.ids);
    if(total>MaxAllocSize/sizeof(hash128_t)) elog(ERROR,"text containment: result exceeds allocation capacity");
    /* The composed root may not be stored: keep only ids with stored facets,
     * read in one set query. */
    bool spi_top=false;
    if(laplace_spi_connect(&spi_top)!=SPI_OK_CONNECT) elog(ERROR,"text containment: connect failed");
    static SPIPlanPtr text_facets_plan=NULL;
    Oid argtypes[]={BYTEAARRAYOID};Datum args[]={PointerGetDatum(set_array(match.ids))};
    if(SPI_execute_plan(spi_common_ensure_plan(&text_facets_plan,laplace_sql_query_text("entity.facets"),1,argtypes),args,NULL,true,0)!=SPI_OK_SELECT)
        elog(ERROR,"text containment: facet read failed");
    hash128_t *out=palloc(Max(total,1)*sizeof(hash128_t));*count=0;
    for(uint64 i=0;i<SPI_processed;++i)
    {
        bool isnull;
        hash128_t id=datum_to_hash128(SPI_getbinval(SPI_tuptable->vals[i],SPI_tuptable->tupdesc,1,&isnull));
        /* An id can return more than one facet row. */
        bool found;
        hash_search(match.ids,&id,HASH_REMOVE,&found);
        if(found) out[(*count)++]=id;
    }
    SPI_freetuptable(SPI_tuptable);
    qsort(out,*count,sizeof(hash128_t),compare_id);
    hash_destroy(match.ids);MemoryContextDelete(match.row);
    /* Allocate the result in the caller's context before finishing top-level SPI. */
    hash128_t *result=SPI_palloc(Max(*count,1)*sizeof(hash128_t));
    memcpy(result,out,*count*sizeof(hash128_t));
    laplace_spi_finish(spi_top);
    return result;
}

PG_FUNCTION_INFO_V1(pg_laplace_content_text_containers);
Datum pg_laplace_content_text_containers(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo,MAT_SRF_USE_EXPECTED_DESC);
    if(PG_ARGISNULL(0)) return (Datum)0;
    ReturnSetInfo *r=(ReturnSetInfo*)fcinfo->resultinfo;int count;
    hash128_t *ids=laplace_content_text_containers(PG_GETARG_ARRAYTYPE_P(0),&count);
    for(int i=0;i<count;++i)
    {
        Datum values[]={hash128_to_datum(&ids[i])};bool nulls[]={false};
        tuplestore_putvalues(r->setResult,r->setDesc,values,nulls);
    }
    return (Datum)0;
}

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
