
















#ifndef LAPLACE_SPI_COMMON_H
#define LAPLACE_SPI_COMMON_H

#include "postgres.h"

#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "executor/tuptable.h"
#include "fmgr.h"
#include "nodes/makefuncs.h"
#include "nodes/pg_list.h"
#include "parser/parse_func.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/fmgrprotos.h"
#include "utils/numeric.h"
#include "utils/memutils.h"

#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "laplace/core/glicko2.h"



static inline Datum
copy_bytea_datum(Datum d)
{
    bytea *src;
    Size   len;
    bytea *dst;

    if (d == (Datum) 0)
        return (Datum) 0;
    src = DatumGetByteaPP(d);
    len = VARSIZE_ANY(src);
    dst = (bytea *) palloc(len);

    memcpy(dst, src, len);
    return PointerGetDatum(dst);
}

/* Optional bytea fields must be checked before detoasting. A nullable object
 * endpoint is a normal unary claim, not a pointer that can be dereferenced. */
static inline bytea *
spi_slot_bytea(TupleTableSlot *slot, AttrNumber column)
{
    bool isnull;
    Datum value = slot_getattr(slot, column, &isnull);
    return isnull ? NULL : DatumGetByteaPP(value);
}

static inline bool
bytea_eq(Datum a, Datum b)
{
    bytea *ba = DatumGetByteaPP(a);
    bytea *bb = DatumGetByteaPP(b);
    Size   la = VARSIZE_ANY_EXHDR(ba);
    Size   lb = VARSIZE_ANY_EXHDR(bb);

    if (la != lb)
        return false;
    return memcmp(VARDATA_ANY(ba), VARDATA_ANY(bb), la) == 0;
}

static inline hash128_t
datum_to_hash128(Datum d)
{
    bytea    *b = DatumGetByteaPP(d);
    hash128_t h;

    if (VARSIZE_ANY_EXHDR(b) != (int) sizeof(hash128_t))
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                        errmsg("laplace_substrate: expected exactly 16-byte entity id")));
    memcpy(&h, VARDATA_ANY(b), sizeof(hash128_t));
    return h;
}

static inline Datum
hash128_to_datum(const hash128_t *h)
{
    bytea *b = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));

    SET_VARSIZE(b, VARHDRSZ + sizeof(hash128_t));
    memcpy(VARDATA(b), h, sizeof(hash128_t));
    return PointerGetDatum(b);
}

/* Fixed-size identities need one PostgreSQL allocation for the entire array.
 * bytea array elements have four-byte alignment and a normal varlena header;
 * no separate Datum or bytea allocation is needed for each identity. */
static inline ArrayType *
hash128_array_from_ids(const hash128_t *ids, int count)
{
    const Size item_size = VARHDRSZ + sizeof(hash128_t);
    const Size header_size = ARR_OVERHEAD_NONULLS(1);
    ArrayType *array;
    char *data;
    if (count < 0 || (Size) count > (MaxAllocSize - header_size) / item_size)
        ereport(ERROR, (errmsg("laplace_substrate: identity array is too large")));
    if (count == 0) return construct_empty_array(BYTEAOID);
    array = palloc0(header_size + (Size) count * item_size);
    SET_VARSIZE(array, header_size + (Size) count * item_size);
    ARR_NDIM(array) = 1;
    ARR_ELEMTYPE(array) = BYTEAOID;
    ARR_DIMS(array)[0] = count;
    ARR_LBOUND(array)[0] = 1;
    data = ARR_DATA_PTR(array);
    for (int i = 0; i < count; ++i, data += item_size)
    {
        SET_VARSIZE(data, item_size);
        memcpy(data + VARHDRSZ, ids + i, sizeof(hash128_t));
    }
    return array;
}

static inline bool
hash128_eq(const hash128_t *a, const hash128_t *b)
{
    return a->hi == b->hi && a->lo == b->lo;
}

static inline Datum
eff_mu_display_numeric(int64 rating, int64 rd)
{


    int64 eff = laplace_effective_mu_fp(rating, rd);
    Datum n = DirectFunctionCall1(int8_numeric, Int64GetDatum(eff));
    Datum b = DirectFunctionCall1(int8_numeric, Int64GetDatum(INT64CONST(1000000000)));
    Datum d = DirectFunctionCall2(numeric_div, n, b);

    return DirectFunctionCall2(numeric_round, d, Int32GetDatum(3));
}

/* The display-rounded eff_mu as int64 fixed point: eff rounded to 3 display
 * decimals = the nearest multiple of 1e6 fp units, half away from zero —
 * exactly what eff_mu_display_numeric emits, kept in native int64 so hot
 * paths can accumulate/compare without numeric Datums. */
static inline int64
eff_mu_display_fp(int64 rating, int64 rd)
{
    int64 eff  = laplace_effective_mu_fp(rating, rd);
    __int128 rounded = ((__int128)eff + (eff >= 0 ? 500000 : -500000))
                       / 1000000 * 1000000;
    return rounded > INT64_MAX ? INT64_MAX : rounded < INT64_MIN ? INT64_MIN : (int64)rounded;
}

