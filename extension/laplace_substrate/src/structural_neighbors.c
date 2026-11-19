#include "postgres.h"

#include <limits.h>
#include <math.h>
#include <string.h>

#include "catalog/pg_type.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "laplace/core/hash128.h"
#include "laplace_pg/executor.h"

#include "public_direct.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_structural_neighbors_of);
PG_FUNCTION_INFO_V1(pg_laplace_nearest_neighbors_4d);
PG_FUNCTION_INFO_V1(pg_laplace_explore_anchor_neighbors);
PG_FUNCTION_INFO_V1(pg_laplace_walk_branches_rendered);

static HTAB *
sn_hash_id(MemoryContext ctx, const char *name, Size entry_sz, long init)
{
    HASHCTL ctl;
    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = entry_sz;
    ctl.hcxt = ctx;
    return hash_create(name, Max(init, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

static int
sn_hash128_cmp(const void *a, const void *b)
{
    return memcmp(a, b, sizeof(hash128_t));
}

static int
sn_dedup_ids(const hash128_t *src, int n, hash128_t *dst)
{
    int out = 0;
    if (n <= 0 || src == NULL)
        return 0;
    memcpy(dst, src, sizeof(hash128_t) * (Size) n);
    if (n > 1)
        qsort(dst, (Size) n, sizeof(hash128_t), sn_hash128_cmp);
    dst[out++] = dst[0];
    for (int i = 1; i < n; i++)
    {
        if (!hash128_eq(&dst[out - 1], &dst[i]))
            dst[out++] = dst[i];
    }
    return out;
}

typedef struct
{
    hash128_t id;
    int16     tier;
    Datum     coord;
    double    xyzm[4];
    double    distance;
} SnKnnRow;

typedef struct
{
    MemoryContext caller;
    SnKnnRow     *rows;
    int           count;
    int           cap;
} SnKnnCtx;

static bool
sn_visit_knn(TupleTableSlot *slot, void *opaque)
{
    SnKnnCtx *ctx = (SnKnnCtx *) opaque;
    bool      isnull = false;
    Datum     d_id, d_tier, d_coord, d_dist;
    hash128_t id;
    int16     tier = 0;
    double    xyzm[4] = {0, 0, 0, 0};
    double    dist = 0.0;
    Datum     coord_cp = (Datum) 0;
    MemoryContext prev;

    d_id = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    id = laplace_pub_blake3_to_hash128(d_id);

    d_tier = slot_getattr(slot, 3, &isnull);
    if (!isnull) tier = DatumGetInt16(d_tier);

    d_coord = slot_getattr(slot, 4, &isnull);
    if (!isnull)
    {
        laplace_pub_geo_point4d(d_coord, xyzm);
        prev = MemoryContextSwitchTo(ctx->caller);
        coord_cp = PointerGetDatum(PG_DETOAST_DATUM_COPY(d_coord));
        MemoryContextSwitchTo(prev);
    }

    d_dist = slot_getattr(slot, 5, &isnull);
    if (!isnull) dist = DatumGetFloat8(d_dist);

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 32;
        sz = sizeof(SnKnnRow) * (Size) ctx->cap;
        ctx->rows = ctx->rows ? (SnKnnRow *) repalloc(ctx->rows, sz) : (SnKnnRow *) palloc(sz);
    }
    ctx->rows[ctx->count].id = id;
    ctx->rows[ctx->count].tier = tier;
    ctx->rows[ctx->count].coord = coord_cp;
    memcpy(ctx->rows[ctx->count].xyzm, xyzm, sizeof(xyzm));
    ctx->rows[ctx->count].distance = dist;
    ctx->count++;
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct
{
    hash128_t entity;
    Datum     curve;
} SnCurveEntry;

typedef struct
{
    MemoryContext caller;
    HTAB         *curves;
} SnCurveCtx;

static bool
sn_visit_curve(TupleTableSlot *slot, void *opaque)
{
    SnCurveCtx   *ctx = (SnCurveCtx *) opaque;
    bool          isnull = false;
    Datum         d_ent, d_wkb, geom;
    bool          g_null = false;
    Oid           bytea_oid = BYTEAOID;
    hash128_t     ent;
    SnCurveEntry *ce;
    bool          found = false;
    MemoryContext prev;

    d_wkb = slot_getattr(slot, 3, &isnull);
    if (isnull) return true;
    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);

    prev = MemoryContextSwitchTo(ctx->caller);
    geom = laplace_pub_call("public", "st_geomfromewkb", 1, &bytea_oid,
                            &d_wkb, &isnull, &g_null);
    if (!g_null)
    {
        ce = (SnCurveEntry *) hash_search(ctx->curves, &ent, HASH_ENTER, &found);
        if (!found)
            ce->curve = geom;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct
{
    hash128_t id;
    char     *content;
    bool      content_null;
} SnLabelEntry;

typedef struct
{
    MemoryContext caller;
    HTAB         *labels;
} SnLabelCtx;

static bool
sn_visit_label(TupleTableSlot *slot, void *opaque)
{
    SnLabelCtx   *ctx = (SnLabelCtx *) opaque;
    bool          isnull = false;
    Datum         d_id, d_lbl;
    hash128_t     id;
    char         *txt = NULL;
    SnLabelEntry *le;
    bool          found = false;
    MemoryContext prev;

    d_id = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    id = laplace_pub_blake3_to_hash128(d_id);

    d_lbl = slot_getattr(slot, 3, &isnull);
    if (!isnull)
        txt = TextDatumGetCString(d_lbl);

    prev = MemoryContextSwitchTo(ctx->caller);
    le = (SnLabelEntry *) hash_search(ctx->labels, &id, HASH_ENTER, &found);
    if (!found)
    {
        le->content_null = isnull;
        le->content = isnull ? NULL : pstrdup(txt);
    }
    MemoryContextSwitchTo(prev);
    if (txt != NULL) pfree(txt);
    return true;
}

static int
sn_knn_order_cmp(const void *a, const void *b)
{
    const SnKnnRow *x = (const SnKnnRow *) a;
    const SnKnnRow *y = (const SnKnnRow *) b;
    if (x->distance != y->distance)
        return x->distance < y->distance ? -1 : 1;
    return memcmp(&x->id, &y->id, sizeof(hash128_t));
}

typedef struct
{
    MemoryContext caller;
    Datum         coord;
    bool          has_coord;
} SnAnchorEntCtx;

static bool
sn_visit_anchor_ent(TupleTableSlot *slot, void *opaque)
{
    SnAnchorEntCtx *ctx = (SnAnchorEntCtx *) opaque;
    bool            isnull = false;
    Datum           d_id, d_coord;
    MemoryContext   prev;

    d_id = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    d_coord = slot_getattr(slot, 4, &isnull);
    if (isnull) return true;

    prev = MemoryContextSwitchTo(ctx->caller);
    ctx->coord = PointerGetDatum(PG_DETOAST_DATUM_COPY(d_coord));
    ctx->has_coord = true;
    MemoryContextSwitchTo(prev);
    return false;
}

/* ========================================================================
 * 1. structural.neighbors_of(bytea, int4)
 * ======================================================================== */

Datum
pg_laplace_structural_neighbors_of(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    hash128_t      anchor_id;
    int32          limit;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        return (Datum) 0;
    anchor_id = datum_to_hash128(PG_GETARG_DATUM(0));
    limit = PG_GETARG_INT32(1);
    if (limit < 0)
        limit = 0;

    work = AllocSetContextCreate(caller, "structural_neighbors_of", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    {
        Oid            b3_oid = laplace_pub_blake3_oid();
        Oid            b3_arr_oid = laplace_pub_blake3_array_oid();
        Oid            geom_oid = laplace_pub_geometry_oid();
        ArrayType     *anch_arr = laplace_pub_blake3_array(&anchor_id, 1);
        Datum          e_arg = PointerGetDatum(anch_arr);
        bool           e_null = false;
        SnAnchorEntCtx actx = { work, (Datum) 0, false };

        (void) laplace_pub_call_srf("public", "laplace_entities", 1, &b3_arr_oid,
                                    &e_arg, &e_null, sn_visit_anchor_ent, &actx);
        pfree(anch_arr);

        if (actx.has_coord)
        {
            Oid      knn_types[6] = { geom_oid, INT4OID, BOOLOID, INT2OID, INT2OID, b3_oid };
            Datum    knn_args[6] = {
                actx.coord, Int32GetDatum(limit), BoolGetDatum(true),
                Int16GetDatum(0), (Datum) 0, laplace_pub_hash128_to_blake3(&anchor_id)
            };
            bool     knn_nulls[6] = { false, false, false, false, true, false };
            SnKnnCtx kctx;

            MemSet(&kctx, 0, sizeof(kctx));
            kctx.caller = work;
            (void) laplace_pub_call_srf("public", "laplace_knn4d", 6, knn_types,
                                        knn_args, knn_nulls, sn_visit_knn, &kctx);

            if (kctx.count > 0)
            {
                hash128_t    *all_c_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (kctx.count + 1));
                hash128_t    *n_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) kctx.count);
                ArrayType    *c_arr, *l_arr;
                HTAB         *curves = sn_hash_id(work, "sn curves", sizeof(SnCurveEntry), kctx.count + 1);
                HTAB         *labels = sn_hash_id(work, "sn labels", sizeof(SnLabelEntry), kctx.count);
                SnCurveCtx    cctx = { work, curves };
                SnLabelCtx    lctx = { work, labels };
                SnCurveEntry *anch_curve;
                double        zero_xyzm[4] = { 0.0, 0.0, 0.0, 0.0 };
                Datum         zero_pt = laplace_pub_point4d_geom(zero_xyzm);
                Oid           g2_types[2] = { geom_oid, geom_oid };

                for (int i = 0; i < kctx.count; i++)
                {
                    n_ids[i] = kctx.rows[i].id;
                    all_c_ids[i] = kctx.rows[i].id;
                }
                all_c_ids[kctx.count] = anchor_id;

                c_arr = laplace_pub_blake3_array(all_c_ids, kctx.count + 1);
                {
                    Datum ca = PointerGetDatum(c_arr);
                    bool  cn = false;
                    (void) laplace_pub_call_srf("public", "laplace_curves", 1,
                                                &b3_arr_oid, &ca, &cn, sn_visit_curve, &cctx);
                }
                pfree(c_arr);

                l_arr = laplace_pub_blake3_array(n_ids, kctx.count);
                {
                    Datum la = PointerGetDatum(l_arr);
                    bool  ln = false;
                    (void) laplace_pub_call_srf("public", "laplace_labels", 1,
                                                &b3_arr_oid, &la, &ln, sn_visit_label, &lctx);
                }
                pfree(l_arr);

                anch_curve = (SnCurveEntry *) hash_search(curves, &anchor_id, HASH_FIND, NULL);
                if (kctx.count > 1)
                    qsort(kctx.rows, (Size) kctx.count, sizeof(SnKnnRow), sn_knn_order_cmp);

                for (int i = 0; i < kctx.count; i++)
                {
                    const SnKnnRow *r = &kctx.rows[i];
                    SnLabelEntry   *le = (SnLabelEntry *) hash_search(labels, &r->id, HASH_FIND, NULL);
                    SnCurveEntry   *ce = (SnCurveEntry *) hash_search(curves, &r->id, HASH_FIND, NULL);
                    char            hex[33];
                    double          frechet = 0.0, radius = 0.0;
                    bool            frechet_null = true, rad_null = true;
                    Datum           vals[9];
                    bool            nulls[9];

                    lp_id_hex((const lp_id *) &r->id, hex);
                    if (anch_curve != NULL && ce != NULL)
                    {
                        Datum fa[2] = { ce->curve, anch_curve->curve };
                        bool  fn[2] = { false, false };
                        Datum fres = laplace_pub_call("public", "laplace_frechet4d", 2,
                                                      g2_types, fa, fn, &frechet_null);
                        if (!frechet_null)
                            frechet = DatumGetFloat8(fres);
                    }
                    if (r->coord != (Datum) 0)
                    {
                        Datum da[2] = { r->coord, zero_pt };
                        bool  dn[2] = { false, false };
                        Datum dres = laplace_pub_call("public", "laplace_distance4d", 2,
                                                      g2_types, da, dn, &rad_null);
                        if (!rad_null)
                            radius = DatumGetFloat8(dres);
                    }

                    MemSet(nulls, 0, sizeof(nulls));
                    vals[0] = CStringGetTextDatum(hex);
                    vals[1] = CStringGetTextDatum((le && !le->content_null && le->content)
                                                  ? le->content : "Unrealized entity");
                    vals[2] = Float8GetDatum(r->distance);
                    if (frechet_null) { nulls[3] = true; vals[3] = (Datum) 0; }
                    else vals[3] = Float8GetDatum(frechet);
                    vals[4] = Float8GetDatum(r->xyzm[0]);
                    vals[5] = Float8GetDatum(r->xyzm[1]);
                    vals[6] = Float8GetDatum(r->xyzm[2]);
                    vals[7] = Float8GetDatum(r->xyzm[3]);
                    if (rad_null) { nulls[8] = true; vals[8] = (Datum) 0; }
                    else vals[8] = Float8GetDatum(radius);

                    tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
                    pfree(DatumGetPointer(vals[0]));
                    pfree(DatumGetPointer(vals[1]));
                }
            }
        }
    }

    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return (Datum) 0;
}

/* ========================================================================
 * 2. structural.nearest_neighbors_4d(text, int4)
 * ======================================================================== */

Datum
pg_laplace_nearest_neighbors_4d(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    text          *word;
    int32          limit;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        return (Datum) 0;
    word = PG_GETARG_TEXT_PP(0);
    limit = PG_GETARG_INT32(1);
    if (limit < 0)
        limit = 0;

    work = AllocSetContextCreate(caller, "nearest_neighbors_4d", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    {
        Oid    text_oid = TEXTOID;
        Oid    b3_oid = laplace_pub_blake3_oid();
        Oid    b3_arr_oid = laplace_pub_blake3_array_oid();
        Oid    geom_oid = laplace_pub_geometry_oid();
        Datum  w_arg = PointerGetDatum(word);
        bool   w_null = false, id_null = false, coord_null = false;
        Datum  d_aid, d_coord;

        d_aid = laplace_pub_call("public", "laplace_text_id", 1, &text_oid, &w_arg, &w_null, &id_null);
        d_coord = laplace_pub_call("public", "laplace_text_coord", 1, &text_oid, &w_arg, &w_null, &coord_null);

        if (!id_null && !coord_null)
        {
            hash128_t anchor_id = laplace_pub_blake3_to_hash128(d_aid);
            Oid       knn_types[6] = { geom_oid, INT4OID, BOOLOID, INT2OID, INT2OID, b3_oid };
            Datum     knn_args[6] = {
                d_coord, Int32GetDatum(limit), BoolGetDatum(true),
                Int16GetDatum(0), (Datum) 0, laplace_pub_hash128_to_blake3(&anchor_id)
            };
            bool      knn_nulls[6] = { false, false, false, false, true, false };
            SnKnnCtx  kctx;

            MemSet(&kctx, 0, sizeof(kctx));
            kctx.caller = work;
            (void) laplace_pub_call_srf("public", "laplace_knn4d", 6, knn_types,
                                        knn_args, knn_nulls, sn_visit_knn, &kctx);

            if (kctx.count > 0)
            {
                hash128_t    *all_c_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (kctx.count + 1));
                hash128_t    *n_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) kctx.count);
                ArrayType    *c_arr, *l_arr;
                HTAB         *curves = sn_hash_id(work, "nn4d curves", sizeof(SnCurveEntry), kctx.count + 1);
                HTAB         *labels = sn_hash_id(work, "nn4d labels", sizeof(SnLabelEntry), kctx.count);
                SnCurveCtx    cctx = { work, curves };
                SnLabelCtx    lctx = { work, labels };
                SnCurveEntry *anch_curve;
                Oid           g2_types[2] = { geom_oid, geom_oid };

                for (int i = 0; i < kctx.count; i++)
                {
                    n_ids[i] = kctx.rows[i].id;
                    all_c_ids[i] = kctx.rows[i].id;
                }
                all_c_ids[kctx.count] = anchor_id;

                c_arr = laplace_pub_blake3_array(all_c_ids, kctx.count + 1);
                {
                    Datum ca = PointerGetDatum(c_arr);
                    bool  cn = false;
                    (void) laplace_pub_call_srf("public", "laplace_curves", 1,
                                                &b3_arr_oid, &ca, &cn, sn_visit_curve, &cctx);
                }
                pfree(c_arr);

                l_arr = laplace_pub_blake3_array(n_ids, kctx.count);
                {
                    Datum la = PointerGetDatum(l_arr);
                    bool  ln = false;
                    (void) laplace_pub_call_srf("public", "laplace_labels", 1,
                                                &b3_arr_oid, &la, &ln, sn_visit_label, &lctx);
                }
                pfree(l_arr);

                anch_curve = (SnCurveEntry *) hash_search(curves, &anchor_id, HASH_FIND, NULL);
                if (kctx.count > 1)
                    qsort(kctx.rows, (Size) kctx.count, sizeof(SnKnnRow), sn_knn_order_cmp);

                for (int i = 0; i < kctx.count; i++)
                {
                    const SnKnnRow *r = &kctx.rows[i];
                    SnLabelEntry   *le = (SnLabelEntry *) hash_search(labels, &r->id, HASH_FIND, NULL);
                    SnCurveEntry   *ce;
                    bool            frechet_null = true;
                    double          frechet = 0.0;
                    Datum           vals[3];
                    bool            nulls[3] = { false, false, true };

                    if (le == NULL)
                        continue;

                    ce = (SnCurveEntry *) hash_search(curves, &r->id, HASH_FIND, NULL);
                    if (anch_curve != NULL && ce != NULL)
                    {
                        Datum fa[2] = { ce->curve, anch_curve->curve };
                        bool  fn[2] = { false, false };
                        Datum fres = laplace_pub_call("public", "laplace_frechet4d", 2,
                                                      g2_types, fa, fn, &frechet_null);
                        if (!frechet_null)
                            frechet = DatumGetFloat8(fres);
                    }

                    vals[0] = le->content_null ? (Datum) 0 : CStringGetTextDatum(le->content);
                    nulls[0] = le->content_null;
                    vals[1] = Float8GetDatum(r->distance);
                    vals[2] = frechet_null ? (Datum) 0 : Float8GetDatum(frechet);
                    nulls[2] = frechet_null;

                    tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
                    if (!nulls[0]) pfree(DatumGetPointer(vals[0]));
                }
            }
        }
    }

    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return (Datum) 0;
}

