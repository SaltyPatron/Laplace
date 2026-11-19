#include "postgres.h"

#include <limits.h>
#include <math.h>
#include <string.h>

#include "catalog/pg_type.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "nodes/params.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "laplace/core/hash128.h"
#include "laplace/core/sql_catalog.h"
#include "laplace/geometry.h"
#include "laplace/laplace.h"
#include "laplace_pg/executor.h"

#include "public_direct.h"
#include "spi_common.h"
#include "trajectory_wkb.h"

typedef enum MetricKind
{
    METRIC_ANGULAR = 0,
    METRIC_FRECHET,
    METRIC_HAUSDORFF
} MetricKind;

typedef struct MetricCand
{
    int    idx;
    double d;
} MetricCand;

typedef struct MetricCurveEntry
{
    hash128_t id;
    double   *pts;
    uint32    n_pts;
} MetricCurveEntry;

typedef struct MetricEntityTmp
{
    hash128_t id;
    double    coord[4];
} MetricEntityTmp;

typedef struct MetricVertState
{
    MemoryContext  caller;
    bool           need_curves;
    HTAB          *curve_map;
    MetricEntityTmp *tmp;
    int            tmp_count;
    int            tmp_cap;
    hash128_t     *ids;
    double        *coords;
    double       **curve_pts;
    uint32        *curve_n;
    int            count;
    int            cap;
    uint64         seen;
} MetricVertState;

static int
cmp_metric_hash128(const void *pa, const void *pb)
{
    return memcmp(pa, pb, sizeof(hash128_t));
}

static int
cmp_metric_entity_tmp(const void *pa, const void *pb)
{
    const MetricEntityTmp *a = (const MetricEntityTmp *) pa;
    const MetricEntityTmp *b = (const MetricEntityTmp *) pb;
    return memcmp(&a->id, &b->id, sizeof(hash128_t));
}

static int
cmp_pg_float8(double a, double b)
{
    bool na = isnan(a);
    bool nb = isnan(b);

    if (na != nb)
        return na ? 1 : -1;
    if (na || a == b)
        return 0;
    return a < b ? -1 : 1;
}

static int
cmp_metric_cand(const void *pa, const void *pb)
{
    const MetricCand *a = (const MetricCand *) pa;
    const MetricCand *b = (const MetricCand *) pb;
    int               c = cmp_pg_float8(a->d, b->d);

    if (c != 0)
        return c;
    return a->idx < b->idx ? -1 : (a->idx > b->idx ? 1 : 0);
}

