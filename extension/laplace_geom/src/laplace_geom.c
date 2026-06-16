#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "nodes/execnodes.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/tuplestore.h"
#include "catalog/pg_type.h"
#include "access/htup_details.h"

#include "laplace/core/version.h"
#include "laplace/core/hash128.h"
#include "laplace/core/math4d.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/mantissa.h"

#include "liblwgeom.h"

PG_MODULE_MAGIC;

static LWGEOM *
lwgeom_from_datum(Datum d, GSERIALIZED **out_gser)
{
    GSERIALIZED *gser = (GSERIALIZED *) PG_DETOAST_DATUM(d);
    LWGEOM      *geom = lwgeom_from_gserialized(gser);
    if (geom == NULL)
    {
        ereport(ERROR,
                (errcode(ERRCODE_DATA_CORRUPTED),
                 errmsg("laplace_geom: failed to deserialize geometry")));
    }
    *out_gser = gser;
    return geom;
}

static void
require_point4d(LWGEOM *geom, const char *fn_name, POINT4D *out)
{
    if (geom->type != POINTTYPE)
    {
        const char *got = lwtype_name(geom->type);
        lwgeom_free(geom);
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("%s: expected POINT geometry, got %s", fn_name, got)));
    }
    LWPOINT *lwpoint = (LWPOINT *) geom;
    if (lwpoint->point->npoints != 1)
    {
        lwgeom_free(geom);
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("%s: POINT has %u vertices, expected exactly 1",
                        fn_name, lwpoint->point->npoints)));
    }
    getPoint4d_p(lwpoint->point, 0, out);
}

static double *
geom_to_xyzm_buffer(LWGEOM *geom, const char *fn_name, size_t *out_npoints)
{
    POINTARRAY *pa = NULL;

    switch (geom->type)
    {
        case POINTTYPE:
            pa = ((LWPOINT *) geom)->point;
            break;
        case LINETYPE:
            pa = ((LWLINE *) geom)->points;
            break;
        case MULTIPOINTTYPE:
        {
            LWMPOINT *mp = (LWMPOINT *) geom;
            const uint32_t n = mp->ngeoms;
            double *buf = (double *) palloc(sizeof(double) * 4 * (Size) n);
            for (uint32_t i = 0; i < n; ++i)
            {
                POINT4D p4;
                getPoint4d_p(mp->geoms[i]->point, 0, &p4);
                buf[i * 4 + 0] = p4.x;
                buf[i * 4 + 1] = p4.y;
                buf[i * 4 + 2] = p4.z;
                buf[i * 4 + 3] = p4.m;
            }
            *out_npoints = (size_t) n;
            return buf;
        }
        default:
        {
            const char *got = lwtype_name(geom->type);
            lwgeom_free(geom);
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("%s: expected POINT, LINESTRING, or MULTIPOINT, got %s",
                            fn_name, got)));
        }
    }

    const uint32_t n = pa->npoints;
    double *buf = (double *) palloc(sizeof(double) * 4 * (Size) n);
    for (uint32_t i = 0; i < n; ++i)
    {
        POINT4D p4;
        getPoint4d_p(pa, i, &p4);
        buf[i * 4 + 0] = p4.x;
        buf[i * 4 + 1] = p4.y;
        buf[i * 4 + 2] = p4.z;
        buf[i * 4 + 3] = p4.m;
    }
    *out_npoints = (size_t) n;
    return buf;
}

static Datum
gserialized_point4d_datum(double x, double y, double z, double m)
{
    LWPOINT *lwpoint = lwpoint_make4d(SRID_UNKNOWN, x, y, z, m);
    size_t sz;
    GSERIALIZED *gser = gserialized_from_lwgeom((LWGEOM *) lwpoint, &sz);
    SET_VARSIZE(gser, sz);
    lwpoint_free(lwpoint);
    PG_RETURN_POINTER(gser);
}

PG_FUNCTION_INFO_V1(pg_laplace_geom_version);

Datum
pg_laplace_geom_version(PG_FUNCTION_ARGS)
{
    const char *v = laplace_core_version();
    PG_RETURN_TEXT_P(cstring_to_text(v));
}

PG_FUNCTION_INFO_V1(pg_laplace_hash128_blake3);

