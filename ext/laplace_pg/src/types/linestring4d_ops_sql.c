/*
 * linestring4d_ops_sql.c — SQL operators on LINESTRING4D.
 *
 * Substrate-relevant operators: vertex_count, vertex_at, length (sum of
 * geodesic chord lengths), vertex_centroid, frechet/hausdorff distance
 * to another linestring, bounding box, append/prepend (for incremental
 * composition emission).
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"

#include "laplace_pg/linestring4d_type.h"
#include "laplace_pg/point4d_type.h"
#include "laplace_pg/box4d_type.h"
#include "laplace_pg/geometry4d.h"
#include "laplace_pg/polyline4d.h"

PG_FUNCTION_INFO_V1(linestring4d_vertex_count);
Datum linestring4d_vertex_count(PG_FUNCTION_ARGS)
{
    laplace_linestring4d_pg_t *ls = PG_GETARG_LINESTRING4D(0);
    PG_RETURN_INT32((int32) ls->vertex_count);
}

PG_FUNCTION_INFO_V1(linestring4d_vertex_at);
Datum linestring4d_vertex_at(PG_FUNCTION_ARGS)
{
    laplace_linestring4d_pg_t *ls = PG_GETARG_LINESTRING4D(0);
    int32 idx = PG_GETARG_INT32(1);
    if (idx < 0 || (uint32) idx >= ls->vertex_count) {
        ereport(ERROR, (errmsg("linestring4d vertex index %d out of range [0, %u)",
                               idx, ls->vertex_count)));
    }
    laplace_point4d_pg_t *pt = (laplace_point4d_pg_t *) palloc(sizeof *pt);
    pt->x = ls->vertices[idx * 4 + 0];
    pt->y = ls->vertices[idx * 4 + 1];
    pt->z = ls->vertices[idx * 4 + 2];
    pt->w = ls->vertices[idx * 4 + 3];
    PG_RETURN_POINT4D(pt);
}

PG_FUNCTION_INFO_V1(linestring4d_length);
Datum linestring4d_length(PG_FUNCTION_ARGS)
{
    laplace_linestring4d_pg_t *ls = PG_GETARG_LINESTRING4D(0);
    if (ls->vertex_count < 2) { PG_RETURN_FLOAT8(0.0); }
    double total = 0.0;
    for (uint32 i = 1; i < ls->vertex_count; ++i) {
        const laplace_point4d_t a = {
            ls->vertices[(i-1)*4+0], ls->vertices[(i-1)*4+1],
            ls->vertices[(i-1)*4+2], ls->vertices[(i-1)*4+3]
        };
        const laplace_point4d_t b = {
            ls->vertices[i*4+0], ls->vertices[i*4+1],
            ls->vertices[i*4+2], ls->vertices[i*4+3]
        };
        total += laplace_point4d_distance(&a, &b);
    }
    PG_RETURN_FLOAT8(total);
}

PG_FUNCTION_INFO_V1(linestring4d_vertex_centroid);
Datum linestring4d_vertex_centroid(PG_FUNCTION_ARGS)
{
    laplace_linestring4d_pg_t *ls = PG_GETARG_LINESTRING4D(0);
    laplace_point4d_pg_t *out = (laplace_point4d_pg_t *) palloc(sizeof *out);
    if (ls->vertex_count == 0) {
        out->x = out->y = out->z = 0.0; out->w = 1.0;
        PG_RETURN_POINT4D(out);
    }
    double sx = 0, sy = 0, sz = 0, sw = 0;
    for (uint32 i = 0; i < ls->vertex_count; ++i) {
        sx += ls->vertices[i*4+0];
        sy += ls->vertices[i*4+1];
        sz += ls->vertices[i*4+2];
        sw += ls->vertices[i*4+3];
    }
    const double inv = 1.0 / (double) ls->vertex_count;
    out->x = sx * inv; out->y = sy * inv; out->z = sz * inv; out->w = sw * inv;
    PG_RETURN_POINT4D(out);
}

PG_FUNCTION_INFO_V1(linestring4d_frechet);
Datum linestring4d_frechet(PG_FUNCTION_ARGS)
{
    laplace_linestring4d_pg_t *a = PG_GETARG_LINESTRING4D(0);
    laplace_linestring4d_pg_t *b = PG_GETARG_LINESTRING4D(1);
    PG_RETURN_FLOAT8(laplace_frechet_distance_4d(
        (const laplace_point4d_t *) a->vertices, a->vertex_count,
        (const laplace_point4d_t *) b->vertices, b->vertex_count));
}

PG_FUNCTION_INFO_V1(linestring4d_hausdorff);
Datum linestring4d_hausdorff(PG_FUNCTION_ARGS)
{
    laplace_linestring4d_pg_t *a = PG_GETARG_LINESTRING4D(0);
    laplace_linestring4d_pg_t *b = PG_GETARG_LINESTRING4D(1);
    PG_RETURN_FLOAT8(laplace_hausdorff_distance_4d(
        (const laplace_point4d_t *) a->vertices, a->vertex_count,
        (const laplace_point4d_t *) b->vertices, b->vertex_count));
}

PG_FUNCTION_INFO_V1(linestring4d_envelope);
Datum linestring4d_envelope(PG_FUNCTION_ARGS)
{
    laplace_linestring4d_pg_t *ls = PG_GETARG_LINESTRING4D(0);
    laplace_box4d_pg_t *box = (laplace_box4d_pg_t *) palloc(sizeof *box);
    if (ls->vertex_count == 0) {
        box->min_x = box->min_y = box->min_z = box->min_w = 0.0;
        box->max_x = box->max_y = box->max_z = box->max_w = 0.0;
        PG_RETURN_BOX4D(box);
    }
    box->min_x = box->max_x = ls->vertices[0];
    box->min_y = box->max_y = ls->vertices[1];
    box->min_z = box->max_z = ls->vertices[2];
    box->min_w = box->max_w = ls->vertices[3];
    for (uint32 i = 1; i < ls->vertex_count; ++i) {
        const double *v = &ls->vertices[i * 4];
        if (v[0] < box->min_x) box->min_x = v[0]; else if (v[0] > box->max_x) box->max_x = v[0];
        if (v[1] < box->min_y) box->min_y = v[1]; else if (v[1] > box->max_y) box->max_y = v[1];
        if (v[2] < box->min_z) box->min_z = v[2]; else if (v[2] > box->max_z) box->max_z = v[2];
        if (v[3] < box->min_w) box->min_w = v[3]; else if (v[3] > box->max_w) box->max_w = v[3];
    }
    PG_RETURN_BOX4D(box);
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
