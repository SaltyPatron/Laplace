#include "postgres.h"

#include <limits.h>
#include <string.h>

#include "access/htup_details.h"
#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "executor/tuptable.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "nodes/execnodes.h"
#include "nodes/makefuncs.h"
#include "nodes/pg_list.h"
#include "parser/parse_func.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/inval.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/syscache.h"
#include "utils/tuplestore.h"
#include "varatt.h"

#include "public_direct.h"
#include "spi_common.h"

#define LAPLACE_PUB_MAX_ARGS FUNC_MAX_ARGS
#define LP_G2_Z         0x01
#define LP_G2_M         0x02
#define LP_G2_BBOX      0x04
#define LP_G2_GEODETIC  0x08
#define LP_G2_EXTENDED  0x10
#define LP_G2_VERSION   0x40

typedef struct PubProcKey
{
    char schema[NAMEDATALEN];
    char funcname[NAMEDATALEN];
    int  nargs;
    Oid  argtypes[16];
} PubProcKey;

typedef struct PubProcEntry
{
    PubProcKey key;
    uint64     generation;
    Oid        fn_oid;
    FmgrInfo   flinfo;
    bool       valid;
} PubProcEntry;

typedef struct PubBankBitKey
{
    char bank[64];
    char value[64];
} PubBankBitKey;

typedef struct PubBankBitEntry
{
    PubBankBitKey key;
    uint64        generation;
    int16         bit;
    bool          valid;
} PubBankBitEntry;

static bool          pub_callbacks_registered = false;
static bool          pub_cache_dirty = true;
static uint64        pub_generation = 0;
static Oid           pub_public_nsp = InvalidOid;
static Oid           pub_blake3_oid = InvalidOid;
static Oid           pub_blake3_arr_oid = InvalidOid;
static Oid           pub_geometry_oid = InvalidOid;
static Oid           pub_geometry_arr_oid = InvalidOid;
static MemoryContext pub_cache_cxt = NULL;
static HTAB         *pub_proc_cache = NULL;
static HTAB         *pub_bank_cache = NULL;

static void
pub_invalidate_callback(Datum arg, int cacheid, uint32 hashvalue)
{
    (void) arg;
    (void) cacheid;
    (void) hashvalue;
    pub_cache_dirty = true;
}