Datum
pg_laplace_hash128_blake3(PG_FUNCTION_ARGS)
{
    bytea       *data_arg = PG_GETARG_BYTEA_PP(0);
    const uint8 *data     = (const uint8 *) VARDATA_ANY(data_arg);
    Size         data_len = VARSIZE_ANY_EXHDR(data_arg);

    bytea *result = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
    SET_VARSIZE(result, VARHDRSZ + sizeof(hash128_t));

    hash128_blake3(data, (size_t) data_len, (hash128_t *) VARDATA(result));

    PG_RETURN_BYTEA_P(result);
}

PG_FUNCTION_INFO_V1(pg_laplace_hash128_merkle);

Datum
pg_laplace_hash128_merkle(PG_FUNCTION_ARGS)
{
    int16      tier_arg     = PG_GETARG_INT16(0);
    ArrayType *children_arr = PG_GETARG_ARRAYTYPE_P(1);

    if (tier_arg < 0 || tier_arg > 255)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("hash128_merkle: tier %d out of range [0, 255]", tier_arg)));

    if (ARR_NDIM(children_arr) != 1)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("hash128_merkle: children must be a one-dimensional array")));
    if (ARR_ELEMTYPE(children_arr) != BYTEAOID)
        ereport(ERROR,
                (errcode(ERRCODE_DATATYPE_MISMATCH),
                 errmsg("hash128_merkle: children must be bytea[]")));

    Datum *elems;
    bool  *nulls;
    int    n_elems;

    deconstruct_array(children_arr, BYTEAOID, -1, false, 'i',
                      &elems, &nulls, &n_elems);

    hash128_t *children = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_elems);
    for (int i = 0; i < n_elems; i++)
    {
        if (nulls[i])
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("hash128_merkle: child hash at index %d is NULL", i)));
        bytea *child = DatumGetByteaPP(elems[i]);
        Size child_len = VARSIZE_ANY_EXHDR(child);
        if (child_len != sizeof(hash128_t))
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("hash128_merkle: child hash at index %d has length %zu, expected %zu",
                            i, child_len, sizeof(hash128_t))));
        memcpy(&children[i], VARDATA_ANY(child), sizeof(hash128_t));
    }

    bytea *result = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
    SET_VARSIZE(result, VARHDRSZ + sizeof(hash128_t));
    hash128_merkle((uint8_t) tier_arg, children, (size_t) n_elems,
                   (hash128_t *) VARDATA(result));

    PG_RETURN_BYTEA_P(result);
}

PG_FUNCTION_INFO_V1(pg_laplace_distance_4d);

Datum
pg_laplace_distance_4d(PG_FUNCTION_ARGS)
{
    GSERIALIZED *g_a, *g_b;
    LWGEOM *l_a = lwgeom_from_datum(PG_GETARG_DATUM(0), &g_a);
    LWGEOM *l_b = lwgeom_from_datum(PG_GETARG_DATUM(1), &g_b);

    POINT4D pa, pb;
    require_point4d(l_a, "laplace_distance_4d", &pa);
    require_point4d(l_b, "laplace_distance_4d", &pb);

    const double a[4] = {pa.x, pa.y, pa.z, pa.m};
    const double b[4] = {pb.x, pb.y, pb.z, pb.m};
    const double d = math4d_distance(a, b);

    lwgeom_free(l_a);
    lwgeom_free(l_b);
    PG_RETURN_FLOAT8(d);
}

PG_FUNCTION_INFO_V1(pg_laplace_angular_distance_4d);

Datum
pg_laplace_angular_distance_4d(PG_FUNCTION_ARGS)
{
    GSERIALIZED *g_a, *g_b;
    LWGEOM *l_a = lwgeom_from_datum(PG_GETARG_DATUM(0), &g_a);
    LWGEOM *l_b = lwgeom_from_datum(PG_GETARG_DATUM(1), &g_b);

    POINT4D pa, pb;
    require_point4d(l_a, "laplace_angular_distance_4d", &pa);
    require_point4d(l_b, "laplace_angular_distance_4d", &pb);

    const double a[4] = {pa.x, pa.y, pa.z, pa.m};
    const double b[4] = {pb.x, pb.y, pb.z, pb.m};
    const double d = math4d_angular_distance(a, b);

    lwgeom_free(l_a);
    lwgeom_free(l_b);
    PG_RETURN_FLOAT8(d);
}

PG_FUNCTION_INFO_V1(pg_laplace_dwithin_4d);

