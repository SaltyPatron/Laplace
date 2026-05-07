/*
 * gram_schmidt.c — modified Gram-Schmidt orthonormalization.
 */

#include "laplace_pg/gram_schmidt.h"

#include <math.h>
#include <string.h>

static double dot(const double *a, const double *b, size_t dim)
{
    double s = 0.0;
    for (size_t k = 0; k < dim; ++k) {
        s += a[k] * b[k];
    }
    return s;
}

static double norm(const double *a, size_t dim)
{
    return sqrt(dot(a, a, dim));
}

size_t laplace_gram_schmidt_orthonormalize(double *vectors,
                                           size_t  n_vectors,
                                           size_t  dim,
                                           double  rank_tol)
{
    size_t out_count = 0;
    for (size_t i = 0; i < n_vectors; ++i) {
        double *v = vectors + i * dim;

        /* Subtract projections onto previously kept vectors (modified GS). */
        for (size_t j = 0; j < out_count; ++j) {
            const double *u = vectors + j * dim;
            const double  c = dot(v, u, dim);
            for (size_t k = 0; k < dim; ++k) {
                v[k] -= c * u[k];
            }
        }

        const double n = norm(v, dim);
        if (n <= rank_tol || !isfinite(n)) {
            memset(v, 0, dim * sizeof(double));
            continue;
        }
        const double inv = 1.0 / n;
        for (size_t k = 0; k < dim; ++k) {
            v[k] *= inv;
        }

        /* If we skipped any earlier vectors, slot this kept one into the
         * next out position so kept rows are contiguous. */
        if (out_count != i) {
            double *dst = vectors + out_count * dim;
            memmove(dst, v, dim * sizeof(double));
            memset(v, 0, dim * sizeof(double));
        }
        ++out_count;
    }
    return out_count;
}