static void
pub_ensure_cache(void)
{
    if (!pub_callbacks_registered)
    {
        CacheRegisterSyscacheCallback(PROCOID, pub_invalidate_callback, (Datum) 0);
        CacheRegisterSyscacheCallback(TYPEOID, pub_invalidate_callback, (Datum) 0);
        CacheRegisterSyscacheCallback(NAMESPACEOID, pub_invalidate_callback, (Datum) 0);
        CacheRegisterSyscacheCallback(EXTENSIONOID, pub_invalidate_callback, (Datum) 0);
        pub_callbacks_registered = true;
    }

    if (pub_cache_cxt == NULL)
        pub_cache_cxt = AllocSetContextCreate(TopMemoryContext,
                                              "laplace_substrate public_direct cache",
                                              ALLOCSET_SMALL_SIZES);

    if (pub_proc_cache == NULL)
    {
        HASHCTL ctl;
        memset(&ctl, 0, sizeof(ctl));
        ctl.keysize = sizeof(PubProcKey);
        ctl.entrysize = sizeof(PubProcEntry);
        ctl.hcxt = pub_cache_cxt;
        pub_proc_cache = hash_create("laplace_pub_proc_cache", 64, &ctl,
                                     HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    }

    if (pub_bank_cache == NULL)
    {
        HASHCTL ctl;
        memset(&ctl, 0, sizeof(ctl));
        ctl.keysize = sizeof(PubBankBitKey);
        ctl.entrysize = sizeof(PubBankBitEntry);
        ctl.hcxt = pub_cache_cxt;
        pub_bank_cache = hash_create("laplace_pub_bank_cache", 32, &ctl,
                                     HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    }

    if (pub_cache_dirty)
    {
        pub_generation++;
        pub_public_nsp = get_namespace_oid("public", false);
        pub_blake3_oid = GetSysCacheOid2(TYPENAMENSP, Anum_pg_type_oid,
                                         CStringGetDatum("blake3"),
                                         ObjectIdGetDatum(pub_public_nsp));
        if (!OidIsValid(pub_blake3_oid))
            ereport(ERROR, (errmsg("laplace_substrate: public.blake3 type is missing")));
        pub_blake3_arr_oid = get_array_type(pub_blake3_oid);
        if (!OidIsValid(pub_blake3_arr_oid))
            ereport(ERROR, (errmsg("laplace_substrate: public.blake3[] type is missing")));

        pub_geometry_oid = GetSysCacheOid2(TYPENAMENSP, Anum_pg_type_oid,
                                           CStringGetDatum("geometry"),
                                           ObjectIdGetDatum(pub_public_nsp));
        if (!OidIsValid(pub_geometry_oid))
            ereport(ERROR, (errmsg("laplace_substrate: public.geometry type is missing")));
        pub_geometry_arr_oid = get_array_type(pub_geometry_oid);
        pub_cache_dirty = false;
    }
}

uint64
laplace_pub_generation(void)
{
    pub_ensure_cache();
    return pub_generation;
}

Oid
laplace_pub_blake3_oid(void)
{
    pub_ensure_cache();
    return pub_blake3_oid;
}

Oid
laplace_pub_blake3_array_oid(void)
{
    pub_ensure_cache();
    return pub_blake3_arr_oid;
}

Oid
laplace_pub_geometry_oid(void)
{
    pub_ensure_cache();
    return pub_geometry_oid;
}

Oid
laplace_pub_geometry_array_oid(void)
{
    pub_ensure_cache();
    return pub_geometry_arr_oid;
}

static FmgrInfo *
pub_resolve_proc(const char *schema, const char *funcname, int nargs, const Oid *argtypes)
{
    PubProcKey    key;
    PubProcEntry *entry;
    bool          found;

    if (nargs < 0 || nargs > 16)
        ereport(ERROR, (errmsg("laplace_substrate: unsupported argument count %d for %s.%s",
                               nargs, schema, funcname)));

    pub_ensure_cache();

    memset(&key, 0, sizeof(key));
    strlcpy(key.schema, schema, sizeof(key.schema));
    strlcpy(key.funcname, funcname, sizeof(key.funcname));
    key.nargs = nargs;
    if (nargs > 0 && argtypes != NULL)
        memcpy(key.argtypes, argtypes, sizeof(Oid) * (Size) nargs);

    entry = (PubProcEntry *) hash_search(pub_proc_cache, &key, HASH_ENTER, &found);
    if (!found || !entry->valid || entry->generation != pub_generation)
    {
        List *names = list_make2(makeString(pstrdup(schema)), makeString(pstrdup(funcname)));
        Oid   fn_oid;
        Oid   types_copy[16];

        entry->valid = false;
        if (nargs > 0 && argtypes != NULL)
            memcpy(types_copy, argtypes, sizeof(Oid) * (Size) nargs);
        fn_oid = LookupFuncName(names, nargs, types_copy, false);
        list_free_deep(names);
        if (!OidIsValid(fn_oid))
            ereport(ERROR, (errmsg("laplace_substrate: function %s.%s(%d args) not found",
                                   schema, funcname, nargs)));
        fmgr_info_cxt(fn_oid, &entry->flinfo, pub_cache_cxt);
        entry->fn_oid = fn_oid;
        entry->generation = pub_generation;
        entry->valid = true;
    }

    return &entry->flinfo;
}

ArrayType *
laplace_pub_blake3_array(const hash128_t *ids, int count)
{
    Oid       elem_oid = laplace_pub_blake3_oid();
    Datum    *datums;
    ArrayType *arr;

    if (count <= 0 || ids == NULL)
        return construct_empty_array(elem_oid);
    datums = (Datum *) palloc(sizeof(Datum) * (Size) count);
    for (int i = 0; i < count; ++i)
        datums[i] = PointerGetDatum(&ids[i]);
    arr = construct_array(datums, count, elem_oid, sizeof(hash128_t), false, TYPALIGN_CHAR);
    pfree(datums);
    return arr;
}

int
laplace_pub_deconstruct_blake3_array(ArrayType *arr, hash128_t **out_ids)
{
    Datum     *elems;
    bool      *nulls;
    int        count;
    hash128_t *ids;
    int        valid = 0;

    *out_ids = NULL;
    if (arr == NULL)
        return 0;

    deconstruct_array(arr, laplace_pub_blake3_oid(), sizeof(hash128_t), false, TYPALIGN_CHAR,
                      &elems, &nulls, &count);
    if (count == 0)
    {
        pfree(elems);
        pfree(nulls);
        return 0;
    }

    ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) count);
    for (int i = 0; i < count; ++i)
    {
        if (nulls[i])
            continue;
        ids[valid++] = laplace_pub_blake3_to_hash128(elems[i]);
    }
    pfree(elems);
    pfree(nulls);
    if (valid == 0)
    {
        pfree(ids);
        return 0;
    }
    *out_ids = ids;
    return valid;
}

