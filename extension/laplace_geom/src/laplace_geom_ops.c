#include "laplace_geom_internal.h"

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
    return laplace_geom_gserialized_point4d_datum(v[0], v[1], v[2], v[3]);
}

PG_FUNCTION_INFO_V1(pg_laplace_mantissa_unpack);

Datum
pg_laplace_mantissa_unpack(PG_FUNCTION_ARGS)
{
    GSERIALIZED *g;
    LWGEOM *l = laplace_geom_lwgeom_from_datum(PG_GETARG_DATUM(0), &g);

    POINT4D p;
    laplace_geom_require_point4d(l, "laplace_mantissa_unpack", &p);

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

/*
 * laplace_trajectory_build(bytea[]) -> geometry
 *
 * The mantissa PACKER, bound to SQL. The decoder
 * (laplace_trajectory_constituents) has always been callable from SQL; the
 * builder was reachable only from managed code, so anything in SQL that needed a
 * trajectory -- a regress fixture, a repair, an ad-hoc probe -- had no way to make
 * one except to reimplement the packing, which would be a second implementation
 * of the identity encoding and is exactly what must never exist.
 *
 * This is not a second implementation: it calls the canonical RLE trajectory
 * packer from
 * engine/core, the same function the compose path calls, so a trajectory produced
 * here is byte-identical to one produced during ingest. Same code, one more
 * binding.
 *
 * Cheap by construction: the engine is already resident in the backend (this
 * extension links it and mantissa_unpack runs on every decode), so this is a
 * direct call into loaded memory -- no process boundary, no serialization beyond
 * the array unwrap. Round-trip contract: laplace_trajectory_constituents() over
 * the result returns the input ids, in order.
 */
PG_FUNCTION_INFO_V1(pg_laplace_trajectory_build);

Datum
pg_laplace_trajectory_build(PG_FUNCTION_ARGS)
{
    ArrayType *arr = PG_GETARG_ARRAYTYPE_P(0);

    if (ARR_HASNULL(arr))
        ereport(ERROR,
                (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                 errmsg("laplace_trajectory_build: id array must not contain NULLs")));

    Datum *elems;
    int    n;
    deconstruct_array(arr, BYTEAOID, -1, false, TYPALIGN_INT, &elems, NULL, &n);

    if (n <= 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("laplace_trajectory_build: need at least one id")));

    hash128_t *ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n);
    for (int i = 0; i < n; ++i)
    {
        bytea *b = DatumGetByteaPP(elems[i]);
        if (VARSIZE_ANY_EXHDR(b) != sizeof(hash128_t))
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("laplace_trajectory_build: id %d is %d bytes, expected %d",
                            i, (int) VARSIZE_ANY_EXHDR(b), (int) sizeof(hash128_t))));
        memcpy(&ids[i], VARDATA_ANY(b), sizeof(hash128_t));
    }

    laplace_geom_require_xyzm_capacity((Size) n, "laplace_trajectory_build");
    double *xyzm = (double *) palloc(sizeof(double) * 4 * (Size) n);
    size_t vertex_count = 0;
    if (trajectory_build_rle(ids, (size_t) n, xyzm, &vertex_count) != 0
        || vertex_count > UINT32_MAX)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("laplace_trajectory_build: packer rejected %d id(s)", n)));

    /* ZM linestring: the same shape physicalities.trajectory carries. */
    POINTARRAY *pa = ptarray_construct(1 /*hasz*/, 1 /*hasm*/, (uint32_t) vertex_count);
    for (size_t i = 0; i < vertex_count; ++i)
    {
        POINT4D pt;
        pt.x = xyzm[i * 4 + 0];
        pt.y = xyzm[i * 4 + 1];
        pt.z = xyzm[i * 4 + 2];
        pt.m = xyzm[i * 4 + 3];
        ptarray_set_point4d(pa, (uint32_t) i, &pt);
    }

    LWLINE      *line = lwline_construct(0 /*srid*/, NULL, pa);
    size_t       sz   = 0;
    GSERIALIZED *out  = gserialized_from_lwgeom((LWGEOM *) line, &sz);

    lwline_free(line);
    pfree(xyzm);
    pfree(ids);
    pfree(elems);

    PG_RETURN_POINTER(out);
}

