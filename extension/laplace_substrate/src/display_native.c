#include "postgres.h"
#include "miscadmin.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "utils/timestamp.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/sql_catalog.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "trajectory_wkb.h"

/* Ordered, lazy display election. Only unresolved ids enter the next stage.
 * A textual preview follows one first-child spine in native C; no recursive
 * SQL, per-row rendering, sibling expansion or whole-document reconstruction. */
typedef struct {
    hash128_t id, type, source, target, chosen_evidence;
    int16 tier, target_tier;
    bool exists, has_type, has_source, has_target;
    int64 chosen_time;
    char *label;
} DisplayItem;

static const char *query_keys[] = {
    "display.facets", "display.names", "display.text", "display.metadata_labels",
    "display.file_metadata", "display.definition_types", "display.definitions",
    "display.definition_owners", "display.heads", "entity.facets"
};
static SPIPlanPtr plans[lengthof(query_keys)];
enum { FACETS,NAMES,TEXT,METADATA,FILE_META,DEF_TYPES,DEF_OWNED,DEF_INVERSE,HEADS,NODE_TIERS };

static int
run_query(int which, ArrayType *ids, ArrayType *types)
{
    int n = which == DEF_TYPES ? 0 : which == DEF_OWNED || which == DEF_INVERSE ? 2 : 1;
    Oid oids[] = {BYTEAARRAYOID,BYTEAARRAYOID};
    if (plans[which] == NULL) {
        plans[which] = SPI_prepare_cursor(laplace_sql_query_text(query_keys[which]),n,oids,CURSOR_OPT_GENERIC_PLAN);
        if (plans[which] == NULL || SPI_keepplan(plans[which]) != 0)
            elog(ERROR,"display: cannot retain %s",query_keys[which]);
    }
    Datum args[] = {PointerGetDatum(ids),PointerGetDatum(types)};
    int rc = SPI_execute_plan(plans[which],args,NULL,true,0);
    if (rc != SPI_OK_SELECT) elog(ERROR,"display: read failed for %s",query_keys[which]);
    return rc;
}

static ArrayType *
item_ids(DisplayItem **items, int n, bool targets)
{
    Datum *values = palloc(Max(n,1)*sizeof(Datum));
    for (int i=0;i<n;++i) values[i]=hash128_to_datum(targets?&items[i]->target:&items[i]->id);
    return construct_array(values,n,BYTEAOID,-1,false,TYPALIGN_INT);
}

static char **
batch_text(int which, DisplayItem **items, int n, bool targets)
{
    char **out = palloc0(Max(n,1)*sizeof(char*));
    if (n==0) return out;
    run_query(which,item_ids(items,n,targets),NULL);
    if (SPI_processed != 1) elog(ERROR,"display: batch renderer returned no array");
    bool isnull;
    Datum array=SPI_getbinval(SPI_tuptable->vals[0],SPI_tuptable->tupdesc,1,&isnull);
    if (!isnull) {
        Datum *values; bool *nulls; int count;
        deconstruct_array(DatumGetArrayTypeP(array),TEXTOID,-1,false,TYPALIGN_INT,&values,&nulls,&count);
        if (count!=n) elog(ERROR,"display: batch renderer lost positional alignment");
        for(int i=0;i<n;++i) if(!nulls[i]) out[i]=TextDatumGetCString(values[i]);
    }
    SPI_freetuptable(SPI_tuptable);
    return out;
}

static bool
opaque_name(const char *s, bool lexicon)
{
    if (s==NULL || *s=='\0') return true;
    Size n=strlen(s),i=0;
    while(i<n && ((s[i]>='0'&&s[i]<='9')||(s[i]>='a'&&s[i]<='f')||(s[i]>='A'&&s[i]<='F'))) ++i;
    if(i==32 && (n==32 || strcmp(s+32,"…")==0 || strcmp(s+32,"...")==0)) return true;
    if(!lexicon) return false;
    if(s[0]=='i' && n>1) { i=1; while(i<n && s[i]>='0'&&s[i]<='9') ++i; if(i==n) return true; }
    i=0; while(i<n && s[i]>='0'&&s[i]<='9') ++i;
    return i>=6 && n==i+2 && s[i]=='-' && strchr("nvarspNVARSP",s[i+1])!=NULL;
}