Datum
pg_laplace_dwithin_4d(PG_FUNCTION_ARGS)
{
    GSERIALIZED *g_a, *g_b;
    LWGEOM *l_a = lwgeom_from_datum(PG_GETARG_DATUM(0), &g_a);
    LWGEOM *l_b = lwgeom_from_datum(PG_GETARG_DATUM(1), &g_b);
    float8 eps = PG_GETARG_FLOAT8(2);

    if (eps < 0.0)
    {
        lwgeom_free(l_a);
        lwgeom_free(l_b);
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("laplace_dwithin_4d: eps must be non-negative (got %g)", eps)));
    }

    POINT4D pa, pb;
    require_point4d(l_a, "laplace_dwithin_4d", &pa);
    require_point4d(l_b, "laplace_dwithin_4d", &pb);

    const double a[4] = {pa.x, pa.y, pa.z, pa.m};
    const double b[4] = {pb.x, pb.y, pb.z, pb.m};
    const double d2 = math4d_distance_sq(a, b);
    const bool   within = d2 <= eps * eps;

    lwgeom_free(l_a);
    lwgeom_free(l_b);
    PG_RETURN_BOOL(within);
}

PG_FUNCTION_INFO_V1(pg_laplace_centroid_4d);

Datum
pg_laplace_centroid_4d(PG_FUNCTION_ARGS)
{
    GSERIALIZED *g;
    LWGEOM *l = lwgeom_from_datum(PG_GETARG_DATUM(0), &g);

    size_t npoints;
    double *buf = geom_to_xyzm_buffer(l, "laplace_centroid_4d", &npoints);

    if (npoints == 0)
    {
        pfree(buf);
        lwgeom_free(l);
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("laplace_centroid_4d: empty geometry has no centroid")));
    }

    double c[4];
    math4d_centroid(buf, npoints, c);

    pfree(buf);
    lwgeom_free(l);
    return gserialized_point4d_datum(c[0], c[1], c[2], c[3]);
}

PG_FUNCTION_INFO_V1(pg_laplace_radius_origin);

Datum
pg_laplace_radius_origin(PG_FUNCTION_ARGS)
{
    GSERIALIZED *g;
    LWGEOM *l = lwgeom_from_datum(PG_GETARG_DATUM(0), &g);

    POINT4D p;
    require_point4d(l, "laplace_radius_origin", &p);

    const double v[4] = {p.x, p.y, p.z, p.m};
    const double r = math4d_radius_from_origin(v);

    lwgeom_free(l);
    PG_RETURN_FLOAT8(r);
}

PG_FUNCTION_INFO_V1(pg_laplace_frechet_4d);

Datum
pg_laplace_frechet_4d(PG_FUNCTION_ARGS)
{
    GSERIALIZED *g_a, *g_b;
    LWGEOM *l_a = lwgeom_from_datum(PG_GETARG_DATUM(0), &g_a);
    LWGEOM *l_b = lwgeom_from_datum(PG_GETARG_DATUM(1), &g_b);

    size_t na, nb;
    double *buf_a = geom_to_xyzm_buffer(l_a, "laplace_frechet_4d", &na);
    double *buf_b = geom_to_xyzm_buffer(l_b, "laplace_frechet_4d", &nb);

    const double d = math4d_frechet(buf_a, na, buf_b, nb);

    pfree(buf_a);
    pfree(buf_b);
    lwgeom_free(l_a);
    lwgeom_free(l_b);

    if (isnan(d))
        PG_RETURN_NULL();
    PG_RETURN_FLOAT8(d);
}

PG_FUNCTION_INFO_V1(pg_laplace_hausdorff_4d);

Datum
pg_laplace_hausdorff_4d(PG_FUNCTION_ARGS)
{
    GSERIALIZED *g_a, *g_b;
    LWGEOM *l_a = lwgeom_from_datum(PG_GETARG_DATUM(0), &g_a);
    LWGEOM *l_b = lwgeom_from_datum(PG_GETARG_DATUM(1), &g_b);

    size_t na, nb;
    double *buf_a = geom_to_xyzm_buffer(l_a, "laplace_hausdorff_4d", &na);
    double *buf_b = geom_to_xyzm_buffer(l_b, "laplace_hausdorff_4d", &nb);

    const double d = math4d_hausdorff(buf_a, na, buf_b, nb);

    pfree(buf_a);
    pfree(buf_b);
    lwgeom_free(l_a);
    lwgeom_free(l_b);

    if (isnan(d))
        PG_RETURN_NULL();
    PG_RETURN_FLOAT8(d);
}

PG_FUNCTION_INFO_V1(pg_laplace_hilbert_encode);