static bool
visit_metric_curve(TupleTableSlot *slot, void *context)
{
    MetricVertState  *st = (MetricVertState *) context;
    bool              isnull = false;
    Datum             d_id, d_wkb;
    hash128_t         id;
    bytea            *wkb;
    uint32            n_pts = 0;
    const unsigned char *raw;
    MetricCurveEntry *ce;
    bool              found = false;
    MemoryContext     prev;

    if ((++st->seen & 4095) == 0)
        CHECK_FOR_INTERRUPTS();

    d_id = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    id = laplace_pub_blake3_to_hash128(d_id);

    d_wkb = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;
    wkb = DatumGetByteaPP(d_wkb);
    raw = laplace_trajectory_wkb_points(wkb, &n_pts);
    if (n_pts == 0)
        return true;

    prev = MemoryContextSwitchTo(st->caller);
    ce = (MetricCurveEntry *) hash_search(st->curve_map, &id, HASH_ENTER, &found);
    if (!found)
    {
        Size bytes = (Size) n_pts * 4 * sizeof(double);
        ce->pts   = (double *) palloc(bytes);
        memcpy(ce->pts, raw, bytes);
        ce->n_pts = n_pts;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

static bool
visit_metric_entity(TupleTableSlot *slot, void *context)
{
    MetricVertState *st = (MetricVertState *) context;
    bool             isnull = false;
    Datum            d_id, d_coord;
    MetricEntityTmp  item;
    MemoryContext    prev;

    if ((++st->seen & 4095) == 0)
        CHECK_FOR_INTERRUPTS();

    d_id = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    d_coord = slot_getattr(slot, 4, &isnull);
    if (isnull)
        return true;

    item.id = laplace_pub_blake3_to_hash128(d_id);
    if (!laplace_pub_geo_point4d(d_coord, item.coord))
        return true;

    prev = MemoryContextSwitchTo(st->caller);
    if (st->tmp_count == st->tmp_cap)
    {
        int new_cap = st->tmp_cap == 0 ? 64 : st->tmp_cap * 2;
        if ((Size) new_cap > MaxAllocSize / sizeof(MetricEntityTmp))
            ereport(ERROR, (errmsg("generation.metric_edges: vertex count exceeds allocation limit")));
        st->tmp = st->tmp == NULL
            ? (MetricEntityTmp *) palloc((Size) new_cap * sizeof(MetricEntityTmp))
            : (MetricEntityTmp *) repalloc(st->tmp, (Size) new_cap * sizeof(MetricEntityTmp));
        st->tmp_cap = new_cap;
    }
    st->tmp[st->tmp_count++] = item;
    MemoryContextSwitchTo(prev);
    return true;
}

PG_FUNCTION_INFO_V1(pg_laplace_metric_edges);

Datum
pg_laplace_metric_edges(PG_FUNCTION_ARGS)
{
    MemoryContext   caller = CurrentMemoryContext;
    ReturnSetInfo  *rsinfo;
    ArrayType      *vocab;
    char           *metric_cstr = NULL;
    MetricKind      kind = METRIC_ANGULAR;
    int32           k;
    int32           probe_limit;
    MetricVertState vst;
    double         *ang_buf;
    MetricCand     *cands;

    InitMaterializedSRF(fcinfo, 0);
    rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(2))
        return (Datum) 0;

    k = PG_GETARG_INT32(2);
    if (k <= 0)
        return (Datum) 0;

    probe_limit = PG_ARGISNULL(3) ? k : Max(PG_GETARG_INT32(3), k);

    if (!PG_ARGISNULL(1))
    {
        metric_cstr = text_to_cstring(PG_GETARG_TEXT_PP(1));
        if (strcmp(metric_cstr, "frechet") == 0)
            kind = METRIC_FRECHET;
        else if (strcmp(metric_cstr, "hausdorff") == 0)
            kind = METRIC_HAUSDORFF;
        pfree(metric_cstr);
    }

    vocab = PG_GETARG_ARRAYTYPE_P(0);

    memset(&vst, 0, sizeof(vst));
    vst.caller      = caller;
    vst.need_curves = (kind != METRIC_ANGULAR);

    {
        Datum     *elems;
        bool      *nulls;
        int        raw_count = 0;
        hash128_t *ids;
        int        n_ids = 0;

        deconstruct_array(vocab, BYTEAOID, -1, false, TYPALIGN_INT, &elems, &nulls, &raw_count);
        if (raw_count == 0)
        {
            pfree(elems);
            pfree(nulls);
            return (Datum) 0;
        }
        ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) raw_count);
        for (int i = 0; i < raw_count; ++i)
        {
            if (!nulls[i])
                ids[n_ids++] = datum_to_hash128(elems[i]);
        }
        pfree(elems);
        pfree(nulls);
        if (n_ids <= 1)
        {
            pfree(ids);
            return (Datum) 0;
        }
        qsort(ids, (Size) n_ids, sizeof(hash128_t), cmp_metric_hash128);
        {
            int w = 1;
            for (int i = 1; i < n_ids; ++i)
            {
                if (memcmp(&ids[w - 1], &ids[i], sizeof(hash128_t)) != 0)
                    ids[w++] = ids[i];
            }
            n_ids = w;
        }
        if (n_ids <= 1)
        {
            pfree(ids);
            return (Datum) 0;
        }

        {
            ArrayType *b3_arr = laplace_pub_blake3_array(ids, n_ids);
            Oid        argtype = laplace_pub_blake3_array_oid();
            Datum      args[1] = { PointerGetDatum(b3_arr) };
            bool       argnulls[1] = { false };

            if (vst.need_curves)
            {
                HASHCTL ctl;
                memset(&ctl, 0, sizeof(ctl));
                ctl.keysize   = sizeof(hash128_t);
                ctl.entrysize = sizeof(MetricCurveEntry);
                ctl.hcxt      = caller;
                vst.curve_map = hash_create("metric_edges curves", Max(n_ids, 16), &ctl,
                                            HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
                (void) laplace_pub_call_srf("public", "laplace_curves", 1, &argtype,
                                            args, argnulls, visit_metric_curve, &vst);
            }

            (void) laplace_pub_call_srf("public", "laplace_entities", 1, &argtype,
                                        args, argnulls, visit_metric_entity, &vst);
            pfree(b3_arr);
        }
        pfree(ids);
    }

    if (vst.tmp_count > 1)
        qsort(vst.tmp, (Size) vst.tmp_count, sizeof(MetricEntityTmp), cmp_metric_entity_tmp);

    if (vst.tmp_count > 0)
    {
        vst.ids       = (hash128_t *) palloc((Size) vst.tmp_count * sizeof(hash128_t));
        vst.coords    = (double *) palloc((Size) vst.tmp_count * 4 * sizeof(double));
        vst.curve_pts = (double **) palloc0((Size) vst.tmp_count * sizeof(double *));
        vst.curve_n   = (uint32 *) palloc0((Size) vst.tmp_count * sizeof(uint32));
        for (int i = 0; i < vst.tmp_count; ++i)
        {
            int dst;
            if (vst.count > 0 && memcmp(&vst.ids[vst.count - 1], &vst.tmp[i].id, sizeof(hash128_t)) == 0)
                continue;
            dst = vst.count++;
            vst.ids[dst] = vst.tmp[i].id;
            memcpy(&vst.coords[4 * dst], vst.tmp[i].coord, 4 * sizeof(double));
            if (vst.need_curves && vst.curve_map != NULL)
            {
                MetricCurveEntry *ce = (MetricCurveEntry *) hash_search(
                    vst.curve_map, &vst.tmp[i].id, HASH_FIND, NULL);
                if (ce != NULL)
                {
                    vst.curve_pts[dst] = ce->pts;
                    vst.curve_n[dst]   = ce->n_pts;
                }
            }
        }
        pfree(vst.tmp);
        vst.tmp = NULL;
    }
    if (vst.curve_map != NULL)
    {
        hash_destroy(vst.curve_map);
        vst.curve_map = NULL;
    }

    if (vst.count <= 1)
    {
        for (int i = 0; i < vst.count; ++i)
        {
            if (vst.curve_pts[i] != NULL)
                pfree(vst.curve_pts[i]);
        }
        if (vst.ids != NULL) pfree(vst.ids);
        if (vst.coords != NULL) pfree(vst.coords);
        if (vst.curve_pts != NULL) pfree(vst.curve_pts);
        if (vst.curve_n != NULL) pfree(vst.curve_n);
        return (Datum) 0;
    }

    ang_buf = (double *) palloc((Size) vst.count * sizeof(double));
    cands   = (MetricCand *) palloc((Size) (vst.count - 1) * sizeof(MetricCand));

    for (int i = 0; i < vst.count; ++i)
    {
        int   m = 0;
        int   n_probe;
        int   n_emit;
        Datum subj_datum;

        CHECK_FOR_INTERRUPTS();

        lp_angle4_batch(&vst.coords[4 * i], vst.coords, (size_t) vst.count, ang_buf);
        for (int j = 0; j < vst.count; ++j)
        {
            if (j == i)
                continue;
            cands[m].idx = j;
            cands[m].d   = ang_buf[j];
            m++;
        }

        qsort(cands, (Size) m, sizeof(MetricCand), cmp_metric_cand);
        n_probe = Min(m, probe_limit);

        if (kind != METRIC_ANGULAR)
        {
            for (int c = 0; c < n_probe; ++c)
            {
                int j = cands[c].idx;
                if (vst.curve_n[i] > 0 && vst.curve_n[j] > 0)
                {
                    cands[c].d = (kind == METRIC_FRECHET)
                        ? lp_frechet4(vst.curve_pts[i], vst.curve_n[i],
                                      vst.curve_pts[j], vst.curve_n[j])
                        : lp_hausdorff4(vst.curve_pts[i], vst.curve_n[i],
                                        vst.curve_pts[j], vst.curve_n[j]);
                }
            }
            qsort(cands, (Size) n_probe, sizeof(MetricCand), cmp_metric_cand);
        }

        n_emit     = Min(n_probe, k);
        subj_datum = hash128_to_datum(&vst.ids[i]);
        for (int c = 0; c < n_emit; ++c)
        {
            Datum vals[3];
            bool  nulls[3] = { false, false, false };

            vals[0] = subj_datum;
            vals[1] = hash128_to_datum(&vst.ids[cands[c].idx]);
            vals[2] = Float8GetDatum(cands[c].d);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
        }
    }

    pfree(cands);
    pfree(ang_buf);
    for (int i = 0; i < vst.count; ++i)
    {
        if (vst.curve_pts[i] != NULL)
            pfree(vst.curve_pts[i]);
    }
    pfree(vst.ids);
    pfree(vst.coords);
    pfree(vst.curve_pts);
    pfree(vst.curve_n);

    return (Datum) 0;
}
