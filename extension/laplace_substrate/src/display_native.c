/* Native display acquisition/realization lives in Laplace-postgres. This file
 * projects compiled registry metadata only; it never reads a content table. */
#include "postgres.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "spi_common.h"
#include "public_direct.h"
#include "relations_internal.h"
#include "laplace/core/relation_law.h"
#include "laplace/core/text_identity.h"
#include "laplace/core/language_law.h"
#include "laplace/core/sql_catalog.h"
#include "perfcache_native.h"

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
    (void)laplace_perfcache_ready();
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

bool
laplace_relation_metadata_lookup_c(const hash128_t *surface,
                                   hash128_t *out_canonical,
                                   const char **out_name,
                                   bool *out_inverse,
                                   int16 *out_qualifier)
{
    RelationMetadata *entry;

    if (surface == NULL)
        return false;
    entry = (RelationMetadata *) hash_search(metadata(), surface, HASH_FIND, NULL);
    if (entry == NULL)
        return false;
    if (out_canonical != NULL)
        *out_canonical = entry->canonical;
    if (out_name != NULL)
        *out_name = entry->name;
    if (out_inverse != NULL)
        *out_inverse = entry->inverse;
    if (out_qualifier != NULL)
        *out_qualifier = entry->qualifier;
    return true;
}

LaplaceRelationSurface *
laplace_relation_surfaces_expand_c(const hash128_t *ids, int n_ids, int *out_count)
{
    HTAB                   *lookup;
    hash128_t              *want;
    int                     n_want = 0;
    LaplaceRelationSurface *out = NULL;
    int                     out_n = 0;
    int                     out_cap = 0;

    *out_count = 0;
    if (ids == NULL || n_ids <= 0)
        return NULL;

    lookup = metadata();
    want = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_ids);
    for (int i = 0; i < n_ids; ++i)
    {
        RelationMetadata *entry = (RelationMetadata *) hash_search(lookup, &ids[i], HASH_FIND, NULL);
        hash128_t         cid = entry ? entry->canonical : ids[i];
        bool              dup = false;

        for (int j = 0; j < n_want; ++j)
        {
            if (memcmp(&want[j], &cid, sizeof(hash128_t)) == 0)
            {
                dup = true;
                break;
            }
        }
        if (!dup)
            want[n_want++] = cid;
    }

    out_cap = Max(n_want * 2, 16);
    out = (LaplaceRelationSurface *) palloc(sizeof(LaplaceRelationSurface) * (Size) out_cap);

    for (int w = 0; w < n_want; ++w)
    {
        bool              emitted_canonical = false;
        HASH_SEQ_STATUS   seq;
        RelationMetadata *entry;

        hash_seq_init(&seq, lookup);
        while ((entry = (RelationMetadata *) hash_seq_search(&seq)) != NULL)
        {
            if (memcmp(&entry->canonical, &want[w], sizeof(hash128_t)) != 0)
                continue;
            if (memcmp(&entry->surface, &want[w], sizeof(hash128_t)) == 0)
                emitted_canonical = true;
            if (out_n == out_cap)
            {
                out_cap *= 2;
                out = (LaplaceRelationSurface *) repalloc(out, sizeof(LaplaceRelationSurface) * (Size) out_cap);
            }
            out[out_n++] = (LaplaceRelationSurface){
                .surface   = entry->surface,
                .canonical = entry->canonical,
                .name      = entry->name,
                .inverse   = entry->inverse,
                .qualifier = entry->qualifier
            };
        }
        if (!emitted_canonical)
        {
            if (out_n == out_cap)
            {
                out_cap *= 2;
                out = (LaplaceRelationSurface *) repalloc(out, sizeof(LaplaceRelationSurface) * (Size) out_cap);
            }
            out[out_n++] = (LaplaceRelationSurface){
                .surface   = want[w],
                .canonical = want[w],
                .name      = NULL,
                .inverse   = false,
                .qualifier = -1
            };
        }
    }

    pfree(want);
    *out_count = out_n;
    return out;
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