Datum
pg_laplace_hilbert_encode(PG_FUNCTION_ARGS)
{
    GSERIALIZED *g;
    LWGEOM *l = lwgeom_from_datum(PG_GETARG_DATUM(0), &g);

    POINT4D p;
    require_point4d(l, "laplace_hilbert_encode", &p);

    const double v[4] = {p.x, p.y, p.z, p.m};
    bytea *result = (bytea *) palloc(VARHDRSZ + sizeof(hilbert128_t));
    SET_VARSIZE(result, VARHDRSZ + sizeof(hilbert128_t));
    hilbert4d_encode(v, (hilbert128_t *) VARDATA(result));

    lwgeom_free(l);
    PG_RETURN_BYTEA_P(result);
}

PG_FUNCTION_INFO_V1(pg_laplace_hilbert_decode);

Datum
pg_laplace_hilbert_decode(PG_FUNCTION_ARGS)
{
    bytea *h_arg = PG_GETARG_BYTEA_PP(0);
    Size   h_len = VARSIZE_ANY_EXHDR(h_arg);
    if (h_len != sizeof(hilbert128_t))
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("laplace_hilbert_decode: input must be exactly %zu bytes (got %zu)",
                        sizeof(hilbert128_t), h_len)));

    hilbert128_t h;
    memcpy(&h, VARDATA_ANY(h_arg), sizeof(hilbert128_t));

    double v[4];
    hilbert4d_decode(&h, v);

    return gserialized_point4d_datum(v[0], v[1], v[2], v[3]);
}

PG_FUNCTION_INFO_V1(pg_laplace_mantissa_pack);

Datum
pg_laplace_mantissa_pack(PG_FUNCTION_ARGS)
{
    bytea  *eid_arg    = PG_GETARG_BYTEA_PP(0);
    int32   ordinal    = PG_GETARG_INT32(1);
    int32   run_length = PG_GETARG_INT32(2);
    int64   flags_arg  = PG_GETARG_INT64(3);

    Size eid_len = VARSIZE_ANY_EXHDR(eid_arg);
    if (eid_len != sizeof(hash128_t))
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("laplace_mantissa_pack: entity_id must be %zu bytes (got %zu)",
                        sizeof(hash128_t), eid_len)));
    if (ordinal < 0 || ordinal > UINT16_MAX)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("laplace_mantissa_pack: ordinal %d out of uint16 range", ordinal)));
    if (run_length < 0 || run_length > UINT16_MAX)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("laplace_mantissa_pack: run_length %d out of uint16 range", run_length)));
    if ((uint64_t) flags_arg & 0xFFF0000000000000ULL)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("laplace_mantissa_pack: flags must fit in low 52 bits (high 12 must be zero)")));

    mantissa_payload_t payload;
    memcpy(&payload.entity_id, VARDATA_ANY(eid_arg), sizeof(hash128_t));
    payload.ordinal    = (uint16_t) ordinal;
    payload.run_length = (uint16_t) run_length;
    payload.flags      = (uint64_t) flags_arg;

    double v[4];
    mantissa_pack(v, &payload);
    return gserialized_point4d_datum(v[0], v[1], v[2], v[3]);
}

PG_FUNCTION_INFO_V1(pg_laplace_mantissa_unpack);

Datum
pg_laplace_mantissa_unpack(PG_FUNCTION_ARGS)
{
    GSERIALIZED *g;
    LWGEOM *l = lwgeom_from_datum(PG_GETARG_DATUM(0), &g);

    POINT4D p;
    require_point4d(l, "laplace_mantissa_unpack", &p);

    const double v[4] = {p.x, p.y, p.z, p.m};
    mantissa_payload_t payload;
    mantissa_unpack(v, &payload);

    TupleDesc tupdesc;
    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
    {
        lwgeom_free(l);
        ereport(ERROR,
                (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                 errmsg("laplace_mantissa_unpack: function returning record called in context that cannot accept type record")));
    }
    BlessTupleDesc(tupdesc);

    Datum values[4];
    bool  nulls[4] = {false, false, false, false};

    bytea *eid_out = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
    SET_VARSIZE(eid_out, VARHDRSZ + sizeof(hash128_t));
    memcpy(VARDATA(eid_out), &payload.entity_id, sizeof(hash128_t));

    values[0] = PointerGetDatum(eid_out);
    values[1] = Int32GetDatum((int32) payload.ordinal);
    values[2] = Int32GetDatum((int32) payload.run_length);
    values[3] = Int64GetDatum((int64) payload.flags);

    HeapTuple tuple = heap_form_tuple(tupdesc, values, nulls);

    lwgeom_free(l);
    PG_RETURN_DATUM(HeapTupleGetDatum(tuple));
}