ArrayType *
laplace_pub_bytea_array_to_blake3_array(ArrayType *arr)
{
    Datum     *elems;
    bool      *nulls;
    int        count;
    hash128_t *ids;
    int        n = 0;
    ArrayType *out;

    if (arr == NULL)
        return construct_empty_array(laplace_pub_blake3_oid());
    if (ARR_ELEMTYPE(arr) == laplace_pub_blake3_oid())
        return arr;

    deconstruct_array(arr, BYTEAOID, -1, false, TYPALIGN_INT, &elems, &nulls, &count);
    if (count == 0)
    {
        pfree(elems);
        pfree(nulls);
        return construct_empty_array(laplace_pub_blake3_oid());
    }

    ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) count);
    for (int i = 0; i < count; ++i)
    {
        if (nulls[i])
            continue;
        ids[n++] = datum_to_hash128(elems[i]);
    }
    pfree(elems);
    pfree(nulls);
    out = laplace_pub_blake3_array(ids, n);
    pfree(ids);
    return out;
}

lp_path
laplace_pub_geo_path(Datum geom_datum)
{
    bytea         *g = (bytea *) PG_DETOAST_DATUM(geom_datum);
    const uint8_t *p = (const uint8_t *) g;
    uint8_t        flags;
    size_t         off = 8;
    uint32_t       gtype = 0;
    uint32_t       npts = 0;

    if (VARSIZE_ANY(g) < 16)
        ereport(ERROR, (errmsg("laplace_substrate: truncated PostGIS geometry")));
    flags = p[7];
    if (!(flags & LP_G2_VERSION))
        ereport(ERROR, (errmsg("laplace_substrate: expected PostGIS geometry v2")));
    if (!(flags & LP_G2_Z) || !(flags & LP_G2_M))
        ereport(ERROR, (errmsg("laplace_substrate: expected PostGIS geometry with ZM")));
    if (flags & LP_G2_EXTENDED)
        off += 8;
    if (flags & LP_G2_BBOX)
        off += (flags & LP_G2_GEODETIC) ? 6 * sizeof(float) : 2 * 4 * sizeof(float);
    if (off + 8 > (size_t) VARSIZE_ANY(g))
        ereport(ERROR, (errmsg("laplace_substrate: truncated geometry header")));

    memcpy(&gtype, p + off, 4);
    memcpy(&npts, p + off + 4, 4);
    if (gtype != 1 && gtype != 2)
        ereport(ERROR, (errmsg("laplace_substrate: expected POINT/LINESTRING, got type %u", gtype)));
    if (off + 8 + (size_t) npts * 32 > (size_t) VARSIZE_ANY(g))
        ereport(ERROR, (errmsg("laplace_substrate: truncated geometry vertices")));

    return lp_path_block(p + off + 8, npts);
}

bool
laplace_pub_geo_point4d(Datum geom_datum, double out_xyzm[4])
{
    bytea         *g = (bytea *) PG_DETOAST_DATUM(geom_datum);
    const uint8_t *p = (const uint8_t *) g;
    uint8_t        flags;
    size_t         off = 8;
    uint32_t       gtype = 0;
    uint32_t       npts = 0;

    if (VARSIZE_ANY(g) < 16)
        return false;
    flags = p[7];
    if (!(flags & LP_G2_VERSION) || !(flags & LP_G2_Z) || !(flags & LP_G2_M))
        return false;
    if (flags & LP_G2_EXTENDED)
        off += 8;
    if (flags & LP_G2_BBOX)
        off += (flags & LP_G2_GEODETIC) ? 6 * sizeof(float) : 2 * 4 * sizeof(float);
    if (off + 8 > (size_t) VARSIZE_ANY(g))
        return false;

    memcpy(&gtype, p + off, 4);
    memcpy(&npts, p + off + 4, 4);
    if (gtype != 1 || npts < 1 || off + 8 + 32 > (size_t) VARSIZE_ANY(g))
        return false;

    memcpy(out_xyzm, p + off + 8, 4 * sizeof(double));
    return true;
}