/* The strength of a consensus cell as an edge: its Glicko-2 expected score
 * against a certain neutral reference. Rating supplies the verdict and RD pulls
 * an uncertain cell toward 0.5 through g(phi); there is no fitted scale or floor. */
static inline double
laplace_edge_strength(int64 rating, int64 rd)
{
    return laplace_glicko2_expected_score(rating, rd);
}

/* Renders an int64 fp value (e.g. a sum of eff_mu_display_fp terms) as the
 * same 3-decimal numeric eff_mu_display_numeric produces. */
static inline Datum
fp_display_numeric(int64 fp)
{
    Datum n = DirectFunctionCall1(int8_numeric, Int64GetDatum(fp));
    Datum b = DirectFunctionCall1(int8_numeric, Int64GetDatum(INT64CONST(1000000000)));
    Datum d = DirectFunctionCall2(numeric_div, n, b);

    return DirectFunctionCall2(numeric_round, d, Int32GetDatum(3));
}

static inline SPIPlanPtr
spi_common_ensure_plan(SPIPlanPtr *slot, const char *sql, int nargs, Oid *argtypes)
{
    if (*slot == NULL)
    {
        SPIPlanPtr p = SPI_prepare_cursor(sql, nargs, argtypes, CURSOR_OPT_PARALLEL_OK);

        if (p == NULL)
            elog(ERROR, "laplace_substrate: SPI_prepare(%s) failed: %s",
                 sql, SPI_result_code_string(SPI_result));
        if (SPI_keepplan(p) != 0)
            elog(ERROR, "laplace_substrate: SPI_keepplan(%s) failed", sql);
        *slot = p;
    }
    return *slot;
}

static inline Datum
spi_invoke_public_label(const char *funcname, Oid arg0_type, Datum arg0, bool null_if_hex)
{
    Oid      argtypes[2] = { arg0_type, BOOLOID };
    List    *names;
    Oid      fn_oid;
    FmgrInfo flinfo;
    LOCAL_FCINFO(fcinfo, 2);
    Datum    res;

    if (arg0 == (Datum) 0)
        return (Datum) 0;
    names = list_make2(makeString(pstrdup("public")), makeString(pstrdup(funcname)));
    fn_oid = LookupFuncName(names, 2, argtypes, false);
    list_free_deep(names);
    fmgr_info(fn_oid, &flinfo);
    InitFunctionCallInfoData(*fcinfo, &flinfo, 2, InvalidOid, NULL, NULL);
    fcinfo->args[0].value = arg0;
    fcinfo->args[0].isnull = false;
    fcinfo->args[1].value = BoolGetDatum(null_if_hex);
    fcinfo->args[1].isnull = false;
    res = FunctionCallInvoke(fcinfo);
    if (fcinfo->isnull)
        return (Datum) 0;
    return res;
}

static inline Datum
spi_realize(Datum id, Datum lang)
{
    (void) lang;
    return spi_invoke_public_label("laplace_label_text", BYTEAOID, id, false);
}

/* Batch form of spi_realize. ids_arr is a bytea[] ArrayType Datum; returns the
 * text[] Datum from laplace_label_array, or 0 on empty/NULL. Caller owns any
 * deconstruct_array / DatumGetArrayTypePCopy. */
static inline Datum
spi_realize_batch(Datum ids_arr, Datum lang)
{
    (void) lang;
    return spi_invoke_public_label("laplace_label_array", BYTEAARRAYOID, ids_arr, false);
}

static inline Datum
spi_label(Datum id)
{
    return spi_invoke_public_label("laplace_label_text", BYTEAOID, id, false);
}

static inline Datum
spi_type_label(Datum type_id)
{
    return spi_invoke_public_label("laplace_label_text", BYTEAOID, type_id, false);
}

extern Datum laplace_word_language_c(Datum word);
extern int   laplace_fetch_synset_ids_c(Datum word, Datum **out_ids, int *out_n);

static inline Datum
spi_word_language(Datum word)
{
    return laplace_word_language_c(word);
}

static inline Datum
spi_render_text(Datum id)
{
    return spi_invoke_public_label("laplace_label_text", BYTEAOID, id, true);
}


static inline void
spi_emit_all_rows(ReturnSetInfo *rsinfo)
{
    TupleDesc td    = SPI_tuptable->tupdesc;
    int       ncols = td->natts;
    Datum    *values = (Datum *) palloc(sizeof(Datum) * ncols);
    bool     *nulls  = (bool *) palloc(sizeof(bool) * ncols);

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];

        for (int c = 0; c < ncols; c++)
            values[c] = SPI_getbinval(tup, td, c + 1, &nulls[c]);
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
    }
    pfree(values);
    pfree(nulls);
}

static inline int
spi_fetch_synset_ids(Datum word, Datum **out_ids, int *out_n)
{
    return laplace_fetch_synset_ids_c(word, out_ids, out_n);
}

#endif                          