static int
pending(DisplayItem **all,int n,DisplayItem **out,int mode)
{
    int count=0;
    for(int i=0;i<n;++i) if(all[i]->label==NULL &&
        (mode==0 || (mode==1 && all[i]->exists && all[i]->tier<=3) ||
         (mode==2 && !all[i]->has_target))) out[count++]=all[i];
    return count;
}

static void
choose_targets(int which,DisplayItem **items,int n,ArrayType *definition_types,HTAB *lookup)
{
    if(n==0) return;
    run_query(which,item_ids(items,n,false),definition_types);
    for(uint64 i=0;i<SPI_processed;++i) {
        HeapTuple t=SPI_tuptable->vals[i];TupleDesc d=SPI_tuptable->tupdesc;bool isnull;
        hash128_t owner=datum_to_hash128(SPI_getbinval(t,d,1,&isnull));
        DisplayItem *item=hash_search(lookup,&owner,HASH_FIND,NULL);
        if(item==NULL || item->label!=NULL) continue;
        int64 time=DatumGetTimestampTz(SPI_getbinval(t,d,3,&isnull));
        hash128_t evidence=datum_to_hash128(SPI_getbinval(t,d,4,&isnull));
        if(!item->has_target || time>item->chosen_time || (time==item->chosen_time &&
            memcmp(&evidence,&item->chosen_evidence,sizeof(hash128_t))<0)) {
            item->target=datum_to_hash128(SPI_getbinval(t,d,2,&isnull));
            item->has_target=true;item->chosen_time=time;item->chosen_evidence=evidence;
        }
    }
    SPI_freetuptable(SPI_tuptable);
}

typedef struct {hash128_t id,physicality,child;int16 tier;bool found;} SpineHead;