Datum
laplace_pub_point4d_geom(const double xyzm[4])
{
    uint8_t ewkb[37];
    bytea  *wkb;
    Oid     argtypes[1] = { BYTEAOID };
    Datum   args[1];
    bool    argnulls[1] = { false };
    bool    res_null = false;
    Datum   geom;

    if (lp_ewkb_point4(xyzm, ewkb, sizeof(ewkb)) != sizeof(ewkb))
        ereport(ERROR, (errmsg("laplace_substrate: failed to encode 4D point EWKB")));
    wkb = (bytea *) palloc(VARHDRSZ + sizeof(ewkb));
    SET_VARSIZE(wkb, VARHDRSZ + sizeof(ewkb));
    memcpy(VARDATA(wkb), ewkb, sizeof(ewkb));
    args[0] = PointerGetDatum(wkb);
    geom = laplace_pub_call("public", "st_geomfromewkb", 1, argtypes, args, argnulls, &res_null);
    pfree(wkb);
    if (res_null)
        ereport(ERROR, (errmsg("laplace_substrate: st_geomfromewkb returned NULL")));
    return geom;
}

Datum
laplace_pub_call(const char *schema, const char *funcname,
                 int nargs, const Oid *argtypes,
                 const Datum *args, const bool *argnulls,
                 bool *out_isnull)
{
    FmgrInfo *flinfo = pub_resolve_proc(schema, funcname, nargs, argtypes);
    LOCAL_FCINFO(fcinfo, 16);
    Datum     res;

    if (flinfo->fn_strict && argnulls != NULL)
    {
        for (int i = 0; i < nargs; ++i)
        {
            if (argnulls[i])
            {
                if (out_isnull)
                    *out_isnull = true;
                return (Datum) 0;
            }
        }
    }

    InitFunctionCallInfoData(*fcinfo, flinfo, (short) nargs, InvalidOid, NULL, NULL);
    for (int i = 0; i < nargs; ++i)
    {
        fcinfo->args[i].value = args ? args[i] : (Datum) 0;
        fcinfo->args[i].isnull = argnulls ? argnulls[i] : false;
    }

    res = FunctionCallInvoke(fcinfo);
    if (out_isnull)
        *out_isnull = fcinfo->isnull;
    return fcinfo->isnull ? (Datum) 0 : res;
}

uint64
laplace_pub_call_srf(const char *schema, const char *funcname,
                     int nargs, const Oid *argtypes,
                     const Datum *args, const bool *argnulls,
                     LaplaceDirectSrfVisit visit, void *ctx)
{
    FmgrInfo      *flinfo = pub_resolve_proc(schema, funcname, nargs, argtypes);
    LOCAL_FCINFO(fcinfo, 16);
    MemoryContext  caller_cxt = CurrentMemoryContext;
    MemoryContext  srf_cxt;
    MemoryContext  row_cxt;
    ExprContext    econtext;
    ReturnSetInfo  rsinfo;
    uint64         visited = 0;

    if (flinfo->fn_strict && argnulls != NULL)
    {
        for (int i = 0; i < nargs; ++i)
        {
            if (argnulls[i])
                return 0;
        }
    }

    srf_cxt = AllocSetContextCreate(caller_cxt, "laplace_pub_call_srf",
                                    ALLOCSET_DEFAULT_SIZES);
    row_cxt = AllocSetContextCreate(srf_cxt, "laplace_pub_call_srf_row",
                                    ALLOCSET_SMALL_SIZES);

    memset(&econtext, 0, sizeof(econtext));
    econtext.type = T_ExprContext;
    econtext.ecxt_per_query_memory = srf_cxt;
    econtext.ecxt_per_tuple_memory = row_cxt;

    memset(&rsinfo, 0, sizeof(rsinfo));
    rsinfo.type = T_ReturnSetInfo;
    rsinfo.econtext = &econtext;
    rsinfo.allowedModes = (int) (SFRM_Materialize | SFRM_ValuePerCall);
    rsinfo.returnMode = SFRM_Materialize;

    {
        MemoryContext prev = MemoryContextSwitchTo(srf_cxt);

        InitFunctionCallInfoData(*fcinfo, flinfo, (short) nargs, InvalidOid, NULL, (fmNodePtr) &rsinfo);
        for (int i = 0; i < nargs; ++i)
        {
            fcinfo->args[i].value = args ? args[i] : (Datum) 0;
            fcinfo->args[i].isnull = argnulls ? argnulls[i] : false;
        }

        (void) FunctionCallInvoke(fcinfo);

        if (rsinfo.returnMode == SFRM_Materialize &&
            rsinfo.setResult != NULL &&
            rsinfo.setDesc != NULL)
        {
            TupleTableSlot *slot = MakeSingleTupleTableSlot(rsinfo.setDesc, &TTSOpsMinimalTuple);

            while (tuplestore_gettupleslot(rsinfo.setResult, true, false, slot))
            {
                bool keep;

                CHECK_FOR_INTERRUPTS();
                MemoryContextSwitchTo(row_cxt);
                keep = visit(slot, ctx);
                visited++;
                MemoryContextSwitchTo(srf_cxt);
                ExecClearTuple(slot);
                MemoryContextReset(row_cxt);
                if (!keep)
                    break;
            }
            ExecDropSingleTupleTableSlot(slot);
            tuplestore_end(rsinfo.setResult);
        }

        MemoryContextSwitchTo(prev);
    }

    MemoryContextDelete(srf_cxt);
    return visited;
}

