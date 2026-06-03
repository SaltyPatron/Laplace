/*
 * extension/laplace_geom/src/laplace_geom.c
 *
 * PG_FUNCTION_INFO_V1 wrappers for laplace_geom., this
 * file is thin marshalling code only -- no math. Engine kernels in
 * liblaplace_core do the work; liblwgeom (static-linked)
 * does the gserialized -> POINT4D extraction.
 *
 * Functions exposed:
 *   - pg_laplace_geom_version         : extension self-identity
 *   - pg_laplace_hash128_blake3       : BLAKE3-128 of an arbitrary bytea
 *   - pg_laplace_hash128_merkle       : tier-prefixed Merkle composition
 *
 *   - pg_laplace_distance_4d          : Euclidean 4D distance between POINTs
 *   - pg_laplace_dwithin_4d           : Euclidean 4D within-epsilon predicate
 *   - pg_laplace_centroid_4d          : centroid of a geometry's vertices
 *   - pg_laplace_radius_origin        : radius of a POINT from origin
 *   - pg_laplace_frechet_4d           : discrete Frechet distance, LINESTRINGs
 *   - pg_laplace_hausdorff_4d         : symmetric Hausdorff distance
 *
 *   - pg_laplace_hilbert_encode       : POINT -> bytea(16)
 *   - pg_laplace_hilbert_decode       : bytea(16) -> POINT
 *
 *   - pg_laplace_mantissa_pack        : (entity_id, ordinal, run_length, flags) -> POINT4D
 *   - pg_laplace_mantissa_unpack      : POINT4D -> (entity_id, ordinal, run_length, flags)
 */

#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "catalog/pg_type.h"
#include "access/htup_details.h"

#include "laplace/core/version.h"
#include "laplace/core/hash128.h"
#include "laplace/core/math4d.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/mantissa.h"

#include "liblwgeom.h"

PG_MODULE_MAGIC;

/* ================================================================= */
/* Helpers — geometry deserialization                                */
/* ================================================================= */

/* Detoast + deserialize. Returns a palloc'd LWGEOM. Errors on NULL.
 * Caller passes the detoasted GSERIALIZED in to free_if_copy after use
 * (when the original Datum was toasted, PG_DETOAST_DATUM mallocs a copy). */
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

/* Extract a single POINT4D from a geometry that must be a POINT.
 * Missing Z and/or M are zeroed (so 2D/3D POINTs lift cleanly into 4D).
 * Frees `geom` before erroring. */
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
    /* getPoint4d_p zeros Z/M when the underlying pointarray lacks them. */
    getPoint4d_p(lwpoint->point, 0, out);
}

/* Pull a flat XYZM double buffer from a geometry that exposes a POINTARRAY
 * (POINT, MULTIPOINT, or LINESTRING). The returned buffer is freshly
 * palloc'd and contains `npoints * 4` doubles in XYZM order, with missing
 * Z/M zeroed. Frees `geom` before erroring on type mismatch.
 *
 * On success, the caller is responsible for pfree(buf) and lwgeom_free(geom)
 * when done with both. */
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
            /* MULTIPOINT is a collection of single-point sub-LWPOINTs.
             * Flatten into a single buffer. */
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

/* Build a GSERIALIZED POINT4D from a (x, y, z, m) tuple and return as Datum.
 * lwpoint_make4d + gserialized_from_lwgeom is the canonical path. */
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

/* ================================================================= */
/* pg_laplace_geom_version                                            */
/* ================================================================= */

PG_FUNCTION_INFO_V1(pg_laplace_geom_version);

Datum
pg_laplace_geom_version(PG_FUNCTION_ARGS)
{
    const char *v = laplace_core_version();
    PG_RETURN_TEXT_P(cstring_to_text(v));
}

/* ================================================================= */
/* pg_laplace_hash128_blake3(bytea) -> bytea(16)                      */
/* ================================================================= */

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

/* ================================================================= */
/* pg_laplace_hash128_merkle(int2 tier, bytea[] children) -> bytea(16) */
/* ================================================================= */

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

/* ================================================================= */
/* pg_laplace_distance_4d(geometry, geometry) -> double precision     */
/* ================================================================= */

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

/* ================================================================= */
/* pg_laplace_angular_distance_4d(geometry, geometry) -> double        */
/* ================================================================= */

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
    /* Geodesic distance on S³ = acos(â·b̂); the engine normalizes defensively
     * and clamps the cosine for FP safety. */
    const double d = math4d_angular_distance(a, b);

    lwgeom_free(l_a);
    lwgeom_free(l_b);
    PG_RETURN_FLOAT8(d);
}

/* ================================================================= */
/* pg_laplace_dwithin_4d(geometry, geometry, double) -> boolean       */
/* ================================================================= */

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
    /* Use distance_sq vs eps*eps — avoids the sqrt in the kernel. */
    const double d2 = math4d_distance_sq(a, b);
    const bool   within = d2 <= eps * eps;

    lwgeom_free(l_a);
    lwgeom_free(l_b);
    PG_RETURN_BOOL(within);
}

/* ================================================================= */
/* pg_laplace_centroid_4d(geometry) -> geometry                       */
/* ================================================================= */

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

/* ================================================================= */
/* pg_laplace_radius_origin(geometry) -> double precision             */
/* ================================================================= */

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

/* ================================================================= */
/* pg_laplace_frechet_4d(geometry, geometry) -> double precision      */
/* ================================================================= */

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

/* ================================================================= */
/* pg_laplace_hausdorff_4d(geometry, geometry) -> double precision    */
/* ================================================================= */

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

/* ================================================================= */
/* pg_laplace_hilbert_encode(geometry) -> bytea(16)                   */
/* ================================================================= */

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

/* ================================================================= */
/* pg_laplace_hilbert_decode(bytea) -> geometry                       */
/* ================================================================= */

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

/* ================================================================= */
/* pg_laplace_mantissa_pack(bytea, int, int, bigint) -> geometry      */
/* ================================================================= */

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
    /*: only low 52 bits of flags are usable; high 12 must be zero. */
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

/* ================================================================= */
/* pg_laplace_mantissa_unpack(geometry) ->                            */
/*     TABLE(entity_id bytea, ordinal int, run_length int, flags bigint) */
/* ================================================================= */

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

    /* Build the result tuple via TupleDesc the caller declared with RETURNS TABLE. */
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
