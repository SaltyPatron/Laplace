/*
 * polyline4d_distances.c — discrete Fréchet + symmetric Hausdorff in 4D.
 */

#include "laplace_pg/polyline4d.h"

#include <math.h>
#include <stdlib.h>

static double max2(double a, double b) { return a > b ? a : b; }
static double min2(double a, double b) { return a < b ? a : b; }

double laplace_frechet_distance_4d(const laplace_point4d_t *p, size_t np,
                                   const laplace_point4d_t *q, size_t nq)
{
    if (np == 0 || nq == 0) {
        return INFINITY;
    }
    /* Iterative DP over (i, j) ∈ [0, np) × [0, nq). ca[i*nq + j] holds
     * the coupling distance up to (p[i], q[j]). */
    double *ca = (double *) malloc(np * nq * sizeof(double));
    if (ca == NULL) {
        return INFINITY;
    }
    ca[0] = laplace_point4d_distance(&p[0], &q[0]);
    for (size_t i = 1; i < np; ++i) {
        ca[i * nq + 0] = max2(ca[(i - 1) * nq + 0],
                              laplace_point4d_distance(&p[i], &q[0]));
    }
    for (size_t j = 1; j < nq; ++j) {
        ca[0 * nq + j] = max2(ca[0 * nq + (j - 1)],
                              laplace_point4d_distance(&p[0], &q[j]));
    }
    for (size_t i = 1; i < np; ++i) {
        for (size_t j = 1; j < nq; ++j) {
            const double d  = laplace_point4d_distance(&p[i], &q[j]);
            const double m1 = ca[(i - 1) * nq + j];
            const double m2 = ca[i       * nq + (j - 1)];
            const double m3 = ca[(i - 1) * nq + (j - 1)];
            ca[i * nq + j]  = max2(d, min2(m1, min2(m2, m3)));
        }
    }
    const double result = ca[(np - 1) * nq + (nq - 1)];
    free(ca);
    return result;
}

static double directed_hausdorff(const laplace_point4d_t *a, size_t na,
                                 const laplace_point4d_t *b, size_t nb)
{
    double sup = 0.0;
    for (size_t i = 0; i < na; ++i) {
        double inf = INFINITY;
        for (size_t j = 0; j < nb; ++j) {
            const double d = laplace_point4d_distance(&a[i], &b[j]);
            if (d < inf) {
                inf = d;
            }
        }
        if (inf > sup) {
            sup = inf;
        }
    }
    return sup;
}

double laplace_hausdorff_distance_4d(const laplace_point4d_t *p, size_t np,
                                     const laplace_point4d_t *q, size_t nq)
{
    if (np == 0 || nq == 0) {
        return INFINITY;
    }
    return max2(directed_hausdorff(p, np, q, nq),
                directed_hausdorff(q, nq, p, np));
}