/* ========================================================================
 * 3. structural.explore_anchor_neighbors(float8,float8,float8,float8,text,int4,int4,float8)
 * ======================================================================== */

typedef struct
{
    const char *axis;
    hash128_t   id;
    int16       tier;
    bool        geo_null;
    double      geodesic;
    bool        fre_null;
    double      frechet;
} EanRow;

static int
ean_cmp(const void *a, const void *b)
{
    const EanRow *x = (const EanRow *) a;
    const EanRow *y = (const EanRow *) b;
    int c = strcmp(x->axis, y->axis);
    if (c != 0)
        return c;
    if (x->geo_null != y->geo_null)
        return x->geo_null ? 1 : -1;
    if (!x->geo_null && x->geodesic != y->geodesic)
        return x->geodesic < y->geodesic ? -1 : 1;
    if (x->fre_null != y->fre_null)
        return x->fre_null ? 1 : -1;
    if (!x->fre_null && x->frechet != y->frechet)
        return x->frechet < y->frechet ? -1 : 1;
    return memcmp(&x->id, &y->id, sizeof(hash128_t));
}

Datum
pg_laplace_explore_anchor_neighbors(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    bool           has_anchor = !PG_ARGISNULL(0) && !PG_ARGISNULL(1) &&
                                !PG_ARGISNULL(2) && !PG_ARGISNULL(3);
    int32          geo_lim = PG_ARGISNULL(5) ? 0 : Max(PG_GETARG_INT32(5), 0);
    int32          shape_scan = PG_ARGISNULL(6) ? 0 : Max(PG_GETARG_INT32(6), 0);
    bool           has_max_f = !PG_ARGISNULL(7);
    double         max_f = has_max_f ? PG_GETARG_FLOAT8(7) : 0.0;

    InitMaterializedSRF(fcinfo, 0);
    if (!has_anchor)
        return (Datum) 0;

    work = AllocSetContextCreate(caller, "explore_anchor_neighbors", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    {
        Oid    b3_oid = laplace_pub_blake3_oid();
        Oid    b3_arr_oid = laplace_pub_blake3_array_oid();
        Oid    geom_oid = laplace_pub_geometry_oid();
        Oid    text_oid = TEXTOID;
        double xyzm[4] = {
            PG_GETARG_FLOAT8(0), PG_GETARG_FLOAT8(1),
            PG_GETARG_FLOAT8(2), PG_GETARG_FLOAT8(3)
        };
        Datum  anchor_geom = laplace_pub_point4d_geom(xyzm);
        Datum  curve_geom = (Datum) 0;
        bool   has_curve = false;
        Oid    knn_types[6] = { geom_oid, INT4OID, BOOLOID, INT2OID, INT2OID, b3_oid };
        SnKnnCtx geo_ctx, shape_ctx;
        EanRow *out = NULL;
        int    n_out = 0, out_cap = 64;

        if (!PG_ARGISNULL(4))
        {
            text *wkt_t = PG_GETARG_TEXT_PP(4);
            if (VARSIZE_ANY_EXHDR(wkt_t) > 0)
            {
                Datum w_arg = PointerGetDatum(wkt_t);
                bool  w_null = false, c_null = false;
                curve_geom = laplace_pub_call("public", "st_geomfromtext", 1,
                                              &text_oid, &w_arg, &w_null, &c_null);
                has_curve = !c_null;
            }
        }

        MemSet(&geo_ctx, 0, sizeof(geo_ctx));
        geo_ctx.caller = work;
        {
            Datum ka[6] = { anchor_geom, Int32GetDatum(geo_lim), BoolGetDatum(true),
                            Int16GetDatum(0), (Datum) 0, (Datum) 0 };
            bool  kn[6] = { false, false, false, false, true, true };
            (void) laplace_pub_call_srf("public", "laplace_knn4d", 6, knn_types,
                                        ka, kn, sn_visit_knn, &geo_ctx);
        }

        MemSet(&shape_ctx, 0, sizeof(shape_ctx));
        if (has_curve)
        {
            Datum ka[6] = { anchor_geom, Int32GetDatum(shape_scan), BoolGetDatum(true),
                            Int16GetDatum(0), (Datum) 0, (Datum) 0 };
            bool  kn[6] = { false, false, false, false, true, true };
            shape_ctx.caller = work;
            (void) laplace_pub_call_srf("public", "laplace_knn4d", 6, knn_types,
                                        ka, kn, sn_visit_knn, &shape_ctx);
        }

        out = (EanRow *) palloc(sizeof(EanRow) * (Size) (geo_ctx.count + shape_ctx.count + 16));
        out_cap = geo_ctx.count + shape_ctx.count + 16;

        for (int i = 0; i < geo_ctx.count; i++)
        {
            out[n_out].axis = "geodesic";
            out[n_out].id = geo_ctx.rows[i].id;
            out[n_out].tier = geo_ctx.rows[i].tier;
            out[n_out].geo_null = false;
            out[n_out].geodesic = geo_ctx.rows[i].distance;
            out[n_out].fre_null = true;
            out[n_out].frechet = 0.0;
            n_out++;
        }

        if (has_curve && shape_ctx.count > 0)
        {
            hash128_t *s_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) shape_ctx.count);
            HTAB      *curves = sn_hash_id(work, "ean curves", sizeof(SnCurveEntry), shape_ctx.count);
            SnCurveCtx cctx = { work, curves };
            ArrayType *s_arr;
            Oid        g2_types[2] = { geom_oid, geom_oid };

            for (int i = 0; i < shape_ctx.count; i++)
                s_ids[i] = shape_ctx.rows[i].id;
            s_arr = laplace_pub_blake3_array(s_ids, shape_ctx.count);
            {
                Datum ca = PointerGetDatum(s_arr);
                bool  cn = false;
                (void) laplace_pub_call_srf("public", "laplace_curves", 1, &b3_arr_oid,
                                            &ca, &cn, sn_visit_curve, &cctx);
            }
            pfree(s_arr);

            for (int i = 0; i < shape_ctx.count; i++)
            {
                SnCurveEntry *ce = (SnCurveEntry *) hash_search(curves, &shape_ctx.rows[i].id, HASH_FIND, NULL);
                Datum         fa[2];
                bool          fn[2] = { false, false };
                bool          fnull = false;
                Datum         fres;
                double        d;

                if (ce == NULL)
                    continue;
                fa[0] = ce->curve;
                fa[1] = curve_geom;
                fres = laplace_pub_call("public", "laplace_frechet_4d", 2, g2_types, fa, fn, &fnull);
                if (fnull)
                    continue;
                d = DatumGetFloat8(fres);
                if (has_max_f && !(d <= max_f))
                    continue;
                if (n_out >= out_cap)
                {
                    out_cap *= 2;
                    out = (EanRow *) repalloc(out, sizeof(EanRow) * (Size) out_cap);
                }
                out[n_out].axis = "shape";
                out[n_out].id = shape_ctx.rows[i].id;
                out[n_out].tier = shape_ctx.rows[i].tier;
                out[n_out].geo_null = true;
                out[n_out].geodesic = 0.0;
                out[n_out].fre_null = false;
                out[n_out].frechet = d;
                n_out++;
            }
        }

        if (n_out > 0)
        {
            hash128_t *raw_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_out);
            hash128_t *uniq_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_out);
            int        n_uniq;
            HTAB      *labels;
            SnLabelCtx lctx;
            ArrayType *l_arr;

            for (int i = 0; i < n_out; i++)
                raw_ids[i] = out[i].id;
            n_uniq = sn_dedup_ids(raw_ids, n_out, uniq_ids);
            labels = sn_hash_id(work, "ean labels", sizeof(SnLabelEntry), n_uniq);
            lctx.caller = work;
            lctx.labels = labels;
            l_arr = laplace_pub_blake3_array(uniq_ids, n_uniq);
            {
                Datum la = PointerGetDatum(l_arr);
                bool  ln = false;
                (void) laplace_pub_call_srf("public", "laplace_labels", 1, &b3_arr_oid,
                                            &la, &ln, sn_visit_label, &lctx);
            }
            pfree(l_arr);

            if (n_out > 1)
                qsort(out, (Size) n_out, sizeof(EanRow), ean_cmp);

            for (int i = 0; i < n_out; i++)
            {
                const EanRow *r = &out[i];
                SnLabelEntry *le = (SnLabelEntry *) hash_search(labels, &r->id, HASH_FIND, NULL);
                char          hex[33];
                Datum         vals[6];
                bool          nulls[6] = { false, false, false, false, r->geo_null, r->fre_null };

                lp_id_hex((const lp_id *) &r->id, hex);
                vals[0] = CStringGetTextDatum(r->axis);
                vals[1] = CStringGetTextDatum(hex);
                vals[2] = CStringGetTextDatum((le && !le->content_null && le->content)
                                              ? le->content : "Unrealized entity");
                vals[3] = Int16GetDatum(r->tier);
                vals[4] = r->geo_null ? (Datum) 0 : Float8GetDatum(r->geodesic);
                vals[5] = r->fre_null ? (Datum) 0 : Float8GetDatum(r->frechet);

                tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
                pfree(DatumGetPointer(vals[0]));
                pfree(DatumGetPointer(vals[1]));
                pfree(DatumGetPointer(vals[2]));
            }
        }
    }

    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return (Datum) 0;
}