/* Vertex flag-word field decode — the mantissa.h accessors are the single
 * source of the bit layout; the SQL vertex_atom/vertex_tier helpers delegate
 * here instead of re-spelling shifts and masks. */
PG_FUNCTION_INFO_V1(pg_laplace_vertex_atom);

Datum
pg_laplace_vertex_atom(PG_FUNCTION_ARGS)
{
    uint64 flags = (uint64) PG_GETARG_INT64(0);

    if (!laplace_vflag_has_atom(flags))
        PG_RETURN_NULL();
    PG_RETURN_INT32((int32) laplace_vflag_atom(flags));
}

PG_FUNCTION_INFO_V1(pg_laplace_vertex_tier);

Datum
pg_laplace_vertex_tier(PG_FUNCTION_ARGS)
{
    PG_RETURN_INT16((int16) laplace_vflag_tier((uint64) PG_GETARG_INT64(0)));
}

/* One C loop over a stored trajectory: every vertex mantissa-unpacked in
 * process. Replaces the per-vertex fmgr lateral (ST_DumpPoints +
 * laplace_mantissa_unpack per point) that the substrate's constituents()
 * surface used to fan out into. Output is bit-identical to that lateral. */
PG_FUNCTION_INFO_V1(pg_laplace_trajectory_constituents);

Datum
pg_laplace_trajectory_constituents(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;

    InitMaterializedSRF(fcinfo, 0);

    GSERIALIZED *g;
    LWGEOM *l = lwgeom_from_datum(PG_GETARG_DATUM(0), &g);

    size_t  n = 0;
    double *xyzm = geom_to_xyzm_buffer(l, "laplace_trajectory_constituents", &n);

    for (size_t i = 0; i < n; ++i)
    {
        mantissa_payload_t payload;
        mantissa_unpack(&xyzm[i * 4], &payload);

        bytea *eid_out = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
        SET_VARSIZE(eid_out, VARHDRSZ + sizeof(hash128_t));
        memcpy(VARDATA(eid_out), &payload.entity_id, sizeof(hash128_t));

        Datum values[4];
        bool  nulls[4] = {false, false, false, false};
        values[0] = Int32GetDatum((int32) payload.ordinal);
        values[1] = PointerGetDatum(eid_out);
        values[2] = Int32GetDatum((int32) payload.run_length);
        values[3] = Int64GetDatum((int64) payload.flags);
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
    }

    pfree(xyzm);
    lwgeom_free(l);
    return (Datum) 0;
}

/* Native bytea[] of the DISTINCT constituent entity-ids of a trajectory. Same decode as
 * laplace_trajectory_constituents, but returns the id array directly (no SQL array_agg over an
 * SRF) so it can back a content-addressed GIN index (physicalities_constituents_gin) and the
 * @> reverse lookup at native speed. */
PG_FUNCTION_INFO_V1(pg_laplace_trajectory_constituent_ids);

Datum
pg_laplace_trajectory_constituent_ids(PG_FUNCTION_ARGS)
{
    GSERIALIZED *g;
    LWGEOM *l = lwgeom_from_datum(PG_GETARG_DATUM(0), &g);

    size_t  n = 0;
    double *xyzm = geom_to_xyzm_buffer(l, "laplace_trajectory_constituent_ids", &n);

    Datum     *elems = (n > 0) ? (Datum *) palloc(sizeof(Datum) * n) : NULL;
    hash128_t *seen  = (n > 0) ? (hash128_t *) palloc(sizeof(hash128_t) * n) : NULL;
    int m = 0;

    for (size_t i = 0; i < n; ++i)
    {
        mantissa_payload_t payload;
        mantissa_unpack(&xyzm[i * 4], &payload);

        bool dup = false;
        for (int j = 0; j < m; ++j)
            if (memcmp(&seen[j], &payload.entity_id, sizeof(hash128_t)) == 0) { dup = true; break; }
        if (dup) continue;

        seen[m] = payload.entity_id;
        bytea *eid_out = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
        SET_VARSIZE(eid_out, VARHDRSZ + sizeof(hash128_t));
        memcpy(VARDATA(eid_out), &payload.entity_id, sizeof(hash128_t));
        elems[m++] = PointerGetDatum(eid_out);
    }

    pfree(xyzm);
    lwgeom_free(l);

    ArrayType *result = (m == 0)
        ? construct_empty_array(BYTEAOID)
        : construct_array(elems, m, BYTEAOID, -1, false, TYPALIGN_INT);
    PG_RETURN_ARRAYTYPE_P(result);
}
