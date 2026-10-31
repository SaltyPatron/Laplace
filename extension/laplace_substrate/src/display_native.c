/* Native display acquisition/realization lives in Laplace-postgres. This file
 * projects compiled registry metadata only; it never reads a content table. */
#include "postgres.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "spi_common.h"
#include "laplace/core/relation_law.h"
#include "laplace/core/text_identity.h"
#include "laplace/core/language_law.h"
#include "laplace/core/sql_catalog.h"
#include "spi_nested.h"

typedef struct {
    hash128_t surface,canonical;
    const char *name;
    bool inverse;
    int16 qualifier;
} RelationMetadata;
static HTAB *relation_metadata;

static HTAB *metadata(void)
{
    if(relation_metadata)return relation_metadata;
    size_t n=laplace_relation_table_count+laplace_relation_alias_table_count;
    MemoryContext owner=AllocSetContextCreate(TopMemoryContext,"Native relation metadata",ALLOCSET_DEFAULT_SIZES);
    HASHCTL ctl={0};ctl.keysize=sizeof(hash128_t);ctl.entrysize=sizeof(RelationMetadata);ctl.hcxt=owner;
    HTAB *next=hash_create("Native relation metadata",Max(n,1),&ctl,HASH_ELEM|HASH_BLOBS|HASH_CONTEXT);
    PG_TRY();{
      for(size_t i=0;i<n;i++){
        const char *surface;const laplace_relation_def_t *def;bool inverse=false;int16 qualifier=-1;
        if(i<laplace_relation_table_count){def=laplace_relation_table+i;surface=def->canonical;}
        else{
          const laplace_relation_alias_t *alias=laplace_relation_alias_table+i-laplace_relation_table_count;
          if(alias->canon_idx<0 || (size_t)alias->canon_idx>=laplace_relation_table_count)
            elog(ERROR,"relation metadata: invalid declared alias target");
          def=laplace_relation_table+alias->canon_idx;surface=alias->surface;
          inverse=alias->flip!=0;qualifier=alias->qualifier_bit;
        }
        hash128_t id,canonical;
        if(hash128_text((const uint8_t *)surface,strlen(surface),&id)!=0
           || laplace_relation_type_id(def->canonical,&canonical)!=0)
          elog(ERROR,"relation metadata: Native identity unavailable");
        bool found;RelationMetadata *entry=hash_search(next,&id,HASH_ENTER,&found);
        if(found && (memcmp(&entry->canonical,&canonical,sizeof canonical)
                     || entry->inverse!=inverse || entry->qualifier!=qualifier))
          elog(ERROR,"relation metadata: conflicting declarations for one Native identity");
        *entry=(RelationMetadata){id,canonical,def->canonical,inverse,qualifier};
      }
    }PG_CATCH();{MemoryContextDelete(owner);PG_RE_THROW();}PG_END_TRY();
    relation_metadata=next;return relation_metadata;
}

PG_FUNCTION_INFO_V1(pg_relation_metadata);
Datum pg_relation_metadata(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo,0);ReturnSetInfo *r=(ReturnSetInfo *)fcinfo->resultinfo;
    if(PG_ARGISNULL(0))return (Datum)0;
    Datum *ids;bool *nulls;int n;
    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0),BYTEAOID,-1,false,TYPALIGN_INT,&ids,&nulls,&n);
    if(!n)return (Datum)0;
    HTAB *lookup=metadata();
    for(int i=0;i<n;i++){
      if(nulls[i])continue;hash128_t id=datum_to_hash128(ids[i]);
      RelationMetadata *entry=hash_search(lookup,&id,HASH_FIND,NULL);if(!entry)continue;
      Datum values[]={ids[i],hash128_to_datum(&entry->canonical),CStringGetTextDatum(entry->name),
                      BoolGetDatum(entry->inverse),Int16GetDatum(entry->qualifier)};
      bool missing[]={false,false,false,false,false};
      tuplestore_putvalues(r->setResult,r->setDesc,values,missing);
    }
    return (Datum)0;
}

/* Governed language tags are metadata. Resolve the selected surfaces together;
 * this neither asserts a new type nor deposits language testimony. */
PG_FUNCTION_INFO_V1(pg_language_codes);
Datum pg_language_codes(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo,0);ReturnSetInfo *r=(ReturnSetInfo*)fcinfo->resultinfo;
    if(PG_ARGISNULL(0))return (Datum)0;
    bool finish=false;
    if(laplace_spi_connect(&finish)!=SPI_OK_CONNECT)elog(ERROR,"language codes SPI connect failed");
    static SPIPlanPtr plan=NULL;Oid types[]={BYTEAARRAYOID};Datum args[]={PG_GETARG_DATUM(0)};
    if(SPI_execute_plan(spi_common_ensure_plan(&plan,laplace_sql_query_text("language.code_surfaces"),1,types),args,NULL,true,0)!=SPI_OK_SELECT)
        elog(ERROR,"language codes read failed");
    for(uint64 i=0;i<SPI_processed;i++){
        bool missing;const char *canonical=NULL;
        Datum id=SPI_getbinval(SPI_tuptable->vals[i],SPI_tuptable->tupdesc,1,&missing);
        Datum label=SPI_getbinval(SPI_tuptable->vals[i],SPI_tuptable->tupdesc,2,&missing);
        if(missing)continue;
        char *tag=TextDatumGetCString(label);
        if(laplace_language_canonical(tag,&canonical)==0){
            Datum values[]={id,CStringGetTextDatum(canonical)};bool nulls[]={false,false};
            tuplestore_putvalues(r->setResult,r->setDesc,values,nulls);
        }
        pfree(tag);
    }
    SPI_freetuptable(SPI_tuptable);laplace_spi_finish(finish);return (Datum)0;
}