PG_FUNCTION_INFO_V1(pg_relation_surfaces);
Datum pg_relation_surfaces(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo,0);ReturnSetInfo *r=(ReturnSetInfo *)fcinfo->resultinfo;
    if(PG_ARGISNULL(0))return (Datum)0;
    Datum *ids;bool *nulls;int n;
    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0),BYTEAOID,-1,false,TYPALIGN_INT,&ids,&nulls,&n);
    if(!n)return (Datum)0;
    HTAB *lookup=metadata();
    hash128_t *want=palloc(sizeof(hash128_t)*Max(n,1));
    int n_want=0;
    for(int i=0;i<n;i++){
      if(nulls[i])continue;hash128_t id=datum_to_hash128(ids[i]);
      RelationMetadata *entry=hash_search(lookup,&id,HASH_FIND,NULL);
      hash128_t cid=entry?entry->canonical:id;
      bool dup=false;
      for(int j=0;j<n_want;j++)if(!memcmp(&want[j],&cid,sizeof(hash128_t))){dup=true;break;}
      if(!dup)want[n_want++]=cid;
    }
    for(int w=0;w<n_want;w++){
      bool emitted_canonical=false;
      HASH_SEQ_STATUS seq;RelationMetadata *entry;
      hash_seq_init(&seq,lookup);
      while((entry=hash_seq_search(&seq))!=NULL){
        if(memcmp(&entry->canonical,&want[w],sizeof(hash128_t)))continue;
        if(!memcmp(&entry->surface,&want[w],sizeof(hash128_t)))emitted_canonical=true;
        Datum values[]={hash128_to_datum(&entry->surface),hash128_to_datum(&entry->canonical),
                        CStringGetTextDatum(entry->name),BoolGetDatum(entry->inverse),
                        Int16GetDatum(entry->qualifier)};
        bool missing[]={false,false,false,false,false};
        tuplestore_putvalues(r->setResult,r->setDesc,values,missing);
      }
      if(!emitted_canonical){
        Datum values[]={hash128_to_datum(&want[w]),hash128_to_datum(&want[w]),
                        (Datum)0,BoolGetDatum(false),Int16GetDatum(-1)};
        bool missing[]={false,false,true,false,false};
        tuplestore_putvalues(r->setResult,r->setDesc,values,missing);
      }
    }
    pfree(want);pfree(nulls);pfree(ids);
    return (Datum)0;
}

/* Governed language tags are metadata. Resolve the selected surfaces together;
 * this neither asserts a new type nor deposits language testimony. */
typedef struct LangLabelEmitCtx
{
    ReturnSetInfo *rsinfo;
} LangLabelEmitCtx;

static bool
visit_language_code_label(TupleTableSlot *slot, void *vctx)
{
    LangLabelEmitCtx *ctx = (LangLabelEmitCtx *) vctx;
    bool              missing_id = false;
    bool              missing_lbl = false;
    Datum             d_id;
    Datum             d_lbl;
    hash128_t         id;
    char             *tag;
    const char       *canonical = NULL;

    d_id = slot_getattr(slot, 2, &missing_id);
    d_lbl = slot_getattr(slot, 3, &missing_lbl);
    if (missing_id || missing_lbl)
        return true;

    id = laplace_pub_blake3_to_hash128(d_id);
    tag = TextDatumGetCString(d_lbl);
    if (laplace_language_canonical(tag, &canonical) == 0 && canonical != NULL)
    {
        Datum values[2];
        bool  nulls[2] = { false, false };

        values[0] = hash128_to_datum(&id);
        values[1] = CStringGetTextDatum(canonical);
        tuplestore_putvalues(ctx->rsinfo->setResult, ctx->rsinfo->setDesc, values, nulls);
    }
    pfree(tag);
    return true;
}

PG_FUNCTION_INFO_V1(pg_language_codes);
Datum pg_language_codes(PG_FUNCTION_ARGS)
{
    ReturnSetInfo    *r;
    ArrayType        *b3_arr;
    Oid               argtype;
    Datum             args[1];
    bool              nulls[1] = { false };
    LangLabelEmitCtx  ctx;

    InitMaterializedSRF(fcinfo, 0);
    r = (ReturnSetInfo *) fcinfo->resultinfo;
    if (PG_ARGISNULL(0))
        return (Datum) 0;

    b3_arr = laplace_pub_bytea_array_to_blake3_array(PG_GETARG_ARRAYTYPE_P(0));
    if (ArrayGetNItems(ARR_NDIM(b3_arr), ARR_DIMS(b3_arr)) == 0)
    {
        pfree(b3_arr);
        return (Datum) 0;
    }

    argtype = laplace_pub_blake3_array_oid();
    args[0] = PointerGetDatum(b3_arr);
    ctx.rsinfo = r;
    (void) laplace_pub_call_srf("public", "laplace_labels", 1, &argtype, args, nulls,
                                visit_language_code_label, &ctx);
    pfree(b3_arr);
    return (Datum) 0;
}
