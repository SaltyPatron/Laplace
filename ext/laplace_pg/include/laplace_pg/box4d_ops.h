/*
 * box4d_ops.h — pure header helpers over BOX4D used by the GiST 4D
 * opclass family (POINT4D, LINESTRING4D, BOX4D).
 *
 * static inline so each translation unit gets its own copy; compiler
 * inlines as appropriate. PG-extension-only because it references the
 * PG-side struct typedefs.
 */

#ifndef LAPLACE_BOX4D_OPS_H
#define LAPLACE_BOX4D_OPS_H

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "laplace_pg/point4d_type.h"
#include "laplace_pg/box4d_type.h"

#include <math.h>
#include <stdbool.h>

static inline void
laplace_box4d_init_from_point(laplace_box4d_pg_t *box,
                              const laplace_point4d_pg_t *p)
{
    box->min_x = box->max_x = p->x;
    box->min_y = box->max_y = p->y;
    box->min_z = box->max_z = p->z;
    box->min_w = box->max_w = p->w;
}

static inline void
laplace_box4d_union(laplace_box4d_pg_t *out,
                    const laplace_box4d_pg_t *a,
                    const laplace_box4d_pg_t *b)
{
    out->min_x = a->min_x < b->min_x ? a->min_x : b->min_x;
    out->min_y = a->min_y < b->min_y ? a->min_y : b->min_y;
    out->min_z = a->min_z < b->min_z ? a->min_z : b->min_z;
    out->min_w = a->min_w < b->min_w ? a->min_w : b->min_w;
    out->max_x = a->max_x > b->max_x ? a->max_x : b->max_x;
    out->max_y = a->max_y > b->max_y ? a->max_y : b->max_y;
    out->max_z = a->max_z > b->max_z ? a->max_z : b->max_z;
    out->max_w = a->max_w > b->max_w ? a->max_w : b->max_w;
}

/* Edge-sum size metric. Robust to degenerate (point) boxes where
 * volume = 0. Sum of edge lengths totally orders nested boxes. */
static inline double
laplace_box4d_size(const laplace_box4d_pg_t *b)
{
    return (b->max_x - b->min_x)
         + (b->max_y - b->min_y)
         + (b->max_z - b->min_z)
         + (b->max_w - b->min_w);
}

static inline double
laplace_box4d_min_distance_to_point(const laplace_box4d_pg_t *b,
                                    const laplace_point4d_pg_t *p)
{
    double dx = (p->x < b->min_x) ? (b->min_x - p->x)
              : (p->x > b->max_x) ? (p->x - b->max_x)
              : 0.0;
    double dy = (p->y < b->min_y) ? (b->min_y - p->y)
              : (p->y > b->max_y) ? (p->y - b->max_y)
              : 0.0;
    double dz = (p->z < b->min_z) ? (b->min_z - p->z)
              : (p->z > b->max_z) ? (p->z - b->max_z)
              : 0.0;
    double dw = (p->w < b->min_w) ? (b->min_w - p->w)
              : (p->w > b->max_w) ? (p->w - b->max_w)
              : 0.0;
    return sqrt(dx * dx + dy * dy + dz * dz + dw * dw);
}

/* Min L2 distance between two 4D AABBs. Zero when boxes overlap or
 * touch. Used as the GiST KNN lower bound for trajectory-vs-trajectory
 * Fréchet ordering (see synthesis lines 78, 246, 289): for any pairwise
 * alignment of trajectories whose vertices lie in box A and box B
 * respectively, every matched pair has distance >= this bound, so
 * Fréchet(traj_A, traj_B) >= laplace_box4d_min_distance(box_A, box_B). */
static inline double
laplace_box4d_min_distance(const laplace_box4d_pg_t *a,
                           const laplace_box4d_pg_t *b)
{
    double dx = (a->max_x < b->min_x) ? (b->min_x - a->max_x)
              : (b->max_x < a->min_x) ? (a->min_x - b->max_x)
              : 0.0;
    double dy = (a->max_y < b->min_y) ? (b->min_y - a->max_y)
              : (b->max_y < a->min_y) ? (a->min_y - b->max_y)
              : 0.0;
    double dz = (a->max_z < b->min_z) ? (b->min_z - a->max_z)
              : (b->max_z < a->min_z) ? (a->min_z - b->max_z)
              : 0.0;
    double dw = (a->max_w < b->min_w) ? (b->min_w - a->max_w)
              : (b->max_w < a->min_w) ? (a->min_w - b->max_w)
              : 0.0;
    return sqrt(dx * dx + dy * dy + dz * dz + dw * dw);
}

static inline bool
laplace_box4d_contains_point(const laplace_box4d_pg_t *b,
                             const laplace_point4d_pg_t *p)
{
    return  p->x >= b->min_x && p->x <= b->max_x
         && p->y >= b->min_y && p->y <= b->max_y
         && p->z >= b->min_z && p->z <= b->max_z
         && p->w >= b->min_w && p->w <= b->max_w;
}

/* Box-on-box predicates for BOX4D opclass. */

static inline bool
laplace_box4d_overlaps(const laplace_box4d_pg_t *a,
                       const laplace_box4d_pg_t *b)
{
    return  a->min_x <= b->max_x && a->max_x >= b->min_x
         && a->min_y <= b->max_y && a->max_y >= b->min_y
         && a->min_z <= b->max_z && a->max_z >= b->min_z
         && a->min_w <= b->max_w && a->max_w >= b->min_w;
}

static inline bool
laplace_box4d_contains(const laplace_box4d_pg_t *a,
                       const laplace_box4d_pg_t *b)
{
    /* a contains b */
    return  a->min_x <= b->min_x && a->max_x >= b->max_x
         && a->min_y <= b->min_y && a->max_y >= b->max_y
         && a->min_z <= b->min_z && a->max_z >= b->max_z
         && a->min_w <= b->min_w && a->max_w >= b->max_w;
}

static inline bool
laplace_box4d_equal(const laplace_box4d_pg_t *a,
                    const laplace_box4d_pg_t *b)
{
    return  a->min_x == b->min_x && a->max_x == b->max_x
         && a->min_y == b->min_y && a->max_y == b->max_y
         && a->min_z == b->min_z && a->max_z == b->max_z
         && a->min_w == b->min_w && a->max_w == b->max_w;
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */

#endif /* LAPLACE_BOX4D_OPS_H */