/* ========================================================================
 * 4. consensus.walk_branches_rendered(text, int4, int4)
 * ======================================================================== */

typedef struct
{
    int64      ordinal;
    hash128_t  id;
    hash128_t  claim;
    bool       claim_null;
    double     rating;
    double     deviation;
    double     cost;
    int32      hops;
    hash128_t *chain;
    int        n_chain;
    hash128_t *claims;
    int        n_claims;
} WbPullRow;

typedef struct
{
    MemoryContext caller;
    WbPullRow    *rows;
    int           count;
    int           cap;
    hash128_t    *all_lbl_ids;
    int           n_lbl, cap_lbl;
    hash128_t    *all_tie_cl;
    int           n_tie_cl, cap_tie_cl;
    hash128_t    *all_st_cl;
    int           n_st_cl, cap_st_cl;
} WbPullCtx;

static void
wb_push_id(hash128_t **buf, int *cnt, int *cap, hash128_t id)
{
    if (*cnt >= *cap)
    {
        Size sz;
        *cap = *cap ? *cap * 2 : 64;
        sz = sizeof(hash128_t) * (Size) (*cap);
        *buf = *buf ? (hash128_t *) repalloc(*buf, sz) : (hash128_t *) palloc(sz);
    }
    (*buf)[(*cnt)++] = id;
}