static void
preview(DisplayItem **items,int n)
{
    if(n==0) return;
    HASHCTL ctl={0};ctl.keysize=sizeof(hash128_t);ctl.entrysize=sizeof(SpineHead);
    HTAB *nodes=hash_create("display preview nodes",n,&ctl,HASH_ELEM|HASH_BLOBS);
    for(int i=0;i<n;++i) {
        bool found;SpineHead *node=hash_search(nodes,&items[i]->target,HASH_ENTER,&found);
        if(!found) {node->tier=INT16_MAX;node->found=false;}
    }
    run_query(NODE_TIERS,item_ids(items,n,true),NULL);
    for(uint64 i=0;i<SPI_processed;++i) {
        bool isnull;HeapTuple t=SPI_tuptable->vals[i];TupleDesc d=SPI_tuptable->tupdesc;
        hash128_t id=datum_to_hash128(SPI_getbinval(t,d,1,&isnull));
        SpineHead *node=hash_search(nodes,&id,HASH_FIND,NULL);
        if(node) node->tier=DatumGetInt16(SPI_getbinval(t,d,2,&isnull));
    }
    SPI_freetuptable(SPI_tuptable);
    for(int i=0;i<n;++i) {
        SpineHead *node=hash_search(nodes,&items[i]->target,HASH_FIND,NULL);
        items[i]->target_tier=node->tier;
    }
    hash_destroy(nodes);
    DisplayItem **active=palloc(n*sizeof(DisplayItem*));
    for(int depth=0;depth<32;++depth) {
        CHECK_FOR_INTERRUPTS();
        int count=0;
        for(int i=0;i<n;++i) if(items[i]->has_target && items[i]->target_tier>3) active[count++]=items[i];
        if(count==0) break;
        nodes=hash_create("display preview frontier",count,&ctl,HASH_ELEM|HASH_BLOBS);
        for(int i=0;i<count;++i) {
            bool found;SpineHead *node=hash_search(nodes,&active[i]->target,HASH_ENTER,&found);
            if(!found) node->found=false;
        }
        run_query(HEADS,item_ids(active,count,true),NULL);
        for(uint64 i=0;i<SPI_processed;++i) {
            bool isnull;HeapTuple t=SPI_tuptable->vals[i];TupleDesc d=SPI_tuptable->tupdesc;
            hash128_t id=datum_to_hash128(SPI_getbinval(t,d,1,&isnull));
            hash128_t physicality=datum_to_hash128(SPI_getbinval(t,d,2,&isnull));
            SpineHead *node=hash_search(nodes,&id,HASH_FIND,NULL);
            if(node==NULL || (node->found && memcmp(&node->physicality,&physicality,sizeof(hash128_t))<=0)) continue;
            Datum wkb=SPI_getbinval(t,d,3,&isnull);if(isnull)continue;
            uint32 points;const unsigned char *point=laplace_trajectory_wkb_points(DatumGetByteaPP(wkb),&points);
            if(points==0)continue;
            double vertex[4];mantissa_payload_t payload;memcpy(vertex,point,sizeof(vertex));mantissa_unpack(vertex,&payload);
            node->child=payload.entity_id;node->tier=laplace_vflag_tier(payload.flags);node->physicality=physicality;node->found=true;
        }
        SPI_freetuptable(SPI_tuptable);
        for(int i=0;i<count;++i) {
            SpineHead *node=hash_search(nodes,&active[i]->target,HASH_FIND,NULL);
            if(node && node->found) {active[i]->target=node->child;active[i]->target_tier=node->tier;}
            else active[i]->has_target=false;
        }
        hash_destroy(nodes);
    }
    int count=0;
    for(int i=0;i<n;++i) if(items[i]->has_target && items[i]->target_tier<=3) active[count++]=items[i];
    char **labels=batch_text(TEXT,active,count,true);
    for(int i=0;i<count;++i) if(labels[i] && *labels[i]) {
        /* Collapse display-only ASCII whitespace without touching UTF-8 bytes. */
        char *s=labels[i],*out=s;bool space=false;
        for(char *p=s;*p;++p) {
            if(*p==' '||*p=='\n'||*p=='\r'||*p=='\t'||*p=='\f'||*p=='\v') {
                if(!space)*out++=' ';space=true;
            } else {*out++=*p;space=false;}
        }
        *out='\0';active[i]->label=s;
    }
}

/* Provenance is an identity, not permission to reconstruct its entire content
 * as a label. Apply the same bounded preview law to metadata operands. */
static char **
metadata_labels(DisplayItem **items, int n)
{
    char **labels = palloc0(Max(n, 1) * sizeof(char *));
    if (n == 0) return labels;
    HASHCTL ctl = {0}; ctl.keysize = sizeof(hash128_t); ctl.entrysize = sizeof(SpineHead);
    HTAB *tiers = hash_create("display metadata tiers", n, &ctl, HASH_ELEM | HASH_BLOBS);
    run_query(NODE_TIERS, item_ids(items,n,false), NULL);
    for (uint64 i=0; i<SPI_processed; ++i) {
        bool isnull; HeapTuple t=SPI_tuptable->vals[i]; TupleDesc d=SPI_tuptable->tupdesc;
        hash128_t id=datum_to_hash128(SPI_getbinval(t,d,1,&isnull));
        SpineHead *node=hash_search(tiers,&id,HASH_ENTER,NULL);
        node->tier=DatumGetInt16(SPI_getbinval(t,d,2,&isnull));
    }
    SPI_freetuptable(SPI_tuptable);
    DisplayItem **short_items=palloc(n*sizeof(DisplayItem*)); int *positions=palloc(n*sizeof(int));
    DisplayItem **compositions=palloc(n*sizeof(DisplayItem*)); int short_count=0, composition_count=0;
    for (int i=0; i<n; ++i) {
        SpineHead *node=hash_search(tiers,&items[i]->id,HASH_FIND,NULL);
        if (node && node->tier>3) {
            items[i]->target=items[i]->id; items[i]->has_target=true;
            compositions[composition_count++]=items[i];
        } else {
            short_items[short_count]=items[i]; positions[short_count++]=i;
        }
    }
    char **short_labels=batch_text(METADATA,short_items,short_count,false);
    for (int i=0; i<short_count; ++i) labels[positions[i]]=short_labels[i];
    preview(compositions,composition_count);
    for (int i=0; i<n; ++i) if (items[i]->label) labels[i]=items[i]->label;
    hash_destroy(tiers);
    return labels;
}