typedef struct
{
    ReturnSetInfo *rsinfo;
} trajectory_vertex_emit_t;

static int
emit_trajectory_vertex(void *context, size_t ordinal,
    const hash128_t *entity_id, size_t run_length, uint64_t flags)
{
    trajectory_vertex_emit_t *emit = context;
    if (ordinal > PG_INT32_MAX)
        ereport(ERROR, (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
            errmsg("laplace_trajectory_constituents: ordinal exceeds int4")));
    bytea *id = palloc(VARHDRSZ + sizeof(*entity_id));
    SET_VARSIZE(id, VARHDRSZ + sizeof(*entity_id));
    memcpy(VARDATA(id), entity_id, sizeof(*entity_id));
    Datum values[4] = {Int32GetDatum((int32) ordinal), PointerGetDatum(id),
        Int32GetDatum((int32) run_length), Int64GetDatum((int64) flags)};
    bool nulls[4] = {false, false, false, false};
    tuplestore_putvalues(emit->rsinfo->setResult, emit->rsinfo->setDesc, values, nulls);
    pfree(id);
    return 0;
}

PG_FUNCTION_INFO_V1(pg_laplace_trajectory_constituents);

Datum
pg_laplace_trajectory_constituents(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    InitMaterializedSRF(fcinfo, 0);
    GSERIALIZED *g;
    LWGEOM *l = laplace_geom_lwgeom_from_datum(PG_GETARG_DATUM(0), &g);
    size_t n = 0;
    double *xyzm = laplace_geom_to_xyzm_buffer(l, "laplace_trajectory_constituents", &n);
    trajectory_vertex_emit_t emit = {rsinfo};
    if (trajectory_visit_vertices(xyzm, n, emit_trajectory_vertex, &emit) != 0)
        ereport(ERROR, (errmsg("laplace_trajectory_constituents: invalid trajectory")));
    pfree(xyzm);
    lwgeom_free(l);
    return (Datum) 0;
}

typedef struct
{
    ReturnSetInfo *rsinfo;
} trajectory_expanded_emit_t;

static int
emit_expanded_trajectory_constituent(void *context, size_t ordinal,
                                     const hash128_t *entity_id, uint64_t flags)
{
    trajectory_expanded_emit_t *emit = (trajectory_expanded_emit_t *) context;
    if (ordinal > PG_INT32_MAX)
        return -1;

    bytea *eid_out = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
    SET_VARSIZE(eid_out, VARHDRSZ + sizeof(hash128_t));
    memcpy(VARDATA(eid_out), entity_id, sizeof(hash128_t));

    Datum values[3];
    bool nulls[3] = {false, false, false};
    values[0] = Int32GetDatum((int32) ordinal);
    values[1] = PointerGetDatum(eid_out);
    values[2] = Int64GetDatum((int64) flags);
    tuplestore_putvalues(emit->rsinfo->setResult, emit->rsinfo->setDesc,
                         values, nulls);
    return 0;
}

/* The legacy constituents surface returns stored RLE vertices.  This one
 * returns logical members and delegates prefix-sum ordinals and expansion to
 * the core trajectory kernel, so no SQL/C# caller derives them from geometry
 * vertex positions. */
PG_FUNCTION_INFO_V1(pg_laplace_trajectory_expanded_constituents);