static bool
wb_visit_pull(TupleTableSlot *slot, void *opaque)
{
    WbPullCtx *ctx = (WbPullCtx *) opaque;
    bool       isnull = false;
    Datum      d_ord, d_id, d_cl, d_rat, d_dev, d_cost, d_hops, d_chain, d_claims;
    int32      hops;
    ArrayType *arr_ch, *arr_cl;
    Datum     *ch_d, *cl_d;
    bool      *ch_n, *cl_n;
    int        n_ch = 0, n_cl = 0;
    MemoryContext prev;

    d_hops = slot_getattr(slot, 10, &isnull);
    if (isnull) return true;
    hops = DatumGetInt32(d_hops);
    if (hops < 1)
        return true;

    d_ord = slot_getattr(slot, 1, &isnull);
    d_id = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    {
        bool clm_null = false, ch_null = false, cls_null = false;
        d_cl = slot_getattr(slot, 5, &clm_null);
        d_rat = slot_getattr(slot, 7, &isnull);
        d_dev = slot_getattr(slot, 8, &isnull);
        d_cost = slot_getattr(slot, 9, &isnull);
        d_chain = slot_getattr(slot, 13, &ch_null);
        d_claims = slot_getattr(slot, 14, &cls_null);

        prev = MemoryContextSwitchTo(ctx->caller);
        if (ctx->count >= ctx->cap)
        {
            Size sz;
            ctx->cap = ctx->cap ? ctx->cap * 2 : 32;
            sz = sizeof(WbPullRow) * (Size) ctx->cap;
            ctx->rows = ctx->rows ? (WbPullRow *) repalloc(ctx->rows, sz) : (WbPullRow *) palloc(sz);
        }
        {
            WbPullRow *r = &ctx->rows[ctx->count];
            MemSet(r, 0, sizeof(*r));
            r->ordinal = DatumGetInt64(d_ord);
            r->id = laplace_pub_blake3_to_hash128(d_id);
            wb_push_id(&ctx->all_lbl_ids, &ctx->n_lbl, &ctx->cap_lbl, r->id);

            r->claim_null = clm_null;
            if (!clm_null)
            {
                r->claim = laplace_pub_blake3_to_hash128(d_cl);
                wb_push_id(&ctx->all_st_cl, &ctx->n_st_cl, &ctx->cap_st_cl, r->claim);
            }
            r->rating = DatumGetFloat8(d_rat);
            r->deviation = DatumGetFloat8(d_dev);
            r->cost = DatumGetFloat8(d_cost);
            r->hops = hops;

            if (!ch_null)
            {
                arr_ch = DatumGetArrayTypeP(d_chain);
                deconstruct_array(arr_ch, laplace_pub_blake3_oid(), 16, false, TYPALIGN_CHAR,
                                  &ch_d, &ch_n, &n_ch);
                if (n_ch > 0)
                {
                    r->chain = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_ch);
                    for (int i = 0; i < n_ch; i++)
                    {
                        if (!ch_n[i])
                        {
                            r->chain[r->n_chain++] = laplace_pub_blake3_to_hash128(ch_d[i]);
                            wb_push_id(&ctx->all_lbl_ids, &ctx->n_lbl, &ctx->cap_lbl, r->chain[r->n_chain - 1]);
                        }
                    }
                }
                pfree(ch_d);
                pfree(ch_n);
            }

            if (!cls_null)
        {
            arr_cl = DatumGetArrayTypeP(d_claims);
            deconstruct_array(arr_cl, laplace_pub_blake3_oid(), 16, false, TYPALIGN_CHAR,
                              &cl_d, &cl_n, &n_cl);
            if (n_cl > 0)
            {
                r->claims = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_cl);
                for (int i = 0; i < n_cl; i++)
                {
                    if (!cl_n[i])
                    {
                        r->claims[r->n_claims++] = laplace_pub_blake3_to_hash128(cl_d[i]);
                        wb_push_id(&ctx->all_tie_cl, &ctx->n_tie_cl, &ctx->cap_tie_cl, r->claims[r->n_claims - 1]);
                    }
                }
            }
            pfree(cl_d);
            pfree(cl_n);
        }
        ctx->count++;
        }
    }
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct { hash128_t claim; hash128_t rel; } WbTieEntry;
typedef struct { hash128_t claim; int64 matches; } WbStandEntry;