PG_FUNCTION_INFO_V1(pg_laplace_display_label_batch);
Datum
pg_laplace_display_label_batch(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo,0);ReturnSetInfo *r=(ReturnSetInfo*)fcinfo->resultinfo;
    if(PG_ARGISNULL(0))return (Datum)0;
    Datum *ids;bool *nulls;int n;
    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0),BYTEAOID,-1,false,TYPALIGN_INT,&ids,&nulls,&n);
    if(n==0)return (Datum)0;
    HASHCTL ctl={0};ctl.keysize=sizeof(hash128_t);ctl.entrysize=sizeof(DisplayItem);
    HTAB *lookup=hash_create("display items",n,&ctl,HASH_ELEM|HASH_BLOBS);
    DisplayItem **all=palloc(n*sizeof(DisplayItem*)),**work=palloc(n*sizeof(DisplayItem*));int unique=0;
    for(int i=0;i<n;++i) {
        if(nulls[i])continue;
        hash128_t id=datum_to_hash128(ids[i]);bool found;
        DisplayItem *item=hash_search(lookup,&id,HASH_ENTER,&found);
        if(!found){memset(item,0,sizeof(*item));item->id=id;all[unique++]=item;}
    }
    bool spi_top=false;if(laplace_spi_connect(&spi_top)!=SPI_OK_CONNECT)elog(ERROR,"display: SPI connect failed");
    run_query(FACETS,item_ids(all,unique,false),NULL);
    for(uint64 i=0;i<SPI_processed;++i) {
        bool isnull;HeapTuple t=SPI_tuptable->vals[i];TupleDesc d=SPI_tuptable->tupdesc;
        hash128_t id=datum_to_hash128(SPI_getbinval(t,d,1,&isnull));DisplayItem *item=hash_search(lookup,&id,HASH_FIND,NULL);
        if(!item)continue;
        item->exists=true;item->tier=DatumGetInt16(SPI_getbinval(t,d,2,&isnull));
        Datum type=SPI_getbinval(t,d,3,&isnull);item->has_type=!isnull;if(!isnull)item->type=datum_to_hash128(type);
        Datum source=SPI_getbinval(t,d,4,&isnull);item->has_source=!isnull;if(!isnull)item->source=datum_to_hash128(source);
        Datum name=SPI_getbinval(t,d,5,&isnull);if(!isnull){char *s=TextDatumGetCString(name);if(*s)item->label=s;}
    }
    SPI_freetuptable(SPI_tuptable);
    int count=pending(all,unique,work,0);char **labels=batch_text(NAMES,work,count,false);
    for(int i=0;i<count;++i)if(!opaque_name(labels[i],true))work[i]->label=labels[i];
    count=pending(all,unique,work,1);labels=batch_text(TEXT,work,count,false);
    for(int i=0;i<count;++i)if(labels[i]&&*labels[i])work[i]->label=labels[i];
    count=pending(all,unique,work,0);choose_targets(FILE_META,work,count,NULL,lookup);
    int selected=0;for(int i=0;i<count;++i)if(work[i]->has_target)work[selected++]=work[i];
    labels=batch_text(TEXT,work,selected,true);
    for(int i=0;i<selected;++i) {
        char *s=labels[i];
        while(s && *s) {
            char *end=strchr(s,'\n');Size len=end?(Size)(end-s):strlen(s);
            if(len>5 && strncmp(s,"name=",5)==0){work[i]->label=pnstrdup(s+5,len-5);break;}
            s=end?end+1:NULL;
        }
    }
    for(int i=0;i<unique;++i)all[i]->has_target=false;
    count=pending(all,unique,work,0);
    if(count>0) {
        run_query(DEF_TYPES,NULL,NULL);
        Datum *types=palloc(Max(SPI_processed,1)*sizeof(Datum));int ntypes=0;
        for(uint64 i=0;i<SPI_processed;++i){bool isnull;types[ntypes++]=copy_bytea_datum(SPI_getbinval(SPI_tuptable->vals[i],SPI_tuptable->tupdesc,1,&isnull));}
        ArrayType *type_array=construct_array(types,ntypes,BYTEAOID,-1,false,TYPALIGN_INT);SPI_freetuptable(SPI_tuptable);
        choose_targets(DEF_OWNED,work,count,type_array,lookup);
        count=pending(all,unique,work,2);choose_targets(DEF_INVERSE,work,count,type_array,lookup);
        selected=0;for(int i=0;i<unique;++i)if(!all[i]->label&&all[i]->has_target)work[selected++]=all[i];
        preview(work,selected);
    }
    /* Any still-unnamed composition can supply its own first witnessed unit.
     * This also handles self-observed documents without following provenance
     * back into an unbounded render of the same document. */
    selected=0;
    for(int i=0;i<unique;++i) if(!all[i]->label && all[i]->exists && all[i]->tier>3) {
        all[i]->target=all[i]->id; all[i]->has_target=true; work[selected++]=all[i];
    }
    preview(work,selected);
    count=pending(all,unique,work,0);
    for(int i=0;i<count;++i) {
        const laplace_relation_def_t *def=NULL;
        if(laplace_relation_lookup(&work[i]->id,&def)==0 && def)work[i]->label=pstrdup(def->canonical);
    }
    count=pending(all,unique,work,0);
    DisplayItem *meta=palloc0(Max(count*2,1)*sizeof(DisplayItem));DisplayItem **meta_ptr=palloc(Max(count*2,1)*sizeof(DisplayItem*));
    int nmeta=0;int *type_slot=palloc(Max(count,1)*sizeof(int)),*source_slot=palloc(Max(count,1)*sizeof(int));
    for(int i=0;i<count;++i) {
        type_slot[i]=source_slot[i]=-1;
        if(work[i]->has_type){type_slot[i]=nmeta;meta[nmeta].id=work[i]->type;meta_ptr[nmeta]=&meta[nmeta];++nmeta;}
        if(work[i]->has_source){source_slot[i]=nmeta;meta[nmeta].id=work[i]->source;meta_ptr[nmeta]=&meta[nmeta];++nmeta;}
    }
    labels=metadata_labels(meta_ptr,nmeta);
    for(int i=0;i<count;++i) {
        char *type=type_slot[i]>=0?labels[type_slot[i]]:NULL,*source=source_slot[i]>=0?labels[source_slot[i]]:NULL;
        if(opaque_name(type,false))type=NULL;if(opaque_name(source,false))source=NULL;
        if(type)for(char *p=type;*p;++p)if(*p=='_')*p=' ';
        if(type && source)work[i]->label=psprintf("%s · %s",type,source);
        else if(type || source)work[i]->label=type?type:source;
    }
    for(int i=0;i<n;++i) {
        hash128_t id;DisplayItem *item=NULL;
        if(!nulls[i]){id=datum_to_hash128(ids[i]);item=hash_search(lookup,&id,HASH_FIND,NULL);}
        Datum v[]={ids[i],CStringGetTextDatum(item&&item->label?item->label:"Unrealized entity"),Int16GetDatum(item?item->tier:0)};
        bool out_nulls[]={nulls[i],false,item==NULL||!item->exists};tuplestore_putvalues(r->setResult,r->setDesc,v,out_nulls);
    }
    hash_destroy(lookup);laplace_spi_finish(spi_top);return (Datum)0;
}
