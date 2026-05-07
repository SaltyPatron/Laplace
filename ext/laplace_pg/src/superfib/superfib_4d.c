/*
 * superfib_4d.c — Super-Fibonacci spiral on S³.
 *
 * Direct port of the algorithm in Marc Alexa, CVPR 2022. Verified against
 * the managed Hartonomous-002 SuperFibonacci.cs implementation which uses
 * the same constants.
 */

#include "laplace_pg/superfib.h"

#include <math.h>

#ifndef M_PI
#  define M_PI 3.14159265358979323846
#endif

static const double LAPLACE_PSI = 1.5343237490380328129; /* ψ where ψ⁴ = ψ + 4 */
static const double LAPLACE_PHI = 1.6180339887498948482; /* golden ratio        */

void laplace_super_fibonacci_4d(int i, int total, double out_xyzw[4])
{
    if (total <= 0) {
        out_xyzw[0] = 0.0; out_xyzw[1] = 0.0; out_xyzw[2] = 0.0; out_xyzw[3] = 1.0;
        return;
    }
    const double t     = ((double) i + 0.5) / (double) total;
    const double r     = sqrt(t);
    const double R     = sqrt(1.0 - t);
    const double alpha = 2.0 * M_PI * (double) i / LAPLACE_PSI;
    const double beta  = 2.0 * M_PI * (double) i / LAPLACE_PHI;

    double x = r * sin(alpha);
    double y = r * cos(alpha);
    double z = R * sin(beta);
    double w = R * cos(beta);

    /* Defensive normalization — the spiral construction yields unit
     * quaternions analytically, but float roundoff can leave tiny drift.
     */
    const double norm = sqrt(x * x + y * y + z * z + w * w);
    if (norm > 0.0 && isfinite(norm)) {
        x /= norm; y /= norm; z /= norm; w /= norm;
    }

    out_xyzw[0] = x;
    out_xyzw[1] = y;
    out_xyzw[2] = z;
    out_xyzw[3] = w;
}

void laplace_super_fibonacci_4d_range(int start_inclusive,
                                      int end_exclusive,
                                      int total,
                                      double *out_array)
{
    for (int i = start_inclusive; i < end_exclusive; ++i) {
        laplace_super_fibonacci_4d(i, total, out_array + ((size_t)(i - start_inclusive) * 4));
    }
}