typedef struct
{
    MemoryContext caller;
    HTAB         *ties;
    hash128_t    *all_lbl_ids;
    int          *n_lbl;
    int          *cap_lbl;
} WbTieCtx;

static bool
wb_visit_path(TupleTableSlot *slot, void *opaque)
{
    WbTieCtx  *ctx = (WbTieCtx *) opaque;
    bool       isnull = false;
    Datum      d_ent, d_path;
    lp_path    path;
    lp_id      parts[2];
    hash128_t  ent, rel;
    WbTieEntry *te;
    bool       f = false;
    MemoryContext prev;

    d_path = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    path = laplace_pub_geo_path(d_path);
    if (lp_path_len(path) < 2 || lp_path_expand(path, parts, 2) < 2)
        return true;

    d_ent = slot_getattr(slot, 1, &isnull);
    if (isnull) return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);
    memcpy(&rel, parts[1].b, sizeof(hash128_t));

    prev = MemoryContextSwitchTo(ctx->caller);
    te = (WbTieEntry *) hash_search(ctx->ties, &ent, HASH_ENTER, &f);
    if (!f)
        te->rel = rel;
    wb_push_id(&ctx->all_lbl_ids, ctx->n_lbl, ctx->cap_lbl, rel);
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct
{
    MemoryContext caller;
    HTAB         *stand;
} WbStandCtx;