Datum
pg_laplace_trajectory_expanded_constituents(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    InitMaterializedSRF(fcinfo, 0);

    GSERIALIZED *g;
    LWGEOM *l = laplace_geom_lwgeom_from_datum(PG_GETARG_DATUM(0), &g);
    size_t n = 0;
    double *xyzm = laplace_geom_to_xyzm_buffer(
        l, "laplace_trajectory_expanded_constituents", &n);

    trajectory_expanded_emit_t emit = { rsinfo };
    if (trajectory_visit_constituents(xyzm, n,
                                      emit_expanded_trajectory_constituent,
                                      &emit) != 0)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("laplace_trajectory_expanded_constituents: logical ordinal exceeds int4")));

    pfree(xyzm);
    lwgeom_free(l);
    return (Datum) 0;
}

/* Logical trajectory equality. Stored vertex count and RLE chunk boundaries
 * are encoding details; the core compares the ordered ids, multiplicity, and
 * complete flag payload without expanding either manifest. */
PG_FUNCTION_INFO_V1(pg_laplace_trajectory_equivalent);

Datum
pg_laplace_trajectory_equivalent(PG_FUNCTION_ARGS)
{
    GSERIALIZED *left_serialized;
    GSERIALIZED *right_serialized;
    LWGEOM *left = laplace_geom_lwgeom_from_datum(PG_GETARG_DATUM(0), &left_serialized);
    LWGEOM *right = laplace_geom_lwgeom_from_datum(PG_GETARG_DATUM(1), &right_serialized);
    size_t left_points = 0;
    size_t right_points = 0;
    double *left_xyzm = laplace_geom_to_xyzm_buffer(
        left, "laplace_trajectory_equivalent", &left_points);
    double *right_xyzm = laplace_geom_to_xyzm_buffer(
        right, "laplace_trajectory_equivalent", &right_points);

    int equivalent = trajectory_equivalent(
        left_xyzm, left_points, right_xyzm, right_points);
    pfree(right_xyzm);
    pfree(left_xyzm);
    lwgeom_free(right);
    lwgeom_free(left);
    if (equivalent < 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("laplace_trajectory_equivalent: invalid trajectory")));
    PG_RETURN_BOOL(equivalent == 1);
}

PG_FUNCTION_INFO_V1(pg_laplace_trajectory_constituent_ids);

Datum
pg_laplace_trajectory_constituent_ids(PG_FUNCTION_ARGS)
{
    GSERIALIZED *g;
    LWGEOM *l = laplace_geom_lwgeom_from_datum(PG_GETARG_DATUM(0), &g);

    size_t  n = 0;
    double *xyzm = laplace_geom_to_xyzm_buffer(l, "laplace_trajectory_constituent_ids", &n);

    Datum     *elems = (n > 0) ? (Datum *) palloc(sizeof(Datum) * n) : NULL;
    HASHCTL ctl = {0};
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(hash128_t);
    HTAB *seen = hash_create("trajectory membership ids", 128, &ctl,
                             HASH_ELEM | HASH_BLOBS);
    int m = 0;

    for (size_t i = 0; i < n; ++i)
    {
        if ((i & 1023) == 0) CHECK_FOR_INTERRUPTS();
        mantissa_payload_t payload;
        mantissa_unpack(&xyzm[i * 4], &payload);

        /* This is the GIN membership projection, not an ordered manifest.
         * Keep first-occurrence output byte-for-byte compatible with existing
         * indexes, but do not linearly rescan all earlier ids per vertex. */
        bool dup;
        hash_search(seen, &payload.entity_id, HASH_ENTER, &dup);
        if (dup) continue;

        bytea *eid_out = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
        SET_VARSIZE(eid_out, VARHDRSZ + sizeof(hash128_t));
        memcpy(VARDATA(eid_out), &payload.entity_id, sizeof(hash128_t));
        elems[m++] = PointerGetDatum(eid_out);
    }

    hash_destroy(seen);
    pfree(xyzm);
    lwgeom_free(l);

    ArrayType *result = (m == 0)
        ? construct_empty_array(BYTEAOID)
        : construct_array(elems, m, BYTEAOID, -1, false, TYPALIGN_INT);
    PG_RETURN_ARRAYTYPE_P(result);
}