int16
laplace_pub_bank_bit(const char *bank, const char *value)
{
    PubBankBitKey    key;
    PubBankBitEntry *entry;
    bool             found;

    pub_ensure_cache();
    memset(&key, 0, sizeof(key));
    strlcpy(key.bank, bank, sizeof(key.bank));
    strlcpy(key.value, value, sizeof(key.value));

    entry = (PubBankBitEntry *) hash_search(pub_bank_cache, &key, HASH_ENTER, &found);
    if (!found || !entry->valid || entry->generation != pub_generation)
    {
        Oid   argtypes[2] = { TEXTOID, TEXTOID };
        Datum args[2];
        bool  argnulls[2] = { false, false };
        bool  isnull = false;
        Datum res;

        entry->valid = false;
        args[0] = CStringGetTextDatum(bank);
        args[1] = CStringGetTextDatum(value);
        res = laplace_pub_call("public", "laplace_bank_bit", 2, argtypes, args, argnulls, &isnull);
        pfree(DatumGetPointer(args[0]));
        pfree(DatumGetPointer(args[1]));
        if (isnull)
            ereport(ERROR, (errmsg("laplace_substrate: laplace_bank_bit(%s, %s) returned NULL", bank, value)));
        entry->bit = DatumGetInt16(res);
        entry->generation = pub_generation;
        entry->valid = true;
    }
    return entry->bit;
}

bool
laplace_pub_text_id_hash(const char *str, size_t len, hash128_t *out)
{
    Oid   argtypes[1] = { TEXTOID };
    Datum args[1];
    bool  argnulls[1] = { false };
    bool  isnull = false;
    text *txt;
    Datum res;

    if (str == NULL || len == 0)
        return false;
    txt = cstring_to_text_with_len(str, (int) len);
    args[0] = PointerGetDatum(txt);
    res = laplace_pub_call("public", "laplace_text_id", 1, argtypes, args, argnulls, &isnull);
    pfree(txt);
    if (isnull)
        return false;
    *out = laplace_pub_blake3_to_hash128(res);
    pfree(DatumGetPointer(res));
    return true;
}

int16
laplace_pub_mask_value(Datum mask_datum, const char *bank)
{
    Oid   argtypes[2] = { BITOID, TEXTOID };
    Datum args[2];
    bool  argnulls[2] = { false, false };
    bool  isnull = false;
    Datum res;

    args[0] = mask_datum;
    args[1] = CStringGetTextDatum(bank);
    res = laplace_pub_call("public", "laplace_mask_value", 2, argtypes, args, argnulls, &isnull);
    pfree(DatumGetPointer(args[1]));
    if (isnull)
        return -1;
    return DatumGetInt16(res);
}

bool
laplace_pub_mask_has_value(Datum mask_datum, const char *bank, const char *value)
{
    Oid   argtypes[3] = { BITOID, TEXTOID, TEXTOID };
    Datum args[3];
    bool  argnulls[3] = { false, false, false };
    bool  isnull = false;
    Datum res;

    args[0] = mask_datum;
    args[1] = CStringGetTextDatum(bank);
    args[2] = CStringGetTextDatum(value);
    res = laplace_pub_call("public", "laplace_mask_has_value", 3, argtypes, args, argnulls, &isnull);
    pfree(DatumGetPointer(args[1]));
    pfree(DatumGetPointer(args[2]));
    return !isnull && DatumGetBool(res);
}