static bool
wb_visit_standing(TupleTableSlot *slot, void *opaque)
{
    WbStandCtx   *ctx = (WbStandCtx *) opaque;
    bool          isnull = false;
    Datum         d_cl, d_m;
    hash128_t     cl;
    WbStandEntry *se;
    bool          f = false;
    MemoryContext prev;

    d_cl = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    cl = laplace_pub_blake3_to_hash128(d_cl);
    d_m = slot_getattr(slot, 6, &isnull);
    if (isnull) return true;

    prev = MemoryContextSwitchTo(ctx->caller);
    se = (WbStandEntry *) hash_search(ctx->stand, &cl, HASH_ENTER, &f);
    if (!f)
        se->matches = DatumGetInt64(d_m);
    MemoryContextSwitchTo(prev);
    return true;
}

static int
wb_cmp(const void *a, const void *b)
{
    const WbPullRow *x = (const WbPullRow *) a;
    const WbPullRow *y = (const WbPullRow *) b;
    if (x->hops != y->hops) return x->hops < y->hops ? -1 : 1;
    if (x->cost != y->cost) return x->cost < y->cost ? -1 : 1;
    if (x->ordinal != y->ordinal) return x->ordinal < y->ordinal ? -1 : 1;
    return 0;
}

Datum
pg_laplace_walk_branches_rendered(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    text          *word;
    int32          hops, fan;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1) || PG_ARGISNULL(2))
        return (Datum) 0;
    word = PG_GETARG_TEXT_PP(0);
    hops = PG_GETARG_INT32(1);
    fan = PG_GETARG_INT32(2);

    work = AllocSetContextCreate(caller, "walk_branches_rendered", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    {
        Oid    text_oid = TEXTOID;
        Oid    b3_arr_oid = laplace_pub_blake3_array_oid();
        Datum  w_arg = PointerGetDatum(word);
        bool   w_null = false, id_null = false;
        Datum  d_root = laplace_pub_call("public", "laplace_text_id", 1, &text_oid, &w_arg, &w_null, &id_null);

        if (!id_null)
        {
            hash128_t  root_id = laplace_pub_blake3_to_hash128(d_root);
            ArrayType *roots_arr = laplace_pub_blake3_array(&root_id, 1);
            ArrayType *empty_b3 = laplace_pub_blake3_array(NULL, 0);
            Oid        p_types[9] = {
                b3_arr_oid, b3_arr_oid, INT4OID, INT8OID, INT8OID,
                b3_arr_oid, b3_arr_oid, FLOAT8OID, FLOAT8OID
            };
            Datum      p_args[9] = {
                PointerGetDatum(roots_arr), (Datum) 0, Int32GetDatum(hops),
                Int64GetDatum((int64) fan), Int64GetDatum(4096),
                (Datum) 0, PointerGetDatum(empty_b3),
                Float8GetDatum(2.0), Float8GetDatum(0.05)
            };
            bool       p_nulls[9] = {
                false, true, false, false, false, true, false, false, false
            };
            WbPullCtx  pctx;

            MemSet(&pctx, 0, sizeof(pctx));
            pctx.caller = work;
            (void) laplace_pub_call_srf("public", "laplace_pull", 9,
                                        p_types, p_args, p_nulls,
                                        wb_visit_pull, &pctx);
            pfree(roots_arr);
            pfree(empty_b3);

            if (pctx.count > 0)
            {
                HTAB *ties = sn_hash_id(work, "wb ties", sizeof(WbTieEntry), Max(pctx.n_tie_cl, 16));
                HTAB *stand = sn_hash_id(work, "wb stand", sizeof(WbStandEntry), Max(pctx.n_st_cl, 16));
                HTAB *labels;

                if (pctx.n_tie_cl > 0)
                {
                    hash128_t *uniq_tc = (hash128_t *) palloc(sizeof(hash128_t) * (Size) pctx.n_tie_cl);
                    int        n_utc = sn_dedup_ids(pctx.all_tie_cl, pctx.n_tie_cl, uniq_tc);
                    ArrayType *tarr = laplace_pub_blake3_array(uniq_tc, n_utc);
                    Datum      ta = PointerGetDatum(tarr);
                    bool       tn = false;
                    WbTieCtx   tctx = { work, ties, pctx.all_lbl_ids, &pctx.n_lbl, &pctx.cap_lbl };

                    (void) laplace_pub_call_srf("public", "laplace_paths", 1, &b3_arr_oid,
                                                &ta, &tn, wb_visit_path, &tctx);
                    pctx.all_lbl_ids = tctx.all_lbl_ids;
                    pfree(tarr);
                }

                if (pctx.n_st_cl > 0)
                {
                    hash128_t *uniq_sc = (hash128_t *) palloc(sizeof(hash128_t) * (Size) pctx.n_st_cl);
                    int        n_usc = sn_dedup_ids(pctx.all_st_cl, pctx.n_st_cl, uniq_sc);
                    ArrayType *sarr = laplace_pub_blake3_array(uniq_sc, n_usc);
                    Datum      sa = PointerGetDatum(sarr);
                    bool       sn = false;
                    WbStandCtx sctx = { work, stand };

                    (void) laplace_pub_call_srf("public", "laplace_standings", 1, &b3_arr_oid,
                                                &sa, &sn, wb_visit_standing, &sctx);
                    pfree(sarr);
                }

                labels = sn_hash_id(work, "wb labels", sizeof(SnLabelEntry), Max(pctx.n_lbl, 16));
                if (pctx.n_lbl > 0)
                {
                    hash128_t *uniq_lb = (hash128_t *) palloc(sizeof(hash128_t) * (Size) pctx.n_lbl);
                    int        n_ulb = sn_dedup_ids(pctx.all_lbl_ids, pctx.n_lbl, uniq_lb);
                    ArrayType *larr = laplace_pub_blake3_array(uniq_lb, n_ulb);
                    Datum      la = PointerGetDatum(larr);
                    bool       ln = false;
                    SnLabelCtx lctx = { work, labels };

                    (void) laplace_pub_call_srf("public", "laplace_labels", 1, &b3_arr_oid,
                                                &la, &ln, sn_visit_label, &lctx);
                    pfree(larr);
                }

                if (pctx.count > 1)
                    qsort(pctx.rows, (Size) pctx.count, sizeof(WbPullRow), wb_cmp);

                for (int i = 0; i < pctx.count; i++)
                {
                    const WbPullRow *r = &pctx.rows[i];
                    Datum           *ch_t = r->n_chain > 0 ? (Datum *) palloc(sizeof(Datum) * (Size) r->n_chain) : NULL;
                    Datum           *rl_t = r->n_claims > 0 ? (Datum *) palloc(sizeof(Datum) * (Size) r->n_claims) : NULL;
                    ArrayType       *ch_arr, *rl_arr;
                    char             id_hex[33];
                    SnLabelEntry    *le = (SnLabelEntry *) hash_search(labels, &r->id, HASH_FIND, NULL);
                    WbStandEntry    *se = r->claim_null ? NULL
                        : (WbStandEntry *) hash_search(stand, &r->claim, HASH_FIND, NULL);
                    Datum            vals[8];
                    bool             nulls[8] = { false, false, false, false, false, false, false, false };

                    for (int j = 0; j < r->n_chain; j++)
                    {
                        char hex[33];
                        lp_id_hex((const lp_id *) &r->chain[j], hex);
                        ch_t[j] = CStringGetTextDatum(hex);
                    }
                    ch_arr = r->n_chain > 0
                        ? construct_array(ch_t, r->n_chain, TEXTOID, -1, false, TYPALIGN_INT)
                        : construct_empty_array(TEXTOID);

                    for (int j = 0; j < r->n_claims; j++)
                    {
                        WbTieEntry *te = (WbTieEntry *) hash_search(ties, &r->claims[j], HASH_FIND, NULL);
                        if (te != NULL)
                        {
                            char hex[33];
                            lp_id_hex((const lp_id *) &te->rel, hex);
                            rl_t[j] = CStringGetTextDatum(hex);
                        }
                        else
                            rl_t[j] = CStringGetTextDatum("");
                    }
                    rl_arr = r->n_claims > 0
                        ? construct_array(rl_t, r->n_claims, TEXTOID, -1, false, TYPALIGN_INT)
                        : construct_empty_array(TEXTOID);

                    lp_id_hex((const lp_id *) &r->id, id_hex);
                    vals[0] = Int32GetDatum(r->hops);
                    vals[1] = PointerGetDatum(ch_arr);
                    vals[2] = PointerGetDatum(rl_arr);
                    vals[3] = CStringGetTextDatum(id_hex);
                    vals[4] = CStringGetTextDatum((le && !le->content_null && le->content)
                                                  ? le->content : id_hex);
                    vals[5] = DirectFunctionCall1(float8_numeric,
                                                  Float8GetDatum(r->rating - 2.0 * r->deviation));
                    vals[6] = DirectFunctionCall1(float8_numeric, Float8GetDatum(exp(-r->cost)));
                    vals[7] = Int64GetDatum(se ? se->matches : 0);

                    tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
                }
            }
        }
    }

    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return (Datum) 0;
}
